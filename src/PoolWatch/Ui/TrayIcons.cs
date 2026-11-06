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

    /// <summary>TASK_TRIGGER_LOGON: a trigger that runs the task when a user signs in.</summary>
    private const int LogonTriggerType = 9;

    /// <summary>
    /// Whether PoolWatch starts when you sign in, now (<see cref="Engine.LogonTaskPolicy.AtSignIn"/>): the task as
    /// <see cref="Read"/> reads it, as <see cref="AtSignIn(Engine.LogonTaskPolicy.ExistingTask?)"/> judges it. Throws when
    /// the task is there but cannot be read.
    /// </summary>
    public static Core.Setup.SignInState AtSignIn() => AtSignIn(Read());

    /// <summary>
    /// Whether <paramref name="task"/>, as read on this PC, starts PoolWatch when you sign in: judged as the check at start
    /// judges it, with this PC's need for highest privileges (<see cref="HighestNeeded()"/>), which decides its run level
    /// here (the check's own judgement is asked for without it).
    /// </summary>
    public static Core.Setup.SignInState AtSignIn(Engine.LogonTaskPolicy.ExistingTask? task) =>
        Engine.LogonTaskPolicy.AtSignIn(task, Engine.LogonTaskPolicy.Judge(task, null, false, Engine.InstallGuard.AdminRoots(), IsThisAccount, judgeRunLevel: false).Judgement,
            HighestNeeded());

    /// <summary>
    /// Whether a logon task must run with highest privileges to start PoolWatch's launcher, which starts only as
    /// administrator. The task signs in in person, whose token UAC splits for an administrator (the usual case): a task
    /// without them then gets the account's token without administrator rights, so they are needed. They are not needed
    /// only when every program of the account runs as administrator, as the launcher judges its own account (<see
    /// cref="Engine.ElevatedAccounts.OfThisProcess"/>): this PoolWatch's own token is an administrator's that UAC did not
    /// split and is a sign-in in person, or, when PoolWatch runs from a batch or service sign-in (whose token can be
    /// unsplit even where UAC splits the account's sign-in in person, so its token cannot tell), UAC's settings say UAC is
    /// off, or that this is the PC's built-in Administrator with its Admin Approval Mode off. When any of it cannot be
    /// read, they are needed.
    /// </summary>
    public static bool HighestNeeded() => HighestNeeded(Engine.ElevatedAccounts.OfThisProcess());

    /// <summary>Pure: <see cref="HighestNeeded()"/> for the account as <see cref="Engine.ElevatedAccounts.OfThisProcess"/> judged it (none: needed).</summary>
    internal static bool HighestNeeded(Engine.ElevatedAccounts ownAccount) => ownAccount.Known.Count == 0;

    /// <summary>
    /// The account this PoolWatch runs as, the one it registers the task for (<see cref="TaskXml"/> writes its name): by that
    /// name as text, or by SID (<see cref="Engine.LogonTaskPolicy.IsAccount"/>).
    /// </summary>
    public static bool IsThisAccount(string? userId)
    {
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        return Engine.LogonTaskPolicy.IsAccount(userId, me.Name, me.User?.Value, Engine.Changes.ClearTaskReplacer.SameAccount);
    }

    /// <summary>
    /// The existing task's actions (program, arguments, start folder), run level, on or off state, sign-in triggers,
    /// account and sign-in kind (read only); null when there is no such task. Every action is returned (Task Scheduler
    /// runs them all); one that is not a program (COM handler, e-mail, message) as an action that cannot be checked. Any
    /// failure other than "no such task" (<see cref="IsNoSuchTask"/>) is thrown, so a task that is there but could not be
    /// read is never taken for no task. Its run level, whether it is on, its sign-in triggers, its account and its sign-in
    /// kind are each null when Task Scheduler's answer for it cannot be read; the judgement counts an account or a sign-in
    /// kind that is null as not PoolWatch's.
    /// </summary>
    public static Engine.LogonTaskPolicy.ExistingTask? Read()
    {
        using var com = new Engine.ComScope();
        dynamic service = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!)!;
        service.Connect();
        dynamic root = com.Track(service.GetFolder(@"\"))!;
        dynamic? task;
        try { task = com.Track(root.GetTask(Name)); }
        catch (Exception ex) when (IsNoSuchTask(ex)) { return null; }
        if (task is null) return null;
        dynamic def = com.Track(task.Definition)!;
        bool? highest = null; // TASK_RUNLEVEL_HIGHEST; not known when it cannot be read
        try { highest = (int)com.Track(def.Principal)!.RunLevel == 1; } catch { }
        string? userId = null; // the account it runs as (a name or a SID); not known when it cannot be read
        try { userId = (string?)com.Track(def.Principal)!.UserId; } catch { }
        int? logonType = null; // its sign-in kind (TASK_LOGON_*); not known when it cannot be read
        try { logonType = (int)com.Track(def.Principal)!.LogonType; } catch { }
        bool? enabled = null; // IRegisteredTask.Enabled: false when the owner turned the task off; not known when it cannot be read
        try { enabled = (bool)task.Enabled; } catch { }
        Engine.LogonTaskPolicy.Trigger? signIn = null; // its sign-in triggers; not known when they cannot be read
        try { signIn = SignInTriggers(com, (object)def.Triggers); } catch { }
        var actions = new List<Engine.LogonTaskPolicy.TaskAction>();
        foreach (dynamic a in com.Each(def.Actions))
        {
            int type = (int)a.Type;
            if (type == 0) actions.Add(new Engine.LogonTaskPolicy.TaskAction((string)(a.Path ?? ""), (string?)a.Arguments, WorkingDirectory: (string?)a.WorkingDirectory));
            else actions.Add(new Engine.LogonTaskPolicy.TaskAction($"a task action of type {type} (not a program)", null, IsExec: false));
        }
        return new Engine.LogonTaskPolicy.ExistingTask(actions, highest, enabled, signIn, userId, logonType);
    }

    /// <summary>
    /// The sign-in triggers among <paramref name="triggers"/> (ITriggerCollection): one is on; there are some, and each is
    /// turned off; there is none (the task may have triggers of other kinds).
    /// </summary>
    internal static Engine.LogonTaskPolicy.Trigger SignInTriggers(Engine.ComScope com, object triggers)
    {
        bool any = false, on = false;
        foreach (dynamic t in com.Each(triggers))
        {
            if ((int)t.Type != LogonTriggerType) continue;
            any = true;
            if ((bool)t.Enabled) on = true;
        }
        return on ? Engine.LogonTaskPolicy.Trigger.On : any ? Engine.LogonTaskPolicy.Trigger.Off : Engine.LogonTaskPolicy.Trigger.None;
    }

    /// <summary>
    /// Whether Task Scheduler's error means there is no such task: the task, or the folder it would be in, is not there
    /// (ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND, as an HRESULT; .NET may raise either as a COMException or as its own
    /// FileNotFoundException or DirectoryNotFoundException). Any other error, access denied among them, is not.
    /// </summary>
    internal static bool IsNoSuchTask(Exception ex)
    {
        if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner }) ex = inner;
        return ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003);
    }

    /// <summary>Creates the task for the account that runs PoolWatch, or deletes it.</summary>
    /// <param name="xmlPath">Where the task definition is written for schtasks /xml.</param>
    /// <param name="checkWrite">The write policy (PathGuard.CheckReal), applied right before the write and the delete.</param>
    /// <param name="on">The task is registered on (false: turned off).</param>
    /// <param name="signInTrigger">Its sign-in trigger is registered on (false: turned off).</param>
    public static (bool ok, string output) Set(bool enabled, string xmlPath, Func<string, string> checkWrite, bool on = true, bool signInTrigger = true)
    {
        if (!enabled) { var d = Schtasks($"/delete /tn {Name} /f"); return (d.code == 0, d.output); }
        // The task starts this PoolWatch (through its launcher) with highest privileges and no UAC
        // prompt, so only a copy in an admin-only install folder is ever registered (never under --allow-any-location or
        // the CI UI test).
        if (Engine.LogonTaskPolicy.WhyNotRegister(Environment.ProcessPath, Engine.RunFlags.AllowAnyLocation, Engine.RunFlags.UiSmoke,
                Engine.InstallGuard.AdminRoots()) is { } why)
            return (false, "PoolWatch did not create the logon task: " + why + ".");
        string exe = Environment.ProcessPath!;
        if (Engine.LaunchGate.LauncherOf(exe, Engine.LayoutFiles.OnDisk) is not { } launcher)
            return (false, $"PoolWatch did not create the logon task: its launcher, {Engine.LaunchGate.ExpectedLauncher(exe)}, which the task starts, is not there, or is not of this PoolWatch's version. Install PoolWatch again.");
        // /xml instead of /tr: schtasks' defaults would stop the monitor after 72 h and skip it on battery.
        string xml = checkWrite(xmlPath);
        File.WriteAllText(xml, TaskXml(launcher, System.Security.Principal.WindowsIdentity.GetCurrent().Name, on, signInTrigger), System.Text.Encoding.Unicode);
        var r = Schtasks($"/create /tn {Name} /xml \"{xml}\" /f");
        try { File.Delete(checkWrite(xml)); } catch { }
        return (r.code == 0, r.output);
    }

    /// <summary>
    /// The task for <paramref name="user"/>: at their sign-in it starts <paramref name="launcher"/> (the install folder's
    /// PoolWatch.exe, PoolWatch's launcher) with --tray, which starts PoolWatch with the environment it builds, never
    /// PoolWatch's program itself. The task and its sign-in trigger are on, or turned off (<paramref name="on"/>,
    /// <paramref name="signInTrigger"/> false).
    /// </summary>
    internal static string TaskXml(string launcher, string user, bool on = true, bool signInTrigger = true) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>PoolWatch kernel pool monitor (tray), started at logon with highest privileges.</Description></RegistrationInfo>
          <Triggers><LogonTrigger><Enabled>{(signInTrigger ? "true" : "false")}</Enabled><UserId>{System.Security.SecurityElement.Escape(user)}</UserId></LogonTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>{System.Security.SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <Enabled>{(on ? "true" : "false")}</Enabled>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author"><Exec><Command>{System.Security.SecurityElement.Escape(launcher)}</Command><Arguments>--tray</Arguments></Exec></Actions>
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
/// <param name="paths">The data folder, where the task's definition is written for schtasks; asked for only when a task
/// is registered (removing one needs no data folder, so it works when that folder cannot be used).</param>
internal sealed class WindowsLogonTasks(Func<Engine.AppPaths> paths) : Engine.ILogonTasks
{
    public WindowsLogonTasks(Engine.AppPaths paths) : this(() => paths) { }

    public Engine.LogonTaskPolicy.ExistingTask? Read() => LogonTask.Read();

    /// <summary>The reason when it did not, any failure included (the data folder, writing the definition): never thrown, so the owner's answer is always reported.</summary>
    public string? RegisterCurrent(bool on = true, bool signInTrigger = true)
    {
        try
        {
            var p = paths();
            var (ok, output) = LogonTask.Set(true, Path.Combine(p.DataRoot, "logon-task.xml"), p.Guard.CheckReal, on, signInTrigger);
            return ok ? null : output;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public string? Remove()
    {
        var (ok, output) = LogonTask.Set(false, "", p => p);
        return ok ? null : output;
    }

    /// <summary>The account this PoolWatch runs as, the one it registers the task for (<see cref="LogonTask.IsThisAccount"/>).</summary>
    public bool IsThisAccount(string? userId) => LogonTask.IsThisAccount(userId);
}
