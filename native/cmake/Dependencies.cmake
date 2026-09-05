include(ExternalProject)

# Build both precisions from one pinned release. No system/Homebrew libraries
# or checked-in Windows binaries participate in the portable build.
set(THETIS_FFTW_URL "https://www.fftw.org/fftw-3.3.10.tar.gz" CACHE STRING "FFTW release URL (or local archive)")
set(fftw_hash "56c932549852cddcfafdab3820b0200c7742675be92179e59e6215b340e26467")
foreach(precision IN ITEMS double float)
  if(precision STREQUAL float)
    set(fftw_name fftw3f)
    set(enable_float ON)
  else()
    set(fftw_name fftw3)
    set(enable_float OFF)
  endif()
  set(stage "${CMAKE_BINARY_DIR}/deps/${precision}")
  set(archive "${stage}/lib/${CMAKE_STATIC_LIBRARY_PREFIX}${fftw_name}${CMAKE_STATIC_LIBRARY_SUFFIX}")
  file(MAKE_DIRECTORY "${stage}/include")
  ExternalProject_Add(build_${fftw_name}
    URL "${THETIS_FFTW_URL}" URL_HASH "SHA256=${fftw_hash}"
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    CMAKE_ARGS
      "-DCMAKE_INSTALL_PREFIX=${stage}" -DCMAKE_INSTALL_LIBDIR=lib
      "-DCMAKE_C_COMPILER=${CMAKE_C_COMPILER}"
      "-DCMAKE_OSX_ARCHITECTURES=${CMAKE_OSX_ARCHITECTURES}"
      "-DCMAKE_OSX_DEPLOYMENT_TARGET=${CMAKE_OSX_DEPLOYMENT_TARGET}"
      -DCMAKE_POLICY_VERSION_MINIMUM=3.5 -DCMAKE_BUILD_TYPE=Release
      -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DCMAKE_DEBUG_POSTFIX=
      -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTS=OFF -DDISABLE_FORTRAN=ON
      -DENABLE_THREADS=OFF -DENABLE_OPENMP=OFF "-DENABLE_FLOAT=${enable_float}"
    BUILD_COMMAND "${CMAKE_COMMAND}" --build <BINARY_DIR> --config Release --parallel 4
    INSTALL_COMMAND "${CMAKE_COMMAND}" --install <BINARY_DIR> --config Release
    BUILD_BYPRODUCTS "${archive}"
    LOG_DOWNLOAD ON LOG_CONFIGURE ON LOG_BUILD ON LOG_INSTALL ON
    LOG_OUTPUT_ON_FAILURE ON)
  add_library(thetis_${fftw_name} STATIC IMPORTED GLOBAL)
  set_target_properties(thetis_${fftw_name} PROPERTIES
    IMPORTED_LOCATION "${archive}" INTERFACE_INCLUDE_DIRECTORIES "${stage}/include")
  add_dependencies(thetis_${fftw_name} build_${fftw_name})
endforeach()

set(nr_root "${CMAKE_CURRENT_LIST_DIR}/../../Project Files/lib/NR_Algorithms_x64/src")
set(rnn_root "${nr_root}/rnnoise")
# Match the vendored Windows build's embedded model, excluding training tools,
# duplicate model definitions and separately dispatched x86 translation units.
set(rnn_units denoise rnn pitch kiss_fft celt_lpc nnet nnet_default
  parse_lpcnet_weights rnnoise_data_little rnnoise_tables)
foreach(unit IN LISTS rnn_units)
  list(APPEND rnn_sources "${rnn_root}/src/${unit}.c")
endforeach()
add_library(thetis_rnnoise STATIC ${rnn_sources})
target_include_directories(thetis_rnnoise PUBLIC "${rnn_root}/include" PRIVATE "${rnn_root}/src")
target_include_directories(thetis_rnnoise PRIVATE "${CMAKE_CURRENT_LIST_DIR}/../third_party/rnnoise")
target_compile_definitions(thetis_rnnoise PRIVATE RNNOISE_BUILD)
# vec.h selects NEON on arm64 and SSE2 on supported x86 compilers. Deliberately
# avoid -march=native and fast-math so the baseline is reproducible and NaN-aware.

set(sb_root "${nr_root}/libspecbleach")
file(GLOB_RECURSE sb_sources CONFIGURE_DEPENDS "${sb_root}/src/*.c")
add_library(thetis_specbleach STATIC ${sb_sources})
target_include_directories(thetis_specbleach PUBLIC "${sb_root}/include")
target_link_libraries(thetis_specbleach PRIVATE thetis_fftw3f)
if(MSVC)
  target_compile_definitions(thetis_rnnoise PRIVATE _USE_MATH_DEFINES _CRT_SECURE_NO_WARNINGS "restrict=__restrict")
  target_compile_definitions(thetis_specbleach PRIVATE _USE_MATH_DEFINES _CRT_SECURE_NO_WARNINGS)
endif()
