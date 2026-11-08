#!/usr/bin/env bash
# Compiles the launcher / Steam module into build/libs/parabox-cloud.jar. The runtime dependencies
# (JavaSteam 1.8.0 and what it needs) are in build/deps, copied from princess-android/cloud.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
JDK='/c/Program Files/Eclipse Adoptium/jdk-21.0.12.101-hotspot/bin'
# Android SDK: ANDROID_HOME, or put ANDROID_HOME=... in local.env at the repo root (not committed).
[ -f "$HERE/../local.env" ] && . "$HERE/../local.env"
: "${ANDROID_HOME:?set ANDROID_HOME (or write it to local.env)}"
JAR="$(cygpath -w "$ANDROID_HOME/platforms/android-35/android.jar")"
CP="$JAR"
for j in "$HERE"/build/deps/*.jar; do CP="$CP;$(cygpath -w "$j")"; done
rm -rf "$HERE/build/classes"; mkdir -p "$HERE/build/classes" "$HERE/build/libs"
mapfile -t SRCS < <(find "$HERE/src/main/java" -name '*.java' | while read -r f; do cygpath -w "$f"; done)
"$JDK/javac" -nowarn -encoding UTF-8 --release 17 -cp "$CP" -d "$(cygpath -w "$HERE/build/classes")" "${SRCS[@]}" 2>&1 | grep -vE '^Note|^注' || true
[ -f "$HERE/build/classes/dev/kmparabox/cloud/LauncherActivity.class" ] || { echo "cloud: compile failed" >&2; exit 1; }
(cd "$HERE/build/classes" && "$JDK/jar" cf ../libs/parabox-cloud.jar .)
ls -la "$HERE/build/libs/parabox-cloud.jar"
