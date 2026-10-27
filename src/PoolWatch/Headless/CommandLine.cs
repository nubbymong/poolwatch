using System.Globalization;

namespace PoolWatch.Headless;

/// <summary>Command-line options. Unknown options are an error so CI typos fail loudly.</summary>
internal sealed class CommandLineOptions
{
    public bool Headless { get; private set; }
    public bool SelfTest { get; private set; }
    public double? DurationSeconds { get; private set; }
    public string? OutDir { get; private set; }
    public string? ConfigFile { get; private set; }
    public double? IntervalSeconds { get; private set; }
    public bool VerboseCsv { get; private set; }
    public bool Tray { get; private set; }
    public double? UiSmokeSeconds { get; private set; }
    public string? Mark { get; private set; }
    public string? ExperimentApp { get; private set; }
    public double? PhaseMinutes { get; private set; }
    public bool ExportBundle { get; private set; }
    public bool NoRedact { get; private set; }
    /// <summary>"remove" (the old SYSTEM tasks a previous build installed), "enable-auto" or "remove-legacy" (a clear task
    /// of your own: RAMMap in a scheduled task). S5 (review round 2): there is no setup any more.</summary>
    public string? CacheSetup { get; private set; }
    /// <summary>The answer to "remove your own clear task?" given on the command line (else the step asks in a dialog).</summary>
    public bool? LegacyAnswer { get; private set; }
    /// <summary>The hidden leak-test helper: <c>--token-leak-test n file</c> (started by the token-leak guard).</summary>
    public (int n, string file)? TokenLeakTest { get; private set; }
    /// <summary>M3 (review S2): the PID of the PoolWatch that started the helper, so the helper can see it die mid-test.</summary>
    public int? TokenTestParent { get; private set; }
    /// <summary>Headless: run the token-leak guard's logon check too.</summary>
    public bool TokenGuard { get; private set; }
    /// <summary>Background services (addendum 4): dump the enumerated services to this file, then exit.</summary>
    public string? ServicesDump { get; private set; }
    /// <summary>Turn a catalogue/unknown service off (gated), then exit.</summary>
    public string? ServiceOff { get; private set; }
    /// <summary>Restore a service PoolWatch changed (or "all"), then exit.</summary>
    public string? ServiceRestore { get; private set; }
    /// <summary>Dump the non-Microsoft scheduled tasks and startup entries to this file, then exit.</summary>
    public string? TasksDump { get; private set; }
    /// <summary>Headless: clear the standby cache once after the first sample ("standby" or "lowpriority").</summary>
    public string? ClearCache { get; private set; }
    /// <summary>Headless: a "Capture now" (pool + CPU) this many seconds after start.</summary>
    public double? CaptureNowAfter { get; private set; }
    /// <summary>S4: run elevated from a folder that is not administrator-only (CI and development only: the build output).</summary>
    public bool AllowAnyLocation { get; private set; }
    public List<string> Errors { get; } = [];

    public static string Usage => """
        PoolWatch.exe                       tray app (opens the window)
        PoolWatch.exe --tray                tray app without opening the window (used by the logon task)
        PoolWatch.exe --ui-smoke <seconds> --out <dir>  open the UI, save screenshots of each tab, exit (CI)
        PoolWatch.exe --headless [options]  collect without UI
          --duration <seconds>   stop after this long (default: until the stop file or Ctrl+C)
          --out <dir>            write CSV / incidents / logs here instead of %ProgramData%\PoolWatch
          --config <file>        settings JSON to use (read-only)
          --interval <seconds>   override the sample interval
          --verbose-csv          write every tag and process row every sample
          --experiment <app>     run a guided A/B experiment with this app (prompts in the log and findings.jsonl)
          --phase-minutes <m>    experiment phase length (default: the setting, 10 min)
          --export-bundle        write a vendor bundle zip at the end (redacted; add --no-redact to keep names)
        PoolWatch.exe --mark "<note>" [--out <dir>]  put a note on the running PoolWatch's timeline, then exit
          --clear-cache <mode>   clear the standby cache once after start (standby | lowpriority; in-process, elevated)
          --capture-now <seconds>  a Capture now (pool and CPU trace) this long after start
          --token-guard          also run the token-leak guard's logon check (as the tray app does at logon)
        PoolWatch.exe --services-dump <file>   write the installed services, grouped, to <file>
        PoolWatch.exe --service-off <name>     (elevated) turn an optional/third-party service off (gated)
        PoolWatch.exe --service-restore <name|all>  (elevated) restore a service PoolWatch changed
        PoolWatch.exe --tasks-dump <file>      write the non-Microsoft scheduled tasks and startup entries to <file>
        PoolWatch.exe --remove-clear-cache   (elevated) remove the two "\PoolWatch\Clear cache" SYSTEM tasks and the helper
                                             files an older PoolWatch installed (Clear cache runs in-process now)
        PoolWatch.exe --enable-auto-clear [--yes|--no]  (elevated) find your own clear task (a task in the Task Scheduler
                                             root that runs RAMMap with an -E option), show it, remove it on a yes
                                             (exit 4: kept, so the automatic clear stays off)
        PoolWatch.exe --remove-legacy-clear-task [--yes|--no]  (elevated) the same step
        PoolWatch.exe --selftest --out <dir>  native sanity checks, writes selftest.json
        Any mode: --allow-any-location      run elevated from a folder that is not administrator-only (CI / development;
                                             without it an elevated PoolWatch refuses to run outside %ProgramFiles%)
        """;

    public static CommandLineOptions Parse(string[] args)
    {
        var o = new CommandLineOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next()
            {
                if (i + 1 < args.Length) return args[++i];
                o.Errors.Add($"{a} needs a value");
                return null;
            }
            double? NextDouble()
            {
                var v = Next();
                if (v is null) return null;
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0) return d;
                o.Errors.Add($"{a}: '{v}' is not a positive number");
                return null;
            }
            switch (a.ToLowerInvariant())
            {
                case "--headless": o.Headless = true; break;
                case "--selftest": o.SelfTest = true; break;
                case "--duration": o.DurationSeconds = NextDouble(); break;
                case "--out": o.OutDir = Next(); break;
                case "--config": o.ConfigFile = Next(); break;
                case "--interval": o.IntervalSeconds = NextDouble(); break;
                case "--verbose-csv": o.VerboseCsv = true; break;
                case "--tray": o.Tray = true; break;
                case "--ui-smoke": o.UiSmokeSeconds = NextDouble(); break;
                case "--mark": o.Mark = Next(); break;
                case "--experiment": o.ExperimentApp = Next(); break;
                case "--phase-minutes": o.PhaseMinutes = NextDouble(); break;
                case "--export-bundle": o.ExportBundle = true; break;
                case "--no-redact": o.NoRedact = true; break;
                case "--enable-auto-clear": o.CacheSetup = "enable-auto"; break;
                case "--remove-clear-cache": o.CacheSetup = "remove"; break;
                case "--remove-legacy-clear-task": o.CacheSetup = "remove-legacy"; break;
                case "--token-leak-test":
                    {
                        string? n = Next(), f = Next();
                        if (n is not null && f is not null && int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count is > 0 and <= 1000)
                            o.TokenLeakTest = (count, f);
                        else o.Errors.Add("--token-leak-test: <children 1-1000> <result file>");
                        break;
                    }
                case "--token-test-parent": if (int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pp) && pp > 0) o.TokenTestParent = pp; break;
                case "--token-guard": o.TokenGuard = true; break;
                case "--services-dump": o.ServicesDump = Next(); break;
                case "--service-off": o.ServiceOff = Next(); break;
                case "--service-restore": o.ServiceRestore = Next(); break;
                case "--tasks-dump": o.TasksDump = Next(); break;
                case "--yes": o.LegacyAnswer = true; break;
                case "--no": o.LegacyAnswer = false; break;
                case "--capture-now": o.CaptureNowAfter = NextDouble(); break;
                case "--allow-any-location": o.AllowAnyLocation = true; break;
                case "--clear-cache":
                    o.ClearCache = Next();
                    if (o.ClearCache is not null and not ("standby" or "lowpriority")) o.Errors.Add("--clear-cache: standby or lowpriority");
                    break;
                default: o.Errors.Add($"unknown option '{a}'"); break;
            }
        }
        return o;
    }
}
