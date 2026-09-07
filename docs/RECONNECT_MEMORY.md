# Reconnect memory investigation

The receive endurance campaign exposed growing RSS between reconnect phases,
especially on Linux. RSS alone cannot distinguish live allocations from unused
space retained by an allocator. The engine now places full native topology
open/close on one process-lifetime lifecycle thread. The Linux allocator and
receive-campaign regressions pass; exact measurements are recorded below.

## Fix and lifetime contract

`P2ReceiveSession` and `OfflineRadioSession` dispatch native open and close
(including SafeHandle finalization) to `NativeLifecycle` before acquiring the
existing global gate. This keeps the large topology allocations on one native
allocator thread even when async callers reconnect from different pool threads.
`SessionDiagnostics` dispatches its whole exclusive workload first, avoiding a
gate/dispatcher lock inversion. Reentrant cleanup executes inline on the owner.
Cancellation, partial-start rollback, exclusivity, joined native workers and
synchronous public API behavior remain intact. Audio/spectrum reads, tuning and
state observation remain on their callers under the existing gate.

There is one intentional background lifecycle thread for the process, matching
the native library/cache lifetime; it is not a per-session native worker leak.
The queue is bounded to 64 requests. Worker startup suppresses execution-context
flow and the consumer drops completed delegates before waiting again, so neither
the first caller's request context nor the last abandoned session stays rooted.
No forced GC, production allocator trimming, global malloc settings, DSP buffer
sizes or native destructors are changed. Native initialization and standalone
diagnostics are not a blanket thread-affinity promise for all native functions.

## Isolated experiment

Build `Thetis.CrossPlatform.slnx`, then run each mode in a **fresh process**:

```sh
dotnet run --project tools/Thetis.MemoryProbe -c Release --no-build -- ABSOLUTE_NATIVE_DIR same 6
dotnet run --project tools/Thetis.MemoryProbe -c Release --no-build -- ABSOLUTE_NATIVE_DIR async 6
dotnet run --project tools/Thetis.MemoryProbe -c Release --no-build -- ABSOLUTE_NATIVE_DIR owner 6
dotnet run --project tools/Thetis.MemoryProbe -c Release --no-build -- ABSOLUTE_NATIVE_DIR hopping 20
```

All traffic uses owned loopback peers. Each cycle opens P2 receive, measures
USB audio and spectrum, closes the native session, requires STOP without a
watchdog, and rebinds the native port. Modes differ only in lifecycle placement:

- `same`: synchronous API calls on the calling thread.
- `async`: asynchronous continuations may change the API calling thread,
  matching the shape of the receive-soak campaign.
- `owner`: a probe-only worker consistently calls the open/close APIs;
  observation and asynchronous continuations stay on the caller.
- `hopping`: six probe-only callers stay alive and take turns calling open/close,
  deliberately exercising thread changes without depending on pool scheduling.

Since the fix, all these API callers reach the same internal engine lifecycle
thread. Snapshot `OsThread`/`ManagedThread` identify the observer, and
`OwnerThread` identifies the selected probe caller, **not** the engine worker.

The probe prints progress on stderr and JSON snapshots on stdout. It records
caller thread IDs, process RSS, approximate managed heap bytes and allocator
statistics. macOS uses `malloc_zone_statistics(NULL, ...)`, summing all zones;
Linux/glibc uses `mallinfo2`, adding mmap bytes to allocator-used and reserved
bytes. Windows allocator fields are null, not invented zeros. These are
process-wide observations, not an inventory of WDSP allocations or a portable
leak detector. macOS reserved bytes and RSS need not move together.

The optional `--release-reserved` performs one allocator pressure-relief/trim
request **after all sessions close**, solely as a control experiment in this
short-lived process. It is not an engine fix, automatic policy or forced GC.
No caller-supplied radio address, TX option or arbitrary native function is exposed.
Cycles are limited to 3–20 and the campaign has a three-minute cancellation timer.
As with other native harnesses, hung native code needs an external watchdog.

## Initial observations

With unchanged engine/native runtime source from `fd68660a`, local macOS
allocator-used bytes fall from approximately 1194 MiB while receiving to about
7 MiB after close, across six cycles in all three modes. RSS remains around
1200–1400 MiB; the final pressure-relief request does not materially reduce it.
This supports freed-but-retained allocator space on macOS, not missing DSP
destructors. Same-thread placement alone is not evidence that all macOS RSS
retention disappears.

The [Linux baseline at edd7f6cc](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34169138875/job/101885951064)
also frees the large allocations: approximately 1250 MB used while open drops
to 5–9 MB after close. In six fresh-process cycles, the synchronous caller ends
at 1,309,790,208 bytes RSS / 1,253,470,208 reserved. The async caller switches
from thread 1 to 7 and ends at 1,712,271,360 RSS / 1,655,566,336 reserved. The
fixed probe owner ends at 1,310,494,720 RSS / 1,253,572,608 reserved. The async
control's final explicit trim reduces RSS to 66,490,368 bytes without a matching
drop in used allocations. This supports allocator retention across caller-thread
changes rather than missing native destructors. The earlier multi-phase soak
reached approximately 6.445 GB RSS; its continuation scheduling is not deterministic.

## Linux regression guard

CI runs fresh processes for `same 6`, `async 20 --check-linux-budget` and
`hopping 20 --check-linux-budget`. The latter two require every closed allocator
used snapshot to remain within **32 MiB** of the process baseline and every
post-warmup reserved/RSS snapshot to remain within **128 MiB** of cycle 3's
closed snapshot. Three cycles allow glibc's initial allocation-strategy/arena
high-water adjustment; the remaining cycles must reuse that space even when
the API caller changes. These bounds are regression tripwires for the fixed
topology on hosted glibc Linux, not desktop memory budgets or portable leak
proof. There is no trim, forced GC or allocator environment override in these
checks. A six-caller baseline that retained another topology per caller would
exceed this guard by gigabytes, not a small scheduling-dependent margin.

Signal/lifecycle, no-TX and port-release checks still apply independently.
`--check-linux-budget` requires Linux and at least six cycles; it cannot be
combined with the diagnostic trim option. macOS/Windows memory observations
remain diagnostic rather than inheriting glibc-specific thresholds.

## Fixed-source Linux measurements

The [Linux job at 63484307](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34169904861/job/101888090317)
passes all native/managed/CLI checks and both memory guards:

| Probe | Cycles | Closed used range (bytes) | Largest post-warmup RSS growth | Largest post-warmup reserved growth | Final closed RSS |
| --- | --- | --- | --- | --- | --- |
| Async callers | 20 | 3,028,864–8,918,112 | 14,983,168 | 208,896 | 1,320,951,808 |
| Six rotating callers | 20 | 3,302,096–9,144,976 | 15,204,352 | 180,224 | 1,321,902,080 |

The rotating test observes six distinct persistent probe caller threads. Used
growth above the process baseline is at most 3,200,448 bytes, below the 32 MiB
guard; reserved/RSS growth is well below the unchanged 128 MiB guard. The
synchronous six-cycle reference also passes (final RSS 1,309,986,816). No
allocator trim, forced GC or allocator-setting override is used.

The ordinary six-phase receive/fault campaign passes in 34.395 seconds, with
two reconnects. Its RSS is 1,253,605,376 at the initial steady baseline and
1,322,430,464 at the final reconnect, versus 6,445,420,544 at the same final
phase before the fix (`bb463658`). This is evidence that the observed Linux
reconnect amplification is corrected, not that the approximately 1.2 GB native
topology or all process-wide allocator retention has been eliminated.

## Local fixed-source validation

At `63484307`, macOS 26.6.2 arm64 builds without warnings and passes 139
managed tests with the existing `BUILD_TESTING=OFF` native runtime (109 Core,
30 Engine). The 30 Engine tests also pass after rebuilding the final ephemeral
port fixture. New tests cover single-thread/reentrant dispatch, exception
propagation, gate inversion rejection, execution-context/last-work retention,
and alternating offline/P2 startup from different callers. Existing cancellation,
rollback, concurrent disposal and abandoned SafeHandle tests still pass. The
context-retention test also passes alone in a fresh process, exercising initial
worker startup with a populated caller context.

The `hopping 20` probe passes in 30.783 seconds. Six distinct probe callers are
observed. Closed allocator-used bytes stay between 3,513,072 and 8,211,248;
RSS at cycle 3 close is 1,307,918,336 and cycle 20 close is 1,342,439,424.
No trim/forced GC is used. Raw report:
`artifacts/reconnect-memory-fixed-hopping.json` (ignored local artifact).

A separate receive-soak passes 120.001 seconds steady / 180.178 seconds total,
seven retunes, all three fault phases and twenty reconnects (24 passing phases).
Steady counters show zero native errors/overruns/loss/drops and zero simulator
pacing resyncs, 5,759,936 stereo frames read and 19.658 produced spectrum
frames/s. Every phase disposes and rebinds; STOP/no-watchdog/no-TX checks pass,
with the deliberate disappeared-peer exception. Raw report:
`artifacts/receive-soak-reconnect-fixed.json`.

Steady RSS is 1,261,191,168 → 1,273,479,168 bytes; reconnect 20 ends at
1,518,551,040 bytes, including a roughly 185 MiB step at reconnect 9. The
allocator-probe results do **not** establish zero macOS retention or a portable
RSS plateau. The fix targets allocation-thread amplification, especially glibc;
full topology footprint and longer desktop resource budgets remain separate
qualification work. Local regressions overlapped the steady phase, so CPU and
timing observations are not an isolated benchmark.
