# Lightweight G2/P2 receive simulator

`Thetis.Simulator` is a standalone .NET 10 UDP test peer for developing the
receive path while the G2 is unavailable. It needs no WDSP, native build,
Raspberry Pi software, audio devices or additional NuGet packages. It is an
independent synthetic implementation, **not** an FPGA or complete G2 emulator.

All sockets bind to `127.0.0.1`; all replies/streams are restricted to IPv4
loopback. There is no LAN bind option or hardware connection. TX/PTT/keying is
not emulated. The physical G2's receive-only ANT1 restriction remains in force.

## Quick start

From the repository root, using the same SDK as the port:

```sh
dotnet restore Thetis.CrossPlatform.slnx --locked-mode
dotnet build Thetis.CrossPlatform.slnx -c Release --no-restore
dotnet run --project src/Thetis.Simulator -c Release --no-build -- selftest
```

`selftest` starts a server/client on an available loopback port layout, checks
discovery, configures DDC2, receives 100 sequenced I/Q packets plus status and
mic silence, stops RX and closes both owners. It has a five-second deadline.
The JSON result identifies it as loopback-only. It does not use the native
radio receiver, so this does not yet demonstrate I/Q reaching WDSP.

Start an interactive peer in one terminal:

```sh
dotnet run --project src/Thetis.Simulator -c Release --no-build -- serve --base-port 51024 --duration-seconds 0
```

Then use the existing discovery CLI in another terminal:

```sh
dotnet run --project src/Thetis.Headless -c Release --no-build -- discover --protocol p2 --nic 127.0.0.1 --target 127.0.0.1 --port 51024 --allow-loopback --no-general-broadcast --timeout-ms 1000 --json
```

Ctrl-C stops the simulator and releases its sockets. Without an explicit
duration, `serve` stops after 300 seconds; `--duration-seconds 0` runs until
cancelled. `--base-port 0` chooses a free layout and reports it in the ready
JSON event. `--base-port 1024` uses standard P2 ports. A port conflict is an
error, not permission to share or take over another process's sockets.
Automatic selection retries up to 100 layouts, including Windows access-denied
port conflicts/reservations. Explicit ports still fail; no OS reservation or
firewall settings are changed. See [Winsock bind error semantics](https://learn.microsoft.com/en-us/windows/win32/winsock/windows-sockets-error-codes-2).

Example signal/fault settings:

```sh
dotnet run --project src/Thetis.Simulator -c Release --no-build -- serve --tone-hz 14200000 --amplitude 0.25 --noise 0.001 --seed 123 --drop-every 17
```

This represents a station at 14.200 MHz. Tuning a DDC to 14.199 MHz places its
tone at +1 kHz in complex baseband. `amplitude` is normalized sample peak, not
dBm or calibrated RF strength. Noise is deterministic seeded uniform noise.
Each DDC's 17th I/Q packet is omitted in this example; sequence and signal phase
advance through dropped packets. Reordering and recorded-I/Q playback are not
implemented yet.

For Parallels, run the simulator **inside the guest** to use its loopback address.
This version does not accept connections from another machine/VM. Compatibility
with the full legacy Windows console has not been established.

## Implemented wire subset

Let `B` be the selected base port. Six incoming sockets and ten DDC sockets use
16 UDP ports within the `B..B+20` layout:

| Direction / role | Port | Datagram |
| --- | --- | --- |
| Discovery and general configuration | B | 60 bytes |
| Receiver-specific control / outgoing status | B+1 | 1444 in / 60 out |
| TX-specific setup (ignored) / outgoing mic silence | B+2 | 60 in / 132 out |
| High-priority control, run/stop and tuning | B+3 | 1444 in |
| RX speaker audio (discarded) | B+4 | 260 in |
| TX I/Q (rejected; stops the session) | B+5 | Not emulated |
| Outgoing DDC0..DDC9 I/Q | B+11..B+20 | 1444 bytes, 238 complex samples |

Streams return to the IP **and source port of the accepted general packet**.
General-packet port fields must match the bound layout. Zero selects protocol
defaults only when B=1024; for custom layouts the client must send all matching
explicit fields. Arbitrary runtime port rebinding/NAT layouts are unsupported.

Ten independent, unsynchronized DDCs accept ADC0/ADC1 selection and rates
48/96/192/384 kHz, with signed 24-bit big-endian I/Q. Each stream has its own
32-bit sequence counter. Timestamps are zero. The simulator accepts frequency
or phase-word tuning; the phase-word path uses a 122.88 MHz reference and is the
path exercised by its CLI test. A tone outside the selected Nyquist passband is
omitted rather than aliased back into it. Antenna/ADC analog effects are not
modeled; ADC selection does not attach a physical or virtual RF front end.

Status is sent every 200 ms with zero PTT/CW/power/analog telemetry; mic packets
carry 48 kHz silence. No invented calibrated measurements are reported. TX
setup and speaker audio are discarded. A PTT/keying request or TX-I/Q packet
stops/releases the session and requires a new valid receive handshake.

Only the owning endpoint can control an active session. Once stopped, a new
valid general packet can claim the idle device immediately. A two-second
inactivity timeout releases stale ownership even if the client disables the
hardware watchdog; ignored audio/TX setup traffic does not refresh it. This is
a simulator safety/lifecycle policy, not an assertion of exact firmware timing.

One joinable socket worker owns mutable protocol state and buffers. Receive
batches and catch-up bursts are bounded, and cancellation joins before sockets
are released. Host scheduler stalls increment `pacingResyncs` and rebase the
wall-clock schedule without synthesizing extra sequence loss; this is not a
hard-real-time radio clock. Public state is an immutable snapshot/copy.

## Identity and provenance

Discovery uses locally administered MAC `02-4B-59-4D-4F-01`, board 10 (Saturn),
protocol field 43, firmware field 27, ten DDCs and beta field zero. These are a
**synthetic test profile**, not a claim to run firmware 27 or protocol 4.3 in
full. The ready event explicitly names the simulator. Ten DDCs and firmware 27
are consistent with the earlier [G2 discovery observation](M1_DISCOVERY_RESULTS.md),
but do not reproduce all of that radio's capability/firmware behavior.

Wire layout references were inspected at Saturn revision
`4b0b76f345961cfeeb447abc6d8b0373f5743245` (commit dated 2026-04-29):

- [p2app discovery and return endpoint](https://github.com/laurencebarker/Saturn/blob/4b0b76f345961cfeeb447abc6d8b0373f5743245/sw_projects/P2_app/p2app.c)
- [General packet ports and flags](https://github.com/laurencebarker/Saturn/blob/4b0b76f345961cfeeb447abc6d8b0373f5743245/sw_projects/P2_app/generalpacket.c)
- [DDC enables, rates and sample size](https://github.com/laurencebarker/Saturn/blob/4b0b76f345961cfeeb447abc6d8b0373f5743245/sw_projects/P2_app/IncomingDDCSpecific.c)
- [High-priority run/tuning fields](https://github.com/laurencebarker/Saturn/blob/4b0b76f345961cfeeb447abc6d8b0373f5743245/sw_projects/P2_app/InHighPriority.c)
- [DDC packet headers and zero timestamps](https://github.com/laurencebarker/Saturn/blob/4b0b76f345961cfeeb447abc6d8b0373f5743245/sw_projects/P2_app/OutDDCIQ.c)

The inherited Thetis command/receive layouts were also checked. No Saturn
FPGA/DMA/PA control code is compiled, imported or executed. The existing pinned
P1 `hpsdrsim` remains a separate independent tool.

## Validation and limits

Local macOS passes 95 managed tests (including existing native integration
checks). Simulator tests include literal header/signed-sample checks, analytic
tone phase across packet boundaries, four rates, multiple DDCs, seeded noise,
drops, malformed/unsupported controls, ownership/watchdog/reconnect, real
discovery, active/idle cancellation, partial bind rollback, bounded automatic
port selection and 100 open/dispose/port-rebind cycles. Twenty consecutive local
CLI self-tests also pass; each receives at least 100 I/Q packets. A separate
headless discovery process finds the simulator, and real Ctrl-C exits 130 after
the worker and sockets close.

Validated source: `acc1638fd06a5ed6b3ed0c5ec04950b60e8fe4cf`, recorded 2026-09-07.
The [managed CI workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34153221191)
passes on Windows x64, macOS arm64 and Linux x64: 83 tests pass and the 12
native-only checks skip as expected. Each runner also passes the standalone
simulator CLI with 100 sequenced DDC2 I/Q packets, mic silence and status. No
native library is present in that workflow. The
[native regression workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34153221164)
also passes on all three OSes: 95 managed tests with no skips, the existing
native/CLI DSP and lifecycle tests, and all six Linux sanitizer tests. See the
[native regression record](NATIVE_CI_RESULTS.md). These are separate simulator
and offline engine tests, not a simulator-to-native-P2-to-WDSP integration test.

The [first Windows run](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34152892095/job/101838642085)
exposed Winsock access-denied during random layout allocation. Automatic
selection now retries unavailable Windows layouts after disposing every partial
bind; fixed layouts still fail and the 100-attempt ceiling is tested. The
superseded native workflow was cancelled once the corrected revision started.

This enables packet and engine development without the G2. It cannot qualify
RF behavior, calibration, antenna relays, ADC differences, FPGA quirks, true
clock/loss behavior, synchronized/diversity/PureSignal receivers, firmware
updates, real audio hardware or TX. Native P2 parsing/routing into WDSP and
real-hardware receive qualification are still subsequent work.
