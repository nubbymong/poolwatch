using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PoolWatch.Core;
using PoolWatch.Native;

namespace PoolWatch.Collectors;

/// <summary>Read-only per-process lookups: full image path, command line, user. Opens with limited query rights.</summary>
internal static unsafe class ProcessDetails
{
    private static readonly ConcurrentDictionary<string, string> SidNames = new();

    /// <summary>
    /// Set on the ETW callback thread. Account-name lookups can block on a domain controller, so there the SID
    /// string is returned and the name is resolved in the background for later lookups.
    /// </summary>
    [ThreadStatic] public static bool OnEtwThread;
    private static readonly ConcurrentDictionary<string, byte> PendingSids = new();

    public static void Enrich(ProcessRecord rec)
    {
        using var stage = Core.Util.Profiler.Stage(OnEtwThread ? "enrich (etw thread)" : "enrich (sampler)");
        if (rec.Pid is 0 or 4) { rec.ImagePath ??= rec.Image; return; }
        using var h = SafeProcess.Open(rec.Pid, NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION);
        if (h is null) return;
        // Guard against PID reuse: the handle must refer to the same instance.
        if (NativeMethods.GetProcessTimes(h.Handle, out long created, out _, out _, out _) && rec.Key.CreateTime != 0 && created != rec.Key.CreateTime)
            return;
        rec.ImagePath ??= GetImagePath(h.Handle);
        rec.CommandLine ??= GetCommandLine(h.Handle);
        rec.User ??= GetUser(h.Handle);
        // No environment reads here: a busy PC starts hundreds of shells and git calls a minute. The account profile is
        // read on demand (ReadSessionContext) when a suspect's session is described. The one exception is git.exe without
        // -C / --work-tree: its working directory (cwd only, no environment block) decides the repository root, and it is
        // gone by the next sample. Rate-limited.
        if (rec.Image.Equals("git.exe", StringComparison.OrdinalIgnoreCase) && rec.CommandLine is { } cl &&
            !cl.Contains(" -C ", StringComparison.Ordinal) && !cl.Contains("--work-tree", StringComparison.Ordinal) && GitCwdBudget.TryTake())
        {
            using var gitStage = Core.Util.Profiler.Stage("enrich.gitCwd");
            if (PebReader.Read(rec.Pid, rec.Key.CreateTime, []) is { cwd: { } cwd })
                lock (rec.Context) rec.Context[Core.Aicc.SessionContext.CwdKey] = cwd;
        }
    }

    /// <summary>At most 20 git cwd reads per second.</summary>
    private static class GitCwdBudget
    {
        private static long _second, _used;
        public static bool TryTake()
        {
            long now = Environment.TickCount64 / 1000;
            if (Interlocked.Exchange(ref _second, now) != now) Interlocked.Exchange(ref _used, 0);
            return Interlocked.Increment(ref _used) <= 20;
        }
    }

    private static readonly string[] SessionEnv = ["CLAUDE_CONFIG_DIR", "USERPROFILE"];
    private const string PebTriedKey = "peb.failed";

    /// <summary>Reads cwd + CLAUDE_CONFIG_DIR / USERPROFILE from the PEB into <see cref="ProcessRecord.Context"/>.</summary>
    public static void ReadSessionContext(ProcessRecord rec)
    {
        lock (rec.Context)
            if (rec.Context.ContainsKey(Core.Aicc.SessionContext.UserProfileKey) || rec.Context.ContainsKey(PebTriedKey)) return;
        using var stage = Core.Util.Profiler.Stage("peb (session, on demand)");
        if (PebReader.Read(rec.Pid, rec.Key.CreateTime, SessionEnv) is not { } r)
        {
            lock (rec.Context) rec.Context[PebTriedKey] = "1"; // protected or gone: do not retry on every report rewrite
            return;
        }
        lock (rec.Context)
        {
            if (r.cwd is not null) rec.Context[Core.Aicc.SessionContext.CwdKey] = r.cwd;
            foreach (var (k, v) in r.env) rec.Context["env." + k.ToUpperInvariant()] = v;
        }
    }

    public static string? GetImagePath(IntPtr process)
    {
        char* buf = stackalloc char[1024];
        int size = 1024;
        return NativeMethods.QueryFullProcessImageNameW(process, 0, buf, ref size) ? new string(buf, 0, size) : null;
    }

    public static string? GetCommandLine(IntPtr process)
    {
        int size = 4096;
        for (int i = 0; i < 4; i++)
        {
            var mem = NativeMemory.Alloc((nuint)size);
            try
            {
                int st = NativeMethods.NtQueryInformationProcess(process, NativeMethods.ProcessCommandLineInformation, mem, size, out int ret);
                // A process may tokenise its own command line in place: NULs become spaces (they break CSV / Markdown).
                if (st == NtStatus.Success) return NativeMethods.ReadUnicodeString((NativeMethods.UNICODE_STRING*)mem)?.Replace('\0', ' ');
                if (!NtStatus.IsSizeError(st)) return null;
                size = Math.Max(ret, size * 2);
            }
            finally { NativeMemory.Free(mem); }
        }
        return null;
    }

    public static string? GetUser(IntPtr process)
    {
        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out var token)) return null;
        try
        {
            byte* buf = stackalloc byte[256];
            if (!NativeMethods.GetTokenInformation(token, 1 /* TokenUser */, buf, 256, out _)) return null;
            IntPtr sid = *(IntPtr*)buf; // TOKEN_USER.User.Sid
            string sidKey;
            try { sidKey = new System.Security.Principal.SecurityIdentifier(sid).Value; }
            catch { return null; }
            if (SidNames.TryGetValue(sidKey, out var cached)) return cached;
            if (OnEtwThread)
            {
                if (PendingSids.TryAdd(sidKey, 0)) ThreadPool.QueueUserWorkItem(_ => ResolveSidString(sidKey));
                return sidKey;
            }
            return SidNames.GetOrAdd(sidKey, _ => LookupSid(sid) ?? sidKey);
        }
        finally { NativeMethods.CloseHandle(token); }
    }

    /// <summary>
    /// Account name for a user recorded as a SID string on the ETW thread (resolved and cached since), else the
    /// value as given. Used wherever a user is written out.
    /// </summary>
    public static string DisplayUser(string user) => SidNames.TryGetValue(user, out var n) ? n : user;

    private static void ResolveSidString(string sidKey)
    {
        try
        {
            var sid = new System.Security.Principal.SecurityIdentifier(sidKey);
            var bin = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bin, 0);
            fixed (byte* p = bin) SidNames.TryAdd(sidKey, LookupSid((IntPtr)p) ?? sidKey);
        }
        catch { }
        finally { PendingSids.TryRemove(sidKey, out _); }
    }

    private static string? LookupSid(IntPtr sid)
    {
        char* name = stackalloc char[256];
        char* domain = stackalloc char[256];
        int nl = 256, dl = 256;
        if (!NativeMethods.LookupAccountSidW(null, sid, name, ref nl, domain, ref dl, out _)) return null;
        var n = new string(name, 0, nl);
        var d = new string(domain, 0, dl);
        return d.Length > 0 ? d + "\\" + n : n;
    }
}
