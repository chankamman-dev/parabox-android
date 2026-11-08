#!/usr/bin/env bash
# Staged IL -> C++ -> libil2cpp.so.  ABI=arm64 (Thor, default) or ABI=x86_64 (emulator test build).
set -uo pipefail

ABI=${ABI:-arm64}
case "$ABI" in arm64) ARCH=ARM64 ;; x86_64) ARCH=x64 ;; *) echo "bad ABI"; exit 1 ;; esac
W='E:\android-recompile-work\parabox-android-work'
OUTW="$W\il2cpp-$ABI"
U='E:\lom-android-work\unity\Editor\Data'
NDK='E:\lom-android-work\ndk\android-ndk-r19c'
mkdir -p "/e/android-recompile-work/parabox-android-work/il2cpp-$ABI"
cd "/e/android-recompile-work/parabox-android-work/il2cpp-$ABI"
export ANDROID_NDK_ROOT="$NDK"
"$U\il2cpp\build\deploy\netcoreapp3.1\il2cpp.exe" --convert-to-cpp --compile-cpp --libil2cpp-static \
    --platform=Android --architecture=$ARCH --configuration=Release --dotnetprofile=unityaot \
    --emit-null-checks --enable-array-bounds-check --incremental-g-c-time-slice=3 \
    --directory="$W\stage" \
    --generatedcppdir="$OUTW\cpp" \
    --data-folder="$OUTW\data" \
    --cachedirectory="$OUTW\cache" \
    --outputpath="$OUTW\out\libil2cpp.so" \
    --tool-chain-path="$NDK" \
    --jobs=14 > build.log 2>&1
rc=$?
echo "il2cpp exit $rc"
grep -nE ' error |Error:|undefined reference' build.log | cut -c1-220 | head -12
[ $rc -eq 0 ] || exit $rc
ls -la out/libil2cpp.so
