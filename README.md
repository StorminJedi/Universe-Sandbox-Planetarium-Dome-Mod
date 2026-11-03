# US2Dome - Planetarium Dome Mod for Universe Sandbox 2

A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin for **Universe Sandbox 2** that renders the game as a
**180-degree fisheye (equidistant) dome image** and shows it in its own window, ready to project onto a planetarium dome.
You keep playing in the normal game window; a second window shows the live dome.

On top of the dome picture the mod can place game windows (planet properties, the 3D object view, Data Views) and
planet labels at positions you choose on the dome, so they stay readable on the curved screen.


## Features

- **Second dome window** (separate OS window). Always a perfect circle: black bars fill the rest, so a 16:9 or 21:9
  monitor, a projector, or fullscreen never stretches it into an oval. Remembers its position, supports fullscreen
  (F11 or double-click in the dome window) and "always on top".
- **Five-face capture** (front / right / left / up / down, 90 degrees each) remapped on the GPU into one 180-degree dome.
- **Dome tilt and zoom**, adjustable live.
- **Planet labels** redrawn on the dome (size, offset, distance adjustable).
- **Game windows on the dome**, each with its own position, size and flip:
  - the **planet properties window**
  - its **3D object view**, which can stay in the properties window, become **its own window**, or be **removed**
  - the **Data Views** window (graphs)
- The windows are drawn **on top of everything** on the dome (sky, objects, orbit lines, grids, labels).
- The windows follow the game window size and UI scale automatically: the captured area is computed from the window's
  live on-screen rectangle divided by the current screen size, so changing monitor, resolution or window size is fine.
- **In-game settings menu**: **F2** = simple menu, **Shift+F2** = full menu.
- Optional in-game dome preview, full-dome mode (dome fills the game window), and a "Save PNG" button.
- Optional log file (off by default).

## Requirements

- **Windows** (the second window uses Win32/GDI).
- **Universe Sandbox 2** (64-bit, Universal Render Pipeline build).
- **BepInEx 5.4.x, x64** (developed against 5.4.23.5). BepInEx 6 is not supported.

## Install

1. **Install BepInEx 5 (x64)** into the game folder, if it is not there already.
   - Download `BepInEx_x64_5.4.x.zip` from the BepInEx releases page.
   - Unzip it so that the `BepInEx` folder and `winhttp.dll` sit **next to the game's `.exe`**
     (for example `...\steamapps\common\Universe Sandbox\`).
   - Start the game once and close it. BepInEx creates `BepInEx\plugins`, `BepInEx\config`, etc.
2. **Copy `US2Dome.dll`** (see *Build* below to make it) into:
   ```
   <game folder>\BepInEx\plugins\US2Dome.dll
   ```
3. **Start the game.** A second window titled *US2Dome - 180 Planetarium* appears showing the dome.
   Press **F2** in the game window to open the settings.

To update, close the game and overwrite `US2Dome.dll`. To uninstall, delete it (and optionally
`BepInEx\config\US2Dome.cfg`).

## Build

The plugin is plain C# compiled with Mono's `mcs` against the game's own assemblies. All source files are in `src/`.

### What you need
- Mono (`mcs`), on Linux or Windows.
- Reference DLLs, copied into a `refs/` folder next to the sources:

| DLL | Where it comes from |
|-----|---------------------|
| `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.IMGUIModule.dll`, `UnityEngine.ImageConversionModule.dll`, `UnityEngine.InputLegacyModule.dll`, `UnityEngine.TextRenderingModule.dll` | `<game>\Universe Sandbox x64_Data\Managed\` |
| `netstandard_game.dll` | the game's `netstandard.dll` from the same `Managed` folder, renamed |
| `BepInEx.dll`, `0Harmony.dll` | `<game>\BepInEx\core\` |

Using the game's own DLLs makes the plugin match the exact game build.

### Compile
From the project folder (the one containing `src/` and `refs/`):

```bash
mkdir -p bin
mcs -target:library -out:bin/US2Dome.dll \
  -r:refs/netstandard_game.dll \
  -r:refs/BepInEx.dll \
  -r:refs/0Harmony.dll \
  -r:refs/UnityEngine.CoreModule.dll \
  -r:refs/UnityEngine.IMGUIModule.dll \
  -r:refs/UnityEngine.ImageConversionModule.dll \
  -r:refs/UnityEngine.InputLegacyModule.dll \
  -r:refs/UnityEngine.TextRenderingModule.dll \
  -r:refs/UnityEngine.dll \
  src/DomeMath.cs src/DomeGpu.cs src/DomeWindow.cs src/US2Dome.cs src/US2DomeSettings.cs \
  src/InputBlocker.cs src/UiDiag.cs src/Nameplates.cs src/WindowPos.cs src/WindowMirror.cs
```

Or run `./build.sh`, which refreshes `refs/` from a sibling `Universe_Sandbox_2/` game folder if there is one, compiles,
and copies the DLL into that game's `BepInEx/plugins/`.

Notes:
- `DomeWorker.cs` and `DomeRemap.cs` are old CPU-remap code and are **not** part of the build.
- The Universal Render Pipeline DLLs are not referenced; the mod reaches URP types through reflection.
- Only the `mcs` build above has been used. Other compilers (Visual Studio, `dotnet`) need the same references and a
  .NET Framework 4.x style target, and are untested.

## Using it

### Settings menu
Press **F2** while the game window has focus.

- **F2** opens the **simple menu**: renderer on/off, dome window fullscreen, second window, tilt, labels, the game
  window, the 3D object view, and Data Views.
- **Shift+F2** opens the **full menu** (everything, including zoom, capture rate, preview overlay, quality/resolution,
  Apply / Reset all to defaults, hotkey rebinding, and the **Make log file** switch).
- Pressing the other combination while a menu is open switches to it. Pressing the same one closes it.
- Every slider has a number box: click it, type an exact value, press Enter.
- Changes apply immediately, except resolution, mipmaps and MSAA, which need **Apply** (full menu).
- Settings are saved to `BepInEx\config\US2Dome.cfg` when you close the menu or release the mouse.
- Mouse and keyboard input on the menu is hidden from the game.

### Placing windows on the dome
Each window (properties, 3D view, Data Views) has:

- **Angle from dome centre**: 0 = zenith (centre), 90 = horizon.
- **Direction around the dome**: 0 = top of the image, 90 = right, 180 = bottom, -90 = left.
- **Size**: how many degrees its longest side covers.
- **Flip vertically**: use it if a window shows upside-down.

The 3D object view has three modes: *Keep in properties window*, *Own window on the dome*, *Remove it*. The properties
window on the dome shrinks when the 3D view is cut out of it, but its text stays the same size.

Windows only show on the dome while they are open in the game.

### Labels
Planet and object labels are redrawn on the dome. Size, X/Y offset and distance are adjustable. Distance also sets how
far from the viewer the dome windows are placed.

### Dome window
- Move and resize it like any window. Drag it to the projector's screen and press **F11** (or double-click) for fullscreen.
- The position is remembered between runs.

## Configuration

`BepInEx\config\US2Dome.cfg`, created on first run. Main sections:

| Section | What it holds |
|---------|---------------|
| `General` | `Enabled`, `DomeTiltDegrees`, `Zoom`, `CaptureEveryNFrames`, resolutions, `SecondWindow*`, overlay and full-dome options, `RememberWindowPosition` |
| `Quality` | `FaceMipmaps`, `FaceMSAA`, `FaceShadows`, `FacePostProcessing` |
| `Controls` | `SettingsHotkey` (default `F2`) |
| `Nameplates` | `ShowLabels`, `LabelSize`, `LabelOffsetX`, `LabelOffsetY`, `LabelDistance` |
| `Window` | the properties window: `ShowWindow`, `WindowPath`, `AngleFromCenter`, `Direction`, `SizeDegrees`, `FlipVertical` |
| `ObjectView` | the 3D view: `SeparateView`, `ShowSeparateView`, `ViewName`, position/size, `UseGameTexture`, `FlipVertical` |
| `DataView` | the Data Views window: `ShowDataView`, `WindowPath`, position/size, `FlipVertical` |
| `Logging` | `MakeLogFile` (default `false`) |
| `Diagnostics` | `DumpKey` (F3), `HoverKey` (F4), `ScreenshotKey` (F5) |

`SeparateView = true` with `ShowSeparateView = true` gives the 3D view its own window. With `ShowSeparateView = false` it
is removed. With `SeparateView = false` it stays in the properties window.

## Troubleshooting

- **Log file:** the mod writes `BepInEx\US2Dome.log` only when **Make log file** is on (Shift+F2 menu, or `Logging/MakeLogFile`).
  Turn it on before reproducing a problem. BepInEx's own `BepInEx\LogOutput.log` is separate and always written.
- **Nothing happens:** check `LogOutput.log` for `[US2Dome]` lines. Make sure BepInEx started (a console window or
  `LogOutput.log` appears) and that `US2Dome.dll` is in `BepInEx\plugins`.
- **Dome is black:** make sure the dome renderer is enabled and look for `Initialized: 5/5 face RTs` in the log.
- **A window is upside-down:** toggle its **Flip vertically** setting.
- **Windows missing from the dome:** with the log on, look for `windows painted on top into face N`. If it never
  appears, send the log along with a description of the problem.
- **Low FPS:** raise `CaptureEveryNFrames` to 2-4, or lower face and dome resolution (Apply afterwards).
- **Wrong window shown / a window never appears:** the mod finds game windows by scene path
  (`Window/WindowPath`, `DataView/WindowPath`, `ObjectView/ViewName`). If a game update moves them, use the
  diagnostics below to find the new path.

### Finding another window's path (diagnostics)
1. Press **F3** (writes a snapshot of the UI to the log; turn on **Make log file** first).
2. Open the window you want in the game, then press **F3** again. The log lists what changed.
3. Hover the mouse over the window and press **F4** (lists the UI objects under the mouse), then **F5**
   (saves a full screenshot and a cropped one of the window).
4. Put the window's path into the matching `WindowPath` config entry.

## How it works (short)

Universe Sandbox 2 uses the Universal Render Pipeline. The mod creates five face cameras that render into render
textures, follows the main camera, and warps the five faces into a 180-degree equidistant dome on the GPU. The result
is sent to the second window. Game windows are copied from the game's screen (or, for the 3D view, taken from the game's
own render texture) and painted into each face texture after the face camera finishes, so they sit over everything.

## Files

| File | Purpose |
|------|---------|
| `src/US2Dome.cs` | the plugin: config, capture loop, overlay, logging, second window control |
| `src/US2DomeSettings.cs` | the F2 / Shift+F2 settings menu |
| `src/WindowMirror.cs` | properties window, 3D view and Data Views on the dome |
| `src/Nameplates.cs` | planet labels on the dome |
| `src/DomeGpu.cs`, `src/DomeMath.cs` | five-face to dome warp (GPU) and the math behind it |
| `src/DomeWindow.cs`, `src/WindowPos.cs` | the second OS window and its remembered position |
| `src/InputBlocker.cs` | hides menu clicks and keys from the game |
| `src/UiDiag.cs` | F3 / F4 / F5 diagnostics |
| `build.sh` | build script (mono `mcs`) |
