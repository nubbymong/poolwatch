using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PoolWatch.Analysis;
using PoolWatch.Collectors;

namespace PoolWatch.AppTests;

/// <summary>
/// AI Code Conductor's version goes into cache\versions.txt and its "updated" findings, so it is read only from an exe
/// that is AICC's own: one only administrators can change (installed for everyone, under Program Files), or one signed by
/// the same publisher as PoolWatch through the same certificate authority. Any program can be called "AI Code
/// Conductor.exe" and copy its version resource. The decisions are pure; one test reads a real signature, read only.
/// </summary>
public class AiccVersionTests
{
    private static readonly SignatureInfo Own = new(SignatureKind.Embedded, "Publisher", 0, "Publisher", "CN=Publisher, O=Publisher, C=GB", new string('A', 64));

    [Fact]
    public void OnlyAnExeThatIsAiccsOwnGivesItsVersion()
    {
        // Installed for everyone (admin-only): its signature is not even read.
        Assert.True(VersionCollector.IsAiccsOwn(adminOnly: true, () => throw new InvalidOperationException("not read"), Own));
        // Signed by the same publisher through the same authority (a renewed certificate has another name string, maybe, but the same subject).
        Assert.True(VersionCollector.IsAiccsOwn(false, () => Own with { Signer = "Renewed" }, Own));
        // The same subject under another root (a certificate someone made, under a root added to their own store): not AICC's.
        Assert.False(VersionCollector.IsAiccsOwn(false, () => Own with { RootThumbprint = new string('B', 64) }, Own));
        Assert.False(VersionCollector.IsAiccsOwn(false, () => Own with { Subject = "CN=Publisher, O=Someone else, C=GB" }, Own));
        Assert.False(VersionCollector.IsAiccsOwn(false, () => Own with { Kind = SignatureKind.Untrusted }, Own));
        Assert.False(VersionCollector.IsAiccsOwn(false, () => Own with { Kind = SignatureKind.Catalog }, Own));
        Assert.False(VersionCollector.IsAiccsOwn(false, () => null, Own));
        // PoolWatch itself not signed (a development build): only an install for everyone counts, and no signature is read.
        Assert.False(VersionCollector.IsAiccsOwn(false, () => throw new InvalidOperationException("not read"), null));
        Assert.False(VersionCollector.IsAiccsOwn(false, () => throw new InvalidOperationException("not read"), Own with { Kind = SignatureKind.None }));
        Assert.False(VersionCollector.IsAiccsOwn(false, () => throw new InvalidOperationException("not read"), Own with { RootThumbprint = null }));
        Assert.False(VersionCollector.IsAiccsOwn(false, () => Own with { Subject = null }, Own with { Subject = null }));
        Assert.False(VersionCollector.IsAiccsOwn(false, () => Own with { RootThumbprint = null }, Own with { RootThumbprint = null }));
    }

    private const string Linked = @"C:\Users\x\AppData\Local\Programs\AICC\AI Code Conductor.exe", Final = @"D:\real\AI Code Conductor.exe";

    /// <summary>A hold that records when it is released.</summary>
    private sealed class Hold(List<string> steps) : IDisposable
    {
        public void Dispose() => steps.Add("release");
    }

    /// <summary>
    /// Reads that record each step and the path it is given. The exe's version resource says it is AI Code Conductor
    /// 9.9.9, whatever it is: any program can copy that.
    /// </summary>
    private static VersionCollector.AiccReads Reads(List<string> steps, bool adminOnly, SignatureInfo? signature, SignatureInfo? own = null,
        DateTime? lastWrite = null, bool canHold = true, bool finalKnown = true) => new(
        path =>
        {
            steps.Add("hold " + path);
            if (!canHold) throw new IOException("in use");
            return (new Hold(steps), finalKnown ? new VersionCollector.AiccFile(Final, new(1, 2, 0), lastWrite ?? new DateTime(2026, 11, 1), 1000) : null);
        },
        final => { steps.Add("admin-only " + final); return adminOnly; },
        final => { steps.Add("signature " + final); return signature; },
        () => own ?? Own,
        final => { steps.Add("version " + final); return ("AI Code Conductor", "9.9.9", "9.9.9.0"); });

    [Fact]
    public void TheVersionIsTakenOnlyWhereTheExeIsJudgedAiccsOwn()
    {
        // Not admin-only and not signed by PoolWatch's publisher: no version, though its version resource names AICC.
        var steps = new List<string>();
        Assert.Null(VersionCollector.TrustedAiccVersion(Linked, Reads(steps, adminOnly: false, Own with { Subject = "CN=Someone" }), new()));
        Assert.Equal(["hold " + Linked, "admin-only " + Final, "signature " + Final, "release"], steps); // judged at its final path, never read

        // Admin-only (installed for everyone): the version, and its signature is not read.
        steps.Clear();
        Assert.Equal("9.9.9", VersionCollector.TrustedAiccVersion(Linked, Reads(steps, adminOnly: true, signature: null), new()));
        Assert.Equal(["hold " + Linked, "admin-only " + Final, "version " + Final, "release"], steps); // read while held

        // Signed by PoolWatch's publisher: the version.
        steps.Clear();
        Assert.Equal("9.9.9", VersionCollector.TrustedAiccVersion(Linked, Reads(steps, adminOnly: false, Own with { Signer = "Renewed" }), new()));
        Assert.Equal(["hold " + Linked, "admin-only " + Final, "signature " + Final, "version " + Final, "release"], steps);

        // An exe that cannot be held, or whose final path cannot be told: nothing judged or read.
        steps.Clear();
        Assert.Null(VersionCollector.TrustedAiccVersion(Linked, Reads(steps, adminOnly: true, Own, canHold: false), new()));
        Assert.Equal(["hold " + Linked], steps);
        steps.Clear();
        Assert.Null(VersionCollector.TrustedAiccVersion(Linked, Reads(steps, adminOnly: true, Own, finalKnown: false), new()));
        Assert.Equal(["hold " + Linked, "release"], steps);
    }

    [Fact]
    public void AnExeThatIsNotAiccsHasItsSignatureCheckedOnceWhileItIsUnchanged()
    {
        var verdicts = new VersionCollector.SignatureVerdicts();
        var steps = new List<string>();
        var someone = Own with { Subject = "CN=Someone" };
        for (int i = 0; i < 3; i++)
        {
            Assert.Null(VersionCollector.TrustedAiccVersion(Linked, Reads(steps, adminOnly: false, someone), verdicts));
            verdicts.EndCheck();
        }
        Assert.Equal(1, steps.Count(s => s.StartsWith("signature", StringComparison.Ordinal))); // one full-file check, not one per read
        Assert.Equal(1, verdicts.Count);

        // Written since (another last write time): checked again.
        Assert.Null(VersionCollector.TrustedAiccVersion(Linked, Reads(steps, adminOnly: false, someone, lastWrite: new DateTime(2026, 11, 2)), verdicts));
        Assert.Equal(2, steps.Count(s => s.StartsWith("signature", StringComparison.Ordinal)));

        // Only the files met in the last read are kept: one no longer running is forgotten.
        verdicts.EndCheck();
        verdicts.EndCheck();
        Assert.Equal(0, verdicts.Count);

        // A "no" is never taken as a "yes": the same file found admin-only now gives its version.
        steps.Clear();
        verdicts.NotOwn(new VersionCollector.AiccFile(Final, new(1, 2, 0), new DateTime(2026, 11, 1), 1000));
        Assert.Equal("9.9.9", VersionCollector.TrustedAiccVersion(Linked, Reads(steps, adminOnly: true, null), verdicts));

        // An unsigned development build of PoolWatch: no signature can match, so none is read.
        steps.Clear();
        Assert.Null(VersionCollector.TrustedAiccVersion(Linked, Reads(steps, adminOnly: false, Own, own: Own with { Kind = SignatureKind.None }), new()));
        Assert.DoesNotContain(steps, s => s.StartsWith("signature", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRealHoldKeepsTheExeFromBeingWrittenOrRenamedAndGivesItsFinalPath()
    {
        // A file in TEMP stands in for the exe: nothing is run, and no access rule is read or written.
        string dir = Path.Combine(Path.GetTempPath(), "pw-aicc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "AI Code Conductor.exe");
        try
        {
            File.WriteAllText(exe, "MZ");
            var (hold, file) = VersionCollector.ThisPc.Hold(exe);
            using (hold)
            {
                Assert.NotNull(file);
                Assert.Equal(Path.GetFullPath(exe), file.Final, ignoreCase: true);
                Assert.Equal(2, file.Length);
                Assert.Throws<IOException>(() => new FileStream(exe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose()); // no write while held
                Assert.ThrowsAny<IOException>(() => File.Move(exe, exe + ".moved"));                                                 // no rename either
                Assert.Equal("MZ", File.ReadAllText(exe));                                                                          // reads are shared
            }
            File.WriteAllText(exe, "MZ2"); // released
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void EveryRunningCandidateIsJudgedAndTheFirstThatIsAiccsOwnGivesTheVersion()
    {
        const string downloads = @"C:\Users\x\Downloads\AI Code Conductor.exe", installed = @"C:\Program Files\AI Code Conductor\AI Code Conductor.exe";
        var asked = new List<string>();
        string? Version(string p)
        {
            asked.Add(p);
            return p == installed ? "2.1.0-rc.9" : null;
        }
        // A same-named program listed first neither hides AICC's version nor sets it.
        Assert.Equal("2.1.0-rc.9", VersionCollector.AiccVersion([downloads, null, downloads.ToLowerInvariant(), installed], Version));
        Assert.Equal([downloads, installed], asked); // each path once, until the first that is AICC's own
        Assert.Null(VersionCollector.AiccVersion([downloads], Version));
        Assert.Null(VersionCollector.AiccVersion([], Version));
    }

    [Fact]
    public void EveryLiveProcessNamedLikeAiccsExeIsACandidate()
    {
        var registry = new PoolWatch.Core.ProcessRegistry();
        var t = new DateTime(2026, 11, 1, 2, 0, 0, DateTimeKind.Utc);
        registry.OnStart(10, 4, t.ToFileTimeUtc(), "AI Code Conductor.exe", @"C:\Users\x\Downloads\AI Code Conductor.exe", null, null, t);
        registry.OnStart(11, 4, t.ToFileTimeUtc(), "notepad.exe", @"C:\Windows\notepad.exe", null, null, t);
        registry.OnStart(12, 4, t.ToFileTimeUtc(), "ai code conductor.exe", @"C:\Program Files\AI Code Conductor\AI Code Conductor.exe", null, null, t);
        Assert.Equal([@"C:\Users\x\Downloads\AI Code Conductor.exe", @"C:\Program Files\AI Code Conductor\AI Code Conductor.exe"],
            PoolWatch.Analysis.Diagnostics.AiccCandidates(registry.LiveRecords()).Order(StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public void ASignaturesSubjectAndRootAreReadFromTheVerifiedChain()
    {
        // Read only: the .NET runtime's own library, which its publisher signs.
        string lib = typeof(object).Assembly.Location;
        var sig = FileSignature.Verify(lib);
        Assert.Equal(SignatureKind.Embedded, sig.Kind);
        Assert.Contains("CN=", sig.Subject);
        Assert.Matches("^[0-9A-F]{64}$", sig.RootThumbprint);
        Assert.True(FileSignature.SamePublisher(sig, FileSignature.Verify(lib)));
        Assert.False(FileSignature.SamePublisher(sig with { RootThumbprint = new string('0', 64) }, sig));
        // It is the chain's root: a certificate in this PC's trusted roots, not the signing certificate.
        bool InRoots(StoreLocation where)
        {
            using var store = new X509Store(StoreName.Root, where);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return store.Certificates.Any(c => Convert.ToHexString(SHA256.HashData(c.RawData)) == sig.RootThumbprint);
        }
        Assert.True(InRoots(StoreLocation.LocalMachine) || InRoots(StoreLocation.CurrentUser), sig.RootThumbprint);
    }
}
