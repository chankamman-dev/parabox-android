#!/usr/bin/env bash
# Starts the port (through its launcher) on the parabox-sim emulator and captures its log and a screenshot.
# usage: run-emu.sh <name> <seconds>      (SERIAL defaults to emulator-5620)
export MSYS_NO_PATHCONV=1
A="/h/android-emu/sdk/platform-tools/adb.exe"
S="-s ${SERIAL:-emulator-5620}"
N=${1:-run}; T=${2:-30}
O=/e/android-recompile-work/parabox-android-work/steps; mkdir -p "$O"
"$A" $S shell input keyevent KEYCODE_WAKEUP; "$A" $S shell wm dismiss-keyguard
"$A" $S shell am force-stop dev.kmparabox.game
"$A" $S logcat -c
# The launcher starts the game itself when the data is ready (--ez play true); EXTRA adds more extras.
"$A" $S shell am start -n dev.kmparabox.game/dev.kmparabox.cloud.LauncherActivity ${PLAY:---ez play true} ${EXTRA:-} | tail -1
sleep "$T"
echo "pid=[$("$A" $S shell pidof dev.kmparabox.game | tr -d '\r')]"
"$A" $S logcat -d -v threadtime > "$O/$N.log"
"$A" $S exec-out screencap -p > "$O/$N.png"
grep -E ' (Unity|Parabox|fmod|FMOD|DEBUG|libc|AndroidRuntime)\b' "$O/$N.log" | grep -vE 'Unity +: *$' | cut -c1-220 | head -${LINES_OUT:-60}
