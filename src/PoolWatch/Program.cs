using PoolWatch.Headless;
using PoolWatch.Native;

namespace PoolWatch;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Core.Util.Profiler.ThreadCpuTicks = NativeMethods.CurrentThreadCpuTicks;
        var opt = CommandLineOptions.Parse(args);
        bool consoleMode = opt.Headless || opt.SelfTest || opt.UiSmokeSeconds is not null || opt.CacheSetup is not null || opt.ServicesDump is not null ||
                           opt.ServiceOff is not null || opt.ServiceRestore is not null || opt.TasksDump is not null || opt.Mark is not null || opt.Errors.Count > 0;
        if (consoleMode) NativeMethods.AttachConsole(-1); // ATTACH_PARENT_PROCESS: show output when started from a console
        Engine.RunFlags.AllowAnyLocation = opt.AllowAnyLocation; // S4 (review round 3): never registered as the logon task then
        Engine.RunFlags.UiSmoke = opt.UiSmokeSeconds is not null;
        if (opt.Errors.Count > 0)
        {
            Console.Error.WriteLine(string.Join(Environment.NewLine, opt.Errors));
            Console.Error.WriteLine(CommandLineOptions.Usage);
            return 2;
        }
        try
        {
            // S4 (review, round 2): an elevated PoolWatch runs only from an administrator-only folder, in every mode that does
            // work: the tray app, headless, the self-test and the services / tasks / mark / clear-cache steps (an unelevated
            // user could swap an exe in a folder they can write to). Exempt: the CI UI smoke (--ui-smoke), the token guard's
            // hidden helper (started by a PoolWatch that passed this check, with its own path), and an explicit
            // --allow-any-location (CI and development: the build output is not in Program Files).
            if (Engine.InstallGuard.Applies(opt) &&
                Engine.InstallGuard.RefuseElevatedFromUserWritable(out string where))
            {
                if (!consoleMode) MessageBox.Show(where, "PoolWatch: install to an administrator-only folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Console.Error.WriteLine(where);
                return 5;
            }
            if (opt.SelfTest) return SelfTest.Run(opt);
            if (opt.Mark is not null) return MarkRequest(opt);
            if (opt.CacheSetup is { } what) return CacheSetup(what, opt);
            // The token-leak guard's hidden helper: before the one-instance mutex (the running PoolWatch started it).
            if (opt.TokenLeakTest is { } tl) return Engine.TokenLeakGuard.RunHelperMode(tl.n, tl.file, new Engine.AppPaths(opt.OutDir), opt.TokenTestParent);
            if (opt.ServicesDump is not null || opt.ServiceOff is not null || opt.ServiceRestore is not null) return Services(opt);
            if (opt.TasksDump is { } td) { var p = new Engine.AppPaths(opt.OutDir); new Engine.TasksAndStartup(p.OpenLog()).Dump(td, p.Guard); Console.WriteLine("tasks written: " + td); return 0; }
            // One monitor per machine: two would fight over the ETW session and double the overhead.
            using var mutex = new Mutex(true, @"Global\PoolWatch-Monitor", out bool first);
            if (!first)
            {
                if (opt.Headless) { Console.Error.WriteLine("PoolWatch is already running."); return 3; }
                MessageBox.Show("PoolWatch is already running (see the tray).", "PoolWatch");
                return 3;
            }
            if (opt.Headless) return HeadlessRunner.Run(opt);
            return Ui.UiRunner.Run(opt);
        }
        catch (Engine.DataRootRefusedException ex)
        {
            // S3 (review round 3): the data folder could not be made safe (moved aside, created or secured): nothing was written.
            if (!consoleMode) MessageBox.Show(ex.Message, "PoolWatch: data folder not safe to use", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Console.Error.WriteLine(ex.Message);
            return 6;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PoolWatch failed: " + ex);
            return 1;
        }
    }

    /// <summary><c>--mark "note"</c>: drops a request for the running instance (it reads it on its next sample).</summary>
    private static int MarkRequest(CommandLineOptions opt)
    {
        string note = Core.Experiments.Marks.Clean(opt.Mark!);
        if (note.Length == 0) { Console.Error.WriteLine("--mark needs a note"); return 2; }
        var paths = new Engine.AppPaths(opt.OutDir);
        string dir = paths.EnsureDir(paths.RequestsDir);
        string path = paths.Guard.CheckReal(Path.Combine(dir, Core.Experiments.Marks.RequestName(DateTime.UtcNow, Environment.ProcessId)));
        File.WriteAllText(paths.Guard.CheckReal(path + ".tmp"), note);
        File.Move(paths.Guard.CheckReal(path + ".tmp"), path);
        Console.WriteLine("Mark queued: " + note);
        return 0;
    }

    /// <summary>Exit 0: no clear task of your own (any more); 4: you kept it, so the automatic clear must stay off.</summary>
    private static int Legacy(CommandLineOptions opt)
    {
        var (outcome, tasks) = Engine.CacheHelper.ResolveLegacy(t => opt.LegacyAnswer ??
            MessageBox.Show(Engine.CacheHelper.LegacyQuestion(t), "PoolWatch: your own clear task", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);
        Console.WriteLine(outcome switch
        {
            Engine.CacheHelper.LegacyOutcome.Removed => "Removed your clear task (" + Engine.CacheHelper.LegacyNames(tasks) + "); what it ran is left in place.",
            Engine.CacheHelper.LegacyOutcome.Kept => "Kept your clear task (" + Engine.CacheHelper.LegacyNames(tasks) + "): the automatic clear stays off.",
            _ => "No clear task of your own found.",
        });
        return outcome == Engine.CacheHelper.LegacyOutcome.Kept ? 4 : 0;
    }

    /// <summary>Background services (addendum 4): dump / turn off / restore from the command line.</summary>
    private static int Services(CommandLineOptions opt)
    {
        try
        {
            var paths = new Engine.AppPaths(opt.OutDir);
            var log = paths.OpenLog();
            // No sampler runs here: process costs are not known (the Cost column and "saved" need the running app).
            var ctl = new Engine.ServicesController(paths, log, f => Console.WriteLine(f.Title), _ => null, _ => null);
            if (opt.ServicesDump is { } file) { ctl.Dump(file); Console.WriteLine("services written: " + file); }
            if (opt.ServiceOff is { } off) foreach (var (s, e) in ctl.Apply([off], "command line")) Console.WriteLine(e is null ? $"{s}: off" : $"{s}: {e}");
            if (opt.ServiceRestore is { } r) foreach (var (s, e) in ctl.Restore(r.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : r)) Console.WriteLine(e is null ? $"{s}: restored" : $"{s}: {e}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("services failed: " + ex.Message); return 1; }
    }

    /// <summary>
    /// The Clear cache steps from an elevated console. S5 (review round 2): there is nothing to set up any more (the purge is
    /// in-process); "remove" takes away what a previous build's setup installed (the two \PoolWatch\ SYSTEM tasks and the
    /// helper files), and the automatic-clear step only resolves a clear task of your own (RAMMap in a scheduled task).
    /// </summary>
    private static int CacheSetup(string what, CommandLineOptions opt)
    {
        try
        {
            switch (what)
            {
                case "remove":
                    var removed = Engine.CacheHelper.RemoveOldTasks();
                    Console.WriteLine(removed.Count == 0 ? "No old Clear cache tasks or helper files found." : "Removed: " + string.Join("; ", removed));
                    return 0;
                case "enable-auto": // turning the automatic clear on: only a clear task of your own (removed on a yes)
                case "remove-legacy": return Legacy(opt);
            }
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Clear cache step failed: " + ex.Message);
            return 1;
        }
    }
}
