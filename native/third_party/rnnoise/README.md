# Recovered RNNoise headers

The vendored RNNoise source omits `src/x86/`, although `vec.h` includes
`x86/x86_arch_macros.h` on every architecture and `vec_avx.h` needs `x86cpu.h`.
These two unmodified headers were recovered from Xiph's RNNoise repository:

- Source: https://github.com/xiph/rnnoise
- Commit: `70f1d256acd4b34a572f999a05c87bf00b67730d`
- Paths: `src/x86/x86_arch_macros.h`, `src/x86/x86cpu.h`

Their copyright and BSD-style license notices are retained in full. Only the
missing headers are supplied here; the build continues to use this project's
vendored algorithms and embedded `rnnoise_data_little.c` model. Runtime CPU
dispatch is disabled, so the other x86 dispatch files are not required.
