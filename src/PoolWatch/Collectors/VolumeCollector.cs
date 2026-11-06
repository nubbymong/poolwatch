using System.Runtime.InteropServices;
using PoolWatch.Core.Model;
using PoolWatch.Native;

namespace PoolWatch.Collectors;

/// <summary>Volumes: letter -> file system, label, drive type and Dev Drive flag.</summary>
internal static unsafe class VolumeCollector
{
    // CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 143, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint FSCTL_QUERY_PERSISTENT_VOLUME_STATE = 0x0009023C;
    private const uint PERSISTENT_VOLUME_STATE_DEV_VOLUME = 0x00002000;
    private const uint PERSISTENT_VOLUME_STATE_TRUSTED_VOLUME = 0x00004000; // winioctl.h: an administrator trusted the volume
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_FS_PERSISTENT_VOLUME_INFORMATION
    {
        public uint VolumeFlags, FlagMask, Version, Reserved;
    }

    public static List<VolumeInfo> Query()
    {
        var list = new List<VolumeInfo>();
        char* label = stackalloc char[261];
        char* fs = stackalloc char[261];
        foreach (var root in Environment.GetLogicalDrives())
        {
            uint type = NativeMethods.GetDriveTypeW(root);
            string typeName = type switch { 2 => "Removable", 3 => "Fixed", 4 => "Network", 5 => "CDRom", 6 => "RamDisk", _ => "Unknown" };
            if (type is 2 or 4 or 5) { list.Add(new VolumeInfo(root, "", "", null, typeName)); continue; } // never touch slow / network media
            string fsName = "", labelName = "";
            if (NativeMethods.GetVolumeInformationW(root, label, 261, out _, out _, out _, fs, 261))
            {
                fsName = new string(fs);
                labelName = new string(label);
            }
            list.Add(new VolumeInfo(root, fsName, labelName, QueryDevDrive(root), typeName));
        }
        return list;
    }

    /// <summary>
    /// True if the volume is a Dev Drive. A file system or OS that does not implement the query (Windows Server,
    /// older builds, FAT) cannot host a Dev Drive, so "not supported" answers false; null means the volume
    /// could not be queried at all.
    /// </summary>
    public static bool? QueryDevDrive(string root) => QueryDevDrive(root, out _);

    public static bool? QueryDevDrive(string root, out string detail) => QueryFlag(root, PERSISTENT_VOLUME_STATE_DEV_VOLUME, "not a Dev Drive", out detail);

    /// <summary>
    /// True if a Dev Drive is trusted (at its creation by default, or with <c>fsutil devdrv trust</c>): only then does Microsoft
    /// Defender scan it in performance mode. The same query and answers as <see cref="QueryDevDrive(string)"/>.
    /// </summary>
    public static bool? QueryDevDriveTrusted(string root) => QueryFlag(root, PERSISTENT_VOLUME_STATE_TRUSTED_VOLUME, "not trusted", out _);

    /// <summary>One persistent-volume-state flag; <paramref name="unsupported"/> says what "the query is not supported" means.</summary>
    private static bool? QueryFlag(string root, uint flag, string unsupported, out string detail)
    {
        detail = "";
        bool? answer = null;
        // The FSCTL is documented against a volume handle; a root-directory handle also works on most builds.
        foreach (var target in new[] { @"\\.\" + root.TrimEnd('\\'), root })
        {
            var h = NativeMethods.CreateFileW(target, 0, 7 /* share all */, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h == NativeMethods.InvalidHandle) { detail += $"{target}: open error {Marshal.GetLastWin32Error()}; "; continue; }
            try
            {
                var input = new FILE_FS_PERSISTENT_VOLUME_INFORMATION { Version = 1, FlagMask = flag };
                var output = new FILE_FS_PERSISTENT_VOLUME_INFORMATION();
                if (NativeMethods.DeviceIoControl(h, FSCTL_QUERY_PERSISTENT_VOLUME_STATE, &input, sizeof(FILE_FS_PERSISTENT_VOLUME_INFORMATION),
                        &output, sizeof(FILE_FS_PERSISTENT_VOLUME_INFORMATION), out _, IntPtr.Zero))
                {
                    detail += $"{target}: flags 0x{output.VolumeFlags:x}";
                    return (output.VolumeFlags & flag) != 0;
                }
                int err = Marshal.GetLastWin32Error();
                detail += $"{target}: FSCTL error {err}; ";
                if (err is 1 or 50 or 87) answer = false; // invalid function / not supported / invalid parameter
            }
            finally { NativeMethods.CloseHandle(h); }
        }
        if (answer == false) detail += $"query not supported here, so {unsupported}";
        return answer;
    }
}
