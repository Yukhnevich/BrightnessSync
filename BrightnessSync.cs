// Brightness Sync - https://github.com/Yukhnevich/BrightnessSync
// Copyright (c) 2026 Pavel Yukhnevich. MIT License, see LICENSE.
//
// Keeps display brightness identical across all Windows power schemes, so switching
// power modes (e.g. Armoury Crate Silent / Performance) no longer changes brightness.
//
// Build: build.cmd (uses the C# 5 compiler that ships with .NET Framework 4.5+)
//
// Usage:
//   BrightnessSync.exe             show the tray icon and enable sync (hands over to a running instance)
//   BrightnessSync.exe /autostart  start with saved settings (used by the "Run at startup" task)
//
// Scrolling over the tray icon changes the brightness of all displays, external monitors included
// (over DDC/CI). The tray menu narrows it to one kind of display; Shift (built-in display) and
// Ctrl (external monitors) do the same for a single scroll.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Threading.Timer;

[assembly: AssemblyTitle(AppInfo.DisplayName)]
[assembly: AssemblyProduct(AppInfo.DisplayName)]
[assembly: AssemblyDescription(AppInfo.Description)]
[assembly: AssemblyCompany(AppInfo.Author)]
[assembly: AssemblyCopyright(AppInfo.Copyright)]
[assembly: AssemblyVersion(AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(AppInfo.Version + ".0")]
[assembly: AssemblyInformationalVersion(AppInfo.Version)]

static class AppInfo
{
    public const string Id = "BrightnessSync";
    public const string DisplayName = "Brightness Sync";
    public const string Description = "Keeps display brightness the same across all Windows power schemes";
    public const string Version = "1.2.0";
    public const string Author = "Pavel Yukhnevich";
    public const string Copyright = "Copyright \u00A9 2026 " + Author;
    public const string RepositoryUrl = "https://github.com/Yukhnevich/BrightnessSync";
    public const string AutostartArgument = "/autostart";
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, e) => Log.Write("Unhandled: " + e.ExceptionObject);
        Log.Guard("startup", () => Start(launchedAtLogon: IsAutostart(args)));
    }

    static bool IsAutostart(string[] args)
    {
        return Array.Exists(args, arg =>
            string.Equals(arg, AppInfo.AutostartArgument, StringComparison.OrdinalIgnoreCase));
    }

    static void Start(bool launchedAtLogon)
    {
        if (!launchedAtLogon)
            SettingsStore.Save(SettingsStore.Load().ForManualLaunch()); // a running instance picks this up

        if (SingleInstance.IsRunning())
            return;

        if (!Elevation.IsElevated)
        {
            StartElevated();
            return;
        }

        bool isFirstInstance;
        using (new Mutex(true, SingleInstance.MutexName, out isFirstInstance))
        {
            if (isFirstInstance)
                new TrayApp().Run();
        }
    }

    // Writing power schemes requires admin rights. The startup task elevates without a UAC prompt.
    static void StartElevated()
    {
        if (!StartupTask.Run())
            Elevation.RelaunchAsAdmin();
    }
}

// ============================================================================ application

sealed class TrayApp
{
    readonly BrightnessKeeper keeper = new BrightnessKeeper();
    readonly Displays displays = new Displays();
    readonly BrightnessScroller scroller;
    readonly TrayIconImages icons = new TrayIconImages();
    SynchronizationContext uiThread;
    TrayIcon trayIcon;
    TrayIconWheel trayIconWheel;
    AboutWindow aboutWindow;
    AppSettings settings = AppSettings.Default;
    bool scrollHintSeen = SettingsStore.ScrollHintSeen;
    int? builtInBrightness; // tracked whether sync is on or off

    public TrayApp()
    {
        scroller = new BrightnessScroller(displays);
    }

    public void Run()
    {
        NativeMethods.SetProcessDPIAware();
        Application.EnableVisualStyles();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        uiThread = SynchronizationContext.Current;

        keeper.StateChanged += () => OnUiThread(UpdateTrayIcon);
        scroller.Settled += ReadAndShowBrightness;
        displays.Changed += () => OnUiThread(UpdateTrayIcon);
        SubscribeToSystemEvents();
        CreateTrayIcon();
        ThreadPool.QueueUserWorkItem(_ => Log.Guard("read brightness", ReadInitialState));
        displays.Refresh();

        Apply(SettingsStore.Load());
        Log.Write(string.Format("Started (sync {0}, tray icon {1})",
            settings.SyncEnabled ? "on" : "off", settings.TrayIconVisible ? "visible" : "hidden"));

        SettingsStore.WatchForChanges(changed => OnUiThread(() => Apply(changed)));
        WindowsTheme.WatchForChanges(() => OnUiThread(OnThemeMaybeChanged));
        Application.Run();
    }

    void OnUiThread(Action action)
    {
        uiThread.Post(_ => action(), null);
    }

    void SubscribeToSystemEvents()
    {
        RunOnMtaThread(() =>
        {
            Log.Guard("brightness subscription",
                () => DisplayBrightness.SubscribeToChanges(OnBrightnessChanged));
            Log.Guard("power scheme subscription",
                () => PowerSchemes.SubscribeToActiveSchemeChanges(keeper.HandleActiveSchemeChanged));
        });
        SystemEvents.DisplaySettingsChanged += (s, e) => displays.RefreshSoon();
        SystemEvents.PowerModeChanged += (s, e) =>
        {
            if (e.Mode == PowerModes.Resume)
                displays.RefreshSoon(); // monitor handles do not survive sleep reliably
        };
    }

    // WMI event subscriptions are more reliable when created on an MTA thread than on the STA UI thread.
    static void RunOnMtaThread(Action action)
    {
        var thread = new Thread(() => action());
        thread.Start();
        thread.Join();
    }

    void CreateTrayIcon()
    {
        trayIcon = new TrayIcon();
        trayIcon.MenuRequested += anchor => Log.Guard("tray menu", () => ShowMenu(anchor));
        trayIconWheel = new TrayIconWheel(trayIcon);
        trayIconWheel.Scrolled += notches => Log.Guard("scroll", () => ScrollBrightness(notches));
        trayIconWheel.HoverStarted += displays.ReadMonitorLevels; // catch changes made with the monitor's buttons
    }

    void ReadInitialState()
    {
        BrightnessSteps steps = BrightnessSteps.From(DisplayBrightness.ReadSupportedLevels());
        Log.Write("Scroll step: " + steps);
        OnUiThread(() => scroller.BuiltInSteps = steps);
        ReadAndShowBrightness();
    }

    void ReadAndShowBrightness()
    {
        int? level = DisplayBrightness.Read();
        if (level.HasValue)
            OnUiThread(() => ShowBrightness(level.Value));
    }

    void OnBrightnessChanged(int level)
    {
        keeper.HandleBrightnessChanged(level);
        OnUiThread(() =>
        {
            if (!scroller.IsScrolling)
                ShowBrightness(level);
        });
    }

    // Not with the lid closed or in "Second screen only", unless no external monitor is left.
    int? ActiveBuiltInBrightness
    {
        get { return displays.BuiltInActive || displays.Monitors.Length == 0 ? builtInBrightness : null; }
    }

    void ScrollBrightness(int notches)
    {
        int? builtIn = ActiveBuiltInBrightness;
        if (!builtIn.HasValue && displays.Monitors.Length == 0)
            return;
        HideScrollHint();
        int? newBuiltIn = scroller.Scroll(builtIn, notches, ScopeWithModifierKeys(settings.ScrollScope),
            settings.SmoothBrightnessChanges);
        if (newBuiltIn.HasValue)
            builtInBrightness = newBuiltIn;
        UpdateTrayIcon();
    }

    // Read straight from the keyboard: the hook runs while another app has the focus.
    static ScrollScope ScopeWithModifierKeys(ScrollScope chosenInMenu)
    {
        if (KeyIsDown(NativeMethods.VK_SHIFT))
            return ScrollScope.BuiltInOnly;
        if (KeyIsDown(NativeMethods.VK_CONTROL))
            return ScrollScope.ExternalOnly;
        return chosenInMenu;
    }

    static bool KeyIsDown(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    void HideScrollHint()
    {
        if (scrollHintSeen)
            return;
        scrollHintSeen = true;
        Log.Guard("save scroll hint", SettingsStore.MarkScrollHintSeen);
    }

    void ShowBrightness(int level)
    {
        builtInBrightness = level;
        UpdateTrayIcon();
    }

    void OnThemeMaybeChanged()
    {
        if (icons.FollowTaskbarTheme())
            UpdateTrayIcon();
    }

    void UpdateTrayIcon()
    {
        bool syncActive = keeper.IsEnabled;
        ExternalMonitor[] monitors = displays.Monitors;
        int? builtIn = ActiveBuiltInBrightness;
        trayIcon.Update(icons.For(IconRays(builtIn, monitors), syncActive), Tooltip(syncActive, builtIn, monitors));
    }

    int[] IconRays(int? builtIn, ExternalMonitor[] monitors)
    {
        if (monitors.Length == 0)
            return SunRays.Uniform(builtIn);
        if (!builtIn.HasValue)
            return SunRays.Uniform(monitors[0].Level);
        ExternalMonitor monitor = displays.MonitorNearestToBuiltIn(monitors);
        return SunRays.Split(builtIn.Value, displays.BuiltInArea, monitor.Level, monitor.Area);
    }

    string Tooltip(bool syncActive, int? builtIn, ExternalMonitor[] monitors)
    {
        var lines = new List<string>();
        string sync = syncActive ? "on" : "off";
        if (monitors.Length + (builtIn.HasValue ? 1 : 0) > 1)
        {
            if (builtIn.HasValue)
                lines.Add("Built-in display: " + builtIn.Value + "%");
            foreach (ExternalMonitor monitor in monitors)
                lines.Add(monitor.Name + ": " + monitor.Level + "%");
            lines.Add("Power plan brightness sync: " + sync);
        }
        else
        {
            if (builtIn.HasValue || monitors.Length == 1)
                lines.Add("Brightness: " + (builtIn.HasValue ? builtIn.Value : monitors[0].Level) + "%");
            lines.Add("Power plan sync: " + sync);
        }
        string text = string.Join("\n", lines);
        if (!scrollHintSeen)
            text += "\n\nScroll to change the brightness level.";
        return text;
    }

    void ShowMenu(Point anchor)
    {
        var menu = new PopupMenu();
        menu.Add("Power plan brightness sync", settings.SyncEnabled,
            () => ChangeSettings(settings.WithSyncEnabled(!settings.SyncEnabled)));
        menu.AddSeparator();
        ExternalMonitor[] monitors = displays.Monitors;
        if (monitors.Length > 0 && ActiveBuiltInBrightness.HasValue)
            menu.AddSubmenu("Scroll changes", ScrollScopeMenu(monitors));
        menu.Add("Smooth brightness changes", settings.SmoothBrightnessChanges,
            () => ChangeSettings(settings.WithSmoothBrightnessChanges(!settings.SmoothBrightnessChanges)));
        menu.AddSeparator();
        menu.Add("Run at startup", StartupTask.Exists(), ToggleRunAtStartup);
        menu.Add("Hide tray icon", false, () => ChangeSettings(settings.WithTrayIconVisible(false)));
        menu.AddSeparator();
        menu.Add("About", false, ShowAbout);
        menu.Add("Exit", false, Exit);
        menu.Show(trayIcon.WindowHandle, anchor, WindowsTheme.TaskbarIsDark);
    }

    PopupMenu ScrollScopeMenu(ExternalMonitor[] monitors)
    {
        string external = monitors.Length == 1 ? monitors[0].Name : "External monitors";
        var menu = new PopupMenu();
        AddScopeChoice(menu, monitors.Length == 1 ? "Both displays" : "All displays", ScrollScope.AllDisplays);
        AddScopeChoice(menu, "Built-in display", ScrollScope.BuiltInOnly);
        AddScopeChoice(menu, external, ScrollScope.ExternalOnly);
        menu.AddSeparator();
        menu.AddNote("Shift + scroll: built-in display only");
        menu.AddNote("Ctrl + scroll: " + external + " only");
        return menu;
    }

    void AddScopeChoice(PopupMenu menu, string text, ScrollScope scope)
    {
        menu.AddRadio(text, settings.ScrollScope == scope, () => ChangeSettings(settings.WithScrollScope(scope)));
    }

    void ChangeSettings(AppSettings newSettings)
    {
        Apply(newSettings);
        Log.Guard("save settings", () => SettingsStore.Save(newSettings));
    }

    void Apply(AppSettings newSettings)
    {
        settings = newSettings;

        if (settings.SyncEnabled)
            ThreadPool.QueueUserWorkItem(_ => Log.Guard("enable sync", keeper.Enable));
        else
            keeper.Disable();

        UpdateTrayIcon();
        trayIcon.Visible = settings.TrayIconVisible;
    }

    static void ToggleRunAtStartup()
    {
        bool succeeded = StartupTask.Exists() ? StartupTask.Delete() : StartupTask.Create();
        if (!succeeded)
            MessageBox.Show("Could not update the startup task. See the log for details.",
                AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    void ShowAbout()
    {
        if (aboutWindow != null)
        {
            aboutWindow.Activate();
            return;
        }
        aboutWindow = new AboutWindow(WindowsTheme.AppsAreDark, OpenInBrowser);
        aboutWindow.FormClosed += (s, e) =>
        {
            aboutWindow.Dispose();
            aboutWindow = null;
            GC.Collect(); // the window's controls are not needed until About is opened again
        };
        aboutWindow.Show();
        aboutWindow.Activate();
    }

    // The app runs elevated; explorer.exe hands the URL to the user's shell,
    // so the browser starts with normal rights instead of as admin.
    static void OpenInBrowser(string url)
    {
        Log.Guard("open " + url, () => Process.Start("explorer.exe", "\"" + url + "\""));
    }

    void Exit()
    {
        Log.Write("Exit");
        trayIconWheel.Dispose();
        trayIcon.Dispose();
        Environment.Exit(0);
    }
}

// ============================================================================ tray icon

// Notification area icon on top of Shell_NotifyIcon. Unlike WinForms NotifyIcon it exposes
// its window, so the menu can be a native (theme-aware) popup menu.
sealed class TrayIcon : IDisposable
{
    const uint IconId = 1;
    const int CallbackMessage = NativeMethods.WM_APP + 1;

    readonly int taskbarCreatedMessage;
    readonly MessageWindow window;
    Icon icon;
    string text = "";
    bool visible;
    bool addedToShell;

    public event Action<Point> MenuRequested;
    public event Action MouseMovedOver;

    public TrayIcon()
    {
        taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        window = new MessageWindow(HandleMessage);

        // The app runs elevated; let the non-elevated shell (Explorer) reach our window.
        NativeMethods.ChangeWindowMessageFilterEx(window.Handle, (uint)CallbackMessage, NativeMethods.MSGFLT_ALLOW, IntPtr.Zero);
        NativeMethods.ChangeWindowMessageFilterEx(window.Handle, (uint)taskbarCreatedMessage, NativeMethods.MSGFLT_ALLOW, IntPtr.Zero);
    }

    public IntPtr WindowHandle
    {
        get { return window.Handle; }
    }

    public bool Visible
    {
        get { return visible; }
        set
        {
            if (visible == value)
                return;
            visible = value;
            if (visible)
                AddToShell();
            else
                RemoveFromShell();
        }
    }

    public void Update(Icon newIcon, string newText)
    {
        if (newIcon == icon && newText == text)
            return;
        icon = newIcon;
        text = newText;
        if (addedToShell)
            Notify(NativeMethods.NIM_MODIFY, NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
    }

    // Screen rectangle of the icon, in the same coordinates as Cursor.Position; null when not shown.
    public Rectangle? ScreenBounds()
    {
        if (!addedToShell)
            return null;
        var identifier = new NativeMethods.NotifyIconIdentifier
        {
            cbSize = Marshal.SizeOf(typeof(NativeMethods.NotifyIconIdentifier)),
            hWnd = window.Handle,
            uID = IconId
        };
        NativeMethods.Rect rect;
        if (NativeMethods.Shell_NotifyIconGetRect(ref identifier, out rect) != 0)
            return null;
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public void Dispose()
    {
        RemoveFromShell();
        window.DestroyHandle();
    }

    void AddToShell()
    {
        addedToShell = Notify(NativeMethods.NIM_ADD,
            NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        if (addedToShell)
            Notify(NativeMethods.NIM_SETVERSION, 0);
        else
            Log.Write("Cannot add the tray icon");
    }

    void RemoveFromShell()
    {
        if (addedToShell)
            Notify(NativeMethods.NIM_DELETE, 0);
        addedToShell = false;
    }

    bool Notify(uint command, uint flags)
    {
        var data = new NativeMethods.NotifyIconData
        {
            cbSize = Marshal.SizeOf(typeof(NativeMethods.NotifyIconData)),
            hWnd = window.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = icon != null ? icon.Handle : IntPtr.Zero,
            szTip = text,
            uVersion = NativeMethods.NOTIFYICON_VERSION_4
        };
        return NativeMethods.Shell_NotifyIcon(command, ref data);
    }

    bool HandleMessage(ref Message message)
    {
        if (message.Msg == CallbackMessage)
        {
            int trayEvent = (int)((long)message.LParam & 0xFFFF);
            if (trayEvent == NativeMethods.NIN_SELECT || trayEvent == NativeMethods.NIN_KEYSELECT
                || trayEvent == NativeMethods.WM_CONTEXTMENU)
                RaiseMenuRequested(PointFromWParam(message.WParam));
            else if (trayEvent == NativeMethods.WM_MOUSEMOVE && MouseMovedOver != null)
                MouseMovedOver();
            return true;
        }
        if (message.Msg == taskbarCreatedMessage)
        {
            // Explorer restarted and forgot every tray icon
            addedToShell = false;
            if (visible)
                AddToShell();
            return true;
        }
        return false;
    }

    void RaiseMenuRequested(Point anchor)
    {
        Action<Point> handler = MenuRequested;
        if (handler != null)
            handler(anchor);
    }

    static Point PointFromWParam(IntPtr wParam)
    {
        long value = (long)wParam;
        return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    delegate bool MessageHandler(ref Message message);

    sealed class MessageWindow : NativeWindow
    {
        readonly MessageHandler handler;

        public MessageWindow(MessageHandler handler)
        {
            this.handler = handler;
            CreateHandle(new CreateParams()); // top-level (not message-only): TaskbarCreated is broadcast
        }

        protected override void WndProc(ref Message message)
        {
            if (!handler(ref message))
                base.WndProc(ref message);
        }
    }
}

// Windows does not send mouse wheel messages to notification area icons. While the cursor hovers
// over the icon, a low-level mouse hook picks up wheel notches over it; when the cursor leaves,
// the hook is removed, so the rest of the time the app does not see the mouse at all.
sealed class TrayIconWheel : IDisposable
{
    const int WheelDelta = 120; // one notch; touchpads send smaller deltas that add up
    const int HoverCheckIntervalMs = 200;

    readonly TrayIcon icon;
    readonly System.Windows.Forms.Timer hoverCheck;
    readonly NativeMethods.LowLevelMouseProc hookCallback; // must stay referenced while installed
    IntPtr hook;
    Rectangle iconBounds;
    int pendingDelta;

    public event Action<int> Scrolled; // notches, positive = up
    public event Action HoverStarted;

    public TrayIconWheel(TrayIcon icon)
    {
        this.icon = icon;
        hookCallback = OnMouseEvent;
        hoverCheck = new System.Windows.Forms.Timer { Interval = HoverCheckIntervalMs };
        hoverCheck.Tick += (s, e) => StopIfCursorLeft();
        icon.MouseMovedOver += StartIfNeeded;
    }

    public void Dispose()
    {
        Stop();
        hoverCheck.Dispose();
    }

    void StartIfNeeded()
    {
        if (hook != IntPtr.Zero || !RefreshIconBounds())
            return;
        hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, hookCallback, NativeMethods.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            Log.Write("Cannot install the mouse wheel hook: error " + Marshal.GetLastWin32Error());
            return;
        }
        pendingDelta = 0;
        hoverCheck.Start();
        Action handler = HoverStarted;
        if (handler != null)
            handler();
    }

    void StopIfCursorLeft()
    {
        if (!RefreshIconBounds() || !iconBounds.Contains(Cursor.Position))
            Stop();
    }

    void Stop()
    {
        hoverCheck.Stop();
        if (hook == IntPtr.Zero)
            return;
        NativeMethods.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    bool RefreshIconBounds()
    {
        Rectangle? bounds = icon.ScreenBounds();
        if (bounds.HasValue)
            iconBounds = bounds.Value;
        return bounds.HasValue;
    }

    // Runs on the UI thread for every mouse event while installed, so it must stay cheap.
    IntPtr OnMouseEvent(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (int)message == NativeMethods.WM_MOUSEWHEEL && iconBounds.Contains(Cursor.Position))
        {
            var info = (NativeMethods.MouseHookData)Marshal.PtrToStructure(data, typeof(NativeMethods.MouseHookData));
            AddDelta((short)((info.mouseData >> 16) & 0xFFFF));
            return (IntPtr)1; // swallow: the taskbar has nothing to scroll anyway
        }
        return NativeMethods.CallNextHookEx(hook, code, message, data);
    }

    void AddDelta(int delta)
    {
        if (Math.Sign(delta) != Math.Sign(pendingDelta))
            pendingDelta = 0;
        pendingDelta += delta;
        int notches = pendingDelta / WheelDelta;
        if (notches == 0)
            return;
        pendingDelta -= notches * WheelDelta;
        Action<int> handler = Scrolled;
        if (handler != null)
            handler(notches);
    }
}

// Sun icons in the taskbar's colour, drawn once per step and sync state.
sealed class TrayIconImages
{
    static readonly Color ColorOnDarkTaskbar = Color.White;
    static readonly Color ColorOnLightTaskbar = Color.FromArgb(26, 26, 26);

    readonly Dictionary<string, Icon> cache = new Dictionary<string, Icon>();
    bool taskbarIsDark = WindowsTheme.TaskbarIsDark;

    const int MaxCachedIcons = 64; // two displays can combine their steps in many ways

    public Icon For(int[] raySteps, bool syncActive)
    {
        int size = SystemInformation.SmallIconSize.Width;
        string key = size + "/" + string.Join(",", raySteps) + "/" + syncActive;
        Icon icon;
        if (cache.TryGetValue(key, out icon))
            return icon;
        if (cache.Count >= MaxCachedIcons)
            Clear();
        icon = SunIcon.Create(size, raySteps, syncActive, taskbarIsDark ? ColorOnDarkTaskbar : ColorOnLightTaskbar);
        cache[key] = icon;
        return icon;
    }

    // Returns true when the taskbar theme changed and the icons have to be shown again.
    public bool FollowTaskbarTheme()
    {
        bool dark = WindowsTheme.TaskbarIsDark;
        if (dark == taskbarIsDark)
            return false;
        taskbarIsDark = dark;
        Clear();
        return true;
    }

    // The shell keeps its own copy of the displayed icon, so ours can be destroyed right away.
    void Clear()
    {
        foreach (Icon icon in cache.Values)
        {
            NativeMethods.DestroyIcon(icon.Handle);
            icon.Dispose();
        }
        cache.Clear();
    }
}

// Which display each ray of the sun shows. Two displays split the sun the way Settings > Display
// arranges them: side by side, each gets the rays on its side, the top ray shows the main display
// and the bottom ray the other one; stacked, they get the upper and lower rays, and the left ray
// shows the main display.
static class SunRays
{
    const int E = 0, SE = 1, S = 2, SW = 3, W = 4, NW = 5, N = 6, NE = 7;

    // Before the brightness is known the icon shows full rays.
    public static int[] Uniform(int? percent)
    {
        int step = percent.HasValue ? SunIcon.StepFor(percent.Value) : SunIcon.StepCount - 1;
        return new[] { step, step, step, step, step, step, step, step };
    }

    public static int[] Split(int builtInPercent, ScreenArea builtInArea, int monitorPercent, ScreenArea monitorArea)
    {
        int builtIn = SunIcon.StepFor(builtInPercent);
        int monitor = SunIcon.StepFor(monitorPercent);
        bool monitorIsMain = monitorArea != null && monitorArea.IsPrimary && (builtInArea == null || !builtInArea.IsPrimary);
        int main = monitorIsMain ? monitor : builtIn;
        int other = monitorIsMain ? builtIn : monitor;

        Point offset = MonitorOffsetFromBuiltIn(builtInArea, monitorArea);
        var rays = new int[8];
        if (Math.Abs(offset.Y) > Math.Abs(offset.X))
        {
            bool monitorAbove = offset.Y < 0;
            Assign(rays, monitorAbove ? monitor : builtIn, NW, N, NE);
            Assign(rays, monitorAbove ? builtIn : monitor, SE, S, SW);
            rays[W] = main;
            rays[E] = other;
        }
        else
        {
            bool monitorOnLeft = offset.X < 0;
            Assign(rays, monitorOnLeft ? monitor : builtIn, SW, W, NW);
            Assign(rays, monitorOnLeft ? builtIn : monitor, NE, E, SE);
            rays[N] = main;
            rays[S] = other;
        }
        return rays;
    }

    // Unknown or identical areas (a duplicated image): the monitor counts as being on the right.
    static Point MonitorOffsetFromBuiltIn(ScreenArea builtIn, ScreenArea monitor)
    {
        if (builtIn == null || monitor == null || builtIn.Bounds == monitor.Bounds)
            return new Point(1, 0);
        Point from = builtIn.Center;
        Point to = monitor.Center;
        return new Point(to.X - from.X, to.Y - from.Y);
    }

    static void Assign(int[] rays, int step, params int[] indexes)
    {
        foreach (int index in indexes)
            rays[index] = step;
    }
}

// Windows 11 style tray glyph: an outlined sun whose rays show the brightness in five steps.
// The ring is bright while sync is on and grey while it is off.
static class SunIcon
{
    public const int StepCount = 5;
    const int PercentPerStep = 20;

    // Tuned on the 24 px icon (150 % scaling): the ring lands on whole pixels, while the ray ends
    // sit between pixels, so round caps stay round on horizontal and vertical rays too.
    const float StrokeRatio = 2f / 24;
    const float RingRadius = 5f / 24;
    const float RayStart = 8.75f / 24; // centre of the inner round cap
    const float RayEnd = 10.75f / 24;  // centre of the outer round cap

    // Unlit rays and the ring of a paused sync: the grey of the inactive Wi-Fi and volume waves.
    const int InactiveAlpha = 110;

    public static int StepFor(int percent)
    {
        return Math.Max(0, Math.Min(StepCount - 1, percent / PercentPerStep));
    }

    // raySteps: one step per ray, clockwise from the right: E, SE, S, SW, W, NW, N, NE
    public static Icon Create(int size, int[] raySteps, bool syncActive, Color color)
    {
        using (var bitmap = new Bitmap(size, size))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; // pixel i covers [i, i+1]
                float stroke = Math.Max(1f, size * StrokeRatio);
                Color inactive = Color.FromArgb(InactiveAlpha, color);
                DrawRing(graphics, size, stroke, syncActive ? color : inactive);
                DrawRays(graphics, size, stroke, raySteps, color, inactive);
            }
            return ToIcon(bitmap);
        }
    }

    // Bitmap.GetHicon mishandles semi-transparent pixels, which makes anti-aliased edges and the
    // grey rays much fainter on screen than drawn. An icon built from a straight-alpha DIB keeps them.
    static Icon ToIcon(Bitmap bitmap)
    {
        byte[] dib = IconDib(bitmap);
        IntPtr handle = NativeMethods.CreateIconFromResourceEx(
            dib, (uint)dib.Length, true, NativeMethods.ICON_RESOURCE_VERSION, bitmap.Width, bitmap.Height, 0);
        if (handle == IntPtr.Zero)
            throw new Win32Exception();
        return Icon.FromHandle(handle);
    }

    // Icon resource layout: BITMAPINFOHEADER (height doubled), 32 bpp BGRA rows bottom-up,
    // then a 1 bpp AND mask that stays empty because transparency comes from the alpha channel.
    static byte[] IconDib(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int colorBytes = width * height * 4;
        int maskBytes = (width + 31) / 32 * 4 * height;
        var dib = new byte[40 + colorBytes + maskBytes];
        using (var writer = new BinaryWriter(new MemoryStream(dib)))
        {
            writer.Write(40);                       // biSize
            writer.Write(width);
            writer.Write(height * 2);               // colour image + mask
            writer.Write((short)1);                 // biPlanes
            writer.Write((short)32);                // biBitCount
            writer.Write(0);                        // BI_RGB
            writer.Write(colorBytes + maskBytes);   // biSizeImage
            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = 0; x < width; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    writer.Write(pixel.B); writer.Write(pixel.G); writer.Write(pixel.R); writer.Write(pixel.A);
                }
            }
        }
        return dib;
    }

    static void DrawRing(Graphics graphics, int size, float stroke, Color color)
    {
        float center = size / 2f;
        float radius = size * RingRadius;
        using (var pen = new Pen(color, stroke))
            graphics.DrawEllipse(pen, center - radius, center - radius, 2 * radius, 2 * radius);
    }

    static void DrawRays(Graphics graphics, int size, float stroke, int[] raySteps, Color lit, Color unlit)
    {
        float center = size / 2f;
        float start = size * RayStart;
        float end = size * RayEnd;

        using (var unlitPen = RoundPen(unlit, stroke))
        using (var litPen = RoundPen(lit, stroke))
        using (var litDot = new SolidBrush(lit))
        {
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4;
                float litLength = LitLength(end - start, stroke, raySteps[i]);
                PointF inner = PointOnCircle(center, start, angle);
                graphics.DrawLine(unlitPen, inner, PointOnCircle(center, end, angle));
                if (litLength < 0)
                    continue;
                if (litLength == 0)
                    graphics.FillEllipse(litDot, inner.X - stroke / 2, inner.Y - stroke / 2, stroke, stroke);
                else
                    graphics.DrawLine(litPen, inner, PointOnCircle(center, start + litLength, angle));
            }
        }
    }

    // Distance between the round caps of the lit part of a ray; -1 means "not lit".
    // Step 1 is a dot, step 4 the whole ray, steps in between grow evenly.
    static float LitLength(float rayLength, float stroke, int step)
    {
        if (step <= 0)
            return -1;
        float fullVisible = rayLength + stroke; // including both round caps
        float visible = stroke + (fullVisible - stroke) * (step - 1) / (StepCount - 2);
        return Math.Max(0, Math.Min(rayLength, visible - stroke));
    }

    static Pen RoundPen(Color color, float width)
    {
        return new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
    }

    static PointF PointOnCircle(float center, float radius, double angle)
    {
        return new PointF(center + (float)(Math.Cos(angle) * radius), center + (float)(Math.Sin(angle) * radius));
    }
}

sealed class PopupMenu
{
    sealed class Item
    {
        public string Text;
        public bool Checked;
        public bool IsRadio;
        public bool Enabled = true;
        public Action Action;
        public PopupMenu Submenu;
        public bool IsSeparator { get { return Text == null; } }
    }

    readonly List<Item> items = new List<Item>();

    public void Add(string text, bool isChecked, Action action)
    {
        items.Add(new Item { Text = text, Checked = isChecked, Action = action });
    }

    public void AddRadio(string text, bool selected, Action action)
    {
        items.Add(new Item { Text = text, Checked = selected, IsRadio = true, Action = action });
    }

    // A grayed-out line of text
    public void AddNote(string text)
    {
        items.Add(new Item { Text = text, Enabled = false });
    }

    public void AddSubmenu(string text, PopupMenu submenu)
    {
        items.Add(new Item { Text = text, Submenu = submenu });
    }

    public void AddSeparator()
    {
        items.Add(new Item());
    }

    public void Show(IntPtr owner, Point location, bool dark)
    {
        MenuTheme.Apply(owner, dark);

        var actions = new List<Action>();
        int command;
        IntPtr menu = Build(actions);
        try
        {
            // Without a foreground owner the menu would not close when clicking elsewhere.
            NativeMethods.SetForegroundWindow(owner);
            command = NativeMethods.TrackPopupMenuEx(menu, TrackFlags(), location.X, location.Y, owner, IntPtr.Zero);
            NativeMethods.PostMessage(owner, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu); // submenus included
        }

        if (command > 0)
            actions[command - 1]();
    }

    // A command id is the action's position in actions plus one: 0 means "nothing selected".
    IntPtr Build(List<Action> actions)
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        foreach (Item item in items)
        {
            if (item.IsSeparator)
            {
                NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
                continue;
            }
            if (item.Submenu != null)
            {
                IntPtr submenu = item.Submenu.Build(actions);
                NativeMethods.AppendMenu(menu, NativeMethods.MF_POPUP, new UIntPtr((ulong)submenu.ToInt64()), item.Text);
                continue;
            }

            uint command = 0;
            if (item.Action != null)
            {
                actions.Add(item.Action);
                command = (uint)actions.Count;
            }
            uint flags = NativeMethods.MF_STRING;
            if (!item.Enabled)
                flags |= NativeMethods.MF_GRAYED;
            if (item.Checked && !item.IsRadio)
                flags |= NativeMethods.MF_CHECKED;
            NativeMethods.AppendMenu(menu, flags, (UIntPtr)command, item.Text);
            if (item.Checked && item.IsRadio)
                NativeMethods.CheckMenuRadioItem(menu, command, command, command, NativeMethods.MF_BYCOMMAND);
        }
        return menu;
    }

    static uint TrackFlags()
    {
        uint horizontal = NativeMethods.GetSystemMetrics(NativeMethods.SM_MENUDROPALIGNMENT) != 0
            ? NativeMethods.TPM_RIGHTALIGN
            : NativeMethods.TPM_LEFTALIGN;
        return horizontal | NativeMethods.TPM_BOTTOMALIGN | NativeMethods.TPM_RIGHTBUTTON
            | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY;
    }
}

// Dark popup menus rely on undocumented uxtheme exports (Windows 10 1903+), the same ones Explorer
// and many desktop apps use. On older builds, or if they ever disappear, menus simply stay light.
static class MenuTheme
{
    const int FirstBuildWithAppModes = 18362;
    const int ForceDark = 2;
    const int ForceLight = 3;

    public static void Apply(IntPtr owner, bool dark)
    {
        if (WindowsBuild.Number < FirstBuildWithAppModes)
            return;
        try
        {
            NativeMethods.SetPreferredAppMode(dark ? ForceDark : ForceLight);
            NativeMethods.AllowDarkModeForWindow(owner, dark);
            NativeMethods.FlushMenuThemes();
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}

// ============================================================================ about window

// About window that follows the Windows app theme (a MessageBox is always light) and lets the
// log path be selected and copied. It has no title bar; it can be dragged by its background.
sealed class AboutWindow : Form
{
    const int ContentWidth = 460;
    static readonly Point ContentOffset = new Point(24, 20);

    static readonly Palette Dark = new Palette
    {
        Background = Color.FromArgb(32, 32, 32),
        Text = Color.White,
        SecondaryText = Color.FromArgb(200, 200, 200),
        Link = Color.FromArgb(96, 205, 255),
        ButtonBackground = Color.FromArgb(55, 55, 55),
        ButtonBorder = Color.FromArgb(75, 75, 75)
    };

    static readonly Palette Light = new Palette
    {
        Background = Color.FromArgb(249, 249, 249),
        Text = Color.FromArgb(26, 26, 26),
        SecondaryText = Color.FromArgb(96, 96, 96),
        Link = Color.FromArgb(0, 95, 184),
        ButtonBackground = Color.FromArgb(253, 253, 253),
        ButtonBorder = Color.FromArgb(208, 208, 208)
    };

    sealed class Palette
    {
        public Color Background, Text, SecondaryText, Link, ButtonBackground, ButtonBorder;
    }

    readonly bool dark;
    readonly Palette palette;

    public AboutWindow(bool dark, Action<string> openUrl)
    {
        this.dark = dark;
        palette = dark ? Dark : Light;

        // A fixed dialog without caption text and control box has no title bar, but keeps the
        // Windows 11 border, rounded corners and shadow.
        Text = "";
        ControlBox = false;
        ShowInTaskbar = false;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = SystemFonts.MessageBoxFont;
        BackColor = palette.Background;
        ForeColor = palette.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(0, 0, ContentOffset.X, 16); // left/top: see ContentOffset

        var content = new FlowLayoutPanel
        {
            Location = ContentOffset, // Form.Padding does not move controls that are not docked
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        content.Controls.Add(Header());
        content.Controls.Add(TextLabel("Tips:", palette.Text, FontStyle.Bold, 14));
        content.Controls.Add(TextLabel("\u2022 Scroll over the tray icon to change the brightness level.", palette.Text));
        content.Controls.Add(TextLabel("\u2022 With an external monitor, choose what scrolling changes in the tray menu, " +
            "or hold Shift (built-in display) or Ctrl (external monitors) while scrolling.", palette.Text));
        content.Controls.Add(TextLabel("\u2022 Run BrightnessSync.exe again to bring back a hidden tray icon.", palette.Text));
        content.Controls.Add(LogRow());
        content.Controls.Add(LabeledRow("Project page:", 6,
            Link(AppInfo.RepositoryUrl.Replace("https://", ""), () => openUrl(AppInfo.RepositoryUrl))));
        Button ok = OkButton();
        content.Controls.Add(ok);
        Controls.Add(content);
        ActiveControl = ok; // otherwise the log path gets focus and shows up fully selected
        DragWindowBy(this);
        DragWindowBy(content);
    }

    // Without a title bar, pressing on the background moves the window instead.
    void DragWindowBy(Control area)
    {
        area.MouseDown += (s, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TitleBar.UseDarkMode(Handle, dark);
    }

    Control Header()
    {
        var row = Row();
        var icon = new PictureBox
        {
            Image = Icon.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(40, 40),
            Margin = new Padding(0, 2, 14, 0)
        };
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        var title = TextLabel(AppInfo.DisplayName, palette.Text, FontStyle.Bold, 0);
        title.Font = new Font(Font.FontFamily, Font.Size * 1.4f, FontStyle.Bold);
        text.Controls.Add(title);
        text.Controls.Add(TextLabel("Version " + AppInfo.Version, palette.SecondaryText));
        text.Controls.Add(TextLabel(AppInfo.Copyright + "  \u00B7  MIT License", palette.SecondaryText));
        row.Controls.Add(icon);
        row.Controls.Add(text);
        row.Margin = new Padding(0, 0, 0, 6);
        return row;
    }

    // The path sits in a borderless read-only text box, so it can be selected and copied.
    Control LogRow()
    {
        var path = new TextBox
        {
            Text = Log.FilePath,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = palette.Background,
            ForeColor = palette.SecondaryText,
            Font = Font
        };
        path.Width = Math.Min(ContentWidth + 60, TextRenderer.MeasureText(path.Text, Font).Width + 8);
        return LabeledRow("Log:", 14, path, Link("Open", OpenLog));
    }

    // Every item is top-aligned (the flow panel default) and labels and links share the font,
    // so the bottoms of their words sit on one line.
    Control LabeledRow(string label, int topMargin, params Control[] items)
    {
        var row = Row();
        row.Margin = new Padding(0, topMargin, 0, 0);
        Label caption = TextLabel(label, palette.Text, FontStyle.Regular, 0);
        caption.Margin = new Padding(0, 0, 4, 0);
        row.Controls.Add(caption);
        foreach (Control item in items)
        {
            item.Margin = new Padding(0, 0, 8, 0);
            row.Controls.Add(item);
        }
        return row;
    }

    Button OkButton()
    {
        var button = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(88, 30),
            Anchor = AnchorStyles.None, // centred under the widest row
            Margin = new Padding(0, 20, 0, 0),
            ForeColor = palette.Text,
            BackColor = palette.ButtonBackground,
            FlatStyle = FlatStyle.Flat
        };
        button.FlatAppearance.BorderColor = palette.ButtonBorder;
        button.Click += (s, e) => Close();
        AcceptButton = button;
        CancelButton = button;
        return button;
    }

    static FlowLayoutPanel Row()
    {
        return new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty
        };
    }

    Label TextLabel(string text, Color color, FontStyle style = FontStyle.Regular, int topMargin = 2)
    {
        return new Label
        {
            Text = text,
            ForeColor = color,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            Font = style == FontStyle.Regular ? Font : new Font(Font, style),
            Margin = new Padding(0, topMargin, 0, 0)
        };
    }

    LinkLabel Link(string text, Action onClick)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            LinkColor = palette.Link,
            ActiveLinkColor = palette.Link,
            VisitedLinkColor = palette.Link,
            LinkBehavior = LinkBehavior.HoverUnderline
        };
        link.LinkClicked += (s, e) => onClick();
        return link;
    }

    static void OpenLog()
    {
        Log.Guard("open log", () =>
        {
            if (File.Exists(Log.FilePath))
                Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
        });
    }
}

// Dark title bar for our own windows (Windows 10 1809+; the attribute id changed in build 18985).
static class TitleBar
{
    const int ImmersiveDarkModeAttribute = 20;
    const int ImmersiveDarkModeAttributeBefore18985 = 19;

    public static void UseDarkMode(IntPtr window, bool dark)
    {
        if (WindowsBuild.Number < 17763)
            return;
        int attribute = WindowsBuild.Number >= 18985 ? ImmersiveDarkModeAttribute : ImmersiveDarkModeAttributeBefore18985;
        int enabled = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(window, attribute, ref enabled, sizeof(int));
    }
}

// ============================================================================ brightness

sealed class BrightnessKeeper
{
    static readonly TimeSpan SliderSettleDelay = TimeSpan.FromSeconds(1);
    static readonly TimeSpan SchemeSwitchEchoWindow = TimeSpan.FromMilliseconds(1500);
    static readonly TimeSpan RestoreCheckDelay = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan MinAgeOfUserChange = TimeSpan.FromMilliseconds(300);
    static readonly TimeSpan Never = Timeout.InfiniteTimeSpan;

    readonly object gate = new object();
    readonly Timer settleTimer;
    readonly Timer restoreTimer;
    bool enabled;
    int keptLevel = -1;
    int? pendingLevel;
    DateTime pendingSince;
    DateTime ignoreChangesUntil;

    public event Action StateChanged;

    public BrightnessKeeper()
    {
        settleTimer = new Timer(_ => Log.Guard("save brightness", SavePendingLevel));
        restoreTimer = new Timer(_ => Log.Guard("restore brightness", RestoreKeptLevelIfChanged));
    }

    public bool IsEnabled
    {
        get { lock (gate) return enabled; }
    }

    public int KeptLevel
    {
        get { lock (gate) return keptLevel; }
    }

    public void Enable()
    {
        lock (gate)
        {
            if (enabled)
                return;
            int? current = DisplayBrightness.Read();
            if (current == null)
            {
                Log.Write("Cannot read display brightness via WMI");
                return;
            }
            keptLevel = current.Value;
            PowerSchemes.SetBrightnessInAll(keptLevel);
            enabled = true;
        }
        Log.Write("Sync enabled at " + KeptLevel + "%");
        RaiseStateChanged();
    }

    public void Disable()
    {
        lock (gate)
        {
            if (!enabled)
                return;
            enabled = false;
            pendingLevel = null;
            settleTimer.Change(Never, Never);
            restoreTimer.Change(Never, Never);
        }
        Log.Write("Sync disabled");
        RaiseStateChanged();
    }

    public void HandleBrightnessChanged(int level)
    {
        lock (gate)
        {
            if (!enabled || DateTime.UtcNow < ignoreChangesUntil)
                return;
            pendingLevel = level;
            pendingSince = DateTime.UtcNow;
            settleTimer.Change(SliderSettleDelay, Never);
        }
    }

    // After a scheme switch Windows applies the new scheme's stored brightness. A change arriving
    // right after the switch is that echo, not the user; a change pending from before it is kept.
    public void HandleActiveSchemeChanged()
    {
        lock (gate)
        {
            if (!enabled)
                return;
            settleTimer.Change(Never, Never);
            if (pendingLevel.HasValue && DateTime.UtcNow - pendingSince > MinAgeOfUserChange)
                keptLevel = pendingLevel.Value;
            pendingLevel = null;

            ignoreChangesUntil = DateTime.UtcNow + SchemeSwitchEchoWindow;
            PowerSchemes.SetBrightnessInAll(keptLevel); // also covers newly created schemes
            restoreTimer.Change(RestoreCheckDelay, Never);
        }
    }

    void SavePendingLevel()
    {
        lock (gate)
        {
            int? level = pendingLevel;
            pendingLevel = null;
            if (!enabled || level == null || level == keptLevel)
                return;
            keptLevel = level.Value;
            PowerSchemes.SetBrightnessInAll(keptLevel);
        }
        Log.Write("Brightness " + KeptLevel + "% saved to all power schemes");
        RaiseStateChanged();
    }

    void RestoreKeptLevelIfChanged()
    {
        int? current;
        int kept;
        lock (gate)
        {
            if (!enabled)
                return;
            current = DisplayBrightness.Read();
            kept = keptLevel;
            if (current == null || current == kept)
                return;
            DisplayBrightness.Set(kept);
        }
        Log.Write("Brightness restored after scheme switch: " + current + "% -> " + kept + "%");
    }

    void RaiseStateChanged()
    {
        Action handler = StateChanged;
        if (handler != null)
            handler();
    }
}

enum ScrollScope { AllDisplays, BuiltInOnly, ExternalOnly }

// Turns scroll notches into brightness changes. The built-in display alone moves by its own
// levels; with external monitors all displays move by a small step and keep their differences
// (see LinkedLevels). While scrolling, brightness events echo levels already passed; IsScrolling
// tells the app to ignore them so the icon does not flicker.
sealed class BrightnessScroller
{
    static readonly TimeSpan EchoWindow = TimeSpan.FromMilliseconds(700);
    // Monitors take any level, and a bright monitor (1000+ nits) jumps visibly at 5 %.
    const int ExternalStep = 2;
    static readonly object BuiltInKey = new object();

    readonly BrightnessAnimator animator = new BrightnessAnimator();
    readonly Displays displays;
    readonly LinkedLevels linkedLevels;
    readonly Timer settledTimer;
    DateTime scrollingUntil;

    public event Action Settled; // raised on a pool thread once scrolling stops

    public BrightnessScroller(Displays displays)
    {
        this.displays = displays;
        BuiltInSteps = BrightnessSteps.Fine;
        linkedLevels = new LinkedLevels(SnapToSupportedLevel);
        settledTimer = new Timer(_ => Log.Guard("after scroll", OnSettled));
    }

    public BrightnessSteps BuiltInSteps { get; set; }

    public bool IsScrolling
    {
        get { return DateTime.UtcNow < scrollingUntil; }
    }

    // Takes the built-in display's level (null without one) and returns the level it is heading to.
    public int? Scroll(int? builtIn, int notches, ScrollScope scope, bool smooth)
    {
        scrollingUntil = DateTime.UtcNow + EchoWindow;
        settledTimer.Change(EchoWindow, Timeout.InfiniteTimeSpan);

        ExternalMonitor[] monitors = displays.Monitors;
        if (monitors.Length == 0 && builtIn.HasValue)
            return MoveBuiltIn(builtIn.Value, BuiltInSteps.Move(builtIn.Value, notches), smooth);

        var levels = new Dictionary<object, int>();
        if (builtIn.HasValue)
            levels[BuiltInKey] = builtIn.Value;
        foreach (ExternalMonitor monitor in monitors)
            levels[monitor] = monitor.Level;
        linkedLevels.Follow(levels);

        Func<object, bool> inScope = DisplaysIn(scope, levels.Keys);
        int step = BuiltInSteps.TypicalStep;
        foreach (object display in levels.Keys)
            if (display != BuiltInKey && inScope(display))
                step = ExternalStep;

        int? newBuiltIn = builtIn;
        foreach (KeyValuePair<object, int> change in linkedLevels.Move(notches * step, inScope))
        {
            if (change.Key == BuiltInKey)
                newBuiltIn = MoveBuiltIn(builtIn.Value, change.Value, smooth);
            else
                displays.SetMonitorLevel((ExternalMonitor)change.Key, change.Value, smooth);
        }
        return newBuiltIn;
    }

    // A scope with no display connected falls back to all displays.
    static Func<object, bool> DisplaysIn(ScrollScope scope, IEnumerable<object> connected)
    {
        Func<object, bool> inScope = display =>
            scope == ScrollScope.AllDisplays || (display == BuiltInKey) == (scope == ScrollScope.BuiltInOnly);
        foreach (object display in connected)
            if (inScope(display))
                return inScope;
        return display => true;
    }

    int MoveBuiltIn(int from, int to, bool smooth)
    {
        if (to != from)
            animator.AnimateTo(from, to, BuiltInSteps, smooth);
        return to;
    }

    int SnapToSupportedLevel(object display, int level)
    {
        return display == BuiltInKey ? BuiltInSteps.Nearest(level) : level;
    }

    // Scrolling makes dozens of WMI calls; release their COM objects right away instead of
    // letting the process carry them until the garbage collector happens to run.
    void OnSettled()
    {
        Action handler = Settled;
        if (handler != null)
            handler();
        DisplayBrightness.ReleaseCachedObjects();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

// Levels of displays that scroll together. Each display keeps an offset from a shared level, and
// the shared level may run past 0 and 100: a display that hit a limit waits there while the others
// move on, and on the way back the others catch up first, so the displays get their old
// difference back (30 % and 60 % stay 30 % and 60 % after a trip to 0 % or 100 %).
sealed class LinkedLevels
{
    readonly Func<object, int, int> snapToSupportedLevel;
    readonly Dictionary<object, int> offsets = new Dictionary<object, int>();
    readonly Dictionary<object, int> lastSet = new Dictionary<object, int>();
    int shared;

    public LinkedLevels(Func<object, int, int> snapToSupportedLevel)
    {
        this.snapToSupportedLevel = snapToSupportedLevel;
    }

    // Takes in the displays' actual levels. A display that is new, or was changed by something
    // else (Fn keys, the Windows slider, the monitor's buttons), gets the offset of where it is now.
    public void Follow(IDictionary<object, int> actualLevels)
    {
        foreach (object display in new List<object>(offsets.Keys))
        {
            if (actualLevels.ContainsKey(display))
                continue;
            offsets.Remove(display);
            lastSet.Remove(display);
        }
        foreach (KeyValuePair<object, int> display in actualLevels)
        {
            if (offsets.Count == 0)
                shared = display.Value;
            int level;
            if (lastSet.TryGetValue(display.Key, out level) && level == display.Value)
                continue;
            offsets[display.Key] = display.Value - shared;
            lastSet[display.Key] = display.Value;
        }
    }

    // Moving only some displays changes their offsets, so the new difference is kept from then on.
    // Returns the displays whose level changed, with their new levels.
    public Dictionary<object, int> Move(int delta, Func<object, bool> inScope)
    {
        var moving = new List<object>();
        foreach (object display in offsets.Keys)
            if (inScope(display))
                moving.Add(display);

        if (moving.Count == offsets.Count)
            shared = ClampToUsefulRange(ClampToUsefulRange(shared) + delta);
        else
            foreach (object display in moving)
                offsets[display] = Clamp(lastSet[display] + delta) - shared;

        var changes = new Dictionary<object, int>();
        foreach (object display in moving)
        {
            int level = snapToSupportedLevel(display, Clamp(shared + offsets[display]));
            if (level == lastSet[display])
                continue;
            lastSet[display] = level;
            changes[display] = level;
        }
        return changes;
    }

    // Beyond the point where every display is at 0 (or 100) further notches would only have to be
    // scrolled back before anything happens.
    int ClampToUsefulRange(int level)
    {
        int lowestOffset = int.MaxValue;
        int highestOffset = int.MinValue;
        foreach (int offset in offsets.Values)
        {
            lowestOffset = Math.Min(lowestOffset, offset);
            highestOffset = Math.Max(highestOffset, offset);
        }
        return Math.Max(-highestOffset, Math.Min(100 - lowestOffset, level));
    }

    static int Clamp(int level)
    {
        return Math.Max(0, Math.Min(100, level));
    }
}

// What one scroll notch means on this laptop: the next level the panel supports when it has
// only a few (typically 11: 0, 10 ... 100), or 5 % when it supports every percent.
sealed class BrightnessSteps
{
    const int MaxDiscreteLevels = 21;
    const int FineStep = 5;

    public static readonly BrightnessSteps Fine = new BrightnessSteps(null);

    readonly int[] levels; // ascending; null means fine-grained

    BrightnessSteps(int[] levels)
    {
        this.levels = levels;
    }

    public static BrightnessSteps From(int[] supportedLevels)
    {
        if (supportedLevels == null)
            return Fine;
        var distinct = new SortedSet<int>(supportedLevels);
        if (distinct.Count < 2 || distinct.Count > MaxDiscreteLevels)
            return Fine;
        return new BrightnessSteps(new List<int>(distinct).ToArray());
    }

    public bool IsDiscrete
    {
        get { return levels != null; }
    }

    public int TypicalStep
    {
        get
        {
            if (levels == null)
                return FineStep;
            double average = (levels[levels.Length - 1] - levels[0]) / (double)(levels.Length - 1);
            return Math.Max(1, (int)Math.Round(average));
        }
    }

    public int Nearest(int level)
    {
        if (levels == null)
            return level;
        int nearest = levels[0];
        foreach (int candidate in levels)
            if (Math.Abs(candidate - level) < Math.Abs(nearest - level))
                nearest = candidate;
        return nearest;
    }

    public int Move(int level, int notches)
    {
        for (int i = 0; i < Math.Abs(notches); i++)
            level = notches > 0 ? Up(level) : Down(level);
        return level;
    }

    int Up(int level)
    {
        if (levels == null)
            return Math.Min(100, level - level % FineStep + FineStep);
        foreach (int candidate in levels)
            if (candidate > level)
                return candidate;
        return level;
    }

    int Down(int level)
    {
        if (levels == null)
            return Math.Max(0, level % FineStep == 0 ? level - FineStep : level - level % FineStep);
        for (int i = levels.Length - 1; i >= 0; i--)
            if (levels[i] < level)
                return levels[i];
        return level;
    }

    public override string ToString()
    {
        return levels == null
            ? FineStep + "% per notch"
            : "next of " + levels.Length + " supported levels (" + string.Join(", ", levels) + ")";
    }
}

// Moves the display brightness towards the latest requested level on a background thread, so fast
// scrolling passes through the levels in between (or jumps straight there when smooth changes are
// off). A new request only retargets the running animation.
//  - Panels with a few levels: one supported level per frame at an even pace (200 % per second).
//    Values in between would only be rounded by the panel and make the last level arrive as a jerk.
//  - Panels with fine levels: each frame covers a third of the remaining distance.
sealed class BrightnessAnimator
{
    const int DiscreteMillisecondsPerPercent = 5;
    static readonly TimeSpan FineFrameDelay = TimeSpan.FromMilliseconds(15);
    const int EasingDivisor = 3;
    // Each WMI call leaves COM wrappers behind until a collection; collect now and then during
    // long scrolling sessions so memory does not creep up.
    const int FramesBetweenCollections = 64;

    readonly object gate = new object();
    BrightnessSteps steps = BrightnessSteps.Fine;
    bool smooth = true;
    int applied;
    int target;
    bool running;
    int framesSinceCollection;

    public void AnimateTo(int from, int to, BrightnessSteps levelSteps, bool smooth)
    {
        lock (gate)
        {
            target = to;
            steps = levelSteps;
            this.smooth = smooth;
            if (running)
                return;
            applied = from;
            running = true;
        }
        ThreadPool.QueueUserWorkItem(_ => Log.Guard("brightness animation", RunUntilTargetReached));
    }

    void RunUntilTargetReached()
    {
        try
        {
            while (true)
            {
                int next;
                TimeSpan delay;
                lock (gate)
                {
                    if (applied == target)
                        return;
                    next = smooth ? NextFrame(applied, target, steps) : target;
                    delay = !smooth ? TimeSpan.Zero
                        : steps.IsDiscrete
                        ? TimeSpan.FromMilliseconds(Math.Abs(next - applied) * DiscreteMillisecondsPerPercent)
                        : FineFrameDelay;
                    applied = next;
                }
                DisplayBrightness.Set(next);
                CollectGarbageNowAndThen();
                Thread.Sleep(delay);
            }
        }
        finally
        {
            lock (gate)
                running = false;
        }
    }

    static int NextFrame(int from, int to, BrightnessSteps steps)
    {
        int direction = Math.Sign(to - from);
        if (steps.IsDiscrete)
        {
            int next = steps.Move(from, direction);
            bool overshoots = direction > 0 ? next > to : next < to;
            return overshoots || next == from ? to : next;
        }
        return from + direction * Math.Max(1, Math.Abs(to - from) / EasingDivisor);
    }

    void CollectGarbageNowAndThen()
    {
        if (++framesSinceCollection < FramesBetweenCollections)
            return;
        framesSinceCollection = 0;
        GC.Collect();
    }
}

// ============================================================================ system access

static class DisplayBrightness
{
    const string WmiNamespace = @"root\WMI";
    static readonly object methodsLock = new object();
    static ManagementEventWatcher changeWatcher; // kept referenced so it is never collected
    // Reused while scrolling instead of a new query and new parameter objects for every step
    static ManagementObject brightnessMethods;
    static ManagementBaseObject setBrightnessParameters;

    public static int? Read()
    {
        object value = ReadProperty("CurrentBrightness");
        return value == null ? (int?)null : Convert.ToInt32(value);
    }

    public static int[] ReadSupportedLevels()
    {
        var raw = ReadProperty("Level") as byte[];
        if (raw == null)
            return null;
        var levels = new int[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            levels[i] = raw[i];
        return levels;
    }

    static object ReadProperty(string name)
    {
        using (var searcher = new ManagementObjectSearcher(WmiNamespace, "SELECT " + name + " FROM WmiMonitorBrightness"))
        using (ManagementObjectCollection monitors = searcher.Get())
        {
            foreach (ManagementObject monitor in monitors)
            {
                using (monitor)
                    return monitor[name];
            }
        }
        return null;
    }

    public static void Set(int percent)
    {
        lock (methodsLock)
        {
            if (brightnessMethods == null)
            {
                brightnessMethods = FindBrightnessMethods();
                if (brightnessMethods == null)
                    return;
                setBrightnessParameters = brightnessMethods.GetMethodParameters("WmiSetBrightness");
            }
            try
            {
                setBrightnessParameters["Timeout"] = (uint)1;
                setBrightnessParameters["Brightness"] = (byte)percent;
                using (brightnessMethods.InvokeMethod("WmiSetBrightness", setBrightnessParameters, null))
                {
                }
            }
            catch (ManagementException)
            {
                ReleaseCachedObjectsLocked(); // e.g. the display was re-enumerated; look it up again next time
                throw;
            }
        }
    }

    public static void ReleaseCachedObjects()
    {
        lock (methodsLock)
            ReleaseCachedObjectsLocked();
    }

    static void ReleaseCachedObjectsLocked()
    {
        if (setBrightnessParameters != null)
            setBrightnessParameters.Dispose();
        if (brightnessMethods != null)
            brightnessMethods.Dispose();
        setBrightnessParameters = null;
        brightnessMethods = null;
    }

    static ManagementObject FindBrightnessMethods()
    {
        using (var searcher = new ManagementObjectSearcher(WmiNamespace, "SELECT * FROM WmiMonitorBrightnessMethods"))
        using (ManagementObjectCollection monitors = searcher.Get())
        {
            foreach (ManagementObject monitor in monitors)
                return monitor;
        }
        return null;
    }

    public static void SubscribeToChanges(Action<int> onChanged)
    {
        changeWatcher = new ManagementEventWatcher(
            new ManagementScope(WmiNamespace), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
        changeWatcher.EventArrived += (sender, e) =>
            Log.Guard("brightness change", () =>
            {
                using (ManagementBaseObject change = e.NewEvent)
                    onChanged(Convert.ToInt32(change["Brightness"]));
            });
        changeWatcher.Start();
    }
}

// The displays in use: external monitors controlled over DDC/CI, and whether and where the
// built-in display shows. Monitor calls run on one background thread: they are slow (tens of
// milliseconds each) and monitors do not handle overlapping commands.
sealed class Displays
{
    // DDC/CI asks for a pause of about 50 ms after each command.
    static readonly TimeSpan MinWriteInterval = TimeSpan.FromMilliseconds(60);
    // A newly connected or woken monitor needs a moment before it answers DDC/CI.
    static readonly TimeSpan DisplayChangeSettleDelay = TimeSpan.FromSeconds(2);
    static readonly TimeSpan MinReadInterval = TimeSpan.FromSeconds(2);
    const int EasingDivisor = 3;

    readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
    readonly Timer refreshTimer;
    readonly object gate = new object();
    readonly Dictionary<ExternalMonitor, int> targetLevels = new Dictionary<ExternalMonitor, int>();
    bool smoothWrites;
    bool writeScheduled;
    volatile ExternalMonitor[] monitors = new ExternalMonitor[0];
    volatile bool builtInActive = true;
    volatile ScreenArea builtInArea;
    string loggedDisplays; // worker thread only
    DateTime lastRead;     // worker thread only

    public event Action Changed; // displays or their levels changed; raised on the worker thread

    public Displays()
    {
        var worker = new Thread(() =>
        {
            foreach (Action action in work.GetConsumingEnumerable())
                action();
        });
        worker.IsBackground = true;
        worker.Start();
        refreshTimer = new Timer(_ => Refresh());
    }

    // Only monitors that report their brightness
    public ExternalMonitor[] Monitors
    {
        get { return monitors; }
    }

    // False with the lid closed or in "Second screen only"
    public bool BuiltInActive
    {
        get { return builtInActive; }
    }

    // Null when unknown
    public ScreenArea BuiltInArea
    {
        get { return builtInArea; }
    }

    public ExternalMonitor MonitorNearestToBuiltIn(ExternalMonitor[] candidates)
    {
        ScreenArea builtIn = builtInArea;
        ExternalMonitor nearest = candidates[0];
        if (builtIn == null)
            return nearest;
        foreach (ExternalMonitor candidate in candidates)
        {
            if (candidate.Area == null)
                continue;
            if (nearest.Area == null || builtIn.DistanceTo(candidate.Area) < builtIn.DistanceTo(nearest.Area))
                nearest = candidate;
        }
        return nearest;
    }

    public void Refresh()
    {
        Post("find displays", FindDisplays);
    }

    public void RefreshSoon()
    {
        refreshTimer.Change(DisplayChangeSettleDelay, Timeout.InfiniteTimeSpan);
    }

    public void ReadMonitorLevels()
    {
        Post("read external brightness", ReadMonitorLevelsNow);
    }

    // Level shows the new level right away. The monitor gets there within MinWriteInterval, or,
    // when smooth, glides there in steps that cover a third of the remaining distance.
    public void SetMonitorLevel(ExternalMonitor monitor, int percent, bool smooth)
    {
        monitor.MarkRequested(percent);
        lock (gate)
        {
            targetLevels[monitor] = percent;
            smoothWrites = smooth;
            if (writeScheduled)
                return;
            writeScheduled = true;
        }
        Post("set external brightness", WriteUntilTargetsReached);
    }

    void Post(string operation, Action action)
    {
        work.Add(() => Log.Guard(operation, action));
    }

    void FindDisplays()
    {
        Dictionary<string, DisplayDevice> devices = DisplayDevice.Active();
        ExternalMonitor[] previous = monitors;
        ScreenArea builtIn;
        monitors = ExternalMonitor.FindAll(devices, out builtIn).ToArray();
        builtInArea = builtIn;
        builtInActive = devices.Count == 0 || ShowsOnBuiltIn(devices); // unknown: assume it is in use
        lock (gate)
            targetLevels.Clear();
        foreach (ExternalMonitor monitor in previous)
            monitor.Dispose();
        lastRead = DateTime.UtcNow;

        var names = new List<string> { builtInActive ? "built-in" : "built-in off" };
        foreach (ExternalMonitor monitor in monitors)
            names.Add(monitor.EdidVendor == null ? monitor.Name : monitor.Name + " [" + monitor.EdidVendor + "]");
        string description = string.Join(", ", names);
        if (monitors.Length == 0)
            description += " (no external monitor with DDC/CI brightness)";
        if (description != loggedDisplays)
            Log.Write("Displays: " + description);
        loggedDisplays = description;
        RaiseChanged();
    }

    static bool ShowsOnBuiltIn(Dictionary<string, DisplayDevice> devices)
    {
        foreach (DisplayDevice device in devices.Values)
            if (device.ShowsOnBuiltIn)
                return true;
        return false;
    }

    void ReadMonitorLevelsNow()
    {
        if (DateTime.UtcNow - lastRead < MinReadInterval)
            return;
        lastRead = DateTime.UtcNow;
        bool changed = false;
        foreach (ExternalMonitor monitor in monitors)
        {
            int before = monitor.Level;
            monitor.Read();
            changed |= monitor.Level != before;
        }
        if (changed)
            RaiseChanged();
    }

    void WriteUntilTargetsReached()
    {
        try
        {
            while (WriteNextStep())
                Thread.Sleep(MinWriteInterval);
        }
        catch
        {
            lock (gate)
                writeScheduled = false;
            throw;
        }
    }

    bool WriteNextStep()
    {
        List<KeyValuePair<ExternalMonitor, int>> targets;
        bool smooth;
        lock (gate)
        {
            if (targetLevels.Count == 0)
            {
                writeScheduled = false;
                return false;
            }
            targets = new List<KeyValuePair<ExternalMonitor, int>>(targetLevels);
            smooth = smoothWrites;
        }

        ExternalMonitor[] current = monitors;
        foreach (KeyValuePair<ExternalMonitor, int> target in targets)
        {
            ExternalMonitor monitor = target.Key;
            bool reached = true;
            if (Array.IndexOf(current, monitor) >= 0) // a monitor replaced by a refresh is skipped
            {
                int next = smooth ? NextStep(monitor.Applied, target.Value) : target.Value;
                if (monitor.Write(next))
                {
                    reached = next == target.Value;
                }
                else
                {
                    Log.Write("Cannot set the brightness of " + monitor.Name + ": error " + Marshal.GetLastWin32Error());
                    RefreshSoon();
                }
            }
            if (!reached)
                continue;
            lock (gate)
            {
                int latest;
                if (targetLevels.TryGetValue(monitor, out latest) && latest == target.Value)
                    targetLevels.Remove(monitor);
            }
        }
        return true;
    }

    static int NextStep(int from, int to)
    {
        int distance = to - from;
        return from + Math.Sign(distance) * Math.Max(1, Math.Abs(distance) / EasingDivisor);
    }

    void RaiseChanged()
    {
        Action handler = Changed;
        if (handler != null)
            handler();
    }
}

// A monitor whose brightness is set over DDC/CI (VCP code 0x10, "luminance"). Used only on the
// Displays worker thread, except for the properties and MarkRequested.
sealed class ExternalMonitor : IDisposable
{
    const byte LuminanceCode = 0x10;
    const int Attempts = 3; // DDC/CI drops a command now and then, especially through docks and hubs
    static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    readonly uint maximum;
    IntPtr handle;
    uint lastWrittenValue = uint.MaxValue;
    int applied;      // what the monitor is at, as far as we know
    int requestCount;
    volatile int level; // where it is heading: shown in the tooltip

    ExternalMonitor(IntPtr handle, string name, string edidVendor, ScreenArea area, uint current, uint maximum)
    {
        this.handle = handle;
        this.maximum = maximum;
        Name = name;
        EdidVendor = edidVendor;
        Area = area;
        level = applied = ToPercent(current);
    }

    public string Name { get; private set; }

    public ScreenArea Area { get; private set; }

    // Three-letter vendor code from the monitor's EDID (DEL, GSM ...), null when unknown
    public string EdidVendor { get; private set; }

    public int Level
    {
        get { return level; }
    }

    public int Applied
    {
        get { return applied; }
    }

    // The built-in panel answers DDC/CI rarely, and is skipped anyway: WMI controls it. With a
    // duplicated image one display device has several physical monitors; the external ones get
    // the external names in order.
    public static List<ExternalMonitor> FindAll(Dictionary<string, DisplayDevice> devices, out ScreenArea builtInArea)
    {
        var found = new List<ExternalMonitor>();
        builtInArea = null;
        foreach (IntPtr displayMonitor in DisplayMonitors())
        {
            var info = new NativeMethods.MonitorInfoEx { cbSize = Marshal.SizeOf(typeof(NativeMethods.MonitorInfoEx)) };
            if (!NativeMethods.GetMonitorInfo(displayMonitor, ref info))
                continue;
            var area = new ScreenArea(info);
            DisplayDevice device;
            devices.TryGetValue(info.szDevice, out device);
            if (device != null && device.ShowsOnBuiltIn)
                builtInArea = area;
            if (device != null && device.ExternalScreens.Count == 0)
                continue;
            int nameIndex = 0;
            foreach (NativeMethods.PhysicalMonitor physical in PhysicalMonitors(displayMonitor))
            {
                uint current, maximum;
                if (!TryReadLuminance(physical.handle, out current, out maximum) || maximum == 0)
                {
                    NativeMethods.DestroyPhysicalMonitor(physical.handle);
                    continue;
                }
                DisplayDevice.Screen screen = device != null && nameIndex < device.ExternalScreens.Count
                    ? device.ExternalScreens[nameIndex++]
                    : new DisplayDevice.Screen();
                string name = UniqueName(screen.Name ?? NameOf(physical), found);
                found.Add(new ExternalMonitor(physical.handle, name, screen.EdidVendor, area, current, maximum));
            }
        }
        return found;
    }

    // Called when a new level is requested, so that a read already under way does not overwrite it.
    public void MarkRequested(int percent)
    {
        Interlocked.Increment(ref requestCount);
        level = percent;
    }

    public bool Write(int percent)
    {
        uint value = (uint)Math.Round(percent * maximum / 100.0);
        for (int attempt = 1; handle != IntPtr.Zero && attempt <= Attempts; attempt++)
        {
            if (NativeMethods.SetVCPFeature(handle, LuminanceCode, value))
            {
                lastWrittenValue = value;
                applied = percent;
                return true;
            }
            Thread.Sleep(RetryDelay);
        }
        return false;
    }

    public void Read()
    {
        int requestsBefore = Thread.VolatileRead(ref requestCount);
        uint current, ignoredMaximum;
        if (!TryReadLuminance(handle, out current, out ignoredMaximum))
            return;
        if (current != lastWrittenValue) // the same value would come back rounded differently
            applied = ToPercent(current);
        if (Thread.VolatileRead(ref requestCount) == requestsBefore)
            level = applied;
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
            NativeMethods.DestroyPhysicalMonitor(handle);
        handle = IntPtr.Zero;
    }

    int ToPercent(uint value)
    {
        return (int)Math.Round(Math.Min(value, maximum) * 100.0 / maximum);
    }

    static bool TryReadLuminance(IntPtr monitor, out uint current, out uint maximum)
    {
        current = maximum = 0;
        for (int attempt = 1; monitor != IntPtr.Zero && attempt <= Attempts; attempt++)
        {
            if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(monitor, LuminanceCode, IntPtr.Zero, out current, out maximum))
                return true;
            Thread.Sleep(RetryDelay);
        }
        return false;
    }

    static List<IntPtr> DisplayMonitors()
    {
        var found = new List<IntPtr>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, dc, bounds, data) =>
        {
            found.Add(monitor);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static NativeMethods.PhysicalMonitor[] PhysicalMonitors(IntPtr displayMonitor)
    {
        uint count;
        if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(displayMonitor, out count) || count == 0)
            return new NativeMethods.PhysicalMonitor[0];
        var physical = new NativeMethods.PhysicalMonitor[count];
        return NativeMethods.GetPhysicalMonitorsFromHMONITOR(displayMonitor, count, physical)
            ? physical
            : new NativeMethods.PhysicalMonitor[0];
    }

    static string NameOf(NativeMethods.PhysicalMonitor physical)
    {
        string description = (physical.description ?? "").Trim();
        return description.Length == 0 || description.StartsWith("Generic", StringComparison.OrdinalIgnoreCase)
            ? "External monitor"
            : description;
    }

    static string UniqueName(string name, List<ExternalMonitor> others)
    {
        string unique = name;
        for (int number = 2; others.Exists(other => other.Name == unique); number++)
            unique = name + " " + number;
        return unique;
    }
}

// Where a display sits on the desktop, as arranged in Settings > Display
sealed class ScreenArea
{
    public ScreenArea(NativeMethods.MonitorInfoEx info)
    {
        Bounds = Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
        IsPrimary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
    }

    public Rectangle Bounds { get; private set; }
    public bool IsPrimary { get; private set; } // the main display

    public Point Center
    {
        get { return new Point(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2); }
    }

    public double DistanceTo(ScreenArea other)
    {
        Point a = Center;
        Point b = other.Center;
        return Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));
    }
}

// The screens Windows shows a display device (\\.\DISPLAY1 ...) on: one, or several when the
// image is duplicated. External screens get their own model name, as in Settings > Display,
// with the vendor in front when the model name leaves it out.
sealed class DisplayDevice
{
    public sealed class Screen
    {
        public string Name;
        public string EdidVendor;
    }

    // EDID carries only a registered three-letter code; these are well-known monitor brands.
    static readonly Dictionary<string, string> Vendors = new Dictionary<string, string>
    {
        { "ACI", "ASUS" }, { "ACR", "Acer" }, { "AOC", "AOC" }, { "APP", "Apple" }, { "AUS", "ASUS" },
        { "BNQ", "BenQ" }, { "DEL", "Dell" }, { "EIZ", "EIZO" }, { "GBT", "Gigabyte" }, { "GSM", "LG" },
        { "HPN", "HP" }, { "HWP", "HP" }, { "IVM", "iiyama" }, { "LEN", "Lenovo" }, { "MSI", "MSI" },
        { "NEC", "NEC" }, { "PHL", "Philips" }, { "SAM", "Samsung" }, { "SHP", "Sharp" }, { "SKG", "KTC" },
        { "SNY", "Sony" }, { "VSC", "ViewSonic" }
    };

    public bool ShowsOnBuiltIn;
    public readonly List<Screen> ExternalScreens = new List<Screen>();

    // Empty when Windows cannot tell
    public static Dictionary<string, DisplayDevice> Active()
    {
        var devices = new Dictionary<string, DisplayDevice>(StringComparer.OrdinalIgnoreCase);
        uint pathCount, modeCount;
        if (NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount) != 0)
            return devices;
        var paths = new NativeMethods.DisplayConfigPathInfo[pathCount];
        var modes = new NativeMethods.DisplayConfigModeInfo[modeCount];
        if (NativeMethods.QueryDisplayConfig(NativeMethods.QDC_ONLY_ACTIVE_PATHS,
                ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            return devices;

        for (int i = 0; i < pathCount; i++)
        {
            var source = new NativeMethods.DisplayConfigSourceDeviceName();
            source.header = Header(NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, source,
                paths[i].sourceInfo.adapterId, paths[i].sourceInfo.id);
            if (NativeMethods.DisplayConfigGetDeviceInfo(ref source) != 0)
                continue;
            DisplayDevice device;
            if (!devices.TryGetValue(source.viewGdiDeviceName, out device))
                devices[source.viewGdiDeviceName] = device = new DisplayDevice();

            if (IsBuiltInConnection(paths[i].targetInfo.outputTechnology))
            {
                device.ShowsOnBuiltIn = true;
                continue;
            }
            var target = new NativeMethods.DisplayConfigTargetDeviceName();
            target.header = Header(NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, target,
                paths[i].targetInfo.adapterId, paths[i].targetInfo.id);
            var screen = new Screen();
            if (NativeMethods.DisplayConfigGetDeviceInfo(ref target) == 0)
            {
                if ((target.flags & NativeMethods.DISPLAYCONFIG_TARGET_EDID_IDS_VALID) != 0)
                    screen.EdidVendor = VendorCode(target.edidManufactureId);
                screen.Name = WithVendor(target.monitorFriendlyDeviceName, screen.EdidVendor);
            }
            device.ExternalScreens.Add(screen);
        }
        return devices;
    }

    // EDID packs three letters into five bits each (1 = A), big-endian. Windows hands the two
    // bytes over as they are, so they normally come out swapped.
    static string VendorCode(ushort edidId)
    {
        return Letters(((edidId & 0xFF) << 8) | (edidId >> 8)) ?? Letters(edidId);
    }

    static string Letters(int packed)
    {
        var code = new char[3];
        for (int i = 0; i < 3; i++)
        {
            int letter = (packed >> (10 - 5 * i)) & 0x1F;
            if (letter < 1 || letter > 26)
                return null;
            code[i] = (char)('A' + letter - 1);
        }
        return new string(code);
    }

    static string WithVendor(string model, string vendorCode)
    {
        if (string.IsNullOrEmpty(model))
            return null;
        string vendor;
        if (vendorCode == null || !Vendors.TryGetValue(vendorCode, out vendor)
            || model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
            return model;
        return vendor + " " + model;
    }

    static NativeMethods.DisplayConfigDeviceInfoHeader Header(uint type, object request, NativeMethods.Luid adapter, uint id)
    {
        return new NativeMethods.DisplayConfigDeviceInfoHeader
        {
            type = type,
            size = (uint)Marshal.SizeOf(request),
            adapterId = adapter,
            id = id
        };
    }

    static bool IsBuiltInConnection(uint outputTechnology)
    {
        return outputTechnology == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
            || outputTechnology == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS
            || outputTechnology == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
            || outputTechnology == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED;
    }
}

static class PowerSchemes
{
    static readonly Guid DisplaySubgroup = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");      // GUID_VIDEO_SUBGROUP
    static readonly Guid BrightnessSetting = new Guid("aded5e82-b909-4619-9949-f5d71dac0bcb");    // GUID_DEVICE_POWER_POLICY_VIDEO_BRIGHTNESS
    static readonly Guid ActiveSchemeSetting = new Guid("31f9f286-5084-42fe-b720-2b0264993763");  // GUID_ACTIVE_POWERSCHEME

    static NativeMethods.PowerNotifyCallback activeSchemeCallback; // must stay referenced while registered

    public static void SetBrightnessInAll(int percent)
    {
        Guid subgroup = DisplaySubgroup;
        Guid setting = BrightnessSetting;
        foreach (Guid schemeId in EnumerateAll())
        {
            Guid scheme = schemeId;
            uint ac = NativeMethods.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, (uint)percent);
            uint dc = NativeMethods.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, (uint)percent);
            if (ac != 0 || dc != 0)
                Log.Write(string.Format("Cannot write power scheme {0}: AC error {1}, DC error {2}", scheme, ac, dc));
        }
    }

    static IEnumerable<Guid> EnumerateAll()
    {
        var buffer = new byte[16];
        for (uint index = 0; ; index++)
        {
            uint size = (uint)buffer.Length;
            if (NativeMethods.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    NativeMethods.ACCESS_SCHEME, index, buffer, ref size) != 0)
                yield break;
            yield return new Guid(buffer);
        }
    }

    public static void SubscribeToActiveSchemeChanges(Action onChanged)
    {
        activeSchemeCallback = (context, type, notificationData) =>
        {
            if (type == NativeMethods.PBT_POWERSETTINGCHANGE)
                Log.Guard("power scheme change", onChanged);
            return 0;
        };

        var parameters = new NativeMethods.DeviceNotifySubscribeParameters { Callback = activeSchemeCallback };
        // Never freed: the registration lives as long as the process.
        IntPtr unmanagedParameters = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.DeviceNotifySubscribeParameters)));
        Marshal.StructureToPtr(parameters, unmanagedParameters, false);

        Guid setting = ActiveSchemeSetting;
        IntPtr registration;
        uint error = NativeMethods.PowerSettingRegisterNotification(
            ref setting, NativeMethods.DEVICE_NOTIFY_CALLBACK, unmanagedParameters, out registration);
        if (error != 0)
            throw new Win32Exception((int)error);
    }
}

static class StartupTask
{
    const string DefinitionTemplate =
@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Brightness Sync: keeps the same display brightness in all power schemes.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{0}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{0}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{1}</Command>
      <Arguments>{2}</Arguments>
    </Exec>
  </Actions>
</Task>";

    static readonly string QuotedName = Quote(AppInfo.Id);

    public static bool Exists()
    {
        string ignoredOutput;
        return RunSchtasks("/query /tn " + QuotedName, out ignoredOutput) == 0;
    }

    public static bool Run()
    {
        string ignoredOutput;
        return RunSchtasks("/run /tn " + QuotedName, out ignoredOutput) == 0;
    }

    public static bool Create()
    {
        string definitionFile = Path.Combine(Path.GetTempPath(), AppInfo.Id + ".task.xml");
        try
        {
            File.WriteAllText(definitionFile, BuildDefinition(), Encoding.Unicode);
            return RunAndLog("/create /tn " + QuotedName + " /xml " + Quote(definitionFile) + " /f",
                "Run at startup enabled: " + Application.ExecutablePath);
        }
        catch (Exception ex)
        {
            Log.Write("Create startup task failed: " + ex);
            return false;
        }
        finally
        {
            try { File.Delete(definitionFile); } catch { }
        }
    }

    public static bool Delete()
    {
        return RunAndLog("/delete /tn " + QuotedName + " /f", "Run at startup disabled");
    }

    static string BuildDefinition()
    {
        return string.Format(DefinitionTemplate,
            SecurityElement.Escape(WindowsIdentity.GetCurrent().Name),
            SecurityElement.Escape(Application.ExecutablePath),
            AppInfo.AutostartArgument);
    }

    static bool RunAndLog(string arguments, string successMessage)
    {
        string output;
        int exitCode = RunSchtasks(arguments, out output);
        Log.Write(exitCode == 0 ? successMessage : "schtasks " + arguments + " failed (" + exitCode + "): " + output);
        return exitCode == 0;
    }

    static int RunSchtasks(string arguments, out string output)
    {
        Encoding consoleEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var startInfo = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = consoleEncoding,
            StandardErrorEncoding = consoleEncoding
        };
        try
        {
            using (Process process = Process.Start(startInfo))
            {
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                output = (stdout + " " + stderr).Trim();
                return process.ExitCode;
            }
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }

    static string Quote(string value)
    {
        return "\"" + value + "\"";
    }
}

static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static void RelaunchAsAdmin()
    {
        var startInfo = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" };
        try
        {
            Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            // UAC prompt declined
        }
    }
}

static class SingleInstance
{
    public const string MutexName = @"Local\" + AppInfo.Id;

    public static bool IsRunning()
    {
        try
        {
            Mutex existing;
            if (!Mutex.TryOpenExisting(MutexName, out existing))
                return false;
            existing.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // owned by an elevated instance
        }
    }
}

// Windows has two theme switches: "Windows mode" (taskbar, tray icons and their menus) and
// "app mode" (application windows). Each part of the app follows the one Windows itself uses.
static class WindowsTheme
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static void WatchForChanges(Action onChanged)
    {
        RegistryWatcher.Watch("theme watcher", () => Registry.CurrentUser.OpenSubKey(PersonalizeKey), onChanged);
    }

    public static bool TaskbarIsDark
    {
        get { return !UsesLightTheme("SystemUsesLightTheme", false); } // missing before 1903: always dark
    }

    public static bool AppsAreDark
    {
        get { return !UsesLightTheme("AppsUseLightTheme", true); }
    }

    static bool UsesLightTheme(string valueName, bool whenMissing)
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
        {
            object value = key == null ? null : key.GetValue(valueName);
            return value == null ? whenMissing : Convert.ToInt32(value) != 0;
        }
    }
}

// Environment.OSVersion reports Windows 8 to apps without a compatibility manifest.
static class WindowsBuild
{
    public static readonly int Number = ReadBuildNumber();

    static int ReadBuildNumber()
    {
        try
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                return key == null ? 0 : Convert.ToInt32(key.GetValue("CurrentBuildNumber", "0"));
        }
        catch
        {
            return 0;
        }
    }
}

// ============================================================================ settings

sealed class AppSettings
{
    public static readonly AppSettings Default = new AppSettings(true, true, true, ScrollScope.AllDisplays);

    public AppSettings(bool syncEnabled, bool trayIconVisible, bool smoothBrightnessChanges, ScrollScope scrollScope)
    {
        SyncEnabled = syncEnabled;
        TrayIconVisible = trayIconVisible;
        SmoothBrightnessChanges = smoothBrightnessChanges;
        ScrollScope = scrollScope;
    }

    public bool SyncEnabled { get; private set; }
    public bool TrayIconVisible { get; private set; }
    public bool SmoothBrightnessChanges { get; private set; }
    public ScrollScope ScrollScope { get; private set; } // what scrolling changes with external monitors

    public AppSettings WithSyncEnabled(bool value)
    {
        return new AppSettings(value, TrayIconVisible, SmoothBrightnessChanges, ScrollScope);
    }

    public AppSettings WithTrayIconVisible(bool value)
    {
        return new AppSettings(SyncEnabled, value, SmoothBrightnessChanges, ScrollScope);
    }

    public AppSettings WithSmoothBrightnessChanges(bool value)
    {
        return new AppSettings(SyncEnabled, TrayIconVisible, value, ScrollScope);
    }

    public AppSettings WithScrollScope(ScrollScope value)
    {
        return new AppSettings(SyncEnabled, TrayIconVisible, SmoothBrightnessChanges, value);
    }

    // A manual launch shows the icon and enables sync, but keeps the user's other preferences.
    public AppSettings ForManualLaunch()
    {
        return new AppSettings(true, true, SmoothBrightnessChanges, ScrollScope);
    }
}

static class SettingsStore
{
    const string KeyPath = @"Software\" + AppInfo.Id;
    const string SyncEnabledValue = "SyncEnabled";
    const string TrayIconVisibleValue = "TrayVisible";
    const string SmoothBrightnessChangesValue = "SmoothBrightnessChanges";
    const string ScrollScopeValue = "ScrollScope";
    const string ScrollHintSeenValue = "ScrollHintSeen";

    public static AppSettings Load()
    {
        using (RegistryKey key = OpenKey())
        {
            return new AppSettings(
                ReadFlag(key, SyncEnabledValue),
                ReadFlag(key, TrayIconVisibleValue),
                ReadFlag(key, SmoothBrightnessChangesValue),
                ReadScrollScope(key));
        }
    }

    public static void Save(AppSettings settings)
    {
        using (RegistryKey key = OpenKey())
        {
            WriteFlag(key, SyncEnabledValue, settings.SyncEnabled);
            WriteFlag(key, TrayIconVisibleValue, settings.TrayIconVisible);
            WriteFlag(key, SmoothBrightnessChangesValue, settings.SmoothBrightnessChanges);
            key.SetValue(ScrollScopeValue, (int)settings.ScrollScope, RegistryValueKind.DWord);
        }
    }

    public static void WatchForChanges(Action<AppSettings> onChanged)
    {
        RegistryWatcher.Watch("settings watcher", OpenKey, () => onChanged(Load()));
    }

    // Not part of AppSettings: it is not a preference, just a note that the scroll tip was used.
    public static bool ScrollHintSeen
    {
        get
        {
            using (RegistryKey key = OpenKey())
                return Convert.ToInt32(key.GetValue(ScrollHintSeenValue, 0)) != 0;
        }
    }

    public static void MarkScrollHintSeen()
    {
        using (RegistryKey key = OpenKey())
            WriteFlag(key, ScrollHintSeenValue, true);
    }

    static RegistryKey OpenKey()
    {
        return Registry.CurrentUser.CreateSubKey(KeyPath);
    }

    static ScrollScope ReadScrollScope(RegistryKey key)
    {
        var scope = (ScrollScope)Convert.ToInt32(key.GetValue(ScrollScopeValue, 0));
        return Enum.IsDefined(typeof(ScrollScope), scope) ? scope : ScrollScope.AllDisplays;
    }

    static bool ReadFlag(RegistryKey key, string name)
    {
        return Convert.ToInt32(key.GetValue(name, 1)) != 0;
    }

    static void WriteFlag(RegistryKey key, string name, bool value)
    {
        key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
    }
}

// Calls onChanged (on a background thread) whenever a value in the key changes.
// The thread sleeps inside RegNotifyChangeKeyValue in between.
static class RegistryWatcher
{
    // Writers often set several values in a row; wait so they are read together.
    static readonly TimeSpan BurstDelay = TimeSpan.FromMilliseconds(100);

    public static void Watch(string name, Func<RegistryKey> openKey, Action onChanged)
    {
        var watcher = new Thread(() => Log.Guard(name, () => WatchForever(openKey, onChanged)));
        watcher.IsBackground = true;
        watcher.Start();
    }

    static void WatchForever(Func<RegistryKey> openKey, Action onChanged)
    {
        using (RegistryKey key = openKey())
        {
            if (key == null)
                return;
            while (true)
            {
                int error = NativeMethods.RegNotifyChangeKeyValue(
                    key.Handle.DangerousGetHandle(), false, NativeMethods.REG_NOTIFY_CHANGE_LAST_SET, IntPtr.Zero, false);
                if (error != 0)
                    throw new Win32Exception(error);
                Thread.Sleep(BurstDelay);
                onChanged();
            }
        }
    }
}

// ============================================================================ log

static class Log
{
    const long RotationSizeBytes = 64 * 1024;
    static readonly object writeLock = new object();
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Id + ".log");
    static readonly string PreviousFilePath = FilePath + ".1";

    public static void Write(string message)
    {
        try
        {
            lock (writeLock)
            {
                RotateIfTooLarge();
                File.AppendAllText(FilePath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine);
            }
        }
        catch
        {
            // logging must never break the app
        }
    }

    public static void Guard(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Write(operation + " failed: " + ex);
        }
    }

    static void RotateIfTooLarge()
    {
        var current = new FileInfo(FilePath);
        if (!current.Exists || current.Length <= RotationSizeBytes)
            return;
        if (File.Exists(PreviousFilePath))
            File.Delete(PreviousFilePath);
        File.Move(FilePath, PreviousFilePath);
    }
}

// ============================================================================ Win32 interop

static class NativeMethods
{
    // --- power schemes

    public const uint ACCESS_SCHEME = 16;
    public const uint DEVICE_NOTIFY_CALLBACK = 2;
    public const uint PBT_POWERSETTINGCHANGE = 0x8013;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint PowerNotifyCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceNotifySubscribeParameters
    {
        public PowerNotifyCallback Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll")]
    public static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subgroupGuid,
        uint accessFlags, uint index, byte[] buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup,
        ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup,
        ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    public static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags,
        IntPtr recipient, out IntPtr registrationHandle);

    // --- registry

    public const uint REG_NOTIFY_CHANGE_LAST_SET = 4;

    [DllImport("advapi32.dll")]
    public static extern int RegNotifyChangeKeyValue(IntPtr key, bool watchSubtree, uint notifyFilter,
        IntPtr eventHandle, bool asynchronous);

    // --- windows and icons

    public const int WM_NULL = 0x0000;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int HTCAPTION = 2;
    public const uint ICON_RESOURCE_VERSION = 0x00030000;

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconFromResourceEx(byte[] resourceBits, uint resourceSize, bool isIcon,
        uint version, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    // --- notification area

    public const int WM_CONTEXTMENU = 0x007B;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_APP = 0x8000;
    public const int NIN_SELECT = 0x0400;
    public const int NIN_KEYSELECT = 0x0401;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_SHOWTIP = 0x80;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint MSGFLT_ALLOW = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NotifyIconIdentifier
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("shell32.dll")]
    public static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect iconLocation);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    public static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    public static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeFilterStruct);

    // --- low-level mouse hook

    public const int WH_MOUSE_LL = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseHookData
    {
        public int x, y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int hookType, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string moduleName);

    // --- keyboard

    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    // --- monitors: enumeration, display configuration and DDC/CI (dxva2)

    public const uint QDC_ONLY_ACTIVE_PATHS = 2;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    public const uint DISPLAYCONFIG_TARGET_EDID_IDS_VALID = 4;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS = 6;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

    public const uint MONITORINFOF_PRIMARY = 1;

    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr bounds, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    public struct PhysicalMonitor
    {
        public IntPtr handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigPathSourceInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigPathTargetInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo sourceInfo;
        public DisplayConfigPathTargetInfo targetInfo;
        public uint flags;
    }

    // Only needed as a buffer: 16 bytes of header and a 48-byte union of mode details
    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigModeInfo
    {
        public uint infoType;
        public uint id;
        public Luid adapterId;
        public ulong mode0, mode1, mode2, mode3, mode4, mode5;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigDeviceInfoHeader
    {
        public uint type;
        public uint size;
        public Luid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayConfigPathInfo[] paths,
        ref uint modeCount, [Out] DisplayConfigModeInfo[] modes, IntPtr currentTopology);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName request);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName request);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll")]
    public static extern bool DestroyPhysicalMonitor(IntPtr monitor);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, IntPtr codeType,
        out uint currentValue, out uint maximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);

    // --- popup menu

    public const uint MF_STRING = 0x0000, MF_BYCOMMAND = 0x0000, MF_GRAYED = 0x0001, MF_CHECKED = 0x0008,
        MF_POPUP = 0x0010, MF_SEPARATOR = 0x0800;
    public const uint TPM_LEFTALIGN = 0x0000, TPM_RIGHTBUTTON = 0x0002, TPM_RIGHTALIGN = 0x0008,
        TPM_BOTTOMALIGN = 0x0020, TPM_NONOTIFY = 0x0080, TPM_RETURNCMD = 0x0100;
    public const int SM_MENUDROPALIGNMENT = 40;

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr itemId, string text);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);

    [DllImport("user32.dll")]
    public static extern bool CheckMenuRadioItem(IntPtr menu, uint first, uint last, uint check, uint flags);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr menu);

    // --- undocumented uxtheme exports (Windows 10 1903+)

    [DllImport("uxtheme.dll", EntryPoint = "#133")]
    public static extern bool AllowDarkModeForWindow(IntPtr window, bool allow);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    public static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    public static extern void FlushMenuThemes();
}
