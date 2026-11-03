# US2Dome — Universe Sandbox 2 Planetarium Dome Mod

A BepInEx 5 mod for **Universe Sandbox 2** that renders the game scene as a
**180° fisheye / equidistant planetarium-dome image** (a circular image) so you
can **project the game onto a planetarium dome**.

```
          180° equidistant (fisheye) output
        .-'''''''''''-.
      /  sky, stars,   \
     |    planets, sun  |
      \   and space     /
        '-.,,,,,,,'.-'
          (circular image on black)
```

The mod captures the current game view from **5 face cameras** (front / right /
left / up / down), each covering a 90° square, and remaps them into a single
180° equidistant dome using the validated math in `src/DomeMath.cs`.

## What you get
- A **SEPARATE OS window** that shows the live dome (**default, recommended**).
  The main game window stays normal — you navigate in the game, and a second
  window shows the 180° planetarium view, ready to project or record.
  - The dome is **always drawn as a perfect circle**: the window is painted
    black and the dome blitted into a **centered square of the smaller
    dimension**, so resizing or going **fullscreen** (16:9, 21:9, …) adds
    black bars instead of stretching it into an oval.
  - The dome is **correctly oriented** (matches the in-game preview).
- A **live circular dome preview** overlay in the game (optional; auto-hides
  while the second window is open — see `HideOverlayWhenSecondWindowActive`).
- A **Full-Dome Mode** that fills the whole *game* screen with the circular dome
  on a black background (also letterboxed to a circle) — so you can **project
  the game window directly** onto the dome.
- A **"Save PNG"** button that exports the dome image (circular, black
  corners) to `US2Dome/US2Dome_<timestamp>.png` next to the game.
- Config options for resolution, capture rate, overlay size/position,
  full-dome mode, and the second window.

> **Recommended workflow:** keep `SecondWindow = true` (default),
> `HideOverlayWhenSecondWindowActive = true` (default), `FullDomeMode = false`.
> Play the game normally in its window; a second window titled *"US2Dome -
> 180° Planetarium"* shows the live dome as a clean circle. Make that window
> whatever size you like — even fullscreen — and the dome stays circular (black
> bars fill the rest). Move the game window around and the dome window follows
> your camera automatically.

## Requirements
- Universe Sandbox 2 (the copy in `Universe_Sandbox_2/`)
- **BepInEx 5.4.23.5** — already present in `BepInEx/core/`.
- Windows. (The mod was compiled on Linux mono against the game's own
  assemblies, so it targets the exact game build.)

## Install
The plugin is **already installed** in this game copy:
```
Universe_Sandbox_2/BepInEx/plugins/US2Dome.dll
```
Just launch the game. If you want a clean install / rebuild yourself, see
`build.sh` (run on a machine with mono `mcs`):
```
./build.sh
# copies bin/US2Dome.dll into the game's BepInEx/plugins/
```

To install into *your* copy of the game on Windows, simply copy
`US2_mod/bin/US2Dome.dll` into `<game>/BepInEx/plugins/`.

## Using it (in-game)
1. Launch the game and go to the view you want to project.
2. **Second window (default, recommended):** a second OS window titled
   *"US2Dome - 180° Planetarium"* appears showing the live dome **as a circle**.
   The game window stays completely normal — no overlay, no UI on the dome
   (the in-game preview auto-hides while this window is open). Navigate the
   game with the normal controls and the dome window tracks your camera every
   frame. **Resize the window to any size or make it fullscreen** — the dome
   stays a perfect circle (black bars fill the extra space, so a 16:9 or 21:9
   window never stretches it into an oval). Move it to any monitor.
3. **To save an image:** the in-game **Save PNG** button (shown with the
   overlay) writes `US2Dome/US2Dome_<timestamp>.png` next to the game.
4. **Alternative — project the game window directly:** set `FullDomeMode = true`
   (see config) and the circular dome fills the whole game screen on black.

## In-game settings panel (v1.6.0)
Press **F2** (change it in the panel or via `Controls/SettingsHotkey`) while the **game window** has focus to open a settings window on the game screen. It lists every config option, plus switches for **Enable the dome renderer** and **Dome window fullscreen**.
- Most changes apply instantly (tilt, zoom, overlay, window on/off, fullscreen, always-on-top, capture rate).
- Resolutions, face mipmaps and MSAA need a pipeline rebuild: change them, then click **Apply** (one rebuild for all).
- Values are written to `US2Dome.cfg` when you close the panel or release the mouse.
- Every slider (tilt, zoom, overlay size, second-window resolution) has a box next to it: click it, type an exact value (applies as you type, clamped to the slider range; window resolution accepts up to 8192), press Enter or Esc to leave. While a box has focus the game does not see your keystrokes.
- The panel is drawn on the game window only; it never appears in the dome output.
- Clicks, drags and scrolling on the panel are hidden from the game (Harmony patches on `UnityEngine.Input`, `src/InputBlocker.cs`). This works for the legacy Input API; the log line `Input systems loaded: ...` (written when the panel is first opened) tells you if the game also uses another input package.

## Configuration (BepInEx config)
Config file: `Universe_Sandbox_2/BepInEx/config/US2Dome.cfg`

| Key                   | Default       | Meaning |
|-----------------------|---------------|---------|
| `Enabled`             | `true`        | Enable the dome renderer. |
| `SettingsHotkey` (section `Controls`) | `F2` | Key that opens/closes the in-game settings panel. |
| `Zoom`                | `1.0`         | 1.0 = true 180° dome. 2.0 = circle shows the central 90° (2x bigger), 3.0 = central 60°. Range 1-8. |
| `DomeTiltDegrees`     | `40`          | Tilt of the dome relative to the game camera. |
| `DomeResolution`      | `2048`        | Output square resolution (power of two). |
| `FaceResolution`      | `1024`        | Per-face capture resolution. |
| `CaptureEveryNFrames` | `3`           | Capture every N frames (1 = every frame). |
| `ShowOverlay`         | `true`        | Show the small circular in-game preview (master switch). |
| `OverlayScale`        | `0.35`        | Preview size (fraction of screen height). |
| `OverlayAnchor`       | `BottomRight` | Preview position: TopLeft/TopRight/BottomLeft/BottomRight. |
| `ShowSaveButton`      | `true`        | Show the Save PNG button (with the overlay). |
| `FullDomeMode`        | `false`       | Fill the whole *game* screen with the circular dome (letterboxed; for projecting the game window). |
| `SecondWindow`        | `true`        | **Show the live dome in a separate OS window** (recommended; game window stays normal). |
| `SecondWindowResolution` | `1024`     | Dome size inside the second window (power of two). |
| `SecondWindowTopmost` | `true`        | Keep the dome window above other windows. |
| `HideOverlayWhenSecondWindowActive` | `true` | **Auto-hide the in-game preview while the second window is open.** Set `false` to keep both visible. (The overlay is your fallback: if the second window is closed/disabled, it comes back.) |

> Note: `SecondWindow` and `FullDomeMode` are independent. You can run both at
> once if you want (game screen filled *and* a second window), but normally pick
> one: second window for live navigation, full-dome mode for direct projection.
>
> **Hiding the in-game preview:** with `HideOverlayWhenSecondWindowActive = true`
> (default), the small corner preview disappears as soon as the second window is
> showing the dome. If you ever want *no* preview at all (even when the second
> window is off), set `ShowOverlay = false`. If you want the preview back
> *together* with the second window, set `HideOverlayWhenSecondWindowActive = false`.

## How it works (URP)
Universe Sandbox 2 uses the **Universal Render Pipeline (URP 14)**. Per the
official URP 14 docs, an **enabled camera with `Camera.targetTexture` set
renders to that RenderTexture automatically, before the screen camera** — no
`Camera.Render()` needed (which is unreliable in URP). So:

1. The mod creates 5 enabled "face" cameras (front/right/left/up/down), each
   with its own 512×512 RenderTexture, 90° FOV, square aspect.
2. Each capture frame it repositions them to follow the main (scene) camera,
   enables them, and lets URP render all 5 to their RTs.
3. In `OnGUI` (which runs *after* the render phase) it reads the 5 RTs back to
   the CPU, remaps every dome pixel to the correct face + UV via `DomeMath`,
   and writes a square `Texture2D` (circular content, black corners).
4. That texture is drawn as the overlay or full-screen dome, and can be
   `EncodeToPNG()`-exported.

## Files
- `src/DomeMath.cs`   — Unity-independent, unit-tested 5-face → 180° equidistant math.
- `src/DomeWindow.cs` — Windows-only helper that opens a second OS window and blits the dome into it (GDI).
- `src/InputBlocker.cs` — hides panel clicks from the game.
- `src/US2DomeSettings.cs` — the in-game settings panel (partial class of the plugin).
- `src/US2Dome.cs`    — the BepInEx plugin (capture + remap + overlay + save + second window).
- `bin/US2Dome.dll`   — the compiled plugin (installed into the game).
- `build.sh`          — rebuild script (mono `mcs`).
- `build_test/`       — dev/probe scripts (Cecil API checks, math test).
- `archive/old/`      — obsolete single-file draft, kept for reference (not built).

## Troubleshooting
- **Nothing appears / plugin silent:** check **two** logs —
  `BepInEx/LogOutput.log` *and* `BepInEx/US2Dome.log` (a plain-text file this
  mod writes so problems are never swallowed). The `[US2Dome] Diagnostics:` and
  `Config bound OK` lines tell you whether `Awake`/init completed. If
  `Second window NOT available: ...` is in the log, that Win32 call was blocked
  (some VMs/remote desktops restrict `CreateWindowEx`) — the mod then falls back
  to the overlay automatically.
- **Second window is black / doesn't update:** it is being created but the dome
  texture isn't filling (likely no main camera found). Check the `Diagnostics`
  line; if it says `<none>`, tell me the game's camera setup and I'll adapt
  `FindMainCamera`.
- **Black/blank dome (overlay or window):** same as above — the scene camera may
  not be found.
- **Low FPS:** raise `CaptureEveryNFrames` to 2–4 and/or lower
  `FaceResolution` / `DomeResolution`.
- **Wants a bigger/sharper image:** raise `DomeResolution` (1024 → 2048) and
  `FaceResolution` (512 → 1024) — at the cost of GPU.
- **Dome looks flipped:** the face up-vector is already matched to the game's
  Y-up convention; if it's mirrored, flip the `DomeMath.Up` vectors and
  recompile.
