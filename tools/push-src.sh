#!/usr/bin/env bash
# Developer test of the device-side install without Steam: copies the Windows originals of the files the
# launcher would download into the app's external dir. Then start the launcher with
#   am start -n dev.kmparabox.game/dev.kmparabox.cloud.LauncherActivity --es localSource /sdcard/Android/data/dev.kmparabox.game/files/pbx-src
# usage: push-src.sh [serial]   (emulator with adb root; the app must be installed)
set -euo pipefail
export MSYS_NO_PATHCONV=1
A=/h/android-emu/sdk/platform-tools/adb.exe
S=${1:-emulator-5620}
G="H:/SteamLibrary/steamapps/common/Patrick's Parabox/Patrick's Parabox_Data"
DST=/sdcard/Android/data/dev.kmparabox.game/files/pbx-src
"$A" -s "$S" shell mkdir -p "$DST"
for f in sharedassets0.assets.resS resources.assets.resS globalgamemanagers.assets.resS StreamingAssets/Master.bank StreamingAssets/Master.strings.bank StreamingAssets/Music.bank; do
    "$A" -s "$S" push "$G/$f" "$DST/" | tail -1
done
# Pushed as root (adb root), the folder would be root's and the app could not read it.
U=$("$A" -s "$S" shell stat -c %u /data/data/dev.kmparabox.game | tr -d '\r')
"$A" -s "$S" shell chown -R "$U:ext_data_rw" /data/media/0/Android/data/dev.kmparabox.game
