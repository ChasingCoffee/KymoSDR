# Reconnect memory investigation

The receive endurance campaign exposed growing RSS between reconnect phases,
especially on Linux. RSS alone cannot distinguish live allocations from unused
space retained by an allocator. No production fix is claimed at this checkpoint.

## Isolated experiment

Build `Thetis.CrossPlatform.slnx`, then run each mode in a **fresh process**:

```sh
dotnet run --project tools/Thetis.MemoryProbe -c Release --no-build -- ABSOLUTE_NATIVE_DIR same 6
dotnet run --project tools/Thetis.MemoryProbe -c Release --no-build -- ABSOLUTE_NATIVE_DIR async 6
dotnet run --project tools/Thetis.MemoryProbe -c Release --no-build -- ABSOLUTE_NATIVE_DIR owner 6
```

All traffic uses owned loopback peers. Each cycle opens P2 receive, measures
USB audio and spectrum, closes the native session, requires STOP without a
watchdog, and rebinds the native port. Modes differ only in lifecycle placement:

- `same`: synchronous calls on the calling thread.
- `async`: asynchronous continuations may change the native allocation/free
  calling thread, matching the shape of the receive-soak campaign.
- `owner`: an experimental probe-only worker runs native open/close consistently;
  observation and asynchronous continuations stay on the caller.

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
retention disappears. Linux A/B observations and a validated production fix
remain pending.

The Linux CI diagnostic runs each mode separately. Its signal/lifecycle checks
are pass conditions; its memory numbers are currently diagnostic, not a newly
relaxed memory budget.
