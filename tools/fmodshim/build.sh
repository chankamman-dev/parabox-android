#!/usr/bin/env bash
# FMOD 2.03 Android libraries + the 2.01 adapter (pbxfmod.c) -> E:\android-recompile-work\parabox-android-work\fmod\lib\<abi>\,
# fmod.jar -> E:\android-recompile-work\parabox-android-work\fmod\fmod.jar (what build-apk.sh picks up).
#   SDK=<extracted fmodstudioapi*android dir> (default: fmod/sdk-20315/fmodstudioapi20315android)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK=/e/android-recompile-work/parabox-android-work
SDK=${SDK:-$WORK/fmod/sdk-20315/fmodstudioapi20315android}
[ -f "$HERE/../../local.env" ] && . "$HERE/../../local.env"   # ANDROID_HOME=... (not committed)
NDK="$(cygpath -u "${ANDROID_HOME:?set ANDROID_HOME (or write it to local.env)}")/ndk/28.2.13676358/toolchains/llvm/prebuilt/windows-x86_64/bin"
for abi in arm64-v8a x86_64; do
    case $abi in arm64-v8a) T=aarch64-linux-android26 ;; x86_64) T=x86_64-linux-android26 ;; esac
    OUT=$WORK/fmod/lib/$abi
    mkdir -p "$OUT"
    cp "$SDK/api/core/lib/$abi/libfmod.so" "$SDK/api/studio/lib/$abi/libfmodstudio.so" "$OUT/"
    "$NDK/clang.exe" --target=$T -shared -fPIC -O2 -Wall -Wno-unused-function \
        -I"$(cygpath -w "$SDK/api/core/inc")" -I"$(cygpath -w "$SDK/api/studio/inc")" \
        -o "$(cygpath -w "$OUT/libpbxfmod.so")" "$(cygpath -w "$HERE/pbxfmod.c")" -ldl -llog -Wl,-z,max-page-size=16384
    R="$SDK/plugins/resonance_audio/lib/$abi/libresonanceaudio.so"; [ -f "$R" ] && cp "$R" "$OUT/"
    ls -la "$OUT"
done
cp "$SDK/api/core/lib/fmod.jar" "$WORK/fmod/fmod.jar"
