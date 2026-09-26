// Brightness Sync - https://github.com/Yukhnevich/BrightnessSync
// Copyright (c) 2026 Yukhnevich. MIT License, see LICENSE.
//
// Keeps display brightness identical across all Windows power schemes, so switching
// power modes (e.g. Armoury Crate Silent / Performance) no longer changes brightness.
//
// Build: build.cmd (uses the C# 5 compiler that ships with .NET Framework 4.5+)
//
// Usage:
//   BrightnessSync.exe             show the tray icon and enable sync (hands over to a running instance)
//   BrightnessSync.exe /autostart  start with saved settings (used by the "Run at startup" task)

using System;
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
    public const string Version = "1.0.0";
    public const string Author = "Yukhnevich";
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
            SettingsStore.Save(AppSettings.Default); // a running instance picks this up

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

// ============================================================================ tray UI

sealed class TrayApp
{
    static readonly Color ActiveColor = Color.FromArgb(255, 179, 0);
    static readonly Color InactiveColor = Color.FromArgb(140, 140, 140);

    readonly BrightnessKeeper keeper = new BrightnessKeeper();
    AppSettings settings = AppSettings.Default;
    SynchronizationContext uiThread;
    NotifyIcon trayIcon;
    ToolStripMenuItem syncMenuItem;
    ToolStripMenuItem startupMenuItem;
    Icon activeIcon;
    Icon inactiveIcon;

    public void Run()
    {
        NativeMethods.SetProcessDPIAware();
        Application.EnableVisualStyles();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        uiThread = SynchronizationContext.Current;

        keeper.StateChanged += () => uiThread.Post(_ => RefreshStatus(), null);
        SubscribeToSystemEvents();
        CreateTrayIcon();

        Apply(SettingsStore.Load());
        Log.Write(string.Format("Started (sync {0}, tray icon {1})",
            settings.SyncEnabled ? "on" : "off", settings.TrayIconVisible ? "visible" : "hidden"));

        SettingsStore.WatchForChanges(changed => uiThread.Post(_ => Apply(changed), null));
        Application.Run();
    }

    void SubscribeToSystemEvents()
    {
        RunOnMtaThread(() =>
        {
            Log.Guard("brightness subscription",
                () => DisplayBrightness.SubscribeToChanges(keeper.HandleBrightnessChanged));
            Log.Guard("power scheme subscription",
                () => PowerSchemes.SubscribeToActiveSchemeChanges(keeper.HandleActiveSchemeChanged));
        });
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
        activeIcon = SunIcon.Create(ActiveColor);
        inactiveIcon = SunIcon.Create(InactiveColor);

        syncMenuItem = new ToolStripMenuItem("Sync enabled", null,
            (s, e) => ChangeSettings(settings.WithSyncEnabled(!settings.SyncEnabled)));
        startupMenuItem = new ToolStripMenuItem("Run at startup", null, (s, e) => ToggleRunAtStartup());
        var hideMenuItem = new ToolStripMenuItem("Hide tray icon", null,
            (s, e) => ChangeSettings(settings.WithTrayIconVisible(false)));
        hideMenuItem.ToolTipText = "Run BrightnessSync.exe again to bring it back";
        var logMenuItem = new ToolStripMenuItem("Open log", null, (s, e) => Log.Open());
        var aboutMenuItem = new ToolStripMenuItem("About", null, (s, e) => ShowAbout());
        var exitMenuItem = new ToolStripMenuItem("Exit", null, (s, e) => Exit());

        var menu = new ContextMenuStrip { ShowItemToolTips = true };
        menu.Items.AddRange(new ToolStripItem[]
        {
            syncMenuItem, startupMenuItem, hideMenuItem, new ToolStripSeparator(),
            logMenuItem, aboutMenuItem, exitMenuItem
        });
        menu.Opening += (s, e) => startupMenuItem.Checked = StartupTask.Exists();

        trayIcon = new NotifyIcon { ContextMenuStrip = menu, Icon = inactiveIcon, Text = AppInfo.DisplayName };
        trayIcon.MouseUp += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowMenuOnLeftClick();
        };
    }

    // NotifyIcon opens its menu only on right click; its private ShowContextMenu positions it correctly.
    void ShowMenuOnLeftClick()
    {
        MethodInfo showContextMenu = typeof(NotifyIcon).GetMethod(
            "ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        if (showContextMenu != null)
            showContextMenu.Invoke(trayIcon, null);
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

        trayIcon.Visible = settings.TrayIconVisible;
        RefreshStatus();
    }

    void RefreshStatus()
    {
        bool active = keeper.IsEnabled;
        syncMenuItem.Checked = settings.SyncEnabled;
        trayIcon.Icon = active ? activeIcon : inactiveIcon;
        trayIcon.Text = active
            ? AppInfo.DisplayName + ": on\nLevel: " + keeper.KeptLevel + "%"
            : AppInfo.DisplayName + ": off";
    }

    void ToggleRunAtStartup()
    {
        bool succeeded = StartupTask.Exists() ? StartupTask.Delete() : StartupTask.Create();
        startupMenuItem.Checked = StartupTask.Exists();
        if (!succeeded)
            MessageBox.Show("Could not update the startup task. See the log for details.",
                AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    static void ShowAbout()
    {
        string text = AppInfo.DisplayName + " " + AppInfo.Version + "\n"
            + AppInfo.Copyright + "\n"
            + "MIT License\n\n"
            + AppInfo.RepositoryUrl + "\n\n"
            + "Open the project page?";
        DialogResult answer = MessageBox.Show(text, "About " + AppInfo.DisplayName,
            MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer == DialogResult.Yes)
            OpenInBrowser(AppInfo.RepositoryUrl);
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
        trayIcon.Visible = false;
        Environment.Exit(0);
    }
}

static class SunIcon
{
    public static Icon Create(Color color)
    {
        int size = SystemInformation.SmallIconSize.Width;
        float center = size / 2f;
        float discRadius = size * 0.20f;
        float rayStart = size * 0.33f;
        float rayEnd = size * 0.46f;

        var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var disc = new SolidBrush(color))
        using (var ray = new Pen(color, Math.Max(1.5f, size / 11f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillEllipse(disc, center - discRadius, center - discRadius, 2 * discRadius, 2 * discRadius);
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4;
                graphics.DrawLine(ray, PointOnCircle(center, rayStart, angle), PointOnCircle(center, rayEnd, angle));
            }
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    static PointF PointOnCircle(float center, float radius, double angle)
    {
        return new PointF(center + (float)(Math.Cos(angle) * radius), center + (float)(Math.Sin(angle) * radius));
    }
}

// ============================================================================ core logic

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

// ============================================================================ system access

static class DisplayBrightness
{
    const string WmiNamespace = @"root\WMI";
    static ManagementEventWatcher changeWatcher;

    public static int? Read()
    {
        using (var searcher = new ManagementObjectSearcher(WmiNamespace, "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
        {
            foreach (ManagementObject monitor in searcher.Get())
                return Convert.ToInt32(monitor["CurrentBrightness"]);
        }
        return null;
    }

    public static void Set(int percent)
    {
        using (var searcher = new ManagementObjectSearcher(WmiNamespace, "SELECT * FROM WmiMonitorBrightnessMethods"))
        {
            foreach (ManagementObject monitor in searcher.Get())
            {
                monitor.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
                return;
            }
        }
    }

    public static void SubscribeToChanges(Action<int> onChanged)
    {
        changeWatcher = new ManagementEventWatcher(
            new ManagementScope(WmiNamespace), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
        changeWatcher.EventArrived += (sender, e) =>
            Log.Guard("brightness change", () => onChanged(Convert.ToInt32(e.NewEvent["Brightness"])));
        changeWatcher.Start();
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

// ============================================================================ settings

sealed class AppSettings
{
    public static readonly AppSettings Default = new AppSettings(true, true);

    public AppSettings(bool syncEnabled, bool trayIconVisible)
    {
        SyncEnabled = syncEnabled;
        TrayIconVisible = trayIconVisible;
    }

    public bool SyncEnabled { get; private set; }
    public bool TrayIconVisible { get; private set; }

    public AppSettings WithSyncEnabled(bool value)
    {
        return new AppSettings(value, TrayIconVisible);
    }

    public AppSettings WithTrayIconVisible(bool value)
    {
        return new AppSettings(SyncEnabled, value);
    }
}

static class SettingsStore
{
    const string KeyPath = @"Software\" + AppInfo.Id;
    const string SyncEnabledValue = "SyncEnabled";
    const string TrayIconVisibleValue = "TrayVisible";
    static readonly TimeSpan SaveCompletionDelay = TimeSpan.FromMilliseconds(100);

    public static AppSettings Load()
    {
        using (RegistryKey key = OpenKey())
        {
            return new AppSettings(
                ReadFlag(key, SyncEnabledValue),
                ReadFlag(key, TrayIconVisibleValue));
        }
    }

    public static void Save(AppSettings settings)
    {
        using (RegistryKey key = OpenKey())
        {
            WriteFlag(key, SyncEnabledValue, settings.SyncEnabled);
            WriteFlag(key, TrayIconVisibleValue, settings.TrayIconVisible);
        }
    }

    public static void WatchForChanges(Action<AppSettings> onChanged)
    {
        var watcher = new Thread(() => Log.Guard("settings watcher", () => NotifyAboutChangesForever(onChanged)));
        watcher.IsBackground = true;
        watcher.Start();
    }

    static void NotifyAboutChangesForever(Action<AppSettings> onChanged)
    {
        using (RegistryKey key = OpenKey())
        {
            while (true)
            {
                int error = NativeMethods.RegNotifyChangeKeyValue(
                    key.Handle.DangerousGetHandle(), false, NativeMethods.REG_NOTIFY_CHANGE_LAST_SET, IntPtr.Zero, false);
                if (error != 0)
                    throw new Win32Exception(error);

                Thread.Sleep(SaveCompletionDelay); // Save() writes values one by one; read them together
                onChanged(Load());
            }
        }
    }

    static RegistryKey OpenKey()
    {
        return Registry.CurrentUser.CreateSubKey(KeyPath);
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

// ============================================================================ log

static class Log
{
    const long RotationSizeBytes = 64 * 1024;
    static readonly object writeLock = new object();
    static readonly string FilePath = Path.Combine(
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

    public static void Open()
    {
        Guard("open log", () =>
        {
            if (File.Exists(FilePath))
                Process.Start("notepad.exe", "\"" + FilePath + "\"");
        });
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
    public const uint ACCESS_SCHEME = 16;
    public const uint DEVICE_NOTIFY_CALLBACK = 2;
    public const uint PBT_POWERSETTINGCHANGE = 0x8013;
    public const uint REG_NOTIFY_CHANGE_LAST_SET = 4;

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

    [DllImport("advapi32.dll")]
    public static extern int RegNotifyChangeKeyValue(IntPtr key, bool watchSubtree, uint notifyFilter,
        IntPtr eventHandle, bool asynchronous);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
}
