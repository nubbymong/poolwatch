namespace PoolWatch.Core.Tags;

/// <summary>A tag's owner and meaning.</summary>
public sealed record TagInfo(string Owner, string Description, string Source);

/// <summary>
/// Last-resort built-in tag table (DESIGN.md section 1), plus the object-manager tag -> handle type mapping that
/// powers direct attribution. Tags are case-sensitive.
/// </summary>
public static class BuiltinTags
{
    public static readonly IReadOnlyDictionary<PoolTag, TagInfo> Table = new Dictionary<PoolTag, TagInfo>
    {
        [PoolTag.Parse("FMfn")] = new("fltmgr.sys", "NAME_CACHE_NODE (filter manager file-name cache)", "builtin"),
        [PoolTag.Parse("File")] = new("nt", "File objects (charged to the opening process)", "builtin"),
        [PoolTag.Parse("Reff")] = new("refs.sys", "FCB_DATA (per-file control block)", "builtin"),
        [PoolTag.Parse("RefF")] = new("refs.sys", "FCB_INDEX (per-directory control block)", "builtin"),
        [PoolTag.Parse("Refx")] = new("refs.sys", "General allocation", "builtin"),
        [PoolTag.Parse("RLin")] = new("FsLib", "Range lock entry", "builtin"),
        [PoolTag.Parse("MSPi")] = new("refs.sys", "Minstore (ReFS metadata store) structures", "builtin"),
        [PoolTag.Parse("MSeb")] = new("refs.sys", "Minstore (ReFS metadata store) structures", "builtin"),
        [PoolTag.Parse("MSb+")] = new("refs.sys", "Minstore (ReFS metadata store) structures", "builtin"),
        [PoolTag.Parse("MSde")] = new("refs.sys", "Minstore (ReFS metadata store) structures", "builtin"),
        [PoolTag.Parse("MSpa")] = new("refs.sys", "Minstore (ReFS metadata store) structures", "builtin"),
        [PoolTag.Parse("MmSt")] = new("nt!mm", "Section object prototype PTEs (grows with mapped/cached files)", "builtin"),
        [PoolTag.Parse("CcVl")] = new("nt!cc", "Cache Manager VACB level structures", "builtin"),
        [PoolTag.Parse("Nbuf")] = new("netio.sys", "NetIO MDL allocations", "builtin"),
        [PoolTag.Parse("EtwB")] = new("nt!etw", "ETW buffers", "builtin"),
        [PoolTag.Parse("AfdB")] = new("afd.sys", "AFD data buffer", "builtin"),
        [PoolTag.Parse("Toke")] = new("nt!se", "Token objects (the known 25H2 token-leak tag)", "builtin"),
        // Common NTFS tags (CI runners are NTFS) so file-system growth is labelled without pooltag.txt.
        [PoolTag.Parse("NtfF")] = new("ntfs.sys", "FCB_INDEX (per-directory control block)", "builtin"),
        [PoolTag.Parse("NtFs")] = new("ntfs.sys", "StrucSup.c (general NTFS structures)", "builtin"),
        [PoolTag.Parse("Ntfx")] = new("ntfs.sys", "General allocation", "builtin"),
        [PoolTag.Parse("Ntff")] = new("ntfs.sys", "FCB_DATA", "builtin"),
        [PoolTag.Parse("NtfE")] = new("ntfs.sys", "NTFS SCB_INDEX", "builtin"),
        [PoolTag.Parse("FMsl")] = new("fltmgr.sys", "STREAM_LIST_CTRL (filter manager per-stream context)", "builtin"),
        [PoolTag.Parse("FMfc")] = new("fltmgr.sys", "FCB_CONTEXT (filter manager file context)", "builtin"),
        [PoolTag.Parse("FMwi")] = new("fltmgr.sys", "Filter manager work item / stream context", "builtin"),
        [PoolTag.Parse("NtFC")] = new("ntfs.sys", "NTFS file context (per open file; FCB-related)", "builtin"),
        [PoolTag.Parse("IoNm")] = new("nt!io", "I/O name parsing buffers (file names being opened)", "builtin"),
        // wcifs.sys, the Windows Container Isolation file-system filter (containers, sandboxes, MSIX/AppX isolation).
        [PoolTag.Parse("WCsc")] = new("wcifs.sys", "Windows Container Isolation filter: stream context", "builtin"),
        [PoolTag.Parse("WCfc")] = new("wcifs.sys", "Windows Container Isolation filter: file context", "builtin"),
        [PoolTag.Parse("WCri")] = new("wcifs.sys", "Windows Container Isolation filter: reparse / redirection info", "builtin"),
        [PoolTag.Parse("WCse")] = new("wcifs.sys", "Windows Container Isolation filter: allocation", "builtin"),
        [PoolTag.Parse("WCss")] = new("wcifs.sys", "Windows Container Isolation filter: allocation", "builtin"),
        [PoolTag.Parse("Even")] = new("nt", "Event objects", "builtin"),
        [PoolTag.Parse("Key ")] = new("nt", "Registry key objects", "builtin"),
        [PoolTag.Parse("Sect")] = new("nt", "Section objects", "builtin"),
        [PoolTag.Parse("Thre")] = new("nt", "Thread objects", "builtin"),
        [PoolTag.Parse("Proc")] = new("nt", "Process objects", "builtin"),
        [PoolTag.Parse("Muta")] = new("nt", "Mutant objects", "builtin"),
        [PoolTag.Parse("ObNm")] = new("nt", "Object manager name buffers", "builtin"),
        [PoolTag.Parse("ObHd")] = new("nt", "Object manager handle table entries", "builtin"),
        [PoolTag.Parse("Obtb")] = new("nt", "Object manager handle tables", "builtin"),
        [PoolTag.Parse("Vad ")] = new("nt!mm", "Mm virtual address descriptors", "builtin"),
        [PoolTag.Parse("MmCa")] = new("nt!mm", "Mm control areas for mapped files", "builtin"),
        [PoolTag.Parse("CM31")] = new("nt!cm", "Configuration manager (registry) structures", "builtin"),
        // The token family (seen together with Toke in the public reports; pooltag.txt, when present, gives the exact text).
        [PoolTag.Parse("SeAt")] = new("nt!se", "Security: token-related allocation (pooltag.txt has the exact meaning)", "builtin"),
        [PoolTag.Parse("SeTd")] = new("nt!se", "Security: token-related allocation (pooltag.txt has the exact meaning)", "builtin"),
        [PoolTag.Parse("SeDt")] = new("nt!se", "Security: token-related allocation (pooltag.txt has the exact meaning)", "builtin"),
        [PoolTag.Parse("SeTl")] = new("nt!se", "Security token lock / logon session", "builtin"),
    };

    /// <summary>Object-manager pool tags mapped to the handle type whose growth directly explains them.</summary>
    public static readonly IReadOnlyDictionary<PoolTag, string[]> HandleTypes = new Dictionary<PoolTag, string[]>
    {
        [PoolTag.Parse("File")] = ["File"],
        [PoolTag.Parse("Even")] = ["Event"],
        [PoolTag.Parse("Toke")] = ["Token"],
        [PoolTag.Parse("Key ")] = ["Key"],
        [PoolTag.Parse("Sect")] = ["Section"],
        [PoolTag.Parse("MmSt")] = ["Section"],
        [PoolTag.Parse("Thre")] = ["Thread"],
        [PoolTag.Parse("Proc")] = ["Process"],
        [PoolTag.Parse("Muta")] = ["Mutant"],
        [PoolTag.Parse("Sema")] = ["Semaphore"],
        [PoolTag.Parse("Time")] = ["Timer"],
        [PoolTag.Parse("Desk")] = ["Desktop"],
        [PoolTag.Parse("Wind")] = ["WindowStation"],
        [PoolTag.Parse("Job ")] = ["Job"],
        [PoolTag.Parse("Symb")] = ["SymbolicLink"],
        [PoolTag.Parse("Dire")] = ["Directory"],
        [PoolTag.Parse("IoCo")] = ["IoCompletion"],
        [PoolTag.Parse("EtwR")] = ["EtwRegistration"],
        [PoolTag.Parse("TpWo")] = ["TpWorkerFactory"],
        [PoolTag.Parse("Ntfx")] = ["File"],
        [PoolTag.Parse("FMfc")] = ["File"],
        [PoolTag.Parse("FMsl")] = ["File"],
        [PoolTag.Parse("ObHd")] = ["*"],
        [PoolTag.Parse("Obtb")] = ["*"],
    };

    /// <summary>Token objects and the security structures allocated with them.</summary>
    public static readonly IReadOnlyList<PoolTag> TokenFamily = [PoolTag.Parse("Toke"), PoolTag.Parse("SeAt"), PoolTag.Parse("SeTd"), PoolTag.Parse("SeDt"), PoolTag.Parse("SeTl")];

    /// <summary>Tags written to CSV and tracked even when small (e.g. Toke must be shown to stay flat).</summary>
    public static readonly IReadOnlySet<PoolTag> AlwaysTracked = new HashSet<PoolTag>(Table.Keys);
}
