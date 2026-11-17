# The Bureau: XCOM Declassified – GPU PhysX on modern NVIDIA cards (+ 21:9 fix)

> [!IMPORTANT]
> **This is AI-generated software.** The analysis, the code (nvcuda proxy, installer, patches) and this
> documentation were written by an AI assistant (Claude by Anthropic), directed and tested by a human on one PC
> (with an RTX 50 series card). It has not been reviewed by the game's or NVIDIA's developers. Read the source,
> keep backups, and use it at your own risk.

One-click installer (`BureauPhysXSetup.exe`, see Releases) that makes **GPU-accelerated PhysX** work again in
*The Bureau: XCOM Declassified* (Steam, exe v0.1.0.15143) on modern NVIDIA GPUs.

**Supported cards:** in principle every NVIDIA GPU from the **GTX 900 series (Maxwell 2) up** – GTX 900/10, GTX 16,
RTX 20/30/40/50. **It has only been tested on an RTX 50 series card**; reports from other cards are welcome (open an issue).
- GTX 600/700 (Kepler) don't need it: the game's own GPU code already runs there.
- GTX 750/750 Ti/745 (first-generation Maxwell, compute capability 5.0) probably won't work: NVIDIA's PTX modules need 5.2+.
- AMD and Intel GPUs: no – GPU PhysX requires NVIDIA CUDA.

## The problem
With PhysX enabled, the frame rate drops to 20–25 fps whenever smoke or particles appear: the effects run on the CPU.

- The game's APEX 1.2.3.0 / PhysX3Gpu modules contain GPU code only for `sm_10`–`sm_30` (Kepler and older) and
  **no PTX**, so no newer GPU (GTX 900 and later) can run it, and `PhysX3Gpu` rejects compute capabilities above 3.x.
- Additionally, RTX 50 (Blackwell) drivers expose 32-bit CUDA only to games whose driver profile has the
  "PhysX – 32-bit GPU acceleration" switch (setting `0x5067ECC4`); The Bureau is not on NVIDIA's list.

## What the installer does
- Copies an **`nvcuda.dll` proxy** next to `TheBureau.exe`. It forwards all 753 exports to the real
  `SysWOW64\nvcuda.dll`, reports compute capability 3.0 to the game, and when the game loads one of its old CUDA
  modules it loads NVIDIA's **PTX version** of the same module instead.
- The PTX modules come from the **NVIDIA PhysX System Software already installed on your PC**
  (`PhysX\Engine\73BE2921551D`, APEX 1.2.3.1). They are paired with the game's modules and verified
  (kernel names, parameter offsets/sizes, constant bank sizes, symbols) – 26/26 match. No NVIDIA file is
  redistributed. The game's own DLLs are not modified (NVIDIA's 1.2.3.1 DLLs are not ABI-compatible with the game).
- Updates the game's **`PhysXCore.dll`** (PhysX 2.8.4) to NVIDIA's newer build from the same update folder
  (2.8.4.11, with its `cudart32_65.dll`). The game's copy is kept in `Binaries\Win32\_orig_physx_core` and put back
  by **Uninstall**; a newer `PhysXCore.dll` already in the game folder is left alone.
- On RTX 50 cards only: sets the 32-bit PhysX switch on `thebureau.exe`'s driver profile via NVAPI (UAC prompt).
- Particle amount slider (50–200 %), debris limits, PhysX GPU heap / APEX budget in `XEngine.ini`.

Extras:
- **21:9 tab** – native resolution; menus and HUD in a centered 16:9 area (the main menu is otherwise cut off at the
  top and bottom); the cutscene videos keep the full screen. Patches `TheBureau.exe` (Scaleform viewport hook, original bytes verified, backup kept, fully
  restored when switched off). The approach comes from **Helifax's Widescreen Fixer plugin** for this game; it was
  studied (not run) and independently reimplemented here – no plugin code or files are included.
- **FPS limit** via the driver profile's frame rate limiter (the in-game option overrides the ini otherwise).
- **Graphics tab (experimental)** – DX11, anisotropy, FXAA, shadows, … No guarantee these change anything.
- **Debris limit** (PhysX tab: chunk islands and pieces) – caps the APEX destruction debris simulated at once (the game
  ships 2500 / unlimited; the installer defaults to 1500 / 1500).
  Large fights with lots of debris on the ground are CPU-bound (the 32-bit engine's main threads hit 100 % while the GPU
  idles), and the debris is simulated on the CPU – fewer pieces = fewer frame drops.
- **AI-upscaled cutscenes tab (optional)** – the game's 47 cutscenes in 2560×1472 instead of 1280×736:
  - *Install a video pack*: select any part of a `TheBureau_AI_Upscaled_Cutscenes.partN.zip` pack (the other parts must be in
    the same folder); every video is SHA-256 checked against the pack's manifest. **No video pack is provided in this
    repository** – the videos are the game's copyrighted content.
  - *Local upscale*: makes the same videos on your own PC from your own copy of the game: frames decoded with the game's
    own `bink2w32.dll` → Real-ESRGAN x4plus on the GPU (Vulkan) → Lanczos to 2560×1472, with depth-of-field protection
    (areas that are blurred on purpose keep the original's look instead of AI-invented detail; per-video recipes) → Bink 1
    with all audio tracks. About 36,000 frames: roughly 16 hours on a fast modern GPU, 1–2 days on a mid-range one; needs
    ~40 GB free space. Progress bar with a time-left estimate; can be stopped and resumed.
    Real-ESRGAN (BSD-3, [xinntao/Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN)) is downloaded by the installer
    (SHA-256 checked); RAD Video Tools (free, Bink encoder) must be downloaded from
    [RAD Game Tools](https://www.radgametools.com/bnkdown.htm) and installed by you.
  - The original videos are backed up (`XGame\Movies\_original_cutscenes`) and can be restored with one click.
- English / Hungarian UI.

## Usage
1. Exit the game. 2. Run `BureauPhysXSetup.exe` (finds the game in your Steam libraries). 3. PhysX tab → **Install**.
4. In the game, set PhysX to High. Undo: **Uninstall**, switch the 21:9 fix off, or verify the game files in Steam.

Requirements: Steam version (exe 0.1.0.15143), NVIDIA GPU + PhysX System Software, Windows 10/11 (.NET Framework 4).

## Building
`./build.sh` (Git Bash): the proxy is built with the Android NDK's clang/lld for `i686-pc-windows-msvc` against the
Windows SDK's `kernel32.lib` (no CRT); the installer with the .NET Framework 4 `csc`. `install.sh` is a
command-line alternative to the GUI (needs Java for `tools/PairFatbins.java`).

## Disclaimer
AI-generated (see the note at the top). Unofficial, not affiliated with 2K, Firaxis/2K Marin or NVIDIA.
Use at your own risk.
