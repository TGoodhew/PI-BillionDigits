# Building PI-BillionDigits

This project is a VB.NET WinForms app that depends on **two** native pieces: GMP (`libgmp-10.dll`,
supplied by a NuGet package) and a small custom C++ allocator DLL (`GmpNativeAlloc.dll`, built from
source in this repo). Because the native allocator is a Visual C++ project, the build is **not** a
plain `dotnet build` — the recommended path is the bundled PowerShell script, which builds the native
DLL with Visual Studio MSBuild first and then the managed app.

## Prerequisites

- **Windows x64.** The app is x64-only (`PlatformTarget = x64`) and uses Win32 APIs throughout.
- **.NET SDK 10** (the project targets `net10.0-windows10.0.26100.0`, WinForms).
- **Visual Studio 2022+ with the "Desktop development with C++" workload** (for the
  `GmpNativeAlloc.vcxproj` native project and its MSBuild/`vswhere`). The Community edition is fine.
- Internet access on first build, to restore the NuGet packages below.

## Dependencies

| Dependency | Source | Notes |
|------------|--------|-------|
| `Math.Gmp.Native.NET` 2.0.6 | NuGet | The managed P/Invoke wrapper around GMP. Its own MSBuild targets are **excluded** (`<ExcludeAssets>build</ExcludeAssets>`) because they require .NET-Framework MSBuild; instead the `.vbproj` copies `libgmp-10.dll` directly from the package's `output\x64\` folder to the app output. |
| `PeterO.Numbers` 1.8.2 | NuGet | Arbitrary-precision helper. |
| `libgmp-10.dll` | from the NuGet package above | GMP's native library; copied to the output directory at build time. |
| `GmpNativeAlloc.dll` | `GmpNativeAlloc/GmpNativeAlloc.vcxproj` (this repo) | The custom VirtualAlloc/VirtualFree GMP allocator (issue #30). Built per-configuration into `GmpNativeAlloc\<Config>\` and copied to the app output. |

The build order (native DLL before the managed app) is expressed in the solution's
`ProjectDependencies` section; there is intentionally **no** `ProjectReference` to the `.vcxproj`
because the dotnet CLI cannot load VC++ targets.

## Recommended build & run (script)

`Run-PiCompute.ps1` is machine-independent: it locates Visual Studio MSBuild (via `vswhere`), builds
the `GmpNativeAlloc` native target, then runs `dotnet clean` + `dotnet build`, auto-detects the
produced exe under `bin\<Config>\`, and launches it.

```powershell
# Debug build (default) + standard 1B run
.\Run-PiCompute.ps1

# Release build
.\Run-PiCompute.ps1 -UseRelease
```

The project builds in **Debug** by default in this script. See the
[Command-line options](../README.md#command-line-options) and the script's own `Get-Help
.\Run-PiCompute.ps1 -Full` for the full parameter list.

## Building in Visual Studio

Open `PI-BillionDigits.sln` and build the solution. The `ProjectDependencies` entry ensures
`GmpNativeAlloc` is built before the managed project; both native DLLs are copied to the app output by
the `.vbproj` `None`/`CopyToOutputDirectory` items.

## Build configuration notes

- **`RemoveIntegerChecks = True`** in every configuration (Debug and Release). The 64-bit limb
  arithmetic relies on explicit, reasoned casts (see the §236/§237/§239 markers); any new narrowing
  cast must be justified because overflow checks are off.
- **`OptionStrict On`** — keep it; the interop code depends on explicit conversions.
- Output goes to `bin\<Config>\net10.0-windows10.0.26100.0\`; the run script globs for the exe rather
  than hardcoding the TFM folder.

See also [ARCHITECTURE.md](ARCHITECTURE.md) for how the pieces fit together at runtime.
