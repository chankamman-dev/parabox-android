#!/usr/bin/env bash
# Compiles ParaboxAndroid.dll (the port's game-side code) against the staged assemblies.
set -euo pipefail
WORK=/e/android-recompile-work/parabox-android-work
HERE="$(cd "$(dirname "$0")" && pwd)"
CSC='C:\Program Files\dotnet\sdk\8.0.422\Roslyn\bincore\csc.dll'
OUT=$WORK/extra
[ -f "$WORK/stage/Assembly-CSharp.dll" ] || { echo "run pstage once first"; exit 1; }
RSP="$WORK/paraboxandroid.rsp"
{
    echo '-nologo'; echo '-noconfig'; echo '-nostdlib+'; echo '-target:library'; echo '-optimize+'; echo '-debug-'; echo '-langversion:8.0'
    echo "-out:$(cygpath -w "$OUT/ParaboxAndroid.dll")"
    for f in "$WORK"/stage/*.dll; do
        case "$(basename "$f")" in ParaboxAndroid.dll) continue ;; esac
        echo "-r:$(cygpath -w "$f")"
    done
    find "$HERE/paraboxandroid" -name '*.cs' | while read -r f; do echo "\"$(cygpath -w "$f")\""; done
} > "$RSP"
if dotnet "$CSC" "@$(cygpath -w "$RSP")" > "$WORK/csc.log" 2>&1; then :; else grep -E "error" "$WORK/csc.log" | cut -c1-220 | sort -u | head -20; exit 1; fi
ls -la "$OUT/ParaboxAndroid.dll"
