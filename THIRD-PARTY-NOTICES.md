# Third-party notices

PoolWatch is MIT-licensed ([LICENSE](LICENSE)). Its releases (the installer and the two zip downloads) also contain the
third-party software below. Each item says what ships, where it comes from, and the licence it is distributed under.
PoolWatch does not ship, start or depend on Sysinternals tools (RAMMap, PoolMon): it only recognises a scheduled task of
yours that runs RAMMap. Work PoolWatch builds on without shipping it is credited under
[Credited, not shipped](#credited-not-shipped).

## What ships

| Component | Version | In the release as | Licence (as declared by the package) |
|---|---|---|---|
| Microsoft.Diagnostics.Tracing.TraceEvent ([PerfView](https://github.com/microsoft/perfview)) | 3.1.30 | `Microsoft.Diagnostics.Tracing.TraceEvent.dll`, `Microsoft.Diagnostics.FastSerialization.dll`, `Dia2Lib.dll`, `TraceReloggerLib.dll` (inside `PoolWatch.exe`) | MIT, © Microsoft Corporation |
| TraceEvent's native DLLs | from TraceEvent 3.1.30 | `amd64\KernelTraceControl.dll` (Windows Performance Toolkit, 10.0.22000.1) and `amd64\msdia140.dll` (the Debug Interface Access SDK, 14.50.35403.0), next to the exe; both Microsoft binaries, Authenticode-signed by Microsoft Corporation | Microsoft redistributable binaries. Microsoft ships them inside the TraceEvent NuGet package, which declares MIT (see below), and distributes them under Microsoft's own terms; they are not themselves open source. See the note below. |
| Microsoft.Diagnostics.NETCore.Client ([dotnet/diagnostics](https://github.com/dotnet/diagnostics)) | 0.2.510501 | inside `PoolWatch.exe` (a TraceEvent dependency) | MIT, © Microsoft Corporation |
| Microsoft.Extensions.DependencyInjection, .DependencyInjection.Abstractions, .Logging, .Logging.Abstractions, .Options, .Primitives ([dotnet/runtime](https://github.com/dotnet/runtime)) | 6.0.0 | inside `PoolWatch.exe` (dependencies of Microsoft.Diagnostics.NETCore.Client) | MIT, © .NET Foundation and Contributors |
| System.Management ([dotnet/runtime](https://github.com/dotnet/runtime)) | 10.0.0 | inside `PoolWatch.exe` | MIT, © .NET Foundation and Contributors |
| .NET runtime and Windows Desktop runtime (Microsoft.NETCore.App, Microsoft.WindowsDesktop.App) | the .NET 10 patch the release was built with | inside `PoolWatch-selfcontained.exe` (the self-contained download) and the installer, which installs that build | MIT, © .NET Foundation and Contributors; the runtime's own third-party notices are in [dotnet/runtime THIRD-PARTY-NOTICES.TXT](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT) and [dotnet/winforms THIRD-PARTY-NOTICES.TXT](https://github.com/dotnet/winforms/blob/main/THIRD-PARTY-NOTICES.TXT). The self-contained download and the installer also carry the runtime packs' own files, in `licenses\Microsoft.NETCore.App\` (`LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT`) and `licenses\Microsoft.WindowsDesktop.App\` (`LICENSE.TXT`, the pack's `LICENSE`; that pack carries no notices file) |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | 6.7.3 | the installer's setup program and the uninstaller, `unins000.exe` | the Inno Setup License (below): permissive, any purpose including commercial, keeping its copyright notice; © Jordan Russell, portions © Martijn Laan |

The test projects use xUnit (Apache 2.0) and the .NET test SDK (MIT); they are not part of a release.

### Note on the native DLLs

`KernelTraceControl.dll` and `msdia140.dll` are the only non-managed, non-.NET-runtime binaries in a
framework-dependent release (the installer adds Inno Setup's `unins000.exe`). They are Microsoft components (Windows
Performance Toolkit and the Debug Interface Access SDK), carried inside the TraceEvent NuGet package by its publisher,
Microsoft. The package itself carries **no licence file and no third-party-notice file**; its `.nuspec` is the only
licence statement, and it declares:

```xml
<license type="expression">MIT</license>
<licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>
<copyright>© Microsoft Corporation. All rights reserved.</copyright>
<projectUrl>https://github.com/Microsoft/perfview</projectUrl>
```

So the MIT expression is Microsoft's declared licence for the package as it distributes it. The two native DLLs are
Microsoft redistributable binaries, not open-source projects in their own right; this notice does not claim any licence
over them beyond Microsoft's redistribution of them inside this MIT-licensed package. PerfView's repository
(github.com/microsoft/perfview, `documentation`) is the upstream source for how these files are produced and shipped.

## Credited, not shipped

| Work | What PoolWatch takes from it | Licence |
|---|---|---|
| [bentoner/windows-token-leak](https://github.com/bentoner/windows-token-leak) | The documentation of the Windows 11 token leak: the builds it was seen on and how it happens. No code or text is copied. | MIT |
| [gunrgbcr/windows-token-leak-logon-fix](https://github.com/gunrgbcr/windows-token-leak-logon-fix) | The idea of the workaround (setting the live foreground lock timeout to 0 at logon) and of testing for the leak first. PoolWatch's token-leak guard is an independent re-implementation from the public description; no code or text is copied. | None declared: the repository has no licence file, none in its README and none in GitHub's licence field (checked 2026-10-28), so nothing from it is redistributed |
| [Faarfarin, openai/codex issue 31212](https://github.com/openai/codex/issues/31212#issuecomment-5635622083) | Tracing the ChatGPT (Codex) desktop app's GPU shader-cache file storm, and the `--disable-gpu-shader-disk-cache` workaround (a Chromium command-line switch) that PoolWatch's Codex shortcut passes. | A public comment: credit only |
| [Sysinternals RAMMap](https://learn.microsoft.com/sysinternals/downloads/rammap) | Nothing: PoolWatch recognises a scheduled task of yours that runs it, and never ships, starts or changes RAMMap. | Sysinternals licence terms, which do not allow redistributing it |
| `pooltag.txt` ([Debugging Tools for Windows](https://learn.microsoft.com/windows-hardware/drivers/debugger/)) | Pool tag names, read at run time from your own Debugging Tools install when it is there; never shipped. PoolWatch's built-in tag table is its own short descriptions; a few entries are the standard names of kernel object types and read the same as `pooltag.txt`'s (such as "Event objects"). | Part of the Windows SDK, not redistributed |
| Icons and fonts | Nothing: PoolWatch's icons are its own work, the windows use Segoe UI (Windows' own font, not shipped), and any installer image that is not PoolWatch's own is Inno Setup's, under its licence above. | none needed |

## What was checked

For each package above: the `<license>` element of its `.nuspec` in the NuGet package (all `MIT` expressions); the
`LICENSE.TXT` / `LICENSE` and `THIRD-PARTY-NOTICES.TXT` files the package carries where it has them (the Extensions
packages, System.Management and the runtime packs; **TraceEvent and NETCore.Client carry none** — I listed the TraceEvent
package's entries and found no licence, notice, readme or redist text file, only the `.nuspec`, the `lib/` managed DLLs
and the `build/native/` DLLs); the version resources and Authenticode signers of the DLLs in a framework-dependent build
(every one is signed by Microsoft Corporation); and which assemblies the build actually copies
(`System.Reflection.TypeExtensions` and the other TraceEvent dependencies resolve to the shared framework and are not
shipped).

Re-checked on 2026-10-28 for the installer release: every version above against `packages.lock.json` (unchanged); the
.NET 10.0.14 runtime packs (Microsoft.NETCore.App.Runtime.win-x64 carries `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT`,
Microsoft.WindowsDesktop.App.Runtime.win-x64 carries `LICENSE` only); Inno Setup's licence as published at
jrsoftware.org; the licence files of the two token-leak repositories; and PoolWatch's 48 built-in tag descriptions
against `pooltag.txt` from the Windows 10 SDK Debugging Tools (7 short object-type names read the same; the rest are
PoolWatch's own words).

## Licence texts

**TraceEvent, Microsoft.Diagnostics.NETCore.Client** (these packages carry no licence file: the standard MIT text, with
the copyright holder from their package metadata)

```
MIT License

Copyright (c) Microsoft Corporation.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

**Microsoft.Extensions.\*, System.Management, the .NET runtime and Windows Desktop runtime** (as in the `LICENSE.TXT` /
`LICENSE` of the Extensions packages and the runtime packs; System.Management 10.0.0 carries only its
`THIRD-PARTY-NOTICES.TXT` and declares the same MIT licence)

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

**Inno Setup** (the licence text as published at jrsoftware.org/files/is/license.txt, checked 2026-10-28; the copy in the
Inno Setup version used is the one that applies)

```
Inno Setup License
==================

Except where otherwise noted, all of the documentation and software included in the Inno
Setup package is copyrighted by Jordan Russell.

Copyright (C) 1997-2026 Jordan Russell. All rights reserved.
Portions Copyright (C) 2000-2026 Martijn Laan. All rights reserved.

This software is provided "as-is," without any express or implied warranty. In no event shall
the author be held liable for any damages arising from the use of this software.

Permission is granted to anyone to use this software for any purpose, including commercial
applications, and to alter and redistribute it, provided that the following conditions are met:

1. All redistributions of source code files must retain all copyright notices that are currently
   in place, and this list of conditions without modification.

2. All redistributions in binary form must retain all occurrences of the above copyright notice
   and web site addresses that are currently in place (for example, in the About boxes).

3. The origin of this software must not be misrepresented; you must not claim that you wrote
   the original software. If you use this software to distribute a product, an acknowledgment
   in the product documentation would be appreciated but is not required.

4. Modified versions in source or binary form must be plainly marked as such, and must not
   be misrepresented as being the original software.


Jordan Russell
jr-2020 AT jrsoftware.org
https://jrsoftware.org/
```
