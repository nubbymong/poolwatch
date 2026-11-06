using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// PoolWatch's private namespace (<see cref="AdminNamespace"/>), which holds its one-instance mutex, its exit event and the
/// tray app's guard-notices mark, so
/// that only an elevated administrator or SYSTEM can create or open them. The open-or-create logic runs against a fake.
/// The tests in the second part make the real Windows calls, each under boundaries of its own (a new GUID name, mostly
/// with the test account's SID and the low integrity level, so any test account is inside them): never PoolWatch's
/// boundary and never a Global\ name. A namespace lasts only while a handle to it is open, and each test closes its own.
/// </summary>
public sealed class AdminNamespaceTests
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>A scripted stand-in for the Windows calls: each call takes the next answer and is recorded.</summary>
    private sealed class FakeNative(params (string Call, IntPtr Handle, int Error)[] answers) : INamespaceNative
    {
        private int _next;
        public List<string> Calls { get; } = [];
        public List<(NamespaceBoundary Boundary, byte[]? Sd, string Alias)> Seen { get; } = [];

        public IntPtr Create(NamespaceBoundary boundary, byte[] securityDescriptor, string alias, out int error) => Answer("create", boundary, securityDescriptor, alias, out error);

        public IntPtr Open(NamespaceBoundary boundary, string alias, out int error) => Answer("open", boundary, null, alias, out error);

        private IntPtr Answer(string call, NamespaceBoundary boundary, byte[]? sd, string alias, out int error)
        {
            Calls.Add(call);
            Seen.Add((boundary, sd, alias));
            var (expected, handle, e) = answers[_next++];
            Assert.Equal(expected, call);
            error = e;
            return handle;
        }
    }

    private static AdminNamespace OpenWith(FakeNative native) =>
        AdminNamespace.Open(native, AdminNamespace.Boundary(), new RawSecurityDescriptor(AdminNamespace.Security), AdminNamespace.Alias);

    [Fact]
    public void TheBoundaryHoldsTheAdministratorsGroupAndTheHighIntegrityLevel()
    {
        // Windows refuses to create the namespace for a token that lacks a SID of the boundary, or whose integrity level is
        // below the boundary's: so no program without administrator rights (medium integrity at most) can create it.
        var boundary = AdminNamespace.Boundary();
        Assert.Equal("PoolWatch", boundary.Name);
        Assert.Equal([Administrators], boundary.Sids);
        Assert.Equal(new SecurityIdentifier("S-1-16-12288"), boundary.IntegrityLevel); // SECURITY_MANDATORY_HIGH_RID
    }

    [Fact]
    public void OnlyAdministratorsAndSystemMayOpenTheNamespace()
    {
        var sd = new RawSecurityDescriptor(AdminNamespace.AdminOnly);
        Assert.True(sd.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)); // nothing inherited
        var aces = sd.DiscretionaryAcl!.Cast<CommonAce>().ToList();
        Assert.All(aces, a => Assert.Equal(AceQualifier.AccessAllowed, a.AceQualifier));
        Assert.Equal(new[] { Administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }.Order(), aces.Select(a => a.SecurityIdentifier).Order());
        Assert.Equal(AdminNamespace.AdminOnly, ExitSignal.AdminOnly); // the event's permissions are the same
        // The namespace's whole security has these permissions, and nothing else in its DACL.
        var whole = new RawSecurityDescriptor(AdminNamespace.Security);
        Assert.Equal(sd.DiscretionaryAcl!.Cast<CommonAce>().Select(a => (a.SecurityIdentifier, a.AccessMask, a.AceQualifier)),
            whole.DiscretionaryAcl!.Cast<CommonAce>().Select(a => (a.SecurityIdentifier, a.AccessMask, a.AceQualifier)));
        Assert.True(whole.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
    }

    [Fact]
    public void TheNamespaceIsOwnedByAdministratorsAndLabelledHigh()
    {
        // Owned by the group, never by the account that created it: an owner may read and change the permissions whatever
        // they say, and under Windows' "object creator" owner policy an administrator's own unelevated programs would be
        // that owner. The high label keeps every process below high integrity from reading, changing or using it (an
        // object with no label counts as medium, which an unelevated program is).
        var sd = new RawSecurityDescriptor(AdminNamespace.Security);
        Assert.Equal(Administrators, sd.Owner);
        var label = Assert.Single(sd.SystemAcl!.Cast<GenericAce>());
        Assert.Equal(0x11, (int)label.AceType); // SYSTEM_MANDATORY_LABEL_ACE_TYPE
        var opaque = new byte[label.BinaryLength];
        label.GetBinaryForm(opaque, 0);
        Assert.Equal(0x7, BitConverter.ToInt32(opaque, 4)); // no write up, no read up, no execute up
        Assert.Equal(AdminNamespace.HighIntegrity, new SecurityIdentifier(opaque, 8));
    }

    [Fact]
    public void ANamespaceNobodyHasIsCreatedWithTheAdminOnlySecurity()
    {
        var native = new FakeNative(("create", 42, 0));

        var names = OpenWith(native);

        Assert.Equal((IntPtr)42, names.Handle);
        Assert.Equal(["create"], native.Calls);
        var expected = new byte[new RawSecurityDescriptor(AdminNamespace.Security).BinaryLength];
        new RawSecurityDescriptor(AdminNamespace.Security).GetBinaryForm(expected, 0);
        Assert.Equal(expected, native.Seen[0].Sd);
        Assert.Equal((AdminNamespace.BoundaryName, AdminNamespace.HighIntegrity), (native.Seen[0].Boundary.Name, native.Seen[0].Boundary.IntegrityLevel));
        Assert.Equal([Administrators], native.Seen[0].Boundary.Sids);
        Assert.Equal(@"PoolWatch\Monitor", names.MonitorMutexName);
        Assert.Equal(@"PoolWatch\Exit", names.ExitEventName);
        Assert.Equal(@"PoolWatch\GuardNotices-1", names.GuardNoticesMutexName(1));
        Assert.Equal(@"PoolWatch\GuardNotices-12", names.GuardNoticesMutexName(12));
    }

    [Fact]
    public void ANamespaceAnotherPoolWatchCreatedIsOpened()
    {
        var native = new FakeNative(("create", 0, 183 /* ERROR_ALREADY_EXISTS */), ("open", 7, 0));

        var names = OpenWith(native);

        Assert.Equal((IntPtr)7, names.Handle);
        Assert.Equal(["create", "open"], native.Calls);
    }

    [Fact]
    public void ANamespaceThatWentBetweenTheTwoCallsIsCreatedAgain()
    {
        var native = new FakeNative(("create", 0, 183), ("open", 0, 2 /* not found: its last holder ended */), ("create", 9, 0));

        Assert.Equal((IntPtr)9, OpenWith(native).Handle);
        Assert.Equal(["create", "open", "create"], native.Calls);
    }

    [Fact]
    public void ANamespaceThatCannotBeCreatedIsAFailureThatSaysWhy()
    {
        // Not elevated: Windows refuses the boundary. Never taken for "another PoolWatch has it": nothing is opened.
        var native = new FakeNative(("create", 0, 5));

        var ex = Assert.Throws<AdminNamespaceException>(() => OpenWith(native));

        Assert.Equal(["create"], native.Calls);
        Assert.Contains("could not create its private names", ex.Message);
        Assert.Contains("Windows error 5", ex.Message);
        Assert.Contains("administrator", ex.Message);
    }

    [Fact]
    public void ANamespaceThatCannotBeOpenedIsAFailureAfterAFewTries()
    {
        var answers = Enumerable.Range(0, AdminNamespace.Attempts).SelectMany(_ => new[] { ("create", (IntPtr)0, 183), ("open", (IntPtr)0, 5) }).ToArray();
        var native = new FakeNative(answers);

        var ex = Assert.Throws<AdminNamespaceException>(() => OpenWith(native));

        Assert.Equal(2 * AdminNamespace.Attempts, native.Calls.Count);
        Assert.Contains("could not open its private names", ex.Message);
        Assert.Contains("Windows error 5", ex.Message);
    }

    // ---------------------------------------------------------------- the real Windows calls, under the test's own boundary

    /// <summary>A boundary nobody else has: a new GUID name, the test account's SID, the low integrity level (every test account is at least that).</summary>
    private static NamespaceBoundary TestBoundary(string name) =>
        new(name, [WindowsIdentity.GetCurrent().User!], new SecurityIdentifier("S-1-16-4096"));

    private static RawSecurityDescriptor OnlyMe => new($"D:P(A;;GA;;;{WindowsIdentity.GetCurrent().User!.Value})");

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool ClosePrivateNamespace(IntPtr handle, uint flags);

    /// <summary>
    /// The one-instance mutex and the tray app's guard-notices mark are two names of one namespace (the real Windows calls,
    /// under the test's own boundary): a headless run, which holds that namespace's mutex and no mark, leaves the orphaned
    /// helper's prompt wanted, and the mark, held as the tray app holds it, is found by its name in that namespace.
    /// </summary>
    [Fact]
    public void AHeadlessRunsMutexIsNotTheGuardNoticesMarkOfTheSameNamespace()
    {
        var names = AdminNamespace.Open(new WindowsNamespaceNative(), TestBoundary("PoolWatch-Test-" + Guid.NewGuid().ToString("N")), OnlyMe,
            "PWT" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.NotEqual(names.MonitorMutexName, names.GuardNoticesMutexName(1));
            Assert.False(TokenLeakGuard.GuardNoticesShown(() => names.GuardNoticesMutexName(1)));
            int code = ExitSignal.AsTheOneInstance(names.MonitorMutexName, () => -1,
                _ => Core.TokenGuard.GuardPolicy.HelperPromptStillNeeded(200000, TokenLeakGuard.GuardNoticesShown(() => names.GuardNoticesMutexName(1))) ? 1 : 0);
            Assert.Equal(1, code);
            using (var mark = TokenLeakGuard.HoldGuardNoticesMark(() => names.GuardNoticesMutexName(1)))
            {
                Assert.NotNull(mark);
                Assert.True(TokenLeakGuard.GuardNoticesShown(() => names.GuardNoticesMutexName(1)));
            }
            Assert.False(TokenLeakGuard.GuardNoticesShown(() => names.GuardNoticesMutexName(1)));
        }
        finally { ClosePrivateNamespace(names.Handle, 0); }
    }

    /// <summary>
    /// The guard-notices mark is one per Windows session, in the one namespace (the real Windows calls, under the test's own
    /// boundary): a tray app's mark, held for its session, is found by a helper's look for that session and not by one for
    /// another session, whose live setting that tray app's guard does not look after; each session's name is the leaf, a
    /// dash and the session's number, and none is the one-instance mutex or the exit event. The name step for this process
    /// asks for its own session.
    /// </summary>
    [Fact]
    public void EachSessionHasAGuardNoticesMarkOfItsOwnAndATrayAppInAnotherSessionDoesNotTakeAHelpersPromptBack()
    {
        var names = AdminNamespace.Open(new WindowsNamespaceNative(), TestBoundary("PoolWatch-Test-" + Guid.NewGuid().ToString("N")), OnlyMe,
            "PWT" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(names.NameOf("GuardNotices-2"), names.GuardNoticesMutexName(2));
            Assert.Equal(names.NameOf("GuardNotices-0"), names.GuardNoticesMutexName(0));
            Assert.NotEqual(names.GuardNoticesMutexName(1), names.GuardNoticesMutexName(2));
            Assert.DoesNotContain(names.GuardNoticesMutexName(2), new[] { names.MonitorMutexName, names.ExitEventName });
            using (var other = TokenLeakGuard.HoldGuardNoticesMark(() => names.GuardNoticesMutexName(2)))
            {
                Assert.NotNull(other);
                Assert.True(TokenLeakGuard.GuardNoticesShown(() => names.GuardNoticesMutexName(2)));
                // A helper in session 1: the tray app of session 2 does not take its prompt back.
                Assert.False(TokenLeakGuard.GuardNoticesShown(() => names.GuardNoticesMutexName(1)));
                Assert.True(Core.TokenGuard.GuardPolicy.HelperPromptStillNeeded(200000, TokenLeakGuard.GuardNoticesShown(() => names.GuardNoticesMutexName(1))));
                using var own = TokenLeakGuard.HoldGuardNoticesMark(() => names.GuardNoticesMutexName(1));
                Assert.False(Core.TokenGuard.GuardPolicy.HelperPromptStillNeeded(200000, TokenLeakGuard.GuardNoticesShown(() => names.GuardNoticesMutexName(1))));
            }
        }
        finally { ClosePrivateNamespace(names.Handle, 0); }
        // This process's session, as Windows gives it.
        Assert.True(ProcessIdToSessionId((uint)Environment.ProcessId, out uint session));
        Assert.Equal((int)session, TokenLeakGuard.OwnSession());
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [Fact]
    public void TheWindowsCallsMakeOneNamespaceThatEveryAliasReaches()
    {
        string boundaryName = "PoolWatch-Test-" + Guid.NewGuid().ToString("N");
        string first = "PWT" + Guid.NewGuid().ToString("N"), second = "PWT" + Guid.NewGuid().ToString("N"), other = "PWT" + Guid.NewGuid().ToString("N");
        var native = new WindowsNamespaceNative();
        var opened = new List<IntPtr>();
        try
        {
            // The first open creates it; a second, under another alias in the same process, finds it (create: already exists,
            // then open), as a second PoolWatch process does.
            var a = AdminNamespace.Open(native, TestBoundary(boundaryName), OnlyMe, first);
            opened.Add(a.Handle);
            var b = AdminNamespace.Open(native, TestBoundary(boundaryName), OnlyMe, second);
            opened.Add(b.Handle);
            // Another boundary name is another namespace, whatever its objects are called.
            var c = AdminNamespace.Open(native, TestBoundary(boundaryName + "-other"), OnlyMe, other);
            opened.Add(c.Handle);

            // A PoolWatch that holds the mutex and listens, named through the first alias, is asked to exit and waited for
            // through the second: one event and one mutex, in one namespace.
            var log = new List<string>();
            bool listened = false;
            using var listening = new ManualResetEventSlim();
            using var asked = new ManualResetEventSlim();
            var running = new Thread(() =>
            {
                try
                {
                    using var mutex = new Mutex(true, a.MonitorMutexName);
                    using var listener = ExitSignal.Listen(a.ExitEventName, OnlyMe.GetSddlForm(AccessControlSections.Access), asked.Set, s => { lock (log) log.Add(s); });
                    listened = listener is not null;
                    listening.Set();
                    if (listened) asked.Wait(TimeSpan.FromSeconds(30));
                    mutex.ReleaseMutex();
                }
                catch (Exception ex)
                {
                    lock (log) log.Add("the stand-in failed: " + ex.Message); // a throw on this thread would end the test run
                    listening.Set();
                }
            }) { IsBackground = true };
            running.Start();
            Assert.True(listening.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(listened, string.Join("; ", log));

            Assert.True(EventWaitHandle.TryOpenExisting(b.ExitEventName, out var same));
            same!.Dispose();
            Assert.False(EventWaitHandle.TryOpenExisting(c.ExitEventName, out _));
            Assert.True(ExitSignal.RequestAndWait(b.ExitEventName, b.MonitorMutexName, TimeSpan.FromSeconds(20)));
            Assert.True(running.Join(TimeSpan.FromSeconds(10)));
            lock (log) Assert.Contains("exit signal: exit requested", log);
        }
        finally
        {
            foreach (var h in opened) ClosePrivateNamespace(h, 0);
        }
    }

    [Fact]
    public void ABoundaryTheCallerIsNotInIsRefused()
    {
        // The rule PoolWatch's boundary relies on: a token without the boundary's SID cannot create the namespace. A SID no
        // test account holds (a new, made-up domain SID) stands in for Administrators held by a standard user.
        var outsider = new SecurityIdentifier("S-1-5-21-1-2-3-" + Random.Shared.Next(1000, int.MaxValue));
        var boundary = new NamespaceBoundary("PoolWatch-Test-" + Guid.NewGuid().ToString("N"), [outsider], null);

        IntPtr h = new WindowsNamespaceNative().Create(boundary, Binary(OnlyMe), "PWT" + Guid.NewGuid().ToString("N"), out int error);

        if (h != IntPtr.Zero) ClosePrivateNamespace(h, 0);
        Assert.Equal(IntPtr.Zero, h);
        Assert.NotEqual(0, error);
    }

    [Fact]
    public void ABoundaryAboveTheCallersIntegrityLevelIsRefused()
    {
        // The other rule: a process cannot create a namespace whose boundary's integrity level is above its own. The system
        // level (no test runs as SYSTEM) stands in for the high level PoolWatch's boundary has, met by a medium-level process.
        var boundary = new NamespaceBoundary("PoolWatch-Test-" + Guid.NewGuid().ToString("N"), [WindowsIdentity.GetCurrent().User!],
            new SecurityIdentifier("S-1-16-16384"));

        IntPtr h = new WindowsNamespaceNative().Create(boundary, Binary(OnlyMe), "PWT" + Guid.NewGuid().ToString("N"), out int error);

        if (h != IntPtr.Zero) ClosePrivateNamespace(h, 0);
        Assert.Equal(IntPtr.Zero, h);
        Assert.NotEqual(0, error);
    }

    [Fact]
    public void ANamespaceMayCarryAnOwnerAndAMandatoryLabel()
    {
        // Windows takes a namespace's security with an owner and a mandatory label, as PoolWatch's has. The test account
        // and the medium level stand in for Administrators and the high level, which only an elevated process may set.
        string me = WindowsIdentity.GetCurrent().User!.Value;
        var boundary = TestBoundary("PoolWatch-Test-" + Guid.NewGuid().ToString("N"));

        IntPtr h = new WindowsNamespaceNative().Create(boundary, Binary(new RawSecurityDescriptor($"O:{me}D:P(A;;GA;;;{me})S:(ML;;NWNRNX;;;ME)")),
            "PWT" + Guid.NewGuid().ToString("N"), out int error);

        if (h != IntPtr.Zero) ClosePrivateNamespace(h, 0);
        Assert.True(h != IntPtr.Zero, $"Windows error {error}");
    }

    [Fact]
    public void OnlyAnElevatedAdministratorCanGiveANamespacePoolWatchsSecurity()
    {
        // PoolWatch's own security (owner Administrators, high label), under a boundary of the test's own that every test
        // account is inside: an unelevated test run cannot set that owner or that label, so Windows refuses it; an
        // elevated one (as CI runs) can, as the elevated tray app does.
        var boundary = TestBoundary("PoolWatch-Test-" + Guid.NewGuid().ToString("N"));

        IntPtr h = new WindowsNamespaceNative().Create(boundary, Binary(new RawSecurityDescriptor(AdminNamespace.Security)),
            "PWT" + Guid.NewGuid().ToString("N"), out int error);

        if (h != IntPtr.Zero) ClosePrivateNamespace(h, 0);
        if (InstallGuard.IsElevated) Assert.True(h != IntPtr.Zero, $"Windows error {error}");
        else
        {
            Assert.Equal(IntPtr.Zero, h);
            Assert.NotEqual(0, error);
        }
    }

    /// <summary>SE_GROUP_ENABLED and SE_GROUP_USE_FOR_DENY_ONLY, of a group in a token.</summary>
    private const uint GroupEnabled = 0x4, GroupDenyOnly = 0x10;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returned);

    /// <summary>
    /// How the test process's own token holds BUILTIN\Administrators: that group's attributes (SE_GROUP_*), or null when the
    /// token does not hold it at all. Read from the token's groups (GetTokenInformation, TokenGroups), where an unelevated
    /// administrator's token has it for deny only and an elevated one has it enabled.
    /// </summary>
    internal static uint? AdministratorsInToken()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return AdministratorsIn(identity.Token);
    }

    /// <summary>How <paramref name="token"/> holds BUILTIN\Administrators (<see cref="AdministratorsInToken"/>).</summary>
    private static uint? AdministratorsIn(IntPtr token)
    {
        const int TokenGroups = 2;
        GetTokenInformation(token, TokenGroups, IntPtr.Zero, 0, out int size);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenGroups, buffer, size, out _)) throw new System.ComponentModel.Win32Exception();
            // TOKEN_GROUPS: a DWORD count, then (pointer-aligned) SID_AND_ATTRIBUTES entries of a PSID and a DWORD each.
            int count = Marshal.ReadInt32(buffer), first = IntPtr.Size, entry = 2 * IntPtr.Size;
            for (int i = 0; i < count; i++)
                if (new SecurityIdentifier(Marshal.ReadIntPtr(buffer, first + i * entry)) == Administrators)
                    return (uint)Marshal.ReadInt32(buffer, first + i * entry + IntPtr.Size);
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(IntPtr existing, uint flags, uint disableSidCount, SidAndAttributes[] sidsToDisable,
        uint deletePrivilegeCount, IntPtr privilegesToDelete, uint restrictedSidCount, IntPtr sidsToRestrict, out IntPtr newToken);

    /// <summary>
    /// A copy of the test process's token in which BUILTIN\Administrators is for deny only, as in an unelevated
    /// administrator's token (CreateRestrictedToken, with that group among the SIDs to disable: "SE_GROUP_USE_FOR_DENY_ONLY").
    /// From an unelevated administrator's token it is held so already; from an elevated one (as CI runs) the copy holds it
    /// so too. Null when the token does not hold the group at all.
    /// </summary>
    private static SafeAccessTokenHandle? WithAdministratorsForDenyOnly()
    {
        if (AdministratorsInToken() is null) return null;
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Duplicate | TokenAccessLevels.Query | TokenAccessLevels.Impersonate | TokenAccessLevels.AssignPrimary);
        byte[] sid = new byte[Administrators.BinaryLength];
        Administrators.GetBinaryForm(sid, 0);
        var pinned = GCHandle.Alloc(sid, GCHandleType.Pinned);
        try
        {
            SidAndAttributes[] disable = [new() { Sid = pinned.AddrOfPinnedObject(), Attributes = 0 }];
            if (!CreateRestrictedToken(identity.Token, 0, 1, disable, 0, IntPtr.Zero, 0, IntPtr.Zero, out IntPtr copy))
                throw new System.ComponentModel.Win32Exception();
            return new SafeAccessTokenHandle(copy);
        }
        finally { pinned.Free(); }
    }

    /// <summary>A fact that runs only when the test process's token holds BUILTIN\Administrators with the given attribute.</summary>
    private sealed class WhenAdministratorsIsAttribute : FactAttribute
    {
        /// <param name="attribute">The group attributes of BUILTIN\Administrators in the test's token, any of which runs the test.</param>
        /// <param name="otherwise">Why it is skipped when the token holds the group without any of them.</param>
        /// <param name="absent">Why it is skipped when the token does not hold the group at all (no administrator); <paramref name="otherwise"/> when null.</param>
        public WhenAdministratorsIsAttribute(uint attribute, string otherwise, string? absent = null)
        {
            Skip = SkipReason(AdministratorsInToken(), attribute, otherwise, absent);
        }
    }

    /// <summary>
    /// Why a test that needs BUILTIN\Administrators with one of <paramref name="wanted"/> is skipped, for a token that holds
    /// it with <paramref name="administrators"/> (null: does not hold it): null when it runs, <paramref name="absent"/> (or
    /// <paramref name="otherwise"/>) when the group is not there, <paramref name="otherwise"/> when it is there without them.
    /// </summary>
    internal static string? SkipReason(uint? administrators, uint wanted, string otherwise, string? absent) =>
        administrators is not { } held ? absent ?? otherwise : (held & wanted) == 0 ? otherwise : null;

    [Fact]
    public void ASkippedTestSaysWhyForTheTokenItRanUnder()
    {
        const string denyOnly = "deny only", none = "no administrator";
        Assert.Equal(none, SkipReason(null, GroupEnabled, denyOnly, none));
        Assert.Equal(denyOnly, SkipReason(GroupDenyOnly, GroupEnabled, denyOnly, none));
        Assert.Null(SkipReason(GroupEnabled | 0x3, GroupEnabled, denyOnly, none));
        Assert.Equal(denyOnly, SkipReason(null, GroupEnabled, denyOnly, null));
        // The elevated test names both reasons, each true of the token it is given for.
        var elevated = typeof(AdminNamespaceTests).GetMethod(nameof(AnElevatedAdministratorsGroupSatisfiesTheBoundary))!.GetCustomAttributesData()
            .Single(a => a.AttributeType == typeof(WhenAdministratorsIsAttribute));
        Assert.Contains("for deny only", (string)elevated.ConstructorArguments[1].Value!);
        Assert.Contains("not an administrator", (string)elevated.ConstructorArguments[2].Value!);
    }

    /// <summary>A boundary of exactly BUILTIN\Administrators at the low integrity level, so that only the SID rule decides.</summary>
    private static (IntPtr Handle, int Error) CreateUnderAdministratorsOnly()
    {
        var boundary = new NamespaceBoundary("PoolWatch-Test-" + Guid.NewGuid().ToString("N"), [Administrators], new SecurityIdentifier("S-1-16-4096"));
        IntPtr h = new WindowsNamespaceNative().Create(boundary, Binary(OnlyMe), "PWT" + Guid.NewGuid().ToString("N"), out int error);
        if (h != IntPtr.Zero) ClosePrivateNamespace(h, 0);
        return (h, error);
    }

    /// <summary>
    /// Skipped only for an account that is not an administrator at all: Windows gives a token the groups of its logon, and a
    /// program can take a group out of its own token's use (CreateRestrictedToken) but never add one, so such a run has no
    /// BUILTIN\Administrators to hold for deny only.
    /// </summary>
    [WhenAdministratorsIs(GroupDenyOnly | GroupEnabled, "The test account is not an administrator: its token does not hold BUILTIN\\Administrators, and Windows lets no program add a group to a token.")]
    public void AnUnelevatedAdministratorsDenyOnlyGroupDoesNotSatisfyTheBoundary()
    {
        // The real threat the boundary stands against: an administrator's own programs, which run unelevated. Their token
        // carries the Administrators SID for deny only, not enabled, and Windows does not count it for the boundary. Run
        // under a copy of the test's token that holds it so (WithAdministratorsForDenyOnly), checked first, so the test runs
        // the same in an unelevated run and in an elevated one (as CI's).
        using var denyOnly = WithAdministratorsForDenyOnly()!;
        Assert.Equal(GroupDenyOnly, AdministratorsIn(denyOnly.DangerousGetHandle())!.Value & (GroupDenyOnly | GroupEnabled));

        var (h, error) = WindowsIdentity.RunImpersonated(denyOnly, CreateUnderAdministratorsOnly);

        Assert.Equal(IntPtr.Zero, h);
        Assert.NotEqual(0, error);
    }

    /// <summary>
    /// Skipped in an unelevated run, where Windows cannot run it: an unelevated administrator's token holds
    /// BUILTIN\Administrators for deny only, and a deny-only group "cannot be enabled" (SE_GROUP_USE_FOR_DENY_ONLY, Microsoft
    /// Learn: TOKEN_GROUPS); only an elevation through User Account Control gives a token with that group enabled. An
    /// account that is not an administrator has no such group to enable, elevated or not, and is skipped for that reason.
    /// </summary>
    [WhenAdministratorsIs(GroupEnabled, "The test process is not elevated: its token holds BUILTIN\\Administrators for deny only, which Windows does not let a program enable (SE_GROUP_USE_FOR_DENY_ONLY); only elevation gives it enabled.",
        "The test account is not an administrator: its token does not hold BUILTIN\\Administrators at all, so no elevation of this account gives it enabled.")]
    public void AnElevatedAdministratorsGroupSatisfiesTheBoundary()
    {
        // The same boundary is taken by an elevated administrator's token (as in CI, where the tray app's own is taken).
        Assert.Equal(GroupEnabled, AdministratorsInToken()!.Value & (GroupDenyOnly | GroupEnabled));

        var (h, error) = CreateUnderAdministratorsOnly();

        Assert.True(h != IntPtr.Zero, $"Windows error {error}");
    }

    private static byte[] Binary(RawSecurityDescriptor sd)
    {
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        return bytes;
    }

    // ---------------------------------------------------------------- where the names are used

    private static string Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) return File.ReadAllText(Path.Combine([dir.FullName, .. parts]));
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Whether a PoolWatch may be running, as the steps that must not move a running PoolWatch's folder ask it: when
    /// PoolWatch's private namespace cannot be opened, it cannot be told, so yes, never "none runs"; otherwise it is
    /// whether the mutex there exists (a test's own name here).
    /// </summary>
    [Fact]
    public void WhenTheNamespaceCannotBeOpenedAPoolWatchMayBeRunningNeverNone()
    {
        Assert.True(ExitSignal.InstanceRunning(() => throw new AdminNamespaceException("the namespace could not be opened")));
        string name = @"Local\pw-test-" + Guid.NewGuid().ToString("N");
        Assert.False(ExitSignal.InstanceRunning(() => name));
        using (new Mutex(false, name)) Assert.True(ExitSignal.InstanceRunning(() => name));
        Assert.False(ExitSignal.InstanceRunning(() => name));
    }

    [Fact]
    public void TheMutexAndTheExitEventAreNamedOnlyThroughTheNamespace()
    {
        // No Global\ (or any other open) name is created, opened or waited for, and no bare "PoolWatch-Monitor" /
        // "PoolWatch-Exit" either: each name comes from the namespace. The methods that name the mutex and the
        // event are held whole, so a fallback to an open name on a namespace failure (which Contains would not catch,
        // the original line still being there) fails this test. The tray app's and a headless run's start takes the
        // mutex by the name the first gives (RunAsTheOnlyMonitor, through ExitSignal.AsTheOneInstance), and whether a
        // PoolWatch may hold it is asked of the same name, or taken as yes when the namespace cannot be opened.
        string program = Source("src", "PoolWatch", "Program.cs"), signal = Source("src", "PoolWatch", "Engine", "ExitSignal.cs");
        foreach (var code in new[] { program, signal })
            Assert.DoesNotMatch(@"@?""(Global|Local|Session)\\", code);
        Assert.Equal(
            "private static string MonitorMutexName() { return Engine.AdminNamespace.Shared.MonitorMutexName; }",
            Method(program, "private static string MonitorMutexName()"));
        Assert.Contains("return RunAsTheOnlyMonitor(MonitorMutexName,", System.Text.RegularExpressions.Regex.Replace(program, @"\s+", " "));
        Assert.Equal(
            "public static bool InstanceRunning() { return InstanceRunning(() => AdminNamespace.Shared.MonitorMutexName); }",
            Method(signal, "public static bool InstanceRunning()"));
        Assert.Equal(
            "internal static bool InstanceRunning(Func<string> mutexName) { string name; try { name = mutexName(); } " +
            "catch (AdminNamespaceException) { return true; } return InstanceRunning(name); }",
            Method(signal, "internal static bool InstanceRunning(Func<string> mutexName)"));
        Assert.Equal(
            "public static IDisposable? Listen(Action onExit, Action<string> log) { string name; " +
            "try { name = AdminNamespace.Shared.ExitEventName; } catch (AdminNamespaceException ex) { " +
            "log(\"exit signal: not used (\" + ex.Message + \")\"); return null; } return Listen(name, AdminOnly, onExit, log); }",
            Method(signal, "public static IDisposable? Listen(Action onExit, Action<string> log)"));
        Assert.Equal(
            "public static Hold? StopAndHold(TimeSpan wait) { var names = AdminNamespace.Shared; " +
            "return StopAndHold(names.ExitEventName, names.MonitorMutexName, wait); }",
            Method(signal, "public static Hold? StopAndHold(TimeSpan wait)"));
        // The one namespace PoolWatch opens has PoolWatch's boundary and its whole security (owner, permissions, label).
        Assert.Contains("return _shared ??= Open(new WindowsNamespaceNative(), Boundary(), new RawSecurityDescriptor(Security), Alias);",
            Source("src", "PoolWatch", "Engine", "AdminNamespace.cs"));
    }

    /// <summary>
    /// One method of a C# source, from its signature to the matching close brace, with every run of white space made one
    /// space. The methods it is used on hold no brace inside a string or a comment, so brace counting is exact.
    /// </summary>
    private static string Method(string code, string signature)
    {
        int start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " is not in the source");
        int brace = code.IndexOf('{', start + signature.Length);
        Assert.True(brace >= 0, signature + " has no body");
        int depth = 0, i = brace;
        for (; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) { i++; break; }
        }
        return Regex.Replace(code[start..i], @"\s+", " ").Trim();
    }
}
