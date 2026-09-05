# Initial dependency and toolchain manifest

Recorded 2026-09-04 for discovery and the initial native WDSP milestone.
This is not a complete manifest for future ChannelMaster, desktop, plugin or
FreeDV builds. See [native results](M2_NATIVE_RESULTS.md) for qualification limits.

| Component | Pin/source | Scope |
| --- | --- | --- |
| SDR-VST3 baseline | `3518930bc12b976457130d681a5a80b569232e31`, `https://github.com/nubbyless/SDR-VST3.git` | First 15 reviewed commits adopted by fast-forward from `275f7683`. |
| Later-feature reference | `80538e3884aff03f7e013ce7e7bae9723700cc65` | FreeDV-related commits remain deferred. |
| Original VST reference | `cc62ad73d99131ea005762adef947fb0bebba8a0`, `https://github.com/ChasingCoffee/Thetis-VST.git` | Behavioral reference only; not merged. |
| Authoritative WDSP reference | `584e8aca5ba1c4c6bc66fc0cc164ce567c8ba1e3`, `https://github.com/TAPR/OpenHPSDR-wdsp.git`, `wdsp 2.00/Source` | Source/API reference; not merged. Local source is a modified 2.00-derived baseline, including older PureSignal code; see [WDSP review](WDSP_BASELINE_REVIEW.md). |
| FFTW | `https://www.fftw.org/fftw-3.3.10.tar.gz`; SHA-256 `56c932549852cddcfafdab3820b0200c7742675be92179e59e6215b340e26467` | Source-built static double/float libraries; no FFTW worker threads, OpenMP, Fortran wrappers or optional SIMD kernels in this baseline. |
| RNNoise / libspecbleach | Sources under `Project Files/lib/NR_Algorithms_x64/src` at the adopted SDR-VST3 revision | Compiled in place; existing generated binaries ignored. RNNoise selects NEON on arm64; SSE2 where the compiler advertises it, otherwise scalar C. No CPU runtime dispatch or fast-math flags. |
| Recovered RNNoise headers | Xiph RNNoise `70f1d256acd4b34a572f999a05c87bf00b67730d` | Two missing headers only; [provenance and retained notices](../native/third_party/rnnoise/README.md). |
| .NET SDK | `global.json`: 10.0.400, `latestPatch`, prereleases disabled | New managed solution. Root SDK selection also applies when using dotnet in the legacy tree. |
| Microsoft.NET.Test.Sdk | 18.9.0 | Test project only. |
| MSTest.TestAdapter / TestFramework | 4.4.0 | Test project only. |
| NuGet transitive dependencies | `tests/Thetis.Core.Tests/packages.lock.json` | Resolved versions and content hashes; use locked restore. |
| piHPSDR simulator | `f6c17bd4347a2d80cdf6080c3c19dbd915648cdc`, `https://github.com/g0orx/pihpsdr.git` | External optional test tool, not linked or bundled with the application. |

The portable Core, Engine and Headless application projects have no NuGet package
dependencies. Headless references Engine, but native loading occurs only for the
explicit DSP command. Discovery links the existing discovery and enum files rather
than copying their implementations. Existing copyright/license headers remain
intact. Dependency notices remain with the original source and downloaded
packages; review distribution requirements before shipping broader binaries.

Local verification environment:

- macOS 26.6, arm64; .NET SDK 10.0.400 / runtime 10.0.11.
- MSBuild 18.9.6 from that SDK.
- Apple Clang 21.0.0; simulator built with `-O2 -pthread ... -lm`, no source patches.
- CMake 4.4.2 builds native Release and instrumented RelWithDebInfo targets.
- No native Windows/Linux execution, Parallels VM setup or RADE model recovery performed.

The build/test workflow also pins checkout, setup-dotnet and setup-msbuild to
immutable Git revisions. Update pins and package locks deliberately, with tests.
The vendored RNNoise `package_version` says `unknown`; its embedded model is
pinned by repository content, not an inferred model-release label:

- `rnnoise_data_little.c`: SHA-256 `eea1aa6fd8726d161876508a6f7d79022bf6ec82508660b4c1ac27c4f9b76c04`.
- `rnnoise_data_little.h`: SHA-256 `09ff880bddd0fc74a2ae0e5ec6c8d65714031b08d0c3f672493acd9e189c5855`.

The native output statically includes dependencies. Preserve their source and
license notices (WDSP/FFTW GPL, RNNoise BSD-style, libspecbleach LGPL) when
preparing distribution artifacts; packaging and embedded-model training
provenance review are not completed by this build milestone.
