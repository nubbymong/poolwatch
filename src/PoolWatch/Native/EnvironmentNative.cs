using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PoolWatch.Native;

/// <summary>
/// The two Windows calls the AppData links fixes need besides the registry itself: the name the kernel gives an open
/// registry key (NtQueryKey, KeyNameInformation), so a key reached through a registry link is told apart from the one
/// asked for, and the broadcast that tells running programs the environment changed (WM_SETTINGCHANGE "Environment").
/// </summary>
internal static class EnvironmentNative
{
    /// <summary>KEY_INFORMATION_CLASS.KeyNameInformation (wdm.h).</summary>
    private const int KeyNameInformation = 3;

    /// <summary>The most bytes a key's name is read in: far more than the keys PoolWatch opens need; a longer name is not read.</summary>
    private const int MaxNameBytes = 4096;

    /// <summary>NTSYSAPI NTSTATUS ZwQueryKey(HANDLE KeyHandle, KEY_INFORMATION_CLASS KeyInformationClass, PVOID KeyInformation, ULONG Length, PULONG ResultLength).</summary>
    [DllImport("ntdll.dll")]
    private static extern int NtQueryKey(SafeRegistryHandle keyHandle, int keyInformationClass, IntPtr keyInformation, uint length, out uint resultLength);

    /// <summary>
    /// The open key's full name as the kernel has it ("\REGISTRY\USER\S-1-5-21-...\Environment"): where the handle really
    /// is, after any registry link on the way. Null when it cannot be read. KEY_NAME_INFORMATION is a ULONG NameLength (in
    /// bytes) followed by the name, which has no terminating null.
    /// </summary>
    public static string? KeyName(SafeRegistryHandle key)
    {
        IntPtr buffer = Marshal.AllocHGlobal(MaxNameBytes);
        try
        {
            if (NtQueryKey(key, KeyNameInformation, buffer, MaxNameBytes, out _) != NtStatus.Success) return null;
            int bytes = Marshal.ReadInt32(buffer);
            return bytes is > 0 and <= MaxNameBytes - 4 && bytes % 2 == 0 ? Marshal.PtrToStringUni(buffer + 4, bytes / 2) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>LRESULT SendMessageTimeoutW(HWND, UINT, WPARAM, LPARAM, UINT fuFlags, UINT uTimeout, PDWORD_PTR lpdwResult).</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeoutMs, out UIntPtr result);

    /// <summary>
    /// Tells every top-level window the environment changed, as Windows' own Environment Variables window does, so Explorer
    /// gives programs started from now on the new values. A window that does not answer is skipped (SMTO_ABORTIFHUNG).
    /// Programs already running keep the environment they started with. False when the broadcast failed or timed out.
    /// </summary>
    public static bool BroadcastEnvironmentChange() =>
        SendMessageTimeoutW(HwndBroadcast, WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 5000, out _) != IntPtr.Zero;
}
