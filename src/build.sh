#!/usr/bin/env bash
set -euo pipefail

GAME="c:/Program Files (x86)/Steam/steamapps/common/Nuclear Option"
MANAGED="$GAME/NuclearOption_Data/Managed"
CORE="$GAME/BepInEx/core"
CSC="C:/Program Files/dotnet/sdk/9.0.203/Roslyn/bincore/csc.dll"
SRC="$(cd "$(dirname "$0")" && pwd)"
OUT="/tmp/RNbuild"
mkdir -p "$OUT"

dotnet "$CSC" \
  -nologo -nostdlib -noconfig -optimize+ -target:library -langversion:9.0 \
  -out:"$OUT/RealisticNight.dll" \
  -reference:"$MANAGED/mscorlib.dll" \
  -reference:"$MANAGED/netstandard.dll" \
  -reference:"$MANAGED/System.Runtime.dll" \
  -reference:"$MANAGED/System.dll" \
  -reference:"$MANAGED/System.Core.dll" \
  -reference:"$MANAGED/UnityEngine.dll" \
  -reference:"$MANAGED/UnityEngine.CoreModule.dll" \
  -reference:"$MANAGED/UnityEngine.PhysicsModule.dll" \
  -reference:"$MANAGED/UnityEngine.ParticleSystemModule.dll" \
  -reference:"$MANAGED/UnityEngine.TerrainModule.dll" \
  -reference:"$MANAGED/UnityEngine.AssetBundleModule.dll" \
  -reference:"$MANAGED/Unity.RenderPipelines.Universal.Runtime.dll" \
  -reference:"$MANAGED/Unity.RenderPipelines.Core.Runtime.dll" \
  -reference:"$MANAGED/Mirage.dll" \
  -reference:"$MANAGED/Assembly-CSharp.dll" \
  -reference:"$CORE/BepInEx.dll" \
  -reference:"$CORE/0Harmony.dll" \
  "$SRC"/*.cs

cp "$OUT/RealisticNight.dll" "$SRC/../RealisticNight.dll"
echo "BUILD OK + DEPLOYED -> $SRC/../RealisticNight.dll"
