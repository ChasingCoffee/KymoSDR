# Reconnect memory investigation

The receive endurance campaign exposed growing RSS between reconnect phases,
especially on Linux. RSS alone cannot distinguish live allocations from unused
space retained by an allocator. The engine now places full native topology
open/close on one process-lifetime lifecycle thread. Cross-platform validation
of that fix is in progress; the baseline evidence is recorded below.

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
