# Discovery milestone implementation results

Date: 2026-09-04. Scope: M0 foundation and M1 discovery-only implementation.
The code batch is implemented and locally tested, including live G2 discovery
from macOS. Windows/legacy-reference and Linux acceptance remain open.

## Source and scope

- `cross-platform` fast-forwarded from `275f7683` to reviewed `3518930b` (15 commits).
  The final three FreeDV-related commits were not merged.
- Added `Thetis.CrossPlatform.slnx`, `Thetis.Core`, `Thetis.Headless` and
  `Thetis.Core.Tests`, SDK/package pins, a three-OS CI definition and simulator
  build instructions/script. The discovery implementation is checkpointed before
  beginning M2; its commit is identified in Git history. No push, PR or release
  publication was performed.
- The only existing application source changed after that baseline is
  `Console/HPSDR/clsRadioDiscovery.cs`. It is linked into Core with `enums.cs`.
  Changes add cancellation/deadline and socket diagnostics, an injectable test
  transport, input-length validation, non-mutating option copies, a larger reusable
  receive buffer and a guard for Windows-only DHCP metadata.
- Existing discovery signatures remain callable. The legacy options default to
  `MaxScanMilliseconds = 0`, preserving their profile-based timing; the CLI chooses
  a bounded 3000 ms default. Unexpected exceptions now propagate rather than being
  silently treated as an empty scan; the CLI reports them explicitly.
- Native DSP, radio streaming, the native-init ABI, VST and FreeDV were not changed
  in this batch. The existing Windows project/solution and release workflow were
  not edited beyond the adopted upstream commits.
- Original user plan is unchanged. SHA-256:
  `1e530fb803ea63e3f9e88221c4a6f469f510b2aa6b3b303b0ce4db6cf2097bb3`.
  Upstream's removal of old root handoff/changelog files is recoverable in Git
  history; files in `docs/` were preserved. `.DS_Store` was ignored, not deleted.

## Verified locally

Host: macOS 26.6 / arm64; .NET SDK 10.0.400, runtime 10.0.11.

| Check | Result |
| --- | --- |
| Restore using lock files | Passed for all three projects. |
| Release solution build | Passed: zero warnings, zero errors. |
| Offline unit tests | **43 passed, 0 failed, 0 skipped**; final run reported 77 ms. |
| CLI help | Passed, exit 0; no networking. |
| NIC enumeration | Passed on real macOS interfaces, including loopback, Wi-Fi and APIPA Ethernet; no discovery packets. |
| Invalid port CLI | `discover --port 0 --json` returned structured `invalidArguments`, exit 2. |
| Empty loopback scan | Unused port 45991, 25 ms deadline: `noRadio`, exit 1, `deadlineReached: true`; reported 30 ms total including setup. |
| Real process cancellation | Sent SIGINT during a loopback P2 scan: `cancelled`, exit 130, 0.382 s total including the 0.3 s pre-interrupt delay. |
| P1 simulator build | Pinned piHPSDR `f6c17bd4`, Apple Clang 21.0.0, arm64; no simulator source edits. Repeatable build script also passed. |
| P1 simulator discovery | Loopback discovery found the HL2-profile simulator; exit 0, no socket errors. |
| Live G2 P2 broadcast discovery | Passed on macOS Wi-Fi: found one Saturn/G2 at `192.168.1.210`; exit 0, 1902 ms, six replies and no socket errors. |
| Live G2 targeted P2 discovery | Passed to the same address: exit 0, 840 ms, two replies and no socket errors. |
| Repository checks | `git diff --check` clean; original plan hash matches; native/VST trees and legacy project/release workflow unchanged relative to the adopted baseline. |
| Configuration checks | SDK/lock JSON and CI YAML parsed; simulator shell script passed `bash -n`. CI itself has not run. |

The simulator result reported `P1`, `HermesLite` (hardware type 6), firmware code
71, receiver-count byte 0, not busy, discovery port 1024. There were two replies
and one duplicate rejection, leaving one radio entry. The balanced scan reported
828 ms total. Receiver-count byte 0 is an unpopulated discovery field, not a
verified absence of receivers. The simulator was stopped immediately afterward;
no RX/TX start commands were sent.

Offline coverage includes P1/P2 fixtures and busy/version fields; historical P1
board mappings; invalid headers, truncated and oversized datagrams; duplicate
identity across protocols; protocol/subnet/target/MAC rejection; custom discovery
and local bind ports; quiet/no-radio completion; socket failures; cancellation;
continuous-traffic deadlines and an expired global scan budget; CLI validation,
NIC selection, JSON, partial errors, and documented exit codes.

The synthetic fixtures mirror the existing parser and are not independent proof
of protocol conformance. The external simulator adds independent discovery
coverage, but no legacy Windows simulator comparison has been performed yet.

## Live G2 discovery follow-up

After the user confirmed the G2 was powered on and reachable, both broadcast and
targeted P2 discovery succeeded from `en0` (Wi-Fi), local IPv4 `192.168.1.153/24`.
The radio was found at `192.168.1.210:1024`, identified as `Saturn` (hardware type
10), and reported `isBusy: false`. These addresses describe this test session;
they are not hard-coded into the application.

The existing parser reported firmware-code byte 27, beta byte 50,
`protocol2Supported` byte 43 and receiver-count byte 10. These are raw discovery
fields, not a verified semantic firmware/server release version. The installed
G2 software versions still need separate recording before broader compatibility
claims. The CLI's `portCount: 18` is an existing P2 default, not a measured port
capability or streaming test.

Broadcast used the safe profile and a 3000 ms deadline: six requests/replies,
five duplicate rejections, one unique radio, no malformed/subnet/protocol
rejections, and no deadline expiration. Targeted discovery used the balanced
profile and a 2000 ms deadline: two requests/replies, one duplicate rejection and
the same radio identity. Neither test started RX/TX, changed settings, or opened
a native radio session.

Commands used after confirming the current local interface:

```sh
dotnet src/Thetis.Headless/bin/Release/net10.0/Thetis.Headless.dll discover --protocol p2 --nic 192.168.1.153 --profile safe --timeout-ms 3000 --json
dotnet src/Thetis.Headless/bin/Release/net10.0/Thetis.Headless.dll discover --protocol p2 --nic 192.168.1.153 --target 192.168.1.210 --timeout-ms 2000 --json
```

## Reproducing the checks

Use [Getting started](GETTING_STARTED.md) for ordinary cross-platform commands.
Within this restricted execution environment, NuGet restore and .NET build/test
required approved network/process access. The initial sandboxed restore/build
stalled and were stopped; successful runs used normal local process access.
No machine-wide PATH, certificate, workload or VM changes were made.

The SDK executable used here was `/usr/local/share/dotnet/dotnet`. Temporary SDK
state and packages were kept under ignored `artifacts/` using:

```sh
export DOTNET_CLI_HOME="$PWD/artifacts/dotnet-home"
export NUGET_PACKAGES="$PWD/artifacts/nuget/packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
```

Successful commands, using that SDK executable in place of `dotnet`:

```sh
dotnet restore Thetis.CrossPlatform.slnx --locked-mode --disable-parallel
dotnet build Thetis.CrossPlatform.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false
dotnet test Thetis.CrossPlatform.slnx -c Release --no-build --no-restore --logger 'trx;LogFileName=discovery-final.trx' --results-directory artifacts/test-results
```

The generated TRX is at `artifacts/test-results/discovery-final.trx` locally and
is intentionally not tracked. Simulator build/start/discovery commands and its
network-listener behavior are documented in the getting-started guide.

## Still unverified / next checks

1. **G2 software versions:** macOS broadcast and targeted discovery now pass.
   Record the installed server/FPGA release versions separately; raw discovery
   bytes alone do not establish the complete software configuration.
2. **Windows/macOS comparison:** configure or select a Windows environment, record
   architecture/VM status, build the new solution and repeat G2/P1-simulator
   discovery. A clean legacy Windows source build is also pending.
3. **Linux:** execute the new CI/build/tests and simulator check. YAML existence
   and a platform-neutral target framework are not a successful Linux run.
4. **Native RX:** still M2–M4 work. Correct PORT-1 before using native radio init;
   no receive/streaming capability is claimed by this CLI.

The [feature matrix](FEATURE_MATRIX.md) tracks these evidence levels separately.
