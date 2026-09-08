# Desktop diagnostics, endurance and safe preferences

This increment remains **simulator-only**. It cannot discover/connect a LAN
radio, open a microphone, or enable hardware TX. The G2's receive-only ANT1
restriction is unchanged. See [the desktop](DESKTOP_PREVIEW.md) and
[playback contract](AUDIO_PLAYBACK.md) for connection and audio safety.

## Export a session report

Click **Export diagnostics…** in the window header and choose a local JSON file.
Export works during receive and after disconnect/output failure. Nothing is
uploaded by the interactive app. It takes an immutable snapshot, then serializes
and atomically replaces only the chosen report off the UI/audio processing path.
The saved preferences file cannot be selected as the diagnostic destination.

Schema 1 records:

- Compiled source/informational version, .NET runtime, OS, process architecture
  and logical CPU count. This identifies the managed build, not a cryptographic
  attestation of the loaded native libraries; playback still checks native ABI 2.
- Monotonic elapsed time, distinct session IDs/protocols, initial/applied receive
  settings, lifecycle events and exception **types**, not raw exception messages.
- I/Q counts, packet loss/order/malformed traffic, socket/DSP errors, input
  overruns and audio drops. These counters belong to individual sessions; do
  not subtract them across reconnects.
- Output rate, queue depth, rejected/rendered frames, priming/starvation silence,
  actual underruns, driver underruns/status, clock correction, re-primes and
  latched faults. Terminal playback counters are retained before the native
  output is closed. `lastObserved.output.active` describes that last observation;
  `endedSeconds` records the later completion of owned cleanup.
- For P2, simulator I/Q clock rebases and lost source time. These are not wire
  packet loss. P1 currently has no equivalent lost-time measurement, represented
  by null, not a claimed zero. A silent sample-driven monitor can remain free
  of underruns despite source-clock rebases; it does not qualify physical clocks.
- Process working set, approximate managed heap size, cumulative managed
  allocation, process CPU time, sampled peak memory/CPU and observed sample gaps.
- Cumulative display-accepted and actually drawn distinct frames, observed
  source-sequence gaps within a tuning generation, plot size and sampled cadence.
  A sequence gap is not necessarily a network loss; generation resets and source
  coalescing are distinct from transport errors. Frame counters survive reconnects.

There are no spectrum-bin arrays, raw I/Q/PCM, device names, local paths, radio
addresses or station identity fields in the report. Frequencies/settings and
timestamps are included: review a report before sharing it. No live device
identity/rate is inferred from the user's earlier audible-tone confirmation.

The background sampler targets one second and reads immutable pump observations;
it never consumes PCM, calls WDSP/PortAudio or does disk I/O. Start/stop add
boundary samples. CPU 100% means **one logical core**, so multithreaded totals can
exceed 100%. Rates over intervals shorter than 250 ms are null to avoid misleading
spikes from coarse OS CPU counters. Sampling gaps are explicit, including time
between sessions. CPU/memory include the runtime, simulator, DSP and renderer;
they do not isolate one component. No forced GC or allocator trimming is used.

Storage is bounded to 3,600 samples (roughly one hour), 256 events and 32 session
summaries per window/controller. Oldest entries are evicted, with counts retained.
Lifetime session/failure totals and resource extrema survive eviction. Reports
do not pretend an evicted history is complete. A sampler error is recorded as
unavailable data and does not take down receive. Controller disposal joins the
sampler even when owned cleanup fails.

## Safe settings

Interactive windows automatically load/save schema-1 JSON in
`Environment.SpecialFolder.ApplicationData/KymoSDR/preview-settings.json`.
The exact platform-resolved location is printed by desktop `--help`.
This is separate from legacy Thetis/SDR-VST3 state: there is no profile search,
calibration import, migration, or write into a legacy installation.

The allow-list is simulator protocol, requested frequency, USB/LSB, filter edges,
AGC preset/maximum, normal window size and maximized state. No audio-device
selection, AF level, mute/unmute state, connection state, native-library path,
TX/PTT state or arbitrary command can be restored. Every launch starts
**disconnected, muted, at AF −40 dB, with No device selected**. Connect is explicit
and uses the restored/displayed controls. Reconnect keeps the existing conservative
gain/mute rule. Refresh/reselect is still required after output loss.

Save occurs after successful Connect/Apply and during orderly close. A valid
unapplied draft can be remembered on close; an invalid draft preserves the last
valid receiver preferences while still saving layout. Window dimensions must be
finite and within 900×700–3840×2160 logical units, are constrained to the current
screen subject to the application's minimum size, and no screen coordinates are
restored. Minimized state is never restored.

Files larger than 64 KiB, malformed JSON, missing required sections, unsupported enums/values,
unknown fields and unknown schema versions produce a visible warning and safe
defaults. Such files are **left unchanged**, including on shutdown; saving stays
disabled until a subsequent successful load. Preserve/move the file and restart
to create fresh defaults. Normal saves detect changes made since this window's
load/last save and refuse to overwrite them. This is not a general-purpose
transaction against an arbitrary external editor racing the atomic rename.

Saves are serialized per store, written to a unique same-directory temporary file,
flushed and renamed over the destination. Failure/cancellation before publication
leaves the original intact and removes only that operation's temporary file.
Cleanup only removes a file after successful exclusive creation; a colliding
name already owned by another writer is left untouched. An abrupt process/OS
crash can leave a temporary file, which the loader never treats as preferences.
Read/save errors are visible; they do not unmute or stop a healthy receive session.

Use `--no-settings` for an isolated interactive session. Smoke/endurance modes
automatically disable both settings reads and writes. Tests use dedicated temporary
files; they never use the developer's actual preferences or audio devices.

## Desktop endurance campaign

After building/publishing the [desktop and native libraries](DESKTOP_PREVIEW.md):

```sh
dotnet run --project src/Thetis.Desktop -c Release --no-build --no-restore -- \
  --endurance-seconds 60 --report /absolute/path/desktop-endurance.json

# Longer native-window measurement: 30 connected minutes plus startup/cleanup.
dotnet run --project src/Thetis.Desktop -c Release --no-build --no-restore -- \
  --endurance-seconds 1800 --report /absolute/path/desktop-endurance-30m.json
```

Use a platform-appropriate absolute report path; optional `--native-dir` also
requires an absolute path. Duration accepts 30–3600 seconds. Smoke and endurance
are mutually exclusive; endurance requires a report. Exit 0 means all campaign
checks passed, 2 means invalid arguments and 4 means startup/check/cleanup/export
failure. A campaign failure still writes completed phases and diagnostic events
when the report destination is writable. It does not create a signed package.

Three connections run **P2 → P1 → P2**, each held for one third of the requested
connected duration. Startup, control operations and teardown add wall time.
The campaign retunes, alternates USB/LSB/filter/AGC settings, resizes every five
seconds and explicitly reconnects. It stays muted on the no-device monitor.

Acceptance conditions are fixed before qualification:

- Each normal observation has zero receive gaps/order/malformed/foreign packets,
  native socket/DSP errors, CM input overruns/audio drops, output rejections,
  underruns/driver underruns, nonfinite samples and clipping.
- Output is active, nonphysical and muted, with silent-monitor RMS zero.
  Each reconnect starts muted at AF no higher than −40 dB.
- Controls actually apply; each phase displays/draws at least ten distinct
  spectrum frames and fails after ten seconds without new rendered frames.
  Output PCM must also keep advancing; terminal counters are checked after stop.
- All three requested connected durations finish, sessions join on shutdown and
  no unplanned session failure occurs. Sampler history remains bounded.

CPU, RSS and achieved frame cadence are **measurements**, not new relaxed M5
performance gates. The existing native reconnect memory guards are unchanged.
The 30 Hz display goal, physical latency/endurance, real unplug/sleep/wake and
audible G2 receive remain unqualified. Do not equate a no-device pass with a
60-minute real-speaker session, or a headless Skia pass with native-window cadence.

Native CI runs a 60-second native-window campaign on Windows/macOS and Linux
X11/Xvfb, plus a missing-native failure/report check. Reports are uploaded as
`desktop-diagnostics-<runner>` artifacts for 14 days; this explicit CI artifact
upload is separate from the interactive app's local-only export. Workflow dispatch
`desktop_soak: 30-minutes` opts into 30 minutes on each OS. It can be combined
with the separate receive soak; that opt-in increases the job time limit.

The managed desktop suite also runs a 30-second headless campaign through the
same controls/owner with forced real Skia drawing. An explicit local test can set
`THETIS_DESKTOP_ENDURANCE_SECONDS=1800` and `THETIS_DESKTOP_ENDURANCE_REPORT` to an
absolute JSON path, filtering to `TestCategory=DesktopEndurance`. Give that test
a hang timeout longer than the requested run (for example `--blame-hang-timeout
35m`); other tests retain their normal two-minute bound. Do not build or run other
native campaigns concurrently when interpreting the long-run resource results.

Source-specific results are recorded in [NATIVE_CI_RESULTS.md](NATIVE_CI_RESULTS.md).
