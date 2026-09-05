# Initial dependency and toolchain manifest

Recorded 2026-09-04 for the discovery-only milestone. This is not a complete
manifest for the future native DSP, desktop, plugin or FreeDV builds.

| Component | Pin/source | Scope |
| --- | --- | --- |
| SDR-VST3 baseline | `3518930bc12b976457130d681a5a80b569232e31`, `https://github.com/nubbyless/SDR-VST3.git` | First 15 reviewed commits adopted by fast-forward from `275f7683`. |
| Later-feature reference | `80538e3884aff03f7e013ce7e7bae9723700cc65` | FreeDV-related commits remain deferred. |
| Original VST reference | `cc62ad73d99131ea005762adef947fb0bebba8a0`, `https://github.com/ChasingCoffee/Thetis-VST.git` | Behavioral reference only; not merged. |
| .NET SDK | `global.json`: 10.0.400, `latestPatch`, prereleases disabled | New managed solution. Root SDK selection also applies when using dotnet in the legacy tree. |
| Microsoft.NET.Test.Sdk | 18.9.0 | Test project only. |
| MSTest.TestAdapter / TestFramework | 4.4.0 | Test project only. |
| NuGet transitive dependencies | `tests/Thetis.Core.Tests/packages.lock.json` | Resolved versions and content hashes; use locked restore. |
| piHPSDR simulator | `f6c17bd4347a2d80cdf6080c3c19dbd915648cdc`, `https://github.com/g0orx/pihpsdr.git` | External optional test tool, not linked or bundled with the application. |

The portable Core and Headless application projects have no NuGet package or
native DSP dependency. They link the existing discovery and enum files rather
than copying their implementations. Existing copyright/license headers remain
intact. Dependency notices remain with the original source and downloaded
packages; review distribution requirements before shipping broader binaries.

Local verification environment:

- macOS 26.6, arm64; .NET SDK 10.0.400 / runtime 10.0.11.
- MSBuild 18.9.6 from that SDK.
- Apple Clang 21.0.0; simulator built with `-O2 -pthread ... -lm`, no source patches.
- CMake 4.4.2 is available but not used by this managed milestone.
- No native Windows/Linux execution, Parallels VM setup, WDSP build or RADE model recovery performed.

The build/test workflow also pins checkout, setup-dotnet and setup-msbuild to
immutable Git revisions. Update pins and package locks deliberately, with tests.
Native dependency source revisions, architecture options, patches and model
hashes must be added before their respective implementation milestones.
