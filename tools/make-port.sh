#!/usr/bin/env bash
# Writes the port description the APK carries (assets/port/port.txt). See cloud/.../GameData.java.
#   app, depot, manifest (the depot manifest this build was made from), build, prefix,
#   "base <sha1> <depot path>" for every file the APK carries in some form (code, converted data):
#       a Steam version that changes any of them needs a new APK,
#   "dl <sha1> <size> <depot path>" for every file the device downloads from Steam as is
#       (the *.resS streams next to the player data and the FMOD banks; GameData.downloaded).
# usage: make-port.sh [out dir]   (default E:\android-recompile-work\parabox-android-work\port)
set -euo pipefail
OUT=${1:-/e/android-recompile-work/parabox-android-work/port}
GAME="/h/SteamLibrary/steamapps/common/Patrick's Parabox"
ACF=/h/SteamLibrary/steamapps/appmanifest_1260520.acf
D="Patrick's Parabox_Data"

manifest=$(awk '/"1260521"/{f=1} f && /"manifest"/{gsub(/"/,"",$2); print $2; exit}' "$ACF")
build=$(awk '/"buildid"/{gsub(/"/,"",$2); print $2; exit}' "$ACF")
[ -n "$manifest" ] && [ -n "$build" ] || { echo "no manifest/build in $ACF" >&2; exit 1; }

rm -rf "$OUT"; mkdir -p "$OUT"
T="$OUT/port.txt"
{
    echo "app 1260520"
    echo "depot 1260521"
    echo "manifest $manifest"
    echo "build $build"
    echo "prefix $D\\"
} > "$T"
cd "$GAME"
find . -type f -size +0 -printf '%P\n' | sort | while IFS= read -r f; do
    h=$(sha1sum "$f" | cut -c1-40)
    p=$(printf '%s' "$f" | tr '/' '\\')
    case "$f" in
        "$D"/*.resS|"$D"/*.resource|"$D"/StreamingAssets/*.bank)
            rest="${f#"$D"/}"
            if [ "${rest#StreamingAssets/}" = "$rest" ] && [ "${rest%/*}" != "$rest" ]; then echo "base $h $p"; continue; fi
            echo "dl $h $(stat -c %s "$f") $p" ;;
        *) echo "base $h $p" ;;
    esac
done >> "$T"
echo "port.txt: manifest $manifest build $build, $(grep -c '^base ' "$T") base files, $(grep -c '^dl ' "$T") downloaded ($(grep '^dl ' "$T" | awk '{s+=$3} END {printf "%.0f MB", s/1e6}'))"
