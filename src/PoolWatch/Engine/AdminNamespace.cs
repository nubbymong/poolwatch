using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PoolWatch.Engine;

/// <summary>A private namespace's boundary: its name, the groups the creator's token must hold, and the lowest integrity level it may run at.</summary>
internal sealed record NamespaceBoundary(string Name, IReadOnlyList<SecurityIdentifier> Sids, SecurityIdentifier? IntegrityLevel);

/// <summary>The Windows calls behind <see cref="AdminNamespace"/> (the tests pass fakes, or the real calls under a boundary of their own).</summary>
internal interface INamespaceNative
{
    /// <summary>Creates the namespace with the security descriptor (binary form) under the alias; zero, with the Win32 error, when it cannot be.</summary>
    IntPtr Create(NamespaceBoundary boundary, byte[] securityDescriptor, string alias, out int error);

    /// <summary>Opens the namespace someone created under the same boundary; zero, with the Win32 error, when it cannot be.</summary>
    IntPtr Open(NamespaceBoundary boundary, string alias, out int error);
}

/// <summary>The namespace could not be created or opened: PoolWatch uses none of its named objects then.</summary>
internal sealed class AdminNamespaceException(string message) : Exception(message);

/// <summary>
/// The private object namespace that holds PoolWatch's one-instance mutex, its exit event and the mark of a tray app whose
/// token guard shows its notices. A private namespace is found by its boundary, not by its name. This one's boundary
/// holds the Administrators group and the high integrity level, so only an elevated administrator or SYSTEM can create
/// it (Windows refuses a creator whose token lacks a SID of the boundary, or whose integrity level is below the
/// boundary's), and its permissions let only Administrators and SYSTEM open it, or create anything in it: a program
/// without administrator rights can neither create these objects nor open them. Its owner is the Administrators group
/// and it carries the high mandatory label (<see cref="Security"/>), so neither an owner's implicit rights nor an
/// unlabelled object's medium level lets an unelevated process in.
/// <para>
/// It is opened once per process and kept open until the process ends: the namespace lasts while any process holds it,
/// and PoolWatch never destroys it (another PoolWatch may be using it at that moment). The objects are named through the
/// alias this process registered (<see cref="NameOf"/>), and only after the namespace was created or opened: a name is
/// never used without it.
/// </para>
/// </summary>
internal sealed class AdminNamespace
{
    /// <summary>The boundary's name and the alias the objects are named through (<c>PoolWatch\Monitor</c>, <c>PoolWatch\Exit</c>, <c>PoolWatch\GuardNotices-1</c> for session 1).</summary>
    public const string BoundaryName = "PoolWatch", Alias = "PoolWatch";
    /// <summary>The one-instance mutex a running PoolWatch holds, and the event the setup and the uninstaller set to ask it to exit.</summary>
    public const string MonitorLeaf = "Monitor", ExitLeaf = "Exit";
    /// <summary>
    /// The start of the name of the mutex a tray app holds from the time it starts its token guard with its notices until
    /// it exits, one for each Windows session (<see cref="GuardNoticesMutexName"/>): a token-leak test helper left running
    /// looks for the one of its own session (<see cref="TokenLeakGuard.GuardNoticesShown"/>). Nothing else holds it, a
    /// headless run included.
    /// </summary>
    public const string GuardNoticesLeaf = "GuardNotices";
    /// <summary>The permissions of the namespace and of the exit event: Administrators and SYSTEM, full access; nobody else, and nothing inherited.</summary>
    internal const string AdminOnly = "D:P(A;;GA;;;BA)(A;;GA;;;SY)";
    /// <summary>
    /// The namespace's whole security: owned by the Administrators group, never by the account that created it (which
    /// Windows' "object creator" owner policy for administrators would otherwise give, and an owner may read and change
    /// the permissions whatever they say); <see cref="AdminOnly"/>; and the high mandatory label, so that a process below
    /// high integrity can neither read, change nor use it (an object without a label counts as medium). Only an elevated
    /// administrator or SYSTEM may set either the owner or the label, so only they can create it with this.
    /// </summary>
    internal const string Security = "O:BA" + AdminOnly + "S:(ML;;NWNRNX;;;HI)";
    /// <summary>The high mandatory integrity level: an elevated process's (SYSTEM's is higher, and also qualifies).</summary>
    internal static readonly SecurityIdentifier HighIntegrity = new("S-1-16-12288");
    /// <summary>How often a create that found the namespace, then an open that found it gone, is tried again.</summary>
    internal const int Attempts = 5;
    private const int ERROR_ALREADY_EXISTS = 183;

    private static readonly object SharedGate = new();
    private static AdminNamespace? _shared;

    private readonly string _alias;

    private AdminNamespace(IntPtr handle, string alias)
    {
        Handle = handle;
        _alias = alias;
    }

    /// <summary>The namespace's handle, kept open for the life of the process.</summary>
    internal IntPtr Handle { get; }

    /// <summary>The name of an object in this namespace, through the alias this process registered.</summary>
    public string NameOf(string leaf) => _alias + "\\" + leaf;

    public string MonitorMutexName => NameOf(MonitorLeaf);

    public string ExitEventName => NameOf(ExitLeaf);

    /// <summary>
    /// The guard-notices mark of the Windows session <paramref name="sessionId"/>: <see cref="GuardNoticesLeaf"/>, a dash
    /// and the session's number. Windows finds a private namespace by its boundary, and this one's names no session (it
    /// is meant to be one for the whole PC: its one-instance mutex is to keep one PoolWatch running on the PC), so a name
    /// in it can be seen from every session, while the setting the guard looks after is each session's own (Windows keeps
    /// the foreground lock timeout per session). So each session has a mark of its own, and a tray app in one session
    /// never takes back the prompt of a helper left running in another, whose session that tray app's guard does not
    /// protect.
    /// </summary>
    public string GuardNoticesMutexName(int sessionId) => NameOf(GuardNoticesLeaf + "-" + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// PoolWatch's namespace, created or opened on first use and then kept. Throws <see cref="AdminNamespaceException"/>
    /// when it cannot be (each call tries again, so a later call can still succeed).
    /// </summary>
    public static AdminNamespace Shared
    {
        get
        {
            lock (SharedGate)
                return _shared ??= Open(new WindowsNamespaceNative(), Boundary(), new RawSecurityDescriptor(Security), Alias);
        }
    }

    /// <summary>The boundary: the name, the Administrators group, the high integrity level.</summary>
    internal static NamespaceBoundary Boundary() =>
        new(BoundaryName, [new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)], HighIntegrity);

    /// <summary>
    /// Creates the namespace, or opens it when another process created it first. A namespace that went between the two
    /// calls (its last holder ended) is created again, at most <see cref="Attempts"/> times. Throws
    /// <see cref="AdminNamespaceException"/> with the Windows error when it can be neither created nor opened.
    /// </summary>
    internal static AdminNamespace Open(INamespaceNative native, NamespaceBoundary boundary, RawSecurityDescriptor security, string alias)
    {
        var sd = new byte[security.BinaryLength];
        security.GetBinaryForm(sd, 0);
        string failure = "";
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            IntPtr h = native.Create(boundary, sd, alias, out int error);
            if (h != IntPtr.Zero) return new AdminNamespace(h, alias);
            if (error != ERROR_ALREADY_EXISTS)
                throw new AdminNamespaceException($"PoolWatch could not create its private names ({Describe(error)}). It needs to run as administrator.");
            h = native.Open(boundary, alias, out error);
            if (h != IntPtr.Zero) return new AdminNamespace(h, alias);
            failure = Describe(error);
        }
        throw new AdminNamespaceException($"PoolWatch could not open its private names ({failure}).");
    }

    private static string Describe(int error) => $"Windows error {error}: {new Win32Exception(error).Message}";
}

/// <summary>
/// The real calls: CreateBoundaryDescriptorW, AddSIDToBoundaryDescriptor, AddIntegrityLabelToBoundaryDescriptor,
/// CreatePrivateNamespaceW / OpenPrivateNamespaceW, DeleteBoundaryDescriptor. The boundary descriptor is built for each
/// call and freed after it (Windows copies what it needs); the namespace handle is the caller's to keep.
/// </summary>
internal sealed unsafe class WindowsNamespaceNative : INamespaceNative
{
    public IntPtr Create(NamespaceBoundary boundary, byte[] securityDescriptor, string alias, out int error)
    {
        IntPtr descriptor = Build(boundary, out error);
        if (descriptor == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            fixed (byte* sd = securityDescriptor)
            {
                var attributes = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = (IntPtr)sd, bInheritHandle = 0 };
                IntPtr h = CreatePrivateNamespaceW(&attributes, descriptor, alias);
                error = h == IntPtr.Zero ? Marshal.GetLastPInvokeError() : 0;
                return h;
            }
        }
        finally { DeleteBoundaryDescriptor(descriptor); }
    }

    public IntPtr Open(NamespaceBoundary boundary, string alias, out int error)
    {
        IntPtr descriptor = Build(boundary, out error);
        if (descriptor == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            IntPtr h = OpenPrivateNamespaceW(descriptor, alias);
            error = h == IntPtr.Zero ? Marshal.GetLastPInvokeError() : 0;
            return h;
        }
        finally { DeleteBoundaryDescriptor(descriptor); }
    }

    /// <summary>The boundary descriptor; zero, with the error, when it cannot be built.</summary>
    private static IntPtr Build(NamespaceBoundary boundary, out int error)
    {
        IntPtr descriptor = CreateBoundaryDescriptorW(boundary.Name, 0);
        if (descriptor == IntPtr.Zero)
        {
            error = Marshal.GetLastPInvokeError();
            return IntPtr.Zero;
        }
        foreach (var sid in boundary.Sids)
            if (!Add(ref descriptor, sid, label: false, out error))
            {
                DeleteBoundaryDescriptor(descriptor);
                return IntPtr.Zero;
            }
        if (boundary.IntegrityLevel is { } level && !Add(ref descriptor, level, label: true, out error))
        {
            DeleteBoundaryDescriptor(descriptor);
            return IntPtr.Zero;
        }
        error = 0;
        return descriptor;
    }

    private static bool Add(ref IntPtr descriptor, SecurityIdentifier sid, bool label, out int error)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        bool ok;
        fixed (byte* p = bytes)
            ok = label ? AddIntegrityLabelToBoundaryDescriptor(ref descriptor, (IntPtr)p) : AddSIDToBoundaryDescriptor(ref descriptor, (IntPtr)p);
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateBoundaryDescriptorW(string name, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool AddSIDToBoundaryDescriptor(ref IntPtr boundaryDescriptor, IntPtr requiredSid);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool AddIntegrityLabelToBoundaryDescriptor(ref IntPtr boundaryDescriptor, IntPtr integrityLabel);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteBoundaryDescriptor(IntPtr boundaryDescriptor);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreatePrivateNamespaceW(SECURITY_ATTRIBUTES* attributes, IntPtr boundaryDescriptor, string aliasPrefix);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr OpenPrivateNamespaceW(IntPtr boundaryDescriptor, string aliasPrefix);
}
