package dev.kmparabox.cloud;

import android.content.Context;
import android.content.SharedPreferences;

import java.util.Random;

// The Steam refresh token and the identity it belongs to. Kept in the app's private preferences.
final class TokenStore {
    private static final String PREFS = "parabox_cloud";

    final String account;
    final String refreshToken;

    private TokenStore(String account, String refreshToken) {
        this.account = account;
        this.refreshToken = refreshToken;
    }

    static TokenStore load(Context c) {
        SharedPreferences p = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        String a = p.getString("account", null), t = p.getString("token", null);
        return a == null || t == null ? null : new TokenStore(a, t);
    }

    static void save(Context c, String account, String token) {
        c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString("account", account).putString("token", token).apply();
    }

    static void clear(Context c) {
        c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().remove("account").remove("token").apply();
    }

    // A stable login id of our own, so this session never replaces the desktop client's.
    static int loginId(Context c) {
        SharedPreferences p = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        int id = p.getInt("login_id", 0);
        if (id == 0) {
            id = 0x4C4F0000 | (new Random().nextInt(0xFFFF) + 1);
            p.edit().putInt("login_id", id).apply();
        }
        return id;
    }

}
