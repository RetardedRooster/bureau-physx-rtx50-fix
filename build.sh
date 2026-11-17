#!/bin/bash
# Builds bureau/build/nvcuda.dll (32-bit nvcuda proxy for The Bureau) with the NDK's clang + lld
# and the Windows SDK's kernel32.lib (no MSVC / CRT needed).
#   ./bureau/build.sh            build
#   ./bureau/build.sh install    build + copy next to TheBureau.exe (and the replacement PTX files)
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
NDK_BIN="$LOCALAPPDATA/Android/Sdk/ndk/30.0.16248370/toolchains/llvm/prebuilt/windows-x86_64/bin"
KITS_LIB="/c/Program Files (x86)/Windows Kits/10/Lib/10.0.26100.0/um/x86"
GAME_BIN="${GAME_BIN:-/c/Program Files (x86)/Steam/steamapps/common/The Bureau/Binaries/Win32}"
B="$HERE/build"; mkdir -p "$B"

# lld only picks the COFF linker when invoked as lld-link.
[ -f "$B/lld-link.exe" ] || cp "$NDK_BIN/ld.lld.exe" "$B/lld-link.exe"
for d in libxml2.dll libwinpthread-1.dll; do [ -f "$B/$d" ] || cp "$NDK_BIN/$d" "$B/"; done

# Generated: export name table, jump thunks, .def (hooked exports map to hk_* stdcall functions).
declare -A HOOKS=( [cuModuleLoadDataEx]=20 [cuModuleLoadData]=8 [cuModuleGetFunction]=12
  [cuModuleGetTexRef]=12 [cuModuleGetSurfRef]=12 [cuModuleGetGlobal_v2]=16 [cuDeviceComputeCapability]=12
  [cuDeviceGetAttribute]=12 [cuCtxCreate_v2]=12 [cuLaunchGridAsync]=16 [cuLaunchGrid]=12
  # graphics interop / memory plumbing, logged by TRACKn() in nvcuda_proxy.c (value = 4 * arg count)
  [cuGraphicsD3D11RegisterResource]=12 [cuGraphicsD3D10RegisterResource]=12 [cuGraphicsD3D9RegisterResource]=12
  [cuGraphicsGLRegisterBuffer]=12 [cuGraphicsUnregisterResource]=4 [cuGraphicsMapResources]=12
  [cuGraphicsUnmapResources]=12 [cuGraphicsResourceSetMapFlags]=8 [cuGraphicsResourceGetMappedPointer_v2]=12
  [cuGraphicsSubResourceGetMappedArray]=16 [cuD3D11CtxCreate_v2]=16 [cuD3D10CtxCreate_v2]=16 [cuD3D9CtxCreate_v2]=16
  [cuMemcpy2DAsync_v2]=8 [cuArray3DGetDescriptor_v2]=8 [cuSurfRefSetArray]=12 [cuTexRefSetArray]=12
  [cuMemcpyHtoDAsync_v2]=16 [cuMemcpyDtoHAsync_v2]=16 [cuMemHostAlloc]=12 [cuMemHostGetDevicePointer_v2]=12
  [cuMemAlloc_v2]=8 [cuStreamSynchronize]=4 [cuStreamCreate]=8 [cuEventCreate]=8
  [cuEventRecord]=8 [cuEventQuery]=4 [cuEventSynchronize]=4 [cuMemsetD32Async]=16 [cuTexRefSetAddress_v2]=16
  [cuTexRefSetFormat]=12 )
{
  echo "static const char* const k_exports[] = {"
  while read -r n; do echo "  \"$n\","; done < "$HERE/proxy/nvcuda_exports.txt"
  echo "};"
  echo "enum { kNumExports = $(wc -l < "$HERE/proxy/nvcuda_exports.txt") };"
} > "$B/exports.inc"
{
  echo "  .text"
  i=0
  while read -r n; do
    if [ -z "${HOOKS[$n]}" ]; then echo "  .globl _t_$i"; echo "_t_$i: jmp *(_g_real_fns+$((i*4)))"; fi
    i=$((i+1))
  done < "$HERE/proxy/nvcuda_exports.txt"
} > "$B/thunks.S"
{
  echo "LIBRARY nvcuda"; echo "EXPORTS"
  i=0
  while read -r n; do
    if [ -n "${HOOKS[$n]}" ]; then echo "  $n=_hk_$n@${HOOKS[$n]}"; else echo "  $n=t_$i"; fi
    i=$((i+1))
  done < "$HERE/proxy/nvcuda_exports.txt"
} > "$B/nvcuda.def"

CFLAGS=(--target=i686-pc-windows-msvc -O2 -ffreestanding -fno-builtin -fno-stack-protector
        -mno-stack-arg-probe -Wall -Wno-unused-function -I"$B")
"$NDK_BIN/clang.exe" "${CFLAGS[@]}" -c "$HERE/proxy/nvcuda_proxy.c" -o "$B/nvcuda_proxy.obj"
"$NDK_BIN/clang.exe" --target=i686-pc-windows-msvc -c "$B/thunks.S" -o "$B/thunks.obj"
# Git Bash would rewrite "/dll"-style flags as paths: use "-" flags and Windows paths.
W() { cygpath -w "$1"; }
"$B/lld-link.exe" -dll -nodefaultlib -entry:DllMain -safeseh:no -machine:x86 -def:"$(W "$B/nvcuda.def")" \
  -out:"$(W "$B/nvcuda.dll")" "$(W "$B/nvcuda_proxy.obj")" "$(W "$B/thunks.obj")" "$(W "$KITS_LIB/kernel32.Lib")"
echo "built $B/nvcuda.dll"

# GUI installer (WinForms, .NET Framework 4 csc / C# 5) with the proxy embedded as resource "nvcuda.dll".
CSC=/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe
# icon: drawn by installer/IconGen.cs (original artwork), multi-size .ico
"$CSC" -nologo -r:System.Drawing.dll -out:"$(W "$B/icongen.exe")" "$(W "$HERE/installer/IconGen.cs")"
"$B/icongen.exe" "$(W "$B/app.ico")"
# AI-upscaled cutscenes: video tool (64-bit) + Bink helper (32-bit: the game's bink2w32.dll), embedded in the installer
FW=C:/Windows/Microsoft.NET/Framework64/v4.0.30319
for t in "anycpu BureauVideoTool" "x86 BureauBink32"; do
  set -- $t
  "$CSC" -nologo -codepage:65001 -optimize+ -platform:$1 -r:System.Drawing.dll -r:System.Core.dll -r:$FW/System.IO.Compression.dll \
    -r:$FW/System.IO.Compression.FileSystem.dll -out:"$(W "$B/$2.exe")" "$(W "$HERE/video/BureauVideo.cs")" "$(W "$HERE/video/Recipes.cs")"
done
"$CSC" -nologo -codepage:65001 -target:winexe -platform:anycpu -optimize+ -win32icon:"$(W "$B/app.ico")" \
  -resource:"$(W "$B/nvcuda.dll")",nvcuda.dll -resource:"$(W "$B/BureauVideoTool.exe")",BureauVideoTool.exe \
  -resource:"$(W "$B/BureauBink32.exe")",BureauBink32.exe -r:System.Windows.Forms.dll -r:System.Drawing.dll \
  -r:System.Management.dll -r:System.Core.dll -out:"$(W "$B/BureauPhysXSetup.exe")" "$(W "$HERE/installer/Installer.cs")" "$(W "$HERE/installer/Nvdrs.cs")" \
  "$(W "$HERE/installer/Ultrawide.cs")" "$(W "$HERE/installer/Lang.cs")" "$(W "$HERE/installer/Cutscenes.cs")" "$(W "$HERE/video/Recipes.cs")"
echo "built $B/BureauPhysXSetup.exe"

if [ "$1" = "install" ]; then
  cp "$B/nvcuda.dll" "$GAME_BIN/nvcuda.dll"
  mkdir -p "$GAME_BIN/cuda_ptx"
  if ls "$HERE"/ptx/*.ptx >/dev/null 2>&1; then cp "$HERE"/ptx/*.ptx "$GAME_BIN/cuda_ptx/"; fi
  echo "installed to $GAME_BIN"
fi
