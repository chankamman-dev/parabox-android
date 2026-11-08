#!/usr/bin/env bash
# Assembles dev.kmparabox.game from the il2cpp output and the retargeted player data.
# The texture streams (*.resS) and FMOD banks (134 MB) are not packed: the launcher downloads them from
# Steam (cloud/.../GameData.java); data-dl is the player data with its streams pointed there (ptx respath).
# Needs: make-port.sh (assets/port/port.txt) and cloud/build.sh (the launcher and Steam module).
#   ABI=arm64 (Thor, default) | ABI=x86_64 (emulator test build)
set -euo pipefail

ABI=${ABI:-arm64}
case "$ABI" in arm64) LIBDIR=arm64-v8a ;; x86_64) LIBDIR=x86_64 ;; *) echo "bad ABI"; exit 1 ;; esac
WORK=/e/android-recompile-work/parabox-android-work
AP=/e/lom-android-work/unity/Editor/Data/PlaybackEngines/AndroidPlayer
VAR=$AP/Variations/il2cpp/Release
[ -f "$(dirname "$0")/../local.env" ] && . "$(dirname "$0")/../local.env"   # ANDROID_HOME=... (not committed)
SDK="$(cygpath -u "${ANDROID_HOME:?set ANDROID_HOME (or write it to local.env)}")"
BT="$SDK/build-tools/36.0.0"
JAR="$SDK/platforms/android-35/android.jar"
OUT=${OUT:-$WORK/apk-$ABI}
IL2CPP=$WORK/il2cpp-$ABI
DATA=${DATA:-$WORK/data-dl}
GAME="/h/SteamLibrary/steamapps/common/Patrick's Parabox/Patrick's Parabox_Data"
FMODLIB=${FMODLIB:-$WORK/fmod/lib/$LIBDIR}
HERE="$(cd "$(dirname "$0")" && pwd)"
Z='/c/Program Files/7-Zip/7z.exe'
export PATH="$PATH:/c/Program Files/Eclipse Adoptium/jdk-21.0.12.101-hotspot/bin"

rm -rf "$OUT/res-flat" "$OUT/gen" "$OUT/java" "$OUT/root"
mkdir -p "$OUT/res-flat" "$OUT/gen" "$OUT/java" "$OUT/root/lib/$LIBDIR" "$OUT/root/assets/bin/Data"
GUID=$(sha1sum "$IL2CPP/data/Metadata/global-metadata.dat" | cut -c1-32)
sed "s|<!-- BUILD-ID -->|<meta-data android:name=\"unity.build-id\" android:value=\"$GUID\" />|" "$HERE/AndroidManifest.xml" > "$OUT/AndroidManifest.xml"

# Unity's resources with the game's own icon (art/icon, from the exe via princess pctest/Icon.java).
rm -rf "$OUT/res"; cp -r "$AP/Apk/res" "$OUT/res"; rm -f "$OUT/res/mipmap-mdpi/"*.png; cp -r "$HERE/../art/icon/." "$OUT/res/"
"$BT/aapt2.exe" compile --dir "$(cygpath -w "$OUT/res")" -o "$(cygpath -w "$OUT/res-flat/res.zip")"
"$BT/aapt2.exe" link -o "$(cygpath -w "$OUT/base.apk")" -I "$(cygpath -w "$JAR")" \
    --manifest "$(cygpath -w "$OUT/AndroidManifest.xml")" --java "$(cygpath -w "$OUT/gen")" \
    --custom-package com.unity3d.player --min-sdk-version 26 --target-sdk-version 30 --version-code 1 --version-name 1.0-${ABI} \
    "$(cygpath -w "$OUT/res-flat/res.zip")"

# Unity's activity ships as source; ParaboxActivity (tools/java) starts FMOD's Java side before it.
CP="$(cygpath -w "$VAR/Classes/classes.jar");$(cygpath -w "$JAR")"
JARS=("$(cygpath -w "$VAR/Classes/classes.jar")")
if [ -f "$WORK/fmod/fmod.jar" ]; then CP="$CP;$(cygpath -w "$WORK/fmod/fmod.jar")"; JARS+=("$(cygpath -w "$WORK/fmod/fmod.jar")"); fi
CLOUD="$HERE/../cloud/build"
if [ -f "$CLOUD/libs/parabox-cloud.jar" ]; then
    for j in "$CLOUD/libs/parabox-cloud.jar" "$CLOUD"/deps/*.jar; do JARS+=("$(cygpath -w "$j")"); CP="$CP;$(cygpath -w "$j")"; done
fi
mapfile -t SRCS < <( { find "$HERE/java" -name '*.java'; find "$OUT/gen" -name '*.java'; } | while read -r f; do cygpath -w "$f"; done)
javac -nowarn -source 8 -target 8 -cp "$CP" -d "$OUT/java" \
    "$(cygpath -w "$AP/Source/com/unity3d/player/UnityPlayerActivity.java")" "${SRCS[@]}" 2>&1 | grep -v '^warning\|^Note\|^注\|^警告' || true
(cd "$OUT/java" && jar cf ../activity.jar .)
cmd.exe //c "$(cygpath -w "$BT/d8.bat")" --release --min-api 26 --lib "$(cygpath -w "$JAR")" \
    --output "$(cygpath -w "$OUT/root")" "$(cygpath -w "$OUT/activity.jar")" "${JARS[@]}" \
    2>&1 | grep -vE '^Warning|Missing class|^\s*$' | head -20 || true
ls "$OUT/root"/classes*.dex

cp "$VAR/Libs/$LIBDIR/libmain.so" "$VAR/Libs/$LIBDIR/libunity.so" "$OUT/root/lib/$LIBDIR/"
# il2cpp links with debug info; the package gets a stripped copy.
STRIP="$SDK/ndk/28.2.13676358/toolchains/llvm/prebuilt/windows-x86_64/bin/llvm-strip.exe"
"$STRIP" --strip-unneeded -o "$(cygpath -w "$OUT/root/lib/$LIBDIR/libil2cpp.so")" "$(cygpath -w "$IL2CPP/out/libil2cpp.so")"
if [ -d "$FMODLIB" ]; then cp "$FMODLIB"/*.so "$OUT/root/lib/$LIBDIR/"; else echo "WARNING: no FMOD libraries for $LIBDIR (silent build)"; fi

D="$OUT/root/assets/bin/Data"
mkdir -p "$D/Managed/Metadata" "$D/Managed/Resources"
cp -r "$DATA/." "$D/"
cp "$IL2CPP/data/Metadata/global-metadata.dat" "$D/Managed/Metadata/"
cp "$IL2CPP/data/Resources/"*.dat "$D/Managed/Resources/" 2>/dev/null || true
cp -r "$IL2CPP/data/etc" "$D/Managed/etc"
if [ -f "$WORK/extra/ParaboxAndroid.dll" ]; then node "$(cygpath -w "$HERE/add-assembly.js")" "$(cygpath -w "$D/ScriptingAssemblies.json")" ParaboxAndroid.dll; fi
echo "$GUID" > "$D/unity_app_guid"
# Port description (what the device downloads from Steam, and which version the APK was built from).
PORT=${PORT:-$WORK/port}
[ -f "$PORT/port.txt" ] || { echo "run make-port.sh first" >&2; exit 1; }
mkdir -p "$OUT/root/assets/port"; cp "$PORT/port.txt" "$OUT/root/assets/port/"
cp "$HERE/../art/game-icon-256.png" "$OUT/root/assets/port/avatar.png"
if ls "$D"/*.resS >/dev/null 2>&1; then echo "data still carries *.resS: use the respath copy (data-dl)" >&2; exit 1; fi
# BUNDLE=1: the downloaded files (port.txt "dl" lines) go into the APK as well, under assets/gamedata/; the
# launcher unpacks them to files/gamedata on first start (GameData.AssetSource) -- no Steam login needed.
PACK=(lib classes*.dex)
if [ "${BUNDLE:-0}" = 1 ]; then
    mkdir -p "$OUT/root/assets/gamedata"
    grep "^dl " "$PORT/port.txt" | cut -d" " -f4- | tr -d '\r' | while IFS= read -r p; do
        cp "$(dirname "$GAME")/$(echo "$p" | tr "\\\\" "/")" "$OUT/root/assets/gamedata/"
    done
    ls -la "$OUT/root/assets/gamedata"
    PACK+=(assets/gamedata)
fi

cp "$OUT/base.apk" "$OUT/unaligned.apk"
(cd "$OUT/root" && "$Z" a -tzip -mx=0 -bso0 -bsp0 ../unaligned.apk .)
# Native libraries (extracted at install: extractNativeLibs=true) and dex can be compressed; the player
# data must stay stored, the engine maps it straight out of the APK.
(cd "$OUT/root" && "$Z" a -tzip -mx=7 -bso0 -bsp0 ../unaligned.apk "${PACK[@]}")
"$BT/zipalign.exe" -f -p 4 "$(cygpath -w "$OUT/unaligned.apk")" "$(cygpath -w "$OUT/aligned.apk")"

KS="$WORK/parabox-debug.keystore"
[ -f "$KS" ] || keytool -genkeypair -keystore "$(cygpath -w "$KS")" -storepass pbxdebug -keypass pbxdebug -alias pbx \
    -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=parabox-android" 2>/dev/null
cmd.exe //c "$(cygpath -w "$BT/apksigner.bat")" sign --ks "$(cygpath -w "$KS")" --ks-pass pass:pbxdebug --key-pass pass:pbxdebug \
    --v4-signing-enabled false --out "$(cygpath -w "$OUT/parabox-android.apk")" "$(cygpath -w "$OUT/aligned.apk")"
ls -la "$OUT/parabox-android.apk"
