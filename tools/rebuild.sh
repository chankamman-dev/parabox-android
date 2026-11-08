#!/usr/bin/env bash
# Code -> staged assemblies -> libil2cpp.so -> APK.   ABI=arm64|x86_64, INSTALL=<adb serial> to install.
set -euo pipefail
T="$(cd "$(dirname "$0")" && pwd)"
U='E:\lom-android-work\unity\Editor\Data'
ABI=${ABI:-arm64}
[ -x "$T/build-paraboxandroid.sh" ] && [ -f "$T/paraboxandroid/Bridge.cs" ] && bash "$T/build-paraboxandroid.sh"
dotnet build -c Release -o 'E:\android-recompile-work\parabox-android-work\ptx-bin' "$T/ptx" 2>&1 | grep -E " error |错误" | head -5
dotnet /e/android-recompile-work/parabox-android-work/ptx-bin/ptx.dll pstage "H:\SteamLibrary\steamapps\common\Patrick's Parabox\Patrick's Parabox_Data\Managed" \
    "$U\MonoBleedingEdge\lib\mono\unityaot" "$U\PlaybackEngines\AndroidPlayer\Variations\il2cpp\Managed" \
    'E:\android-recompile-work\parabox-android-work\stage' 'E:\android-recompile-work\parabox-android-work\extra'
ABI=$ABI bash "$T/build-il2cpp.sh"
bash "$T/../cloud/build.sh" | tail -1
ABI=$ABI bash "$T/build-apk.sh" 2>&1 | grep -vE 'stack map|^Warning in|Xlint|ע' | tail -3
if [ -n "${INSTALL:-}" ]; then
    MSYS_NO_PATHCONV=1 /h/android-emu/sdk/platform-tools/adb.exe -s "$INSTALL" install -r -g "$(cygpath -w "${OUT:-/e/android-recompile-work/parabox-android-work/apk-$ABI}/parabox-android.apk")" | tail -1
fi
