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
`paDeviceUnavailable`. Outbound samples are discarded. Socket translation units,
RNet, protocol loops and diagnostic file-writing helpers are excluded from this
target; the inherited sources remain available for the next part of the port.

CM input producers are joined before pipe/DSP consumers are freed. Mixers are
joined before callback targets, resamplers and semaphores are released. Mixer
wait-all adaptation is restricted to its single-consumer semaphore inputs, not
a general Win32 emulation. Windows WDSP channel workers now retain joinable
handles as well; other advanced WDSP completion handshakes still require the
streaming/concurrency audit in M3b.

## Validation and fixes

Native tests exercise 100 complete lifecycles, exact topology/callback counts,
OS thread counts after each close, repeated close, duplicate-open rejection,
invalid options, device/TX rejection, and cancellation/failure after each of
three completed startup stages. The managed tests also force GC during startup,
contain callback exceptions, verify exclusive ownership and reopen at 96 kHz.
The CLI independently repeats 100 sessions through the .NET owner.

The legacy radio initializer now receives its missing seventh argument from the
existing “Inform hardware of ports to use” checkbox. The extracted, shared P2
port-selection rule has native and managed tests: 1024 maps to 1025 in either
mode; discovery at 5000 maps to 1025 normally or 5001 only with relocation enabled.
The portable harness does **not** call `nativeInitMetis` yet. Full seven-argument
socket-start integration and the legacy Windows UI build remain unverified.

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

Current local checks: native four-test suite and 100-cycle CLI pass on macOS
arm64; all 58 managed tests pass. Post-fix leak/sanitizer and Windows/Linux CI
qualification are being recorded separately before marking the slice qualified.

## Remaining M3 / before M4

- Port RNet creation, sockets/events/timers and packet-worker shutdown, preserving
  the P1/P2 algorithms. Test without contacting hardware first.
- Integrate the corrected radio-init ABI with validated endpoint/model/port
  options; exercise default/custom P2 ports through the actual socket initializer.
- Extend rollback into transport startup, including partial socket/worker failure.
- Audit active streaming, advanced WDSP background work and shutdown races;
  these tests send no samples through a live radio pipeline.
- Legacy allocation/thread constructors remain fail-fast. Startup rollback here
  handles completed component boundaries, cancellation and checkpoint failures;
  it does not promise recoverable out-of-memory or partial constructor failure.

Only after that boundary is qualified should the G2/P2 receive milestone begin.
