using System.Diagnostics;

namespace PoolWatch.Ui;

/// <summary>
/// The tray and window icons: PoolWatch's binoculars, in the tray with a dot in the state's colour (green, amber, red;
/// grey binoculars while PoolWatch has no reading yet), on its windows and the taskbar as they are. They are the .ico
/// files in Assets, embedded in PoolWatch.exe, loaded once on first use and kept for the life of the app.
/// </summary>
internal static class TrayIcons
{
    private static readonly Lazy<TrayIconSet> Embedded = new(() => TrayIconSet.Load(typeof(TrayIcons).Assembly.GetManifestResourceStream));

    public static Icon For(TrayState state) => Embedded.Value.For(state);

    /// <summary>The grey binoculars, for while PoolWatch has no reading yet ("PoolWatch: starting").</summary>
    public static Icon Starting => Embedded.Value.Starting;

    /// <summary>The binoculars without a state: every window's title bar and taskbar button (the app's own icon, poolwatch.ico).</summary>
    public static Icon App => Embedded.Value.App;
}

/// <summary>
/// One icon per tray state and the app's own, read from .ico files: each state's holds sizes from 16 to 64 px (the tray
/// and its menu ask for no larger), the app's from 16 to 256 px (Explorer and the installer show it large).
/// </summary>
internal sealed class TrayIconSet : IDisposable
{
    private readonly Icon _green, _amber, _red, _starting, _app;

    private TrayIconSet(Icon green, Icon amber, Icon red, Icon starting, Icon app) =>
        (_green, _amber, _red, _starting, _app) = (green, amber, red, starting, app);

    public Icon For(TrayState state) => state switch
    {
        TrayState.Green => _green,
        TrayState.Amber => _amber,
        TrayState.Red => _red,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No tray icon for this state."),
    };

    public Icon Starting => _starting;

    public Icon App => _app;

    /// <param name="open">Opens a file by name (its embedded resource name); null when there is no such file.</param>
    public static TrayIconSet Load(Func<string, Stream?> open)
    {
        Icon Read(string name)
        {
            using var stream = open(name) ?? throw new InvalidOperationException($"The icon {name} is missing from PoolWatch.exe.");
            return new Icon(stream); // the system's large size: sharp as a window's taskbar icon, scaled down in the tray
        }
        return new TrayIconSet(Read("tray-healthy.ico"), Read("tray-warning.ico"), Read("tray-critical.ico"), Read("tray-inactive.ico"), Read("poolwatch.ico"));
    }

    public void Dispose()
    {
        _green.Dispose();
        _amber.Dispose();
        _red.Dispose();
        _starting.Dispose();
        _app.Dispose();
    }
}

/// <summary>The optional "start at logon" scheduled task with highest privileges (so the UAC prompt is not needed daily).</summary>
internal static class LogonTask
{
    private const string Name = "PoolWatch";

    public static bool Exists() => Schtasks($"/query /tn {Name}").code == 0;

    /// <summary>
    /// The existing task's actions and run level (read only); null when there is no such task. L2: every action is returned
    /// (Task Scheduler runs them all); one that is not a program (COM handler, e-mail, message) as an action that cannot be checked.
    /// </summary>
    public static Engine.LogonTaskPolicy.ExistingTask? Read()
    {
        using var com = new Engine.ComScope();
        dynamic service = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!)!;
        service.Connect();
        dynamic root = com.Track(service.GetFolder(@"\"))!;
        dynamic? task;
        try { task = com.Track(root.GetTask(Name)); } catch { return null; }
        if (task is null) return null;
        dynamic def = com.Track(task.Definition)!;
        bool highest = false;
        try { highest = (int)com.Track(def.Principal)!.RunLevel == 1; } catch { } // TASK_RUNLEVEL_HIGHEST
        var actions = new List<Engine.LogonTaskPolicy.TaskAction>();
        foreach (dynamic a in com.Each(def.Actions))
        {
            int type = (int)a.Type;
            if (type == 0) actions.Add(new Engine.LogonTaskPolicy.TaskAction((string)(a.Path ?? ""), (string?)a.Arguments));
            else actions.Add(new Engine.LogonTaskPolicy.TaskAction($"a task action of type {type} (not a program)", null, IsExec: false));
        }
        return new Engine.LogonTaskPolicy.ExistingTask(actions, highest);
    }

    /// <param name="xmlPath">Where the task definition is written for schtasks /xml.</param>
    /// <param name="checkWrite">The write policy (PathGuard.CheckReal), applied right before the write and the delete (review round 2, S3).</param>
    public static (bool ok, string output) Set(bool enabled, string xmlPath, Func<string, string> checkWrite)
    {
        if (!enabled) { var d = Schtasks($"/delete /tn {Name} /f"); return (d.code == 0, d.output); }
        // S4 (review round 3): the task starts this exe with highest privileges and no UAC prompt, so only an exe in an
        // admin-only install folder is ever registered (never under --allow-any-location or the CI UI test).
        if (Engine.LogonTaskPolicy.WhyNotRegister(Environment.ProcessPath, Engine.RunFlags.AllowAnyLocation, Engine.RunFlags.UiSmoke,
                Engine.InstallGuard.AdminRoots()) is { } why)
            return (false, "PoolWatch did not create the logon task: " + why + ".");
        // /xml instead of /tr: schtasks' defaults would stop the monitor after 72 h and skip it on battery.
        string xml = checkWrite(xmlPath);
        File.WriteAllText(xml, TaskXml(Environment.ProcessPath ?? "PoolWatch.exe", System.Security.Principal.WindowsIdentity.GetCurrent().Name), System.Text.Encoding.Unicode);
        var r = Schtasks($"/create /tn {Name} /xml \"{xml}\" /f");
        try { File.Delete(checkWrite(xml)); } catch { }
        return (r.code == 0, r.output);
    }

    internal static string TaskXml(string exe, string user) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>PoolWatch kernel pool monitor (tray), started at logon with highest privileges.</Description></RegistrationInfo>
          <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{System.Security.SecurityElement.Escape(user)}</UserId></LogonTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>{System.Security.SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <Enabled>true</Enabled>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author"><Exec><Command>{System.Security.SecurityElement.Escape(exe)}</Command><Arguments>--tray</Arguments></Exec></Actions>
        </Task>
        """;

    private static (int code, string output) Schtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);
            return (p.ExitCode, o.Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}

/// <summary>The real logon task for <see cref="Engine.LogonTaskCheck"/>: Task Scheduler and schtasks.</summary>
internal sealed class WindowsLogonTasks(Engine.AppPaths paths) : Engine.ILogonTasks
{
    public Engine.LogonTaskPolicy.ExistingTask? Read() => LogonTask.Read();

    public string? RegisterCurrent()
    {
        var (ok, output) = LogonTask.Set(true, Path.Combine(paths.DataRoot, "logon-task.xml"), paths.Guard.CheckReal);
        return ok ? null : output;
    }

    public string? Remove()
    {
        var (ok, output) = LogonTask.Set(false, "", p => p);
        return ok ? null : output;
    }
}
