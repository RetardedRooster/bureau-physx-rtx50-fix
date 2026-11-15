#!/bin/bash
# The Bureau: XCOM Declassified — GPU PhysX/APEX on NVIDIA GPUs from the GTX 900 series up (tested on an RTX 50 series card only).
#
# Why: the game's APEX 1.2.3.0 / PhysX3Gpu DLLs only carry sm_10..sm_30 CUDA code (no PTX), and
# PhysX3Gpu rejects compute capability > 3.x, so every PhysX effect falls back to the CPU.
# NVIDIA's PhysX System Software ships an APEX 1.2.3.1 update set (Engine\73BE2921551D) whose GPU
# modules contain sm_52 PTX, but those DLLs are not ABI-compatible with the game's ApexFramework
# (moduleScene vtable slot 9 cleans 4 fewer stack bytes -> crash). So we keep the game's DLLs and only
# swap the CUDA modules: nvcuda.dll proxy (proxy/) replaces each original fatbin (by FNV hash) with
# its PTX counterpart, after tools/PairFatbins.java verified kernel names, parameter layouts,
# constant-bank sizes and symbols are identical. The proxy also reports compute capability 3.0.
#
#   ./bureau/install.sh            install into the game
#   ./bureau/install.sh uninstall  remove everything this script added
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
GAME_BIN="${GAME_BIN:-/c/Program Files (x86)/Steam/steamapps/common/The Bureau/Binaries/Win32}"
UPD="${PHYSX_UPDATE:-/c/Program Files (x86)/NVIDIA Corporation/PhysX/Engine/73BE2921551D}"

if [ "$1" = "uninstall" ]; then
  rm -rf "$GAME_BIN/nvcuda.dll" "$GAME_BIN/cuda_ptx" "$GAME_BIN/cuda_fake_cc" "$GAME_BIN/cuda_dump" \
         "$GAME_BIN"/bureau_cuda*.log
  echo "removed proxy, PTX map and logs from $GAME_BIN"
  exit 0
fi

[ -f "$GAME_BIN/TheBureau.exe" ] || { echo "TheBureau.exe not found in $GAME_BIN (set GAME_BIN)"; exit 1; }
[ -d "$UPD" ] || { echo "PhysX update set not found: $UPD (install the NVIDIA PhysX System Software)"; exit 1; }

GAME_BIN="$GAME_BIN" bash "$HERE/build.sh" install

rm -rf "$GAME_BIN/cuda_ptx"; mkdir -p "$GAME_BIN/cuda_ptx"
touch "$GAME_BIN/cuda_fake_cc"
fail=0
for m in BasicFS BasicIOS FieldSampler IOFX TurbulenceFS; do
  java "$HERE/tools/PairFatbins.java" "$GAME_BIN/APEX_${m}CHECKED_x86.dll" "$UPD/APEX_${m}CHECKED_x86.dll" \
       "$GAME_BIN/cuda_ptx" | tee /dev/stderr | grep -q -E "NO MATCH|DIFF" && fail=1 || true
done
java "$HERE/tools/PairFatbins.java" "$GAME_BIN/PhysX3GpuCHECKED_x86.dll" "$UPD/ImplicitLibs/PhysX3GpuCHECKED_x86.dll" \
     "$GAME_BIN/cuda_ptx" | tee /dev/stderr | grep -q -E "NO MATCH|DIFF" && fail=1 || true
n=$(ls "$GAME_BIN/cuda_ptx" | wc -l)
echo "installed: nvcuda.dll proxy, cuda_fake_cc, $n replacement modules (expected 26)"
[ "$fail" = 0 ] && [ "$n" = 26 ] || { echo "WARNING: some modules did not pair cleanly"; exit 1; }
