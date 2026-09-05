# RNet and loopback socket lifecycle (M3b checkpoint)

The portable native module now owns the inherited RNet buffers/locks and runs the
real seven-argument `nativeInitMetis` socket initializer. A new .NET owner and CLI
exercise this lifecycle with real UDP loopback datagrams. This is **not** a P1/P2
packet engine, radio simulator or live receive session; full M3 remains partial.

The G2 currently has a receive-only antenna on ANT1. No transmit testing,
PTT/MOX, tune, CW keying or transmit-enabling commands are authorized. This
checkpoint does not contact the G2 at all.

## Run

After the [native/managed build](NATIVE_DSP.md):

```sh
dotnet run --project src/Thetis.Headless -c Release --no-build -- transport-selftest --native-dir ABSOLUTE_NATIVE_STAGE_DIRECTORY
```

The CLI runs 100 socket lifecycles, receiving/discarding 300 bounded fixture
datagrams and rejecting 100 oversized datagrams. JSON reports those counts,
elapsed time and the loopback-only boundary. Exit codes: 0 pass, 1 failed result,
2 syntax, 3 incompatible/missing native library, 4 startup/execution failure,
130 cancellation. Help does not load native code. There is no radio-address,
device, stream-start or transmit option.

Application use: `using var probe = LoopbackTransportSession.Open(nativePath)`.
Options accept canonical IPv4 loopback addresses only, G2/P2 or Hermes Lite/P1
model settings, an explicit/ephemeral local port, and P2 port relocation. The
native API independently rejects non-loopback endpoints. Model ID 11 is G2;
it must not be confused with discovery board ID 10 (Saturn).

## What is shared with the inherited code

- `rnet.h` contains the existing radio/filter state definitions, with one owner
  for the three global pointers. POSIX excludes unused Windows packet-thread
  fields; this struct is private, not a managed ABI.
- `rnet.c` extracts allocation/cleanup from `netInterface.c`. It preserves the
  eight RX buffers, three ADC wideband buffers, 12 RX and three TX descriptors,
  packet geometries and nonzero defaults. All 21 allocations are checked;
  completed allocations/locks unwind on failure. Objects are zero-initialized
  and the aligned RNet type receives aligned storage.
- Teardown now frees both `outLRbufp` and `outIQbufp`, deletes `sendOUT`, clears
  borrowed snapshot pointers and nulls freed owners. The legacy wrapper still
  installs its original device callbacks **after** successful allocation.
  The portable probe never calls that wrapper or installs outbound callbacks.
- `radio_init.c` contains `nativeInitMetis` and socket cleanup. The initializer
  validates before allocating, publishes configuration only after a successful
  bind, and retains the explicit P2 relocation rule. Windows socket ownership
  balances each successful WSA startup with cleanup; failed starts leave no
  stale socket. POSIX uses `timeval` timeouts, rather than Windows milliseconds.
  Both retain the original buffer requests and 500ms socket timeouts.
- The unused Windows ARP preflight was removed: socket initialization now sends
  no packets. Normal OS neighbor resolution can occur when the legacy sender
  later sends traffic. The legacy P1 path still ignores the P2 relocation flag;
  legacy close preserves model/protocol settings. Legacy project/filter lists
  include the extracted units, but the full Windows UI remains unqualified.

The portable module links no P1/P2 command encoder, `StartAudioNative`, original
read/keepalive loop or outbound-ring worker. Packet-processing/routing functions
remain in the inherited files unchanged; they are not replaced with a new parser.

## Probe ownership and tests

Startup checkpoints are RNet, bound socket, manual-reset stop event, reader,
then timer worker. Checkpoints are synchronous and never retained. Positive
callback returns cancel, negative returns fail; .NET catches callback exceptions
before they can unwind through C. The new thread-start helper can report failure
without aborting; the older CM constructors retain their fail-fast contract.

The reader consumes/discards datagrams into the real RNet input buffer. A 20ms
poll bounds the idle wait; the 50ms diagnostic timer waits on the stop event and
**never sends a keepalive**. Close signals stop, joins both workers, then releases
the event, socket, buffers and locks. These are lifecycle-probe workers, not yet
ported production radio packet workers. No arbitrary send API exists here.

The .NET SafeHandle owner supports idempotent disposal, cancellation during
startup and abandonment cleanup. Managed diagnostics/session owners exclude
one another. A native combined-ownership smoke check also opens the M3a DSP/pipe
core and probe together, then closes transport first. The shared native worker
counter is 20 with both open and returns to 18 after the probe closes.

Native tests cover 100 measured cycles after ten warmups, all ten signed startup
checkpoint rollbacks, injected event/first-worker/second-worker failure, port
collision/rebind, duplicate open/close, zero/exact-limit/oversized datagrams,
shutdown with queued packets, timer progress and no replies to the fixture peer.
Separate RNet tests exercise all 21 allocation failure boundaries, snapshots,
defaults, P1/P2 model settings, and default/custom/relocated P2 ports through the
actual initializer. Resource observations compare warm/final process threads,
descriptors (Windows: process handles) and resident bytes.

The new managed tests also cover pre-load validation, all five cancellation and
exception boundaries, live-owner GC, SafeHandle finalization, concurrent state/
disposal, and the CLI contract. These are finite regression tests, not a proof
of arbitrary scheduling safety or a production memory/latency budget.

## Validation status and next boundary

Local macOS arm64: six native tests, 68 managed tests, the 100-cycle transport CLI,
and ASan/UBSan pass. The separate macOS transport leak scan reports zero leaks;
warm/final threads are 1/1 and descriptors 4/4 in that run. Hosted Windows/Linux
qualification is pending for this checkpoint; see [CI results](NATIVE_CI_RESULTS.md).

Before contacting the G2: adapt the real P2 packet receiver and its start/stop
ownership, exercise recorded/synthetic receive fixtures, and audit the control
writer so receive startup cannot assert transmit. Port P1 packet-worker shutdown
separately and qualify it with the pinned simulator. Output rings, active-stream
callback races, radio watchdog behavior and long-run resource/performance limits
are not established by this loopback probe. Subsequent G2 work remains receive-only.
