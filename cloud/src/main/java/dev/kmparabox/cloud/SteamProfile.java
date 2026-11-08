package dev.kmparabox.cloud;

import android.content.Context;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.Closeable;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import in.dragonbra.javasteam.enums.EResult;
import in.dragonbra.javasteam.protobufs.steamclient.SteammessagesPlayerSteamclient.CPlayer_GetOwnedGames_Request;
import in.dragonbra.javasteam.rpc.service.Player;
import in.dragonbra.javasteam.steam.handlers.steamfriends.SteamFriends;
import in.dragonbra.javasteam.steam.handlers.steamfriends.callback.PersonaStateCallback;
import in.dragonbra.javasteam.steam.handlers.steamunifiedmessages.SteamUnifiedMessages;
import in.dragonbra.javasteam.steam.handlers.steamuserstats.AchievementBlocks;
import in.dragonbra.javasteam.steam.handlers.steamuserstats.SteamUserStats;
import in.dragonbra.javasteam.steam.handlers.steamuserstats.callback.UserStatsCallback;
import in.dragonbra.javasteam.types.SteamID;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

// What the launcher shows about the signed-in account: name and avatar, the game's achievements
// (with their Chinese names, icons and unlock times) and the playtime Steam has on record. Fetched
// in one short session and cached in files/profile/, so the launcher has it at once next time.
final class SteamProfile {
    private static final String TAG = "ParaboxCloud";

    static final class Ach {
        String api, name, desc, icon, iconGray;
        boolean hidden;
        long unlocked; // unix seconds, 0 = locked
    }

    String persona = "";
    long steamId;
    int minutes;         // playtime_forever
    long lastPlayed;     // unix seconds
    long fetched;
    final List<Ach> achs = new ArrayList<>();

    int unlockedCount() {
        int n = 0;
        for (Ach a : achs) if (a.unlocked > 0) n++;
        return n;
    }

    static File dir(Context c) {
        File d = new File(c.getFilesDir(), "profile");
        d.mkdirs();
        return d;
    }

    static File avatar(Context c) {
        return new File(dir(c), "avatar.jpg");
    }

    static File icon(Context c, String hash) {
        return new File(dir(c), "ach-" + hash + ".jpg");
    }

    static SteamProfile cached(Context c) {
        File f = new File(dir(c), "profile.json");
        if (!f.isFile()) return null;
        try {
            JSONObject o = new JSONObject(new String(Files.readAllBytes(f.toPath()), "UTF-8"));
            SteamProfile p = new SteamProfile();
            p.persona = o.optString("persona");
            p.steamId = o.optLong("steamId");
            p.minutes = o.optInt("minutes");
            p.lastPlayed = o.optLong("lastPlayed");
            p.fetched = o.optLong("fetched");
            JSONArray a = o.optJSONArray("achs");
            if (a != null)
                for (int i = 0; i < a.length(); i++) {
                    JSONObject x = a.getJSONObject(i);
                    Ach h = new Ach();
                    h.api = x.optString("api");
                    h.name = x.optString("name");
                    h.desc = x.optString("desc");
                    h.icon = x.optString("icon");
                    h.iconGray = x.optString("iconGray");
                    h.hidden = x.optBoolean("hidden");
                    h.unlocked = x.optLong("unlocked");
                    p.achs.add(h);
                }
            return p;
        } catch (Exception e) {
            return null;
        }
    }

    private void save(Context c) throws Exception {
        JSONObject o = new JSONObject();
        o.put("persona", persona).put("steamId", steamId).put("minutes", minutes).put("lastPlayed", lastPlayed).put("fetched", fetched);
        JSONArray a = new JSONArray();
        for (Ach h : achs)
            a.put(new JSONObject().put("api", h.api).put("name", h.name).put("desc", h.desc).put("icon", h.icon)
                    .put("iconGray", h.iconGray).put("hidden", h.hidden).put("unlocked", h.unlocked));
        o.put("achs", a);
        File f = new File(dir(c), "profile.json"), tmp = new File(dir(c), "profile.json.tmp");
        Files.write(tmp.toPath(), o.toString().getBytes("UTF-8"));
        if (!tmp.renameTo(f)) { f.delete(); tmp.renameTo(f); }
    }

    static void forget(Context c) {
        File[] fs = dir(c).listFiles();
        if (fs != null) for (File f : fs) f.delete();
    }

    static SteamProfile fetch(Context c) throws Exception {
        TokenStore t = TokenStore.load(c);
        if (t == null) throw new Exception("尚未登录 Steam");
        SteamProfile p = new SteamProfile();
        SteamProfile old = cached(c);
        byte[][] avatarHash = { null };
        CountDownLatch persona = new CountDownLatch(1);
        try (SteamSession s = new SteamSession()) {
            if (s.client.getHandler(SteamUserStats.class) == null) s.client.addHandler(new SteamUserStats());
            s.logOn(t.account, t.refreshToken, TokenStore.loginId(c) ^ 0x30000);
            p.steamId = s.steamId64;
            SteamID me = new SteamID(s.steamId64);
            SteamFriends friends = s.client.getHandler(SteamFriends.class);
            try (Closeable sub = s.on(PersonaStateCallback.class, cb -> {
                if (cb.getFriendId() == null || cb.getFriendId().convertToUInt64() != s.steamId64) return;
                if (cb.getName() != null && !cb.getName().isEmpty()) p.persona = cb.getName();
                if (cb.getAvatarHash() != null && cb.getAvatarHash().length > 0) avatarHash[0] = cb.getAvatarHash();
                persona.countDown();
            })) {
                friends.requestFriendInfo(me);
                persona.await(8, TimeUnit.SECONDS);
            }
            if (p.persona.isEmpty() && friends.getPersonaName() != null) p.persona = friends.getPersonaName();
            if (p.persona.isEmpty()) p.persona = t.account;

            // Achievements, in the language the schema offers for Simplified Chinese.
            try {
                UserStatsCallback st = s.client.getHandler(SteamUserStats.class).getUserStats(CloudSync.APP, me).toFuture().get(30, TimeUnit.SECONDS);
                if (st.getResult() == EResult.OK) {
                    for (AchievementBlocks b : st.getExpandedAchievements("schinese")) {
                        Ach a = new Ach();
                        a.api = b.getName();
                        a.name = b.getDisplayName();
                        a.desc = b.getDescription();
                        a.icon = b.getIcon();
                        a.iconGray = b.getIconGray();
                        a.hidden = b.getHidden();
                        a.unlocked = b.getUnlockTimestamp() & 0xFFFFFFFFL;
                        p.achs.add(a);
                    }
                } else Log.w(TAG, "profile: stats " + st.getResult());
            } catch (Exception e) {
                Log.w(TAG, "profile: achievements: " + e);
                if (old != null) p.achs.addAll(old.achs);
            }

            // Playtime on record (minutes), as the Steam library shows it.
            try {
                Player player = s.client.getHandler(SteamUnifiedMessages.class).createService(Player.class);
                CPlayer_GetOwnedGames_Request req = CPlayer_GetOwnedGames_Request.newBuilder()
                        .setSteamid(s.steamId64).setIncludePlayedFreeGames(true).addAppidsFilter(CloudSync.APP).build();
                var body = player.getOwnedGames(req).toFuture().get(30, TimeUnit.SECONDS).getBody();
                for (var g : body.getGamesList())
                    if (g.getAppid() == CloudSync.APP) {
                        p.minutes = g.getPlaytimeForever();
                        p.lastPlayed = g.getRtimeLastPlayed() & 0xFFFFFFFFL;
                    }
            } catch (Exception e) {
                Log.w(TAG, "profile: playtime: " + e);
                if (old != null) { p.minutes = old.minutes; p.lastPlayed = old.lastPlayed; }
            }
        }
        p.fetched = System.currentTimeMillis();
        p.save(c);
        fetchImages(c, p, avatarHash[0]);
        Log.i(TAG, "profile: " + p.persona + ", " + p.unlockedCount() + "/" + p.achs.size() + " achievements, " + p.minutes + " min");
        return p;
    }

    private static final OkHttpClient http = new OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS).readTimeout(30, TimeUnit.SECONDS).build();

    private static void fetchImages(Context c, SteamProfile p, byte[] avatarHash) {
        if (avatarHash != null) {
            String h = GameData.hex(avatarHash);
            if (!h.matches("0+")) get("https://avatars.steamstatic.com/" + h + "_full.jpg", avatar(c), true);
        }
        for (Ach a : p.achs) {
            for (String icon : new String[] { a.icon, a.iconGray }) {
                if (icon == null || icon.isEmpty()) continue;
                String name = icon.endsWith(".jpg") ? icon.substring(0, icon.length() - 4) : icon;
                File f = icon(c, name);
                if (f.isFile()) continue;
                get("https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/" + CloudSync.APP + "/" + name + ".jpg", f, false);
            }
        }
    }

    private static void get(String url, File to, boolean replace) {
        if (!replace && to.isFile()) return;
        try (Response r = http.newCall(new Request.Builder().url(url).build()).execute()) {
            if (!r.isSuccessful()) { Log.w(TAG, "image " + r.code() + " " + url); return; }
            File tmp = new File(to.getPath() + ".part");
            try (FileOutputStream o = new FileOutputStream(tmp)) { o.write(r.body().bytes()); }
            if (!tmp.renameTo(to)) { to.delete(); tmp.renameTo(to); }
        } catch (Exception e) {
            Log.w(TAG, "image " + url + ": " + e);
        }
    }
}
