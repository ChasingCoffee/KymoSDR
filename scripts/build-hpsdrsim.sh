#!/usr/bin/env bash
# Optional external test tool. Does not launch the simulator or contact a radio.
set -euo pipefail

thetis_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
sim_dir="$thetis_root/artifacts/external/pihpsdr"
sim_remote="https://github.com/g0orx/pihpsdr.git"
sim_revision="f6c17bd4347a2d80cdf6080c3c19dbd915648cdc"
sim_compiler="${CC:-cc}"

case "$(uname -s)" in
    Darwin|Linux) ;;
    *) printf 'Build hpsdrsim on macOS or Linux. Windows can connect to that simulator host.\n' >&2; exit 1 ;;
esac

if [[ ! -e "$sim_dir" ]]; then
    git clone --depth 1 "$sim_remote" "$sim_dir"
fi
if [[ ! -d "$sim_dir/.git" ]] || [[ "$(git -C "$sim_dir" remote get-url origin)" != "$sim_remote" ]]; then
    printf 'Unexpected simulator checkout at %s; leaving it untouched.\n' "$sim_dir" >&2
    exit 1
fi
if ! git -C "$sim_dir" diff --quiet || ! git -C "$sim_dir" diff --cached --quiet; then
    printf 'Simulator has local source changes; leaving it untouched.\n' >&2
    exit 1
fi
if ! git -C "$sim_dir" cat-file -e "$sim_revision^{commit}" 2>/dev/null; then
    git -C "$sim_dir" fetch --depth 1 origin "$sim_revision"
fi
git -C "$sim_dir" checkout --detach "$sim_revision"

# Compile only the simulator, not the full piHPSDR desktop application.
"$sim_compiler" -O2 -pthread "$sim_dir/hpsdrsim.c" "$sim_dir/newhpsdrsim.c" -lm -o "$sim_dir/hpsdrsim"
printf 'Built simulator at %s/hpsdrsim (revision %s). Not started.\n' "$sim_dir" "$sim_revision"
