# G2 Ethernet receive-only checkpoint

The opt-in `g2-receive` command connects a physical headless ANAN G2 to the
existing ChannelMaster/WDSP receive chain. The desktop now exposes this same
bounded receive profile through a separate, explicitly confirmed G2 Ethernet
source, and `g2-listen` exercises its shared playback owner. A separate
[`g2-soak` qualification command](#controlled-longer-receive-campaign) explicitly
opts into longer bounded tests; normal desktop/listen limits are unchanged. Simulator APIs and
automated desktop campaigns retain their loopback-only gates. This is not
completion of M4 or long-session hardware/audio qualification.

## Run deliberately

Close other SDR clients. Keep the receive antenna on **ANT1**; do not operate
PTT, a key/paddle, MOX or transmit tune. List current interfaces and perform
targeted P2 discovery first. Supply the Mac/PC Ethernet address to `--nic`, not
the radio's address. Link-local addresses are supported; Wi-Fi fallback is not.

```sh
dotnet src/Thetis.Headless/bin/Release/net10.0/Thetis.Headless.dll g2-receive --help
dotnet src/Thetis.Headless/bin/Release/net10.0/Thetis.Headless.dll g2-receive --native-dir ABSOLUTE_NATIVE_STAGE_DIRECTORY --nic LOCAL_ETHERNET_IPV4 --target G2_IPV4 --mac G2_MAC --confirm-ant1-rx --duration-seconds 10
```

All placeholders must be replaced. Hostnames (including `anan-g2.local`) are
deliberately rejected: enumeration, discovery, native bind and target use the
same explicit addresses. The initial profile is ANT1 → ADC0 → DDC2, 192 kHz,
24-bit I/Q, USB 300–3000 Hz, 10 dB ADC0 attenuation, AF −40 dB and medium AGC.
Frequency defaults to 14.200 MHz; `--frequency-hz` allows only 14.000–14.350 MHz,
matching the fixed 20/15m RX BPF and 30/20m LPF. This restriction concerns the
reviewed receive routing, not a general radio frequency limit.

For `g2-receive`, duration is 5–60 seconds (default 10). Its native entry point
independently enforces a maximum of 60 seconds; the duration starts when that worker starts, not at
the first I/Q sample. Setup, discovery and shutdown add wall-clock time. Startup
can lose about a second before repeated DDC setup takes effect. Output is one
aggregate JSON report, with no recorded I/Q/audio and no physical audio device.
The report contains network identity/frequency data; review before sharing it.

## Safety and ownership

- Enumeration must match exactly one Ethernet address with a valid subnet.
  Discovery is unicast, on that interface, with a two-second deadline. The
  radio IP and MAC, P2/Saturn board 10, standard port 1024, raw firmware-code 27,
  protocol byte 43 and ten-DDC profile must match. Busy, partial, failed or
  mismatched discovery prevents native startup. Beta is reported, not qualified
  as a release version. Installed server/FPGA versions still need recording.
- A separate native ABI accepts only distinct unicast endpoints on the same
  explicitly supplied subnet. There is no interface/DNS/broadcast fallback.
  The old simulator opens and `nativeInitMetis` hardware gates are unchanged.
  The lower-level `G2ReceiveSession` API requires its caller to perform discovery;
  the CLI and desktop share the complete identity-checked preflight workflow.
- Outbound control passes an exact byte/port allowlist immediately before send:
  general on 1024, RX-specific on 1025 and high priority on 1027 only. No DUC
  setup, TX I/Q, speaker samples, CW or arbitrary-packet export exists. PTT,
  CWX, PureSignal, drive, DUC frequency, ATU/transverter and user outputs stay
  clear. General requests PA disabled and watchdog enabled. HP selects ANT1
  with the TX relay bit clear and mutes the radio speaker. Antenna/filter
  selection necessarily uses nonzero relay-control fields; it is not a packet
  with all relay fields zero.
- Startup sends STOP before RUN to clear stale MOX/CW, with settling before
  RUN. General/RX setup repeats each second and RUN/tuning every 100 ms. The
  worker sends three best-effort STOP packets on exit and joins before socket,
  ChannelMaster and DSP disposal. There is no UDP command acknowledgement.
- Only the selected radio IP and relevant source ports are accepted. Mic is
  discarded. Status PTT/key bits (`0x87`) immediately end the worker; PLL-lock
  bit 4 is not mistaken for keying. Malformed status, three seconds without
  accepted I/Q/status, socket errors, cancellation or the native deadline stop
  the run. This status check is a reaction, **not a physical safety interlock**.
- Saturn's ordinary RUN processing calls `SetTXEnable(true)` even for reception.
  `SetPAEnabled(false)` sets its TX-relay-disable bit; STOP clears MOX/CW and the
  internal TX-enable flag. Thus `transmitAllowed: false` describes this
  application's no-transmit policy, not a claim that every internal FPGA TX
  enable stays low or that software can protect against every hardware fault,
  firmware difference, external key input or another LAN client. Do not use
  this receiver as a substitute for a physical transmit inhibit.
- Discovery is not an exclusive lease or cryptographic authentication. Another
  client can race it. Use an idle radio on a trusted/direct link; this command
  will not intentionally take over a busy radio or alter firmware/services.

The packet/routing audit uses the pinned Saturn
[high-priority handler](https://github.com/laurencebarker/Saturn/blob/4b0b76f345961cfeeb447abc6d8b0373f5743245/sw_projects/P2_app/InHighPriority.c),
[general handler](https://github.com/laurencebarker/Saturn/blob/4b0b76f345961cfeeb447abc6d8b0373f5743245/sw_projects/P2_app/generalpacket.c),
[register controls](https://github.com/laurencebarker/Saturn/blob/4b0b76f345961cfeeb447abc6d8b0373f5743245/sw_projects/common/saturnregisters.c),
and local legacy `ChannelMaster/network.c` and pinned piHPSDR `alex.h` definitions.
These references are not proof of the exact server binary installed on the G2.

## Report interpretation

`Native` retains receive counters before disposal; its `socketWorkers: 1` means
one owned/joinable worker, not that it is still running. `Safety.workerRunning`
is the running flag. Stop reasons: 1 deadline, 2 I/Q timeout, 3 status timeout,
4 PTT/key, 5 socket error, 6 disposed/cancelled, 7 malformed status.
`stopDatagramsSent` includes two startup STOPs as well as final STOPs; successful
send is not acknowledgement. A fresh post-close discovery reports idle separately.

Pass requires the deadline stop, zero key/policy/error/loss/overrun counters,
advancing audio and spectrum, nonzero finite audio energy and idle after stop.
Spectrum is uncalibrated dB relative to unit complex amplitude, **not dBm**.
Nonzero decoded audio/noise is not proof of hearing a station or calibrated
sensitivity. ADC overload is reported separately; it is not a power measurement.
Ctrl-C or an exception still disposes the native owner, but some early/cancelled
failures produce an error message rather than a complete aggregate report.

## Controlled longer receive campaign

`g2-soak` is an explicit **test command**, not a change to normal desktop
connections. It requires both `--confirm-ant1-rx` and `--confirm-extended-rx`, an
explicit duration of **60–3600 seconds**, and a new absolute `.json` report path
in an existing directory. There is no unlimited mode and no deadline renewal.
The separate native `ThetisG2ReceiveEnduranceOpen` ABI-1 export accepts at most
3600 seconds; the original native opener still rejects anything over 60.
Managed requests carry an explicit, non-persisted extended-receive flag. Old
native libraries lacking the export cannot silently substitute an unbounded run.
Every other hardware/network/TX restriction above remains in force.

Start with 60 seconds to check the report, then deliberately progress to 5,
30 and 60 minutes only after reviewing the preceding result. Stay present for
initial runs. Close the desktop/other SDR client first; this tool never takes
over a busy G2. Replace every placeholder and choose a different report name
for each run:

```sh
dotnet src/Thetis.Headless/bin/Release/net10.0/Thetis.Headless.dll g2-soak --help
dotnet src/Thetis.Headless/bin/Release/net10.0/Thetis.Headless.dll g2-soak --native-dir ABSOLUTE_NATIVE_DIRECTORY --nic LOCAL_ETHERNET_IPV4 --target G2_IPV4 --mac G2_MAC --confirm-ant1-rx --confirm-extended-rx --duration-seconds 60 --report ABSOLUTE_NEW_JSON
```

The default is 14.074 MHz USB/100–3000 Hz, muted **no-device** output, AF −40 dB
and medium AGC maximum 60 dB. Physical output requires a freshly enumerated
`--device INDEX`; `--first-channel 0` selects physical channels 1–2 (10 selects
11–12). Only available adjacent stereo pairs are accepted. `--unmute` explicitly
authorizes listening after one second of produced PCM **in each requested phase**;
without it every phase remains muted. AF/AGC overrides use the same syntax as
`g2-listen`. No system volume changes, microphone or audio recording are involved.
Physical output identity/pair is revalidated on reconnect; a missing or ambiguous
match stops the campaign rather than choosing a different device/index.

`--reconnects 10` explicitly adds ten separate **10-second** connections after
the initial long session; default is zero. It does not repeat the long run ten
times. Each connection gets fresh Ethernet/identity/idle preflight and its own
native deadline, conservative startup and disposal. After every deadline the
same radio must be confirmed idle by discovery before continuing. There are no
automatic retries, and failure/cancellation prevents remaining reconnects.

Pass requires advancing finite spectrum and PCM, the requested duration (with a
two-second startup-observation tolerance), native deadline shutdown, at least
three STOP sends, and separately confirmed idle. Key/policy, packet loss/order,
malformed/foreign traffic, socket/DSP errors, input overruns/audio drops and
output rejection/underrun/nonfinite/clipping/fault counters must all be zero.
ADC overload remains a separately reported RF observation. Ten seconds without
PCM or spectrum progress, or failure to close within 15 seconds after the
requested streaming interval, fails closed through the shared controller.
An OS/driver call that never returns cannot be forcibly killed by managed
cancellation; the independently running native RX deadline still applies.

The report path is exclusively reserved **before** device enumeration or radio
preflight; an existing file is never overwritten. Ctrl-C, missing native code,
startup failures, lost streams and failed cleanup retain a failed/cancelled
aggregate report after disposal is attempted. Idle is `null` unless confirmed;
cancellation does not initiate a new discovery scan. Progress goes to stderr,
the summary to stdout. Exit codes: 0 pass, 2 syntax, 3 native unavailable,
4 receive/report/cleanup failure, 130 cancellation. Disk failure, SIGKILL or
power loss can still prevent a completed report.

Reports contain no addresses, device names or waveform arrays. They retain
per-phase terminal/last observations plus bounded shared diagnostics with CPU,
working set, managed heap/allocation and receive/output counters. Network loss
and application overruns remain separate. Diagnostic history evictions are
explicit; reconnect overhead is additional to the requested continuous period.
Polled spectrum sequences are **not rendered frame cadence**, and sample-driven
no-device playback is **not physical audio endurance**. CPU/memory have no new
pass/fail budget. Sleep/wake, display performance and audible device handoffs
remain separate manual/desktop qualification tasks. A virtual-hour unit test is
not a one-hour G2 receive result.

## Desktop, listening and optional FT8 capture

See [desktop use](DESKTOP_PREVIEW.md#use). **Discover radios · Ethernet** performs
an explicit bounded P2 subnet scan and lets the user choose a compatible G2.
It sends discovery requests only, never receive start or TX. The last valid
Ethernet/radio IPv4, MAC, duration and source choice are now saved as selection
bookmarks, along with the chosen audio interface/pair. ANT1 confirmation is
never saved, and no connection or discovery occurs automatically at startup.
The existing strict targeted identity/idle preflight still runs for every
Connect. Each connection starts muted at AF −40 dB or lower.
The native deadline is observed by the background PCM pump even if the UI stops
polling; the shared controller closes playback and the receive owner on normal
completion as well as faults. Diagnostic reports label hardware sessions and do
not include endpoints, device names or recorded audio.

The opt-in command below uses the same controller, defaults to 14.074 MHz USB
100–3000 Hz, and opens **no physical output** unless `--device INDEX` is supplied:

```sh
dotnet src/Thetis.Headless/bin/Release/net10.0/Thetis.Headless.dll g2-listen --native-dir ABSOLUTE_NATIVE_DIRECTORY --nic LOCAL_ETHERNET_IPV4 --target G2_IPV4 --mac G2_MAC --confirm-ant1-rx --frequency-hz 14074000 --duration-seconds 60 --unmute
```

For speakers, first enumerate with `audio-devices --native-dir ...`, verify the
output identity and add its current `--device INDEX`. No default/virtual radio
output is chosen automatically. `--unmute` waits for a second of produced RX
audio and keeps AF −40 dB unless explicitly overridden; without it the run
remains muted. `--af-db -60..0` and `--agc-max-db 0..80` select software receive
gain (defaults −40/60), applied only at deliberate unmute. They do not change
RF attenuation, system volume or transmit controls.

Optional `--capture ABSOLUTE_NEW_WAV` requires a 60-second run. It retains at
most 15 seconds of mono receive PCM near a host UTC 15-second boundary and saves
only after shutdown. The 48 kHz PCM16 WAV is peak-normalized to about −6 dBFS
**offline only**; this never increases speaker gain. Do not replay the normalized
file at an unchecked volume. Existing files are never overwritten. Alignment is
host-observed, not a radio sample timestamp, and no recording is enabled in the UI.
The capture can be resampled to 12 kHz for the installed WSJT-X offline `jt9 -8`
decoder without opening its rig-control GUI. A successful transport/output run
alone does not establish FT8 reception; check actual decoded messages.
`--mode usb|lsb` allows an explicit receive-sideband comparison (default USB).
`g2-listen` also requires zero software/driver underruns and verifies the same
idle radio identity after owned shutdown. A failed post-check returns a run
failure, not an assertion that UDP STOP was acknowledged.

### First desktop-owner/FT8 checks — 2026-09-08

At 14.074 MHz on the same Ethernet/ANT1 profile, the shared owner completed a
60-second silent-monitor receive run in 61.717 s: 47,542 I/Q packets, 11,314,996
complex samples and 2,828,736 audio frames. All loss/order/malformed/foreign,
socket, DSP, input-overrun, audio-drop, output-rejection, underrun, clipping,
key/ADC-overload and policy counters were zero. The native deadline ended the
worker with five STOP sends; separate targeted discovery then confirmed idle.
The terminal output snapshot is taken before output close, so `active: true`
does not mean a stream was left open.

An opt-in 720,000-frame mono capture began at host-observed
`2026-09-08T17:50:00.000628Z`. macOS `afconvert` resampled the normalized 15-second
WAV to 12 kHz PCM16; installed WSJT-X `jt9 -8 -p 15 -w 0 -m 1 -d 3` returned
**zero decoded messages**. A separate offline `ft8sim` fixture at 1500 Hz and
−10 dB SNR decoded its expected test message with that same decoder. The fixture
was generated only into a local WAV; no audio device or radio was used for it.

A subsequent 30-second physical-output run used freshly enumerated **MacBook
Pro Speakers**, Core Audio, 48 kHz, explicit unmute and AF −40 dB. It completed
in 31.284 s with 23,343 I/Q packets and 1,388,864 produced audio frames. There
were no receive errors, output rejections, software/driver underruns, clipping
or latched faults. Clock correction ended at +297.53 ppm; 57,071 starvation/
priming frames and one re-prime were reported (not counted as steady-state
underruns). Output peak was only 6.5e−5, approximately **−84 dBFS**. The user did
not hear a clear signal. Deadline shutdown and separate idle discovery passed.
No microphone, virtual radio output, system-volume change or hardware TX was used.
These runs establish short physical-output transport, **not audible FT8 or
validated RF sensitivity/routing**. No station reception is claimed.

### Sideband diagnosis and correction

The first "USB" path selected the wrong physical sideband: the port's simulator
uses positive complex rotation above the VFO, whereas Saturn's wire orientation
matches the opposite rotation used by the legacy Windows WDSP path. Passing
those raw samples to our existing RF-edge translation inverted both receive
sidebands and the spectrum's RF offsets.

A silent, deliberately selected **LSB** comparison at 14.074 MHz established
the mismatch before changing native code. It passed in 62.023 s with 47,541 I/Q
packets, zero transport/output/safety errors, deadline stop and automatic idle
post-check. Its slot at `2026-09-08T18:02:30.002028Z` decoded **three real FT8
messages**, including `CQ W1AW/0` (decoder-reported +6 dB SNR). That result
supersedes the earlier zero-decode finding; it does not mean FT8 normally uses LSB.

The fix is a G2-only receive decoder that negates Q once before the shared
CM spectrum/DSP path. Simulator codecs, tuning words, outbound packet allowlist,
ANT1 routing, attenuation and no-TX controls are unchanged. New native tests
verify exact signed-sample conversion plus all four USB/LSB × positive/negative
RF-offset cases, including correct spectrum placement and opposite-sideband
rejection. Prior generic simulator passes could not detect this mismatch.

**Corrected USB retest: passed, four FT8 decodes.** A 60-second physical-output
run at 14.074 MHz USB, 100–3000 Hz, AF −20 dB and AGC maximum 80 dB completed in
62.450 s. These gain overrides changed software receive gain only; startup was
muted, with no system-volume/RF-routing/TX changes. Freshly selected MacBook Pro
Speakers ran at 48 kHz. Accepted 47,545 I/Q packets / 11,315,710 complex samples,
produced 2,828,864 audio frames, and reported zero loss/order/malformed/foreign,
socket/DSP/input-overrun/audio-drop, output rejection/software/driver underrun,
clipping/nonfinite, key/ADC-overload or packet-policy counters. Output peak was
0.01094 (about −39.2 dBFS); clock correction ended at −57.045 ppm. Priming/
starvation silence was 56,854 frames with one re-prime, not steady-state underruns.
Deadline shutdown sent five STOPs and the command independently confirmed idle.

The 15-second slot at `2026-09-08T18:09:30.000438Z` decoded **four real FT8
messages**, including `KJ4PEO KF0MZU DM79` and `N7AY KB9DED 73`, using the same
offline decoder/resampler. This establishes real USB station recovery after the
orientation fix. Speaker stream success is still not a substitute for a human
listening check, and it does not qualify RF calibration or prolonged audio operation.
Local captures/decoder files are in ignored `artifacts/g2-ft8.Cs0AX6`; normalized
capture gain is independent of the actual speaker level.

### Human listening and desktop audio controls

After selecting the MOTU **828** in the desktop, the user first reported silence,
then very soft audio, and finally confirmed hearing receive audio at AF **−10 dB**
and AGC maximum **80 dB**. This is a human listening check, not a calibrated
signal-level or prolonged-output qualification.

The subsequent desktop increment adds L/R post-mute RMS/peak meters and an
explicit **Start listening** action using those G2 settings with medium AGC.
Startup/reconnect still force mute and AF no higher than −40 dB; the action does
not apply draft tuning controls or change RF routing/system volume. A stereo-pair
selector exposes the 828's enumerated Main Out **1–2**, Phones 1 **11–12**, and
Phones 2 **13–14**. Enumeration did not open a stream. Its route isolation and
meter checks use fake drivers/no-device output and loopback simulators only;
actual listening on the newly selectable headphone pairs remains to be checked.
No G2 connection, physical playback or hardware TX was performed for this UI
increment. The ANT1-only profile and 60-second native limit remain unchanged.

## Initial local evidence — 2026-09-08

macOS arm64, Ethernet `en7`, local `169.254.47.65/16`, G2 `169.254.187.120`.
Discovery matched the user-supplied MAC, reported idle and no network errors;
raw fields were firmware-code 27, beta 50, protocol 43, ten receivers.
The local route table independently identifies `en7` for the G2 endpoint.
No Wi-Fi packets were used for these receive runs. Production native build
(`BUILD_TESTING=OFF`) exports the G2 ABI but no test-only opener/socket helpers.

First 10-second run: **passed**. Total including native initialization and
post-close discovery was 11.524 s. Accepted 7,178 I/Q packets / 1,708,364 complex
samples, 427,072 decoded stereo frames, 178 spectrum frames and 40 status
packets. Audio RMS was 9.93337e−6, peak 4.79641e−5 at AF −40 dB. All sequence,
malformed/foreign, socket, CM-overrun, audio-drop, DSP, key, ADC-overload and
packet-policy counters were zero. Native deadline stopped the worker; five
STOPs were sent (two startup, three exit), and post-stop discovery found idle.
No sound was played and no identifiable on-air station was established.

The subsequent **60-second run also passed**, with 61.150 s total including
initialization and post-close discovery: 47,589 I/Q packets / 11,326,182 complex
samples, 2,831,488 decoded stereo frames, 1,180 spectrum frames and 264 status
packets. Audio RMS was 1.01057e−5, peak 5.43634e−5. The same error/key/overload
counters were all zero, with five STOPs and idle confirmed afterward.

A third, fresh-process **10-second connection at 14.250 MHz passed**: 11.154 s
total, 7,170 I/Q packets / 1,706,460 complex samples, 426,560 decoded stereo
frames, 178 spectrum frames and 40 status packets. Audio RMS was 1.00207e−5,
peak 5.20348e−5. All the same error/key/overload counters remained zero; STOP
and idle checks passed. These three runs are separate process connections, not
a same-process reconnect qualification. Their strongest final spectral peak
was around +50.109 kHz baseband in both tunings; that is **not evidence of a
fixed-frequency on-air station**. A known RF signal/reference comparison is
still needed to establish tuning, antenna-path and sensitivity behavior.

Offline safety tests exercise every fixed packet bit, prohibited ports/lengths,
frequency edges, endpoint/subnet guards, startup rollback, native deadline,
all four key/PTT status bits, missing I/Q/status and malformed status. Hardware
profile worker tests use an explicit **test-only loopback opener**; no automated
test or CI job contacts a physical radio. Standard loopback ports must be free.
All 15 native CTests pass locally (98.95 s); the four targeted G2/transport
checks pass under ASan/UBSan (23.03 s). All 128 Core/CLI cases pass, including
the new preflight/endpoint tests; all 59 engine tests (including the confined
independent P1 reference, 4 m 30 s) and 27 simulator desktop regressions (43 s)
pass. Total: **214/214 managed tests, no skips**. The solution builds with zero
warnings/errors. These are local results; this increment has not been pushed
or validated on Windows/Linux CI.

Still open: longer receive qualification and memory/CPU budgets, a known-signal
or Windows-Thetis RF comparison, installed server/FPGA version capture, hardware
Windows/Linux tests, systematic GUI/output-route acceptance, other bands/rates/modes and
same-process hardware reconnect/endurance. Hardware transmit remains out of scope.
