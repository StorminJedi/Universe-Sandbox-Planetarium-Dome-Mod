#!/usr/bin/env bash
# build.sh — rebuild the US2Dome plugin (run on a machine with mono `mcs`).
#
# Compiles src/DomeMath.cs + src/DomeGpu.cs + src/DomeWindow.cs + src/US2Dome.cs (v1.5.0: DomeRemap/DomeWorker no longer built) against the game's own assemblies
# (in refs/, which are copies of the game's Managed DLLs) and installs the
# resulting DLL into the game's BepInEx/plugins folder.
#
set -euo pipefail
cd "$(dirname "$0")"

R=refs
NS="$R/netstandard_game.dll"   # game's netstandard facade

# The game's assemblies live one level up:
GAME_M="../Universe_Sandbox_2/Universe Sandbox x64_Data/Managed"

# Refresh the reference DLLs from the game (so we always match the installed build).
if [ -d "$GAME_M" ]; then
  cp -f "$GAME_M/UnityEngine.dll"                    refs/
  cp -f "$GAME_M/UnityEngine.CoreModule.dll"         refs/
  cp -f "$GAME_M/UnityEngine.IMGUIModule.dll"        refs/
  cp -f "$GAME_M/UnityEngine.ImageConversionModule.dll" refs/
  cp -f "$GAME_M/UnityEngine.InputLegacyModule.dll"   refs/
  cp -f "$GAME_M/UnityEngine.TextRenderingModule.dll" refs/
  cp -f "$GAME_M/netstandard.dll"                     refs/netstandard_game.dll
fi

mkdir -p bin

# Guard: src/US2Dome.cs uses the _dbgOnGui counter (OnGUI alive-beat) but the
# declaration keeps going missing from the field block (CS0103). If it's used
# but not declared, re-insert it right after _dbgCapture.
if grep -q '_dbgOnGui' src/US2Dome.cs && ! grep -q 'int _dbgOnGui' src/US2Dome.cs; then
  echo "NOTE: _dbgOnGui declaration missing from src/US2Dome.cs - re-adding it."
  python3 - <<'PYEOF'
path = "src/US2Dome.cs"
src = open(path, encoding="utf-8-sig").read()
anchor = "int _dbgCapture = 0;"
assert anchor in src, "anchor line not found"
ins = anchor + "\n        int _dbgOnGui = 0;     // OnGUI() invocations (alive-beat)"
open(path, "w", encoding="utf-8").write(src.replace(anchor, ins, 1))
PYEOF
fi

echo "Compiling US2Dome.dll ..."
mcs -target:library -out:bin/US2Dome.dll \
  -r:"$NS" \
  -r:"$R/BepInEx.dll" \
  -r:"$R/0Harmony.dll" \
  -r:"$R/UnityEngine.CoreModule.dll" \
  -r:"$R/UnityEngine.IMGUIModule.dll" \
  -r:"$R/UnityEngine.ImageConversionModule.dll" \
  -r:"$R/UnityEngine.InputLegacyModule.dll" \
  -r:"$R/UnityEngine.TextRenderingModule.dll" \
  -r:"$R/UnityEngine.dll" \
  src/DomeMath.cs src/DomeGpu.cs src/DomeWindow.cs src/US2Dome.cs src/US2DomeSettings.cs src/InputBlocker.cs src/UiDiag.cs src/Nameplates.cs src/WindowPos.cs src/WindowMirror.cs

echo "Built: bin/US2Dome.dll"

# Install into the game (if present).
PLUG="../Universe_Sandbox_2/BepInEx/plugins"
if [ -d "$PLUG" ]; then
  cp -f bin/US2Dome.dll "$PLUG/US2Dome.dll"
  echo "Installed to: $PLUG/US2Dome.dll"
fi
