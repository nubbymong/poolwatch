using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PoolWatch.Ui;

/// <summary>Tray icons drawn at runtime (no image assets): a coloured disc per state.</summary>
internal static class TrayIcons
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private static readonly Dictionary<TrayState, Icon> Cache = new();

    public static Icon For(TrayState state)
    {
        if (Cache.TryGetValue(state, out var icon)) return icon;
        var color = state switch { TrayState.Red => Color.Crimson, TrayState.Amber => Color.Orange, _ => Color.SeaGreen };
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(color);
            g.FillEllipse(b, 2, 2, 28, 28);
            using var pen = new Pen(Color.White, 3);
            g.DrawLine(pen, 9, 20, 14, 14); // a small rising line: "pool"
            g.DrawLine(pen, 14, 14, 18, 18);
            g.DrawLine(pen, 18, 18, 24, 10);
        }
        IntPtr h = bmp.GetHicon();
        icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        Cache[state] = icon;
        return icon;
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
