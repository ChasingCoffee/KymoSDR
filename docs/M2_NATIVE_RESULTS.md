# M2 native DSP results

Recorded 2026-09-04 on macOS 26.6 arm64, Apple Clang 21.0.0, CMake 4.4.2,
.NET SDK 10.0.400/runtime 10.0.11. Discovery checkpoint: `77792260`.

## Verified locally

- Source-built FFTW 3.3.10 double and float, vendored RNNoise (ARM NEON path)
  and libspecbleach link into `libthetis_wdsp.dylib` with the existing WDSP code.
  Compiler dependency files confirm `vec_neon.h` is used on arm64.
- `file` reports a Mach-O 64-bit arm64 library. `otool -L` lists only the library
  itself and `/usr/lib/libSystem.B.dylib`, with no Homebrew, Intel or Windows DLL
  dependency. This observation is specific to this macOS build.
- Native CTest: 2/2 passed. Coverage includes ABI widths, invalid ABI output
  capacity, atomics, aligned allocation, recursive locks, semaphore counts and
  waits, event reset/coalescing, asynchronous dispatch, thread joins, NR3/NR4
  processing, and 20 receiver/sync-buffer create/process/resize/destroy cycles.
  Analyzer pixel/reference outputs are also surrounded by canaries in the native
  test, including its fifth `GetPixels` output argument.
- The same native tests pass with AddressSanitizer and UndefinedBehaviorSanitizer
  enabled (halt-on-error). This is tested-path memory/UB evidence, not a complete
  race/leak audit. FFTW archives are not instrumented.
- Managed Release build: zero warnings/errors. 51/51 tests pass with the native
  directory set: 47 Core/CLI tests and 4 Engine tests. The latter include 20
  analyzer/receiver lifecycle cycles in addition to the signal checks.
- The offline CLI self-test passes all 11 checks. A representative run took
  41 ms excluding process startup; this is not a real-time performance benchmark.
- A macOS x86-64 cross-build also compiles and links, exercising the RNNoise
  SSE2 source path. Those binaries were not executed. The arm64 .NET harness
  correctly rejects the x86-64 library with exit code 3 and an architecture
  diagnostic. This does not establish Windows/Linux or Intel Mac runtime support.
- Dynamic-loader tracing of ordinary CLI help shows no WDSP, FFTW, RNNoise or
  libspecbleach load. Native DSP remains opt-in.

Local build logs, final TRX files and native test logs are under ignored
`artifacts/native/`, `artifacts/native-asan/` and
`artifacts/test-results/m2-final/`. These local measurements were recorded before
the native implementation was committed and published under the KymoSDR project
name. Cloud CI results are tracked separately from this local validation record.

Representative signal results (full limits and recipes in the
[fixture definition](../tests/fixtures/dsp/README.md)):

| Check | Observed | Acceptance |
| --- | --- | --- |
| Four resampling rate pairs, 1500 Hz tone | Complex RMS approximately 0.2 | 0.2 ± 0.002; exact sample counts |
| 96->48 kHz resampler, 35 kHz input | Complex RMS `1.5653e-11` | Below `0.0002` |
| Impulse flush/replay | Maximum difference 0 | Below `1e-12`; finite/nonempty output |
| Analyzer, 1500 Hz at 48 kHz | Pixel 544; approximately -20.00073 dB | Pixel 544 ± 1; -20 ± 0.2 dB, not dBm |
| Analyzer local ABI extension | Reference 14.2 returned | Exact supplied value |
| USB receiver passband | RMS approximately 0.282843 | Between 0.001 and 1 |
| USB receiver stopband/passband ratio | Approximately -147.32 dB | Below -50 dB |

The initial analyzer fixture used receiver-style I/Q ordering and treated pixels
as raw bins. Source inspection showed `Spectrum0` consumes Q/I pairs and its
pixel interpolation can attenuate a narrow peak. The adapter now orders samples
correctly and uses 1024 positive-peak pixels for a 2048-point FFT. No analyzer
algorithm was changed to satisfy the tests.

## Changes and remaining qualification

The port adds source-build definitions, missing upstream RNNoise headers with
provenance, a POSIX support layer, explicit native loading/ABI checks and offline
diagnostics. Existing DSP algorithms and the modified 2.00/PureSignal baseline
are retained. Native compilation still reports pre-existing assignment-in-
condition warnings; these were not broadly rewritten or suppressed.

Windows x64 and Linux execution remain pending. Their build/test jobs are added,
including a Linux sanitizer job, but no push or CI execution was performed.
The legacy Windows solution has not been rebuilt here. **M2 acceptance is
therefore partial**, not a claim of qualified cross-platform DSP parity.

No radio packets, RX/TX control commands, audio devices, VST hosts or FreeDV
components were involved in this batch. Parallels was not configured. Long-run
resource tests, real-time scheduling, full DSP/mode parity, installed radio
firmware recording and distribution/license packaging remain separate gates.
Next: Windows/Linux validation and M3 ChannelMaster lifecycle/ABI adaptation,
followed by the receive-only G2 milestone.
