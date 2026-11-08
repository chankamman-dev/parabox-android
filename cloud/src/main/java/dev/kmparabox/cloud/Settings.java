package dev.kmparabox.cloud;

import android.content.Context;

// The launcher's player settings, read by the game through CloudBridge.intSetting.
final class Settings {
    static final String PREFS = "parabox_settings";

    static final int[] FPS = { 30, 60, 90, 120 };
    static final String[] FPS_LABEL = { "30 帧（省电）", "60 帧（推荐）", "90 帧", "120 帧" };
    static final int[] SCALE = { 100, 85, 70 };
    static final String[] SCALE_LABEL = { "100%（原生）", "85%", "70%（省电）" };

    static int get(Context c, String key, int def) {
        return c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getInt(key, def);
    }

    static void put(Context c, String key, int v) {
        c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putInt(key, v).apply();
    }

    private Settings() { }
}
