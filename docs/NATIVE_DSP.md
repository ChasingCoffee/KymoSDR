# Native WDSP build and offline checks

This M2 slice builds the project's modified WDSP 2.00 source in place. It does
not build ChannelMaster, contact a radio, open an audio device, or transmit.
See the [baseline review](WDSP_BASELINE_REVIEW.md) before substituting upstream
WDSP APIs or source files.

## Build

Requirements: .NET SDK selected by `global.json`, CMake 3.24+, and a C11 compiler.
On macOS, Xcode/Command Line Tools must provide the SDK and Clang. On Windows,
install Visual Studio's C++ desktop build tools and Windows SDK. On Linux,
install GCC/Clang and the usual C development tools. No system FFTW, RNNoise or
libspecbleach installation is required.

From the repository root:

```sh
cmake -S native -B artifacts/native -DCMAKE_BUILD_TYPE=Release
cmake --build artifacts/native --config Release --parallel 4
ctest --test-dir artifacts/native -C Release --output-on-failure
dotnet restore Thetis.CrossPlatform.slnx --locked-mode
dotnet build Thetis.CrossPlatform.slnx -c Release --no-restore
```

For the Windows Visual Studio generator, add `-A x64` to the configure command.
Use a separate build directory for each architecture/generator. On this Mac,
`/usr/local/share/dotnet/dotnet` works if `dotnet` is not on the terminal's PATH.

The first native build downloads SHA-256-pinned FFTW 3.3.10 and builds separate
static double/float libraries. It compiles the vendored noise-reduction sources
and embedded model, not the checked-in `.dll`, `.lib`, `.o` or `.lo` artifacts.
`-DTHETIS_FFTW_URL=/absolute/path/fftw-3.3.10.tar.gz` permits an offline archive;
the same required hash is enforced. Other source inputs are in the repository.

## Run the .NET self-test

macOS/Linux shell:

```sh
export THETIS_NATIVE_DIR="$PWD/artifacts/native/stage/Release"
dotnet run --project src/Thetis.Headless -c Release --no-build -- dsp-selftest --native-dir "$THETIS_NATIVE_DIR"
dotnet test Thetis.CrossPlatform.slnx -c Release --no-build --no-restore --blame-hang-timeout 2m --logger trx --results-directory artifacts/test-results
```

Windows PowerShell:

```powershell
$env:THETIS_NATIVE_DIR = (Resolve-Path artifacts/native/stage/Release).Path
dotnet run --project src/Thetis.Headless -c Release --no-build -- dsp-selftest --native-dir "$env:THETIS_NATIVE_DIR"
dotnet test Thetis.CrossPlatform.slnx -c Release --no-build --no-restore --blame-hang-timeout 2m --logger trx --results-directory artifacts/test-results
```

The directory must be absolute. The loader selects `thetis_wdsp.dll`,
`libthetis_wdsp.dylib`, or `libthetis_wdsp.so` and checks the versioned ABI record
before initializing the impulse cache. It deliberately does not search arbitrary
system library paths. This standalone engine owns one WDSP instance for the
process lifetime; it cannot hot-swap native libraries or share ownership with
the legacy Windows console. A failed ABI initialization requires a new process.

The command emits JSON with 11 checks and their explicit limits. Exit codes:
0 all passed; 1 a numerical check failed; 2 invalid options; 3 missing/incompatible
native library; 4 execution error; 130 cancellation. `dsp-selftest --help` does
not load native code. Cancellation is checked between operations, not inside a
blocking native buffer exchange. CTest timeouts and the managed test watchdog
bound hangs during automated testing.

Native CTest also executes NR3/NR4, ABI and lifecycle checks; the JSON command
does not score speech quality. [Fixture definitions](../tests/fixtures/dsp/README.md)
describe input generation, ordering, units, settling intervals and tolerances.
No test calls uncalibrated sample levels dBm. Persistent FFTW wisdom generation,
real-time scheduling and RF calibration remain separate work.

## Memory-safety build

With Clang or GCC:

```sh
cmake -S native -B artifacts/native-asan -DCMAKE_BUILD_TYPE=RelWithDebInfo -DTHETIS_SANITIZE=ON
cmake --build artifacts/native-asan --parallel 4
UBSAN_OPTIONS=halt_on_error=1:print_stacktrace=1 ASAN_OPTIONS=halt_on_error=1 ctest --test-dir artifacts/native-asan -C RelWithDebInfo --output-on-failure
```

This instruments WDSP, RNNoise, libspecbleach, the POSIX adaptation and native
tests. The external FFTW archives are not instrumented. macOS checks here do
not establish leak freedom or data-race freedom. Linux CI additionally requests
LeakSanitizer; ThreadSanitizer and long-running resource/real-time tests remain
future work. Do not load the sanitizer library into the normal CLI without the
appropriate sanitizer runtime setup.

## Build boundaries

- `native/CMakeLists.txt` derives its DSP translation-unit list from the existing
  `wdsp.vcxproj`. The legacy Windows project/build configuration is unchanged.
- `native/platform/` provides recursive mutexes, count-limited semaphores,
  auto/manual reset events, asynchronous work, atomics and aligned allocation.
  Wait deadlines use a monotonic clock; handles are process-local, not named IPC.
- POSIX DSP/sync-buffer workers are joined before storage is destroyed or
  resized. Other existing worker completion protocols remain in WDSP. This is
  not a complete concurrency audit of every advanced DSP feature.
- Windows uses its original OS primitives and scheduling. POSIX workers currently
  use ordinary scheduler priority; real-time/QoS tuning is not yet implemented.
- Windows-sized atomic fields use `LONG` (32 bits on every target); cache hash
  width follows pointer width. Cache persistence is disabled in the new harness.
- `Thetis.Engine` currently exposes explicit loading and synchronous offline
  diagnostics. Internal native bindings serialize configuration/planning and
  keep pinned buffers alive until native resamplers are destroyed. A production
  `RadioSession`/streaming API is deferred to M3.
- `BUILD_TESTING=OFF` omits native test executables and the test-only helper
  exports; the ABI probe and .NET self-test support remain available.

The [native CI workflow](../.github/workflows/native-dsp.yml) builds and tests the
host architecture on macOS/Linux and x64 on Windows. Windows x64, macOS arm64
and Linux x64 offline checks, including Linux sanitizer/leak checks, have passed;
see the [CI validation record](NATIVE_CI_RESULTS.md) for exact runners, commits,
test counts and remaining limits. The [initial local record](M2_NATIVE_RESULTS.md)
preserves the earlier macOS measurements.
