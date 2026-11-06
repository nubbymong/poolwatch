using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Native libraries (pdh.dll, wintrust.dll, advapi32.dll, ntdll.dll, user32.dll, ...) are loaded from
// System32 only, never from the application directory or the current directory, so an elevated PoolWatch cannot be
// made to load a planted DLL from a user-writable folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

// The app-layer tests (tests/PoolWatch.AppTests) reach the internal classes.
[assembly: InternalsVisibleTo("PoolWatch.AppTests")]
