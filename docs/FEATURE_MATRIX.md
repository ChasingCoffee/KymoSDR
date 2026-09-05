# Initial cross-platform feature matrix

Status categories are independent: an implemented feature can have fixture and
simulator coverage without any hardware validation. Last updated 2026-09-04.

| Feature | Implementation | macOS arm64 evidence | Windows / Linux | Remaining gate |
| --- | --- | --- | --- | --- |
| Managed solution/toolchain | Implemented | SDK restore and Release build pass | CI definitions added; execution pending | Run CI and Windows reference build. |
| NIC listing and selection | Implemented | Real local enumeration and offline CLI selection tests | Not executed locally | Verify adapter metadata/filtering on each OS. |
| P1 discovery | Implemented/reused | Synthetic fixtures and pinned HL2-profile simulator discovery pass | Not executed locally | Windows-reference simulator comparison and eventual real P1 hardware. |
| P2 discovery | Implemented/reused | Synthetic Saturn fixtures and real G2 broadcast/targeted discovery pass | Not executed locally | Windows G2 check; separately record installed G2 server/FPGA versions. |
| Deadline/cancellation/errors | Implemented | Offline tests, empty loopback deadline and actual SIGINT smoke | Not executed locally | Repeat on CI/Windows. |
| Native WDSP and ChannelMaster | Not started | Static audit only | Existing legacy source remains | M2/M3; correct radio-init ABI before use. |
| Radio streams / RX1 spectrum | Not started | None | None in new app | M4. |
| Audio / shared desktop UI | Not started | None | None in new app | M5. |
| TX / PTT / CW | Not started | None | None in new app | M6 and mode-specific hardware checks. |
| VST3 processing / editors | Not started | Audit findings recorded | Legacy source only | M7/M8 and defect regression tests. |
| FreeDV/RADE | Deferred integration | Missing build inputs identified | Later upstream reference retained | M9 dependency recovery and signal-chain validation. |
| Multi-RX / CAT / MIDI / VAC / TCI / calibration / PureSignal | Not started | Static inventory only | Legacy source only | M10 and explicit per-workflow acceptance. |
| Settings migration / packaged release | Not started | None | None in new app | M10/M11. |

The user's G2/P2 is the first live hardware target. Parallels can supply a local
Windows environment, but installed guest OS, architecture and networking have not
been verified. Linux remains an intended target, not a tested support claim.
