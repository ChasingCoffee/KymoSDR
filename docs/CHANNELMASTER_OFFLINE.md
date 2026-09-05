# ChannelMaster offline lifecycle (M3a)

The portable harness now constructs and disposes ChannelMaster's DSP/pipe core
without WinForms, sockets, audio devices or transmit commands. This is the first
part of M3, **not** the complete native radio/transport port or live RX proof.

## Run

Build native and managed code as described in [Native DSP](NATIVE_DSP.md), then:

```sh
dotnet run --project src/Thetis.Headless -c Release --no-build -- session-selftest --native-dir ABSOLUTE_NATIVE_STAGE_DIRECTORY
```

For example, on macOS/Linux the directory is `$PWD/artifacts/native/stage/Release`;
on Windows use `(Resolve-Path artifacts/native/stage/Release).Path` in PowerShell.
The command performs 100 complete sessions and emits JSON containing the topology,
elapsed time and warm/final resident-memory observations. Exit codes are 0 pass,
1 failed check, 2 invalid options, 3 missing/incompatible native library,
4 startup/execution failure, and 130 cancellation. Help never loads native code.

Application code can use `using var session = OfflineRadioSession.Open(path)`.
Options are validated before loading; only no-device audio is supported. There
is no transmit-enable, tune or packet-input operation in the managed session API.
One session owns the shared channels at a time; DSP diagnostics reject concurrent
session ownership. `Dispose` is idempotent, with a SafeHandle fallback for abandoned
owners. Deterministic disposal is required for predictable shutdown timing.

## Preserved topology and ownership

The configuration comes from the inherited `CMCreateCMaster` setup:

| Item | Offline configuration |
| --- | --- |
| Input streams | 8: five RX, one mic/TX, two special |
| Receivers | 5, with 2 WDSP subchannels each |
| Transmitter | 1 allocated, inactive; no transport/TX control |
| Default input rates | RX/special 192 kHz; mic 48 kHz |
| Output rates | Audio 48 kHz; TX DSP 192 kHz |
| Allocation limits | Input 1.536 MHz; audio 48 kHz; TX output 384 kHz |
| Initial analyzers | Six, each with original 262144 maximum FFT allocation |
| CM workers | Eight input-buffer workers plus five VAC and five TCI mixers |
| Required callbacks | Native lifetime-stable scope/wave constructors; counted by tests |

The core is compiled into the existing `thetis_wdsp` module so WDSP channels,
analyzers and cache have a single owner; it is not a second copy of WDSP. The new
`ThetisCm*` ABI uses fixed-width values and synchronous startup checkpoints.
Native scope/wave callbacks are deliberately headless, not UI implementations.
VAC/TCI resamplers and mixers are real objects even though their run flags are off.

The explicit no-device backend applies on Windows too. PortAudio headers provide
types only: no PortAudio library, initialization or enumeration is linked. ASIO
construction is a no-op, starting ASIO fails, and starting VAC device audio returns
`paDeviceUnavailable`. Outbound samples are discarded. The original radio packet
engines and diagnostic file-writing helpers are excluded from the portable
target. The module now also contains a separate [RNet/loopback probe](TRANSPORT_LOOPBACK.md);
`OfflineRadioSession` itself still opens no socket.

CM input producers are joined before pipe/DSP consumers are freed. Mixers are
joined before callback targets, resamplers and semaphores are released. Mixer
wait-all adaptation is restricted to its single-consumer semaphore inputs, not
a general Win32 emulation. Windows WDSP channel workers now retain joinable
handles as well. The four PureSignal save/restore/calculation/turnoff workers
created with TX now also join on both platforms, after their existing completion
handshakes. Other advanced streaming work still needs the M3b concurrency audit.

## Validation and fixes

Native tests exercise 100 complete lifecycles, exact topology/callback counts,
OS thread counts after each close, repeated close, duplicate-open rejection,
invalid options, device/TX rejection, and cancellation/failure after each of
three completed startup stages. The managed tests also force GC during startup,
contain callback exceptions, verify exclusive ownership and reopen at 96 kHz.
The CLI independently repeats 100 sessions through the .NET owner.

The first Windows CI run built successfully but failed an exact process-thread
count assertion after about 31 seconds; its log did not capture the changed count.
The exact comparison also treats retiring OS/runtime helper threads as failures.
The final Windows run records a decrease from four threads to one. The test now
logs changes and requires
the process count not to exceed its startup ceiling; native-owned CM workers
must still be exactly zero. This is a distinction between OS process accounting
and explicit application worker ownership, not a relaxed native-worker leak limit.

The legacy radio initializer now receives its missing seventh argument from the
existing “Inform hardware of ports to use” checkbox. The extracted, shared P2
port-selection rule has native and managed tests: 1024 maps to 1025 in either
mode; discovery at 5000 maps to 1025 normally or 5001 only with relocation enabled.
M3a itself does **not** call `nativeInitMetis`. The subsequent separate loopback
probe now exercises the actual seven-argument initializer; the legacy Windows
UI build remains unverified.

Full-topology tests exposed ownership defects not covered by M2's RX-only tests:

- VAC teardown omitted its mixer; it now stops/releases that owner first.
- EER teardown omitted two legacy buffers.
- CFIR impulse construction omitted its temporary transition array.
- Sidetone teardown omitted its containing object.

These fixes preserve DSP/routing algorithms. The first macOS leak scan identified
423 allocations / 10,462,240 allocator-reported bytes before the last three fixes.
ASan/UBSan on macOS alone had not detected those leaks; an OS leak scan and Linux
LeakSanitizer are separate checks. Resident memory is diagnostic, not a portable
leak verdict; original full-size analyzer allocation also makes this harness
memory-heavy. It is not an optimized single-receiver engine.

At source `32332704`, Windows x64, macOS arm64 and Linux x64 CI pass the native
tests, 100-cycle .NET CLI and all 58 managed tests. Linux ASan/UBSan/LeakSanitizer
passes; local macOS ASan/UBSan passes, and the separate post-leak-fix macOS scan
reports zero leaks. Real local Ctrl-C exits 130 after cleanup; missing-library
startup exits 3. See [the detailed CI and resource record](NATIVE_CI_RESULTS.md)
for timings, memory observations and remaining qualification limits. M3a is an
offline lifecycle checkpoint, not completion of the full M3 gate.

## Remaining M3 / before M4

- Hardware restriction: the user's G2 currently has a receive-only antenna on
  ANT1. No transmit testing, PTT/MOX, tune, CW keying or transmit-enabling commands
  are authorized. Keep M3 transport tests offline/loopback; future live RX work
  must preserve this restriction until the user explicitly approves a change.
- [RNet/socket allocation and loopback probe](TRANSPORT_LOOPBACK.md) now cover the
  actual radio-init ABI and partial startup rollback. Qualify on all three OSes.
- Port the real P1/P2 packet workers and audit their shutdown, preserving the
  algorithms. The loopback reader/timer are not production radio workers.
- Audit active streaming, advanced WDSP background work and shutdown races;
  these tests send no samples through a live radio pipeline.
- Legacy allocation/thread constructors remain fail-fast. Startup rollback here
  handles completed component boundaries, cancellation and checkpoint failures;
  it does not promise recoverable out-of-memory or partial constructor failure.

Only after that boundary is qualified should the G2/P2 receive milestone begin.
