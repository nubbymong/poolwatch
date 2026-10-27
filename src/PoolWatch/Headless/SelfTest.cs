using System.Diagnostics;
using System.Text.Json;
using PoolWatch.Collectors;
using PoolWatch.Core;
using PoolWatch.Core.Model;
using PoolWatch.Engine;
using PoolWatch.Native;

namespace PoolWatch.Headless;

/// <summary>
/// Native sanity checks run on the Windows CI runner: every collector's numbers are compared with an
/// independent source (System.Diagnostics.Process, GetProcessIoCounters, our own known file handles).
/// </summary>
internal static class SelfTest
{
    private sealed record Check(string Name, bool Pass, string Detail);

    public static int Run(CommandLineOptions opt)
    {
        var paths = new AppPaths(opt.OutDir);
        var log = paths.OpenLog();
        log.EchoToConsole = true;
        var checks = new List<Check>();
        void Add(string name, bool pass, string detail)
        {
            checks.Add(new Check(name, pass, detail));
            log.Info($"[{(pass ? "PASS" : "FAIL")}] {name}: {detail}");
        }
        void Try(string name, Action body)
        {
            try { body(); }
            catch (Exception ex) { Add(name, false, "exception: " + ex.Message); }
        }

        using var q = new SystemQueries();
        using var self = Process.GetCurrentProcess();

        Try("pool tags", () =>
        {
            var tags = q.QueryPoolTags();
            Add("pool tag count > 100", tags.Count > 100, $"{tags.Count} tags");
            var file = tags.FirstOrDefault(t => t.Tag == PoolTag.Parse("File"));
            Add("File tag present and non-zero", file.TotalUsed > 0, $"File paged={file.PagedUsed} nonpaged={file.NonPagedUsed} allocs={file.PagedAllocs + file.NonPagedAllocs}");
            Add("tags unique by raw bytes", tags.Select(t => t.Tag.Raw).Distinct().Count() == tags.Count, "ordinal keys");
            var (cp, cnp) = SystemQueries.QueryCommittedPool();
            ulong sp = (ulong)tags.Sum(t => (long)t.PagedUsed), snp = (ulong)tags.Sum(t => (long)t.NonPagedUsed);
            Add("committed pool totals non-zero", cp > 0 && cnp > 0, $"committed paged={cp / 1048576} MB nonpaged={cnp / 1048576} MB");
            double rp = (double)sp / cp, rnp = (double)snp / cnp;
            Add("tag sums within 0.2-1.5x of committed", rp is > 0.2 and < 1.5 && rnp is > 0.2 and < 1.5,
                $"tag-sum paged={sp / 1048576} MB ({rp:P0}), nonpaged={snp / 1048576} MB ({rnp:P0})");
        });

        Try("processes", () =>
        {
            var procs = q.QueryProcesses();
            Add("process count > 20", procs.Count > 20, $"{procs.Count} processes");
            Add("System (PID 4) present", procs.Any(p => p.Pid == 4), procs.FirstOrDefault(p => p.Pid == 4)?.ImageName ?? "missing");
            var me = procs.Single(p => p.Pid == Environment.ProcessId);
            // The exe may be renamed (PoolWatch-selfcontained.exe), so compare with the real path.
            string ownPath = Environment.ProcessPath ?? "PoolWatch.exe";
            Add("own image name", me.ImageName.Equals(Path.GetFileName(ownPath), StringComparison.OrdinalIgnoreCase), me.ImageName);
            long expectedCreate = self.StartTime.ToUniversalTime().ToFileTimeUtc();
            Add("own create time matches Process.StartTime", Math.Abs(me.CreateTime - expectedCreate) < TimeSpan.TicksPerSecond, $"{me.CreateTime} vs {expectedCreate}");
            self.Refresh();
            Add("own handle count close to Process.HandleCount", Math.Abs(me.HandleCount - self.HandleCount) <= Math.Max(50, self.HandleCount / 5), $"{me.HandleCount} vs {self.HandleCount}");
            Add("own thread count close", Math.Abs(me.ThreadCount - self.Threads.Count) <= 10, $"{me.ThreadCount} vs {self.Threads.Count}");
            NativeMethods.GetProcessIoCounters(NativeMethods.GetCurrentProcess(), out var io);
            Add("own read ops close to GetProcessIoCounters", (ulong)me.ReadOperationCount <= io.ReadOperationCount && io.ReadOperationCount - (ulong)me.ReadOperationCount < 1000,
                $"{me.ReadOperationCount} vs {io.ReadOperationCount}");
            Add("own private bytes plausible", me.PrivateBytes > 1_000_000 && me.PrivateBytes < 2_000_000_000, $"{me.PrivateBytes / 1048576} MB");
            Add("parent PID present", me.ParentPid > 0, $"parent {me.ParentPid}");
            var reg = new ProcessRegistry { Enricher = ProcessDetails.Enrich };
            reg.ObserveSnapshot(procs, DateTime.UtcNow);
            var rec = reg.Get(me.Key)!;
            Add("own image path resolved", string.Equals(rec.ImagePath, ownPath, StringComparison.OrdinalIgnoreCase), rec.ImagePath ?? "null");
            Add("own command line contains --selftest", rec.CommandLine?.Contains("--selftest", StringComparison.OrdinalIgnoreCase) == true, rec.CommandLine ?? "null");
            Add("own user resolved", !string.IsNullOrEmpty(rec.User), rec.User ?? "null");
            Add("ancestry has parent", reg.Ancestry(me.Key).Count >= 2, string.Join(" > ", reg.Ancestry(me.Key).Select(r => r.Image)));
        });

        Try("handles + direct charge", () =>
        {
            var types = q.ObjectTypeNames();
            Add("object types include File/Event/Key/Token", new[] { "File", "Event", "Key", "Token" }.All(types.Values.Contains), $"{types.Count} types");
            var before = q.QueryHandles();
            var tagsBefore = q.QueryPoolTags();
            var meBefore = q.QueryProcesses().Single(p => p.Pid == Environment.ProcessId);
            int fileIdx = types.First(kv => kv.Value == "File").Key;
            var dir = paths.EnsureDir(Path.Combine(paths.OutDir ?? paths.DataRoot, "selftest-files"));
            var streams = new List<FileStream>();
            try
            {
                for (int i = 0; i < 2000; i++)
                    streams.Add(new FileStream(Path.Combine(dir, $"f{i}.tmp"), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
                var after = q.QueryHandles();
                var tagsAfter = q.QueryPoolTags();
                var meAfter = q.QueryProcesses().Single(p => p.Pid == Environment.ProcessId);
                int dFile = after.counts.Count(Environment.ProcessId, fileIdx) - before.counts.Count(Environment.ProcessId, fileIdx);
                Add("own File handles +2000 seen by handle scan", dFile >= 1900, $"+{dFile} File handles; scan {after.elapsed.TotalMilliseconds:0} ms, {after.counts.TotalHandles} handles system-wide");
                ulong Used(List<PoolTagEntry> l) => l.FirstOrDefault(t => t.Tag == PoolTag.Parse("File")).TotalUsed;
                long dTag = (long)Used(tagsAfter) - (long)Used(tagsBefore);
                Add("File tag grew with 2000 open files", dTag > 2000 * 100, $"File tag +{dTag / 1024} KB");
                long dQuota = (long)(meAfter.QuotaNonPagedPoolUsage + meAfter.QuotaPagedPoolUsage) - (long)(meBefore.QuotaNonPagedPoolUsage + meBefore.QuotaPagedPoolUsage);
                Add("own quota pool usage grew", dQuota > 0, $"quota paged+nonpaged +{dQuota / 1024} KB");
                Add("own handle count grew in process snapshot", meAfter.HandleCount - meBefore.HandleCount >= 1900, $"+{meAfter.HandleCount - meBefore.HandleCount}");
            }
            finally
            {
                foreach (var s in streams) s.Dispose();
                try { Directory.Delete(paths.Guard.CheckReal(dir), true); } catch { }
            }
        });

        Try("PEB cwd + environment (AICC session context)", () =>
        {
            var key = new ProcessKey(Environment.ProcessId, self.StartTime.ToUniversalTime().ToFileTimeUtc());
            var r = PebReader.Read(key.Pid, 0, ["USERPROFILE", "PATH"]);
            Add("PEB readable for own process", r is not null, r is null ? "null" : "ok");
            if (r is { } x)
            {
                string expectedCwd = Environment.CurrentDirectory.TrimEnd('\\');
                Add("PEB cwd matches Environment.CurrentDirectory", string.Equals(x.cwd?.TrimEnd('\\'), expectedCwd, StringComparison.OrdinalIgnoreCase), $"{x.cwd} vs {expectedCwd}");
                Add("PEB USERPROFILE matches", x.env.GetValueOrDefault("USERPROFILE") == Environment.GetEnvironmentVariable("USERPROFILE"), x.env.GetValueOrDefault("USERPROFILE") ?? "missing");
            }
            // A child process: the parent's PEB reader must see the child's own cwd.
            string childDir = paths.EnsureDir(Path.Combine(paths.OutDir ?? paths.DataRoot, "selftest-cwd"));
            using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 3 127.0.0.1 >nul")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = childDir,
            })!;
            Thread.Sleep(300);
            var c = PebReader.Read(child.Id, 0, ["USERPROFILE"]);
            Add("PEB cwd of a child process", c?.cwd?.TrimEnd('\\').Equals(childDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true, c?.cwd ?? "null");
            child.WaitForExit(10_000);
        });

        Try("services", () =>
        {
            var services = ServiceCollector.Query();
            Add("running services > 20", services.Count > 20, $"{services.Count} services");
            var hosts = services.Where(s => s.ProcessId > 0).Select(s => s.ProcessId).Distinct().ToList();
            var procs = q.QueryProcesses().ToDictionary(p => p.Pid);
            int svchost = hosts.Count(pid => procs.TryGetValue(pid, out var p) && p.ImageName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase));
            Add("services hosted in svchost", svchost > 5, $"{svchost} svchost hosts");
        });

        Try("volumes", () =>
        {
            var vols = VolumeCollector.Query();
            var c = vols.FirstOrDefault(v => v.Root.StartsWith("C", StringComparison.OrdinalIgnoreCase));
            Add("C: is NTFS", c?.FileSystem == "NTFS", string.Join("; ", vols.Select(v => $"{v.Root} {v.FileSystem} devdrive={v.IsDevDrive?.ToString() ?? "n/a"} {v.DriveType}")));
            bool? dev = VolumeCollector.QueryDevDrive(@"C:\", out var devDetail);
            Add("Dev Drive query answered for C:", dev is not null, $"C: devdrive={dev?.ToString() ?? "unknown"} ({devDetail})");
        });

        Try("etw process events", () =>
        {
            var reg = new ProcessRegistry { Enricher = ProcessDetails.Enrich };
            reg.ObserveSnapshot(q.QueryProcesses(), DateTime.UtcNow);
            using var etw = new ProcessEtwCollector(reg, log.Info, $"PoolWatch-SelfTest-{Environment.ProcessId}");
            etw.Start();
            Thread.Sleep(3000); // session start-up
            // From System32 by its full path: a bare "cmd.exe" would be looked up in the current directory too, which an
            // elevated process must never trust. Every exe PoolWatch starts is named by its full path for the same reason
            // (wpr.exe, fltmc.exe, schtasks.exe, cmd.exe from %SystemRoot%\System32; explorer.exe from %SystemRoot%).
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 2 127.0.0.1 >nul & exit 7") { UseShellExecute = false, CreateNoWindow = true };
            using var child = Process.Start(psi)!;
            child.WaitForExit();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            List<LifecycleEvent> seen = [];
            while (DateTime.UtcNow < deadline)
            {
                seen.AddRange(reg.DrainEvents());
                if (seen.Any(e => e.Kind == LifecycleKind.Exit && e.Pid == child.Id)) break;
                Thread.Sleep(250);
            }
            var start = seen.FirstOrDefault(e => e.Kind == LifecycleKind.Start && e.Pid == child.Id);
            var exit = seen.FirstOrDefault(e => e.Kind == LifecycleKind.Exit && e.Pid == child.Id);
            Add("ETW session running", etw.Running && etw.EventsSeen > 0, etw.Error ?? $"{etw.EventsSeen} events, mode {etw.Mode}, lost {etw.EventsLost}");
            Add("ETW start event for child", start is not null, start is null ? "missing" : $"{start.Image} parent={start.ParentPid}");
            Add("child command line captured", start?.CommandLine?.Contains("exit 7") == true || reg.Get(start?.Key ?? default)?.CommandLine?.Contains("exit 7") == true,
                reg.Get(start?.Key ?? default)?.CommandLine ?? "null");
            Add("ETW exit event with exit code 7", exit?.ExitCode == 7, exit is null ? "missing" : $"exit code {exit.ExitCode}");
            Add("child parent resolves to PoolWatch", start is not null && reg.Get(start.Key)?.ParentKey?.Pid == Environment.ProcessId, $"parent key {reg.Get(start?.Key ?? default)?.ParentKey}");
        });

        string outRoot = paths.OutDir ?? paths.DataRoot;
        File.WriteAllText(paths.Guard.CheckReal(Path.Combine(outRoot, "selftest.json")),
            JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        int failed = checks.Count(c => !c.Pass);
        log.Info($"selftest: {checks.Count - failed}/{checks.Count} passed");
        return failed == 0 ? 0 : 1;
    }
}
