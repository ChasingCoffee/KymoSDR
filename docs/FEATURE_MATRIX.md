# Initial cross-platform feature matrix

Status categories are independent: an implemented feature can have fixture and
simulator coverage without any hardware validation. Last updated 2026-09-04.

| Feature | Implementation | macOS arm64 evidence | Windows / Linux | Remaining gate |
| --- | --- | --- | --- | --- |
| Managed solution/toolchain | Implemented | Local and hosted restore/build/tests pass | Hosted Windows/Linux checks pass | Legacy Windows reference build remains pending. |
| NIC listing and selection | Implemented | Real local enumeration and offline CLI selection tests | Hosted NIC enumeration and offline tests pass | Verify physical/VM adapter metadata and routing locally. |
| P1 discovery | Implemented/reused | Synthetic fixtures and pinned HL2-profile simulator discovery pass | Offline fixture/CLI tests pass in CI | Windows-reference simulator comparison and eventual real P1 hardware. |
| P2 discovery | Implemented/reused | Synthetic Saturn fixtures and real G2 broadcast/targeted discovery pass | Offline fixture/CLI tests pass in CI | Windows G2 check; separately record installed G2 server/FPGA versions. |
| Deadline/cancellation/errors | Implemented | Offline tests, empty loopback deadline and actual SIGINT smoke | Offline regression tests pass in CI | Live-network/process cancellation checks on Windows/Linux. |
| Native WDSP / .NET offline DSP | Initial offline gate passed | 11 signal checks, native ABI/NR3/NR4/lifecycle tests and sanitizer checks pass | 11 signal checks and 51 managed tests pass on both; Linux sanitizer/leak checks pass | Full DSP/mode parity and longer-run qualification; see CI results. |
| ChannelMaster lifecycle | M3a offline core implemented | 100-cycle native/CLI tests and 58 managed tests pass; post-fix leak qualification in progress | New tests added to CI; pending qualification | [M3a boundary](CHANNELMASTER_OFFLINE.md); RNet/socket lifecycle still M3b. |
| Radio streams / RX1 spectrum | Not started | None | None in new app | M4. |
| Audio / shared desktop UI | Not started | None | None in new app | M5. |
| TX / PTT / CW | Not started | None | None in new app | M6 and mode-specific hardware checks. |
| VST3 processing / editors | Not started | Audit findings recorded | Legacy source only | M7/M8 and defect regression tests. |
| FreeDV/RADE | Deferred integration | Missing build inputs identified | Later upstream reference retained | M9 dependency recovery and signal-chain validation. |
| Multi-RX / CAT / MIDI / VAC / TCI / calibration / PureSignal | Not started | Static inventory only | Legacy source only | M10 and explicit per-workflow acceptance. |
| Settings migration / packaged release | Not started | None | None in new app | M10/M11. |

The user's G2/P2 is the first live hardware target. Parallels can supply a local
Windows environment, but installed guest OS, architecture and networking have not
been verified. Linux x64 is tested for the offline harness/DSP on Ubuntu 24.04;
Linux desktop, audio, packaging and radio operation remain unqualified. See the
[CI validation record](NATIVE_CI_RESULTS.md) for exact runners and evidence.
