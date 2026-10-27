# Third-party notices

PoolWatch is MIT-licensed ([LICENSE](LICENSE)). Its release folders also contain the third-party software below. Each
item says what ships, where it comes from, and the licence its NuGet package declares. PoolWatch does not ship, start
or depend on Sysinternals tools (RAMMap, PoolMon): it only recognises a scheduled task of yours that runs RAMMap.

## What ships

| Component | Version | In the release as | Licence (as declared by the package) |
|---|---|---|---|
| Microsoft.Diagnostics.Tracing.TraceEvent ([PerfView](https://github.com/microsoft/perfview)) | 3.1.30 | `Microsoft.Diagnostics.Tracing.TraceEvent.dll`, `Microsoft.Diagnostics.FastSerialization.dll`, `Dia2Lib.dll`, `TraceReloggerLib.dll` (inside `PoolWatch.exe`) | MIT, © Microsoft Corporation |
| TraceEvent's native DLLs | from TraceEvent 3.1.30 | `amd64\KernelTraceControl.dll` (Windows Performance Toolkit, 10.0.22000.1) and `amd64\msdia140.dll` (the Debug Interface Access SDK, 14.50.35403.0), next to the exe; both Microsoft binaries, Authenticode-signed by Microsoft Corporation | Microsoft redistributable binaries. Microsoft ships them inside the TraceEvent NuGet package, which declares MIT (see below), and distributes them under Microsoft's own terms; they are not themselves open source. See the note below. |
| Microsoft.Diagnostics.NETCore.Client ([dotnet/diagnostics](https://github.com/dotnet/diagnostics)) | 0.2.510501 | inside `PoolWatch.exe` (a TraceEvent dependency) | MIT, © Microsoft Corporation |
| Microsoft.Extensions.DependencyInjection, .DependencyInjection.Abstractions, .Logging, .Logging.Abstractions, .Options, .Primitives ([dotnet/runtime](https://github.com/dotnet/runtime)) | 6.0.0 | inside `PoolWatch.exe` (dependencies of Microsoft.Diagnostics.NETCore.Client) | MIT, © .NET Foundation and Contributors |
| System.Management ([dotnet/runtime](https://github.com/dotnet/runtime)) | 10.0.0 | inside `PoolWatch.exe` | MIT, © .NET Foundation and Contributors |
| .NET runtime and Windows Desktop runtime (Microsoft.NETCore.App, Microsoft.WindowsDesktop.App) | the .NET 10 patch the release was built with | inside `PoolWatch-selfcontained.exe` (self-contained download only) | MIT, © .NET Foundation and Contributors; the runtime's own third-party notices are in [dotnet/runtime THIRD-PARTY-NOTICES.TXT](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT) and [dotnet/winforms THIRD-PARTY-NOTICES.TXT](https://github.com/dotnet/winforms/blob/main/THIRD-PARTY-NOTICES.TXT) |

The test projects use xUnit and the .NET test SDK; they are not part of a release.

### Note on the native DLLs

`KernelTraceControl.dll` and `msdia140.dll` are the only non-managed, non-.NET-runtime binaries in a
framework-dependent release. They are Microsoft components (Windows Performance Toolkit and the Debug Interface Access
SDK), carried inside the TraceEvent NuGet package by its publisher, Microsoft. The package itself carries **no licence
file and no third-party-notice file**; its `.nuspec` is the only licence statement, and it declares:

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

## What was checked

For each package above: the `<license>` element of its `.nuspec` in the NuGet package (all `MIT` expressions); the
`LICENSE.TXT` / `LICENSE` and `THIRD-PARTY-NOTICES.TXT` files the package carries where it has them (the Extensions
packages, System.Management and the runtime packs; **TraceEvent and NETCore.Client carry none** — I listed the TraceEvent
package's entries and found no licence, notice, readme or redist text file, only the `.nuspec`, the `lib/` managed DLLs
and the `build/native/` DLLs); the version resources and Authenticode signers of the DLLs in a framework-dependent build
(every one is signed by Microsoft Corporation); and which assemblies the build actually copies
(`System.Reflection.TypeExtensions` and the other TraceEvent dependencies resolve to the shared framework and are not
shipped).

## MIT licence texts

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
