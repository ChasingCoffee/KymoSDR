# Cross-platform discovery harness

This is the first implementation slice of the [port plan](CROSS_PLATFORM_IMPLEMENTATION_PLAN.md).
It discovers HPSDR radios but does **not** open radio streams, play audio or transmit.
The legacy Windows solution remains separate.

## Prerequisites and build

Install the .NET 10 SDK for the host architecture. `global.json` selects SDK
10.0.400, allowing servicing patches within that SDK feature band, not previews
or a different major version. Neither Visual Studio nor native DSP libraries are
needed to build this solution. The optional simulator needs Git and a C compiler.

Run from the repository root on Windows, macOS or Linux:

```sh
dotnet restore Thetis.CrossPlatform.slnx --locked-mode
dotnet build Thetis.CrossPlatform.slnx -c Release --no-restore
dotnet test Thetis.CrossPlatform.slnx -c Release --no-build --no-restore
dotnet run --project src/Thetis.Headless -c Release --no-build -- --help
```

On this Mac the SDK executable is `/usr/local/share/dotnet/dotnet`; use that full
path if an already-running terminal has not picked up the installer's PATH entry.
Offline tests use synthetic packets and an injected transport: they send no
discovery packets and do not require a radio or simulator. Test packages are
pinned in the project and `packages.lock.json` files; ordinary restores must not
silently update those locks.

## List interfaces without sending discovery traffic

```sh
dotnet run --project src/Thetis.Headless -c Release --no-build -- nics --json
```

Choose a local IPv4 address from that result for `--nic`. Ethernet and Wi-Fi are
included by default. `--include-other` includes additional interface types such
as VM adapters; `--allow-loopback` is useful for a local simulator. A selected
address must belong to an eligible, active interface.

## Discover the G2 (opt-in live-network check)

Power on the G2 and select the Mac/Windows interface that reaches its network:

```sh
dotnet run --project src/Thetis.Headless -c Release --no-build -- discover --protocol p2 --nic YOUR_LOCAL_IPV4 --timeout-ms 3000 --json
```

`YOUR_LOCAL_IPV4` is a placeholder, not the radio's address. For a known radio
address, add `--target RADIO_IPV4`. A P2-only targeted scan uses unicast. P1/both
scans retain the legacy broadcast behavior even with a target filter.

Record the reported protocol, model, firmware code/beta, capability fields,
radio/interface addresses and diagnostics. Version/capability fields are raw
discovery bytes; zero may mean not reported, not “zero receivers.” The installed
G2 server/firmware version should also be recorded separately. Discovery success
does not validate native initialization, RX, TX or custom P2 streaming ports.

## P1 simulator (optional; macOS/Linux host)

Build a pinned external simulator without building piHPSDR's desktop app:

```sh
bash scripts/build-hpsdrsim.sh
```

The script clones piHPSDR under ignored `artifacts/external/pihpsdr`, checks out
`f6c17bd4347a2d80cdf6080c3c19dbd915648cdc`, and compiles only its simulator. It
does not start it. Existing local simulator source edits are not overwritten.

Start it explicitly in one terminal:

```sh
artifacts/external/pihpsdr/hpsdrsim -P1 -hermeslite2
```

The upstream simulator listens on UDP/TCP port 1024 on available interfaces.
Keep it running only while testing, then stop it with Ctrl-C. To confine the
harness's discovery requests to loopback, run in another terminal:

```sh
dotnet run --project src/Thetis.Headless -c Release --no-build -- discover --protocol p1 --nic 127.0.0.1 --target 127.0.0.1 --allow-loopback --no-general-broadcast --timeout-ms 1000 --json
```

This uses the fixed loopback target and directed loopback broadcast, not the LAN
limited broadcast. On the tested Mac, this finds `HermesLite`, firmware code 71;
the simulator advertises the shared Hermes Lite hardware ID even in its HL2 mode.
It reports receiver count 0 in this discovery reply. Treat the simulator as a
protocol test peer, not an RF/firmware compatibility oracle. Compare it with the
legacy Windows application before using its output for wider regression claims.

For a Windows VM, the simulator may run on the Mac or another LAN host. First
verify network reachability and broadcast delivery; record the VM's architecture
and emulation status. A Windows ARM VM does not constitute native Windows x64
qualification merely because an x64 executable runs there.

## CLI results and deadlines

| Exit code | Meaning |
| --- | --- |
| 0 | Help, NIC listing or at least one radio discovered without a socket failure. |
| 1 | Eligible interfaces scanned, but no radio found. Includes an empty deadline-limited scan. |
| 2 | Invalid command/options; networking was not started for a parse error. |
| 3 | No eligible interface, NIC enumeration failure or a socket failure. May contain partial radio results. |
| 4 | Unexpected application error. |
| 130 | Cancelled by Ctrl-C; active discovery sockets were disposed. |

JSON mode writes one result to stdout, with `schemaVersion: 1`, status/exit code,
elapsed time, per-interface radios/diagnostics and `deadlineReached`. Do not
discard partial results merely because one interface failed. Discovery identity
includes protocol, address and MAC within each NIC; the same radio seen through
two NICs intentionally remains associated with both interfaces.

The default `--timeout-ms 3000` bounds the whole scan, not each NIC separately.
A profile can finish earlier after quiet polls. A short deadline can leave later
NICs unprobed; inspect diagnostics or select a specific NIC. Cancellation is
checked between operations and in polling slices of at most 50 ms. OS interface
enumeration, scheduling and socket operations can add small overhead; the
deadline is not a hard real-time guarantee. No-radio is a normal diagnostic
result, not proof that a radio is absent from every network.

## CI and Windows reference

`.github/workflows/cross-platform.yml` defines Windows/macOS/Linux build, locked
restore, offline tests, help and NIC-listing checks. It never discovers radios
automatically. A manual workflow dispatch additionally attempts the legacy
Windows x64 solution build without an installer or publication step. The upstream
tag-release workflow was not changed by the harness implementation.

The legacy solution requires Windows/MSVC v145 and its existing native build
inputs; a .NET SDK alone is insufficient. Record clean build failures before
attributing them to the port. Parallels is available, but no Windows VM or legacy
Windows build has been configured or validated in this milestone.

See [recorded results](M1_DISCOVERY_RESULTS.md), [dependency manifest](DEPENDENCIES.md)
and [feature matrix](FEATURE_MATRIX.md) for what has actually been verified.
