# Offline DSP fixtures and acceptance limits

Fixtures are generated deterministically in `Thetis.Engine/DspDiagnostics.cs`
and `native/tests/wdsp_test_api.c`, not RF recordings. All values are normalized
sample amplitudes. No test reports dBm or claims radio/audio-device validation.

- Complex tone: `I=A*cos(2*pi*f*n/rate)`, `Q=A*sin(...)`.
- Resampling: A=0.2, f=1500 Hz, 960-frame input blocks, 20 blocks, discard four
  settling blocks. Rate pairs: 48->96, 96->48, 192->48 and 48->44.1 kHz. Exact
  output counts; complex RMS within 0.002 of 0.2. At 96->48 kHz, a 35 kHz tone
  must have output complex RMS below 0.0002 (60 dB below the input amplitude).
- Impulse: I[0]=1, all other I/Q=0; 96->48 kHz, 960->480 frames. Finite, nonempty
  output and flush/replay maximum absolute difference below 1e-12.
- Analyzer: complex 0.1-amplitude, 1500 Hz tone at 48 kHz, FFT=2048, pixels=1024,
  rectangular window, positive-peak detector, no overlap/averaging/calibration.
  Convert canonical I/Q to the legacy `Spectrum0` Q/I ordering. Peak pixel 544 ± 1,
  peak -20 ± 0.2 dB relative to unit complex input. Local fifth `GetPixels`
  argument must return the exact supplied 14.2 reference. Five-second deadline.
  Pixels are not raw FFT bins: `Celiminate` removes an edge bin and `detector`
  maps bins to pixels. Using 1024 pixels selects peak detection, avoiding the
  interpolated peak attenuation of the greater-than-one-pixel-per-bin path.
- Receive channel: USB, 300–3000 Hz bandpass, fixed 0 dB AGC gain; 1024-frame
  blocks at 48 kHz. 80 blocks per tone, discard 40. 1500 Hz output RMS between
  0.001 and 1; 8000 Hz output at least 50 dB below that passband result. This
  exercises WDSP workers and buffer exchange, not a radio connection.
- NR3/NR4: 48 kHz, 480-frame blocks, 40 blocks each. 0.1-amplitude 1 kHz real
  tone plus seeded LCG noise (`0x12345678`, multiplier 1664525, increment
  1013904223, upper 24 bits normalized then scaled to ±0.01). Require finite
  output below absolute amplitude 10 and nonzero energy after 10 settling
  blocks. This is algorithm execution coverage, not a speech-quality score.

FFTW planning time limits are zero in diagnostics, using its bounded fallback
planning behavior rather than generating persistent wisdom. Numerical limits
above are analytic/property checks; Windows/macOS/Linux equivalence remains
unqualified until those platforms actually execute the same tests.
