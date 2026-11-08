package dev.kmparabox.cloud;

import android.app.Activity;
import android.app.Application;
import android.content.Context;
import android.content.SharedPreferences;
import android.os.Bundle;
import android.util.Log;

import java.io.Closeable;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import in.dragonbra.javasteam.base.ClientMsgProtobuf;
import in.dragonbra.javasteam.base.IPacketMsg;
import in.dragonbra.javasteam.enums.EMsg;
import in.dragonbra.javasteam.enums.EPersonaState;
import in.dragonbra.javasteam.enums.EResult;
import in.dragonbra.javasteam.protobufs.steamclient.SteammessagesClientserver.CMsgClientGamesPlayed;
import in.dragonbra.javasteam.protobufs.steamclient.SteammessagesClientserverUserstats.CMsgClientStoreUserStats2;
import in.dragonbra.javasteam.protobufs.steamclient.SteammessagesClientserverUserstats.CMsgClientStoreUserStatsResponse;
import in.dragonbra.javasteam.steam.handlers.ClientMsgHandler;
import in.dragonbra.javasteam.steam.handlers.steamfriends.SteamFriends;
import in.dragonbra.javasteam.steam.handlers.steamuser.callback.LoggedOffCallback;
import in.dragonbra.javasteam.steam.handlers.steamuser.callback.PlayingSessionStateCallback;
import in.dragonbra.javasteam.steam.handlers.steamuserstats.Stats;
import in.dragonbra.javasteam.steam.handlers.steamuserstats.SteamUserStats;
import in.dragonbra.javasteam.steam.handlers.steamuserstats.callback.UserStatsCallback;
import in.dragonbra.javasteam.steam.steamclient.callbacks.DisconnectedCallback;
import in.dragonbra.javasteam.types.KeyValue;
import in.dragonbra.javasteam.types.SteamID;

// While the game is on screen, holds a Steam connection that reports the game as running, so
// Steam counts the playtime as it does for the desktop client, and unlocks the achievements the
// game earns. Unlocks wait in preferences until a connection takes them; the game also offers every
// achievement it already holds on each start, so ones earned offline or before signing in arrive.
// If another device is playing on this account, Steam blocks the session and nothing is reported
// until that ends, as the desktop client would.
final class SteamPlay implements Application.ActivityLifecycleCallbacks {
    private static final String TAG = "ParaboxCloud";
    private static final String PREFS = "parabox_play";
    private static SteamPlay instance;

    private final Context app;
    private int visible = 1;
    private boolean dirty, closed, blocked, stateSeen, rejected;

    private SteamPlay(Context app) {
        this.app = app;
    }

    static synchronized void start(Context c) {
        if (instance != null) return;
        instance = new SteamPlay(c.getApplicationContext());
        ((Application) instance.app).registerActivityLifecycleCallbacks(instance);
        Thread t = new Thread(instance::run, "ParaboxCloud-play");
        t.setDaemon(true);
        t.start();
    }

    static void unlock(Context c, String[] names) {
        Context app = c.getApplicationContext();
        SharedPreferences p = prefs(app);
        Set<String> done = p.getStringSet("done", new HashSet<>());
        Set<String> pending = new HashSet<>(p.getStringSet("pending", new HashSet<>()));
        int added = 0;
        for (String n : names) if (!n.isEmpty() && !done.contains(n) && pending.add(n)) added++;
        if (added == 0) return;
        p.edit().putStringSet("pending", pending).apply();
        Log.i(TAG, "achievements queued: " + added + " (" + pending.size() + " waiting)");
        SteamPlay s = instance;
        if (s != null) synchronized (s) { s.dirty = true; s.notifyAll(); }
    }

    // Signing out: what was sent belongs to that account.
    static void forget(Context c) {
        c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().clear().apply();
    }

    // The "done" set written by the bit-0 build (format 1, see flushAchievements) is not trusted:
    // names landed there whenever bit 0 of their stat was set, without being sent. It is dropped
    // once, before anything reads it, so the game's next offer of every achievement it holds
    // (Bridge.Unlocked) is checked against Steam again.
    private static SharedPreferences prefs(Context app) {
        SharedPreferences p = app.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        if (p.getInt("format", 1) < 2) {
            p.edit().remove("done").putInt("format", 2).apply();
            Log.i(TAG, "achievement records from the bit-0 build dropped; everything is checked again");
        }
        return p;
    }

    private void run() {
        long backoff = 30;
        while (true) {
            synchronized (this) {
                while (visible <= 0 || rejected || TokenStore.load(app) == null) waitQuietly(60_000);
                closed = false;
                blocked = false;
                stateSeen = false;
                dirty = true;
            }
            try {
                long began = System.currentTimeMillis();
                session();
                if (System.currentTimeMillis() - began > 120_000) backoff = 30;
                else {
                    // Ended on its own right away: do not hammer Steam with logons.
                    synchronized (this) { if (visible > 0) waitQuietly(backoff * 1000); }
                    backoff = Math.min(backoff * 2, 600);
                }
            } catch (SteamSession.LogOnRejected e) {
                Log.w(TAG, "play session: " + e.getMessage() + ", stopped until the next launch");
                synchronized (this) { rejected = true; }
            } catch (Throwable t) {
                Log.w(TAG, "play session failed: " + t + ", retry in " + backoff + "s");
                synchronized (this) { waitQuietly(backoff * 1000); }
                backoff = Math.min(backoff * 2, 600);
            }
        }
    }

    private void session() throws Exception {
        TokenStore t = TokenStore.load(app);
        try (SteamSession s = new SteamSession();
             Closeable a = s.on(PlayingSessionStateCallback.class, cb -> { synchronized (this) { blocked = cb.isPlayingBlocked(); stateSeen = true; Log.i(TAG, "playing session state: blocked " + blocked + ", app " + cb.getPlayingAppID()); notifyAll(); } })) {
            // Handlers go in before connecting: SteamClient keeps them in a plain HashMap that the
            // network thread walks for every message, and adding one mid-stream drops the connection.
            StoreReply reply = new StoreReply();
            s.client.addHandler(reply);
            if (s.client.getHandler(SteamUserStats.class) == null) s.client.addHandler(new SteamUserStats());
            // A login id of its own, so the short cloud upload sessions never replace this one.
            s.logOn(t.account, t.refreshToken, TokenStore.loginId(app) ^ 0x10000);
            try (Closeable b = s.on(DisconnectedCallback.class, cb -> { Log.i(TAG, "play session disconnected, by us: " + cb.isUserInitiated()); synchronized (this) { closed = true; notifyAll(); } });
                 Closeable c = s.on(LoggedOffCallback.class, cb -> { Log.i(TAG, "play session logged off: " + cb.getResult()); synchronized (this) { closed = true; notifyAll(); } })) {
                loop(s, reply);
            }
        }
    }

    private void loop(SteamSession s, StoreReply reply) {
        // A session that only logs on is invisible, and friends never see the game it plays; the
        // desktop client is online while a game runs, so this one is too.
        s.client.getHandler(SteamFriends.class).setPersonaState(EPersonaState.Online);
        Log.i(TAG, "play session up");
        synchronized (this) {
            // Steam states right after logon whether another device is playing; give it a
            // moment before claiming the game.
            if (!s.client.isConnected()) closed = true;
            long until = System.currentTimeMillis() + 3000;
            while (!stateSeen && !closed && System.currentTimeMillis() < until) waitQuietly(until - System.currentTimeMillis());
        }
        boolean playing = false;
        while (true) {
            boolean want, flush;
            synchronized (this) {
                if (closed || visible <= 0) break;
                want = !blocked;
                flush = dirty;
                dirty = false;
                if (want == playing && !flush) {
                    waitQuietly(60_000);
                    continue;
                }
            }
            if (want != playing) {
                gamesPlayed(s, want);
                playing = want;
                Log.i(TAG, want ? "playing " + CloudSync.APP : "play paused: another device is playing");
            }
            if (flush) {
                try {
                    flushAchievements(s, reply);
                } catch (Throwable e) {
                    Log.w(TAG, "achievements not sent: " + e + "; they wait for the next connection");
                }
            }
        }
        if (playing && !closed) gamesPlayed(s, false);
        synchronized (this) { Log.i(TAG, "play session down: closed " + closed + ", visible " + visible); }
    }

    private static void gamesPlayed(SteamSession s, boolean on) {
        ClientMsgProtobuf<CMsgClientGamesPlayed.Builder> m = new ClientMsgProtobuf<>(CMsgClientGamesPlayed.class, EMsg.ClientGamesPlayedWithDataBlob);
        if (on) m.getBody().addGamesPlayed(CMsgClientGamesPlayed.GamePlayed.newBuilder().setGameId(CloudSync.APP).setProcessId(android.os.Process.myPid()));
        s.client.send(m);
    }

    // Each achievement is one bit of a stat in the game's schema: stats/<statId>/bits/<bitIndex>/name.
    // The bit index is the KEY of the child; there is no "bit" field, and the indices need not be
    // contiguous (Legend of Mortal has no bit 4: ACH_30004 is bit 5). The first build read
    // bit.get("bit"), which is absent and reads as 0, so every achievement mapped to bit 0 of its
    // stat. Legend of Mortal keeps all 22 in stat 1, whose bit 0 (ACH_30000) has been set on the
    // account since 2024-06-28, so that build took every name as already unlocked and never wrote
    // anything (checked 2026-11-06 against the public profile and the save; see HANDOFF.md). Its
    // records are dropped once; see prefs.
    // The bits to set are OR-ed into the current values and stored with the stats CRC Steam just
    // reported.
    private void flushAchievements(SteamSession s, StoreReply reply) throws Exception {
        SharedPreferences p = prefs(app);
        Set<String> pending = new HashSet<>(p.getStringSet("pending", new HashSet<>()));
        if (pending.isEmpty()) return;
        SteamUserStats us = s.client.getHandler(SteamUserStats.class);
        UserStatsCallback st = us.getUserStats(CloudSync.APP, new SteamID(s.steamId64)).toFuture().get(30, TimeUnit.SECONDS);
        if (st.getResult() != EResult.OK) throw new Exception("读取成就失败：" + st.getResult());
        Map<Integer, Integer> values = new HashMap<>();
        for (Stats v : st.getStats()) values.put(v.getStatId(), v.getStatValue());
        Map<Integer, Integer> changed = new HashMap<>();
        Set<String> known = new HashSet<>(), unlocked = new HashSet<>();
        KeyValue schema = st.getSchemaKeyValues().get("stats");
        for (KeyValue stat : schema.getChildren()) {
            int id;
            try {
                id = Integer.parseInt(stat.getName());
            } catch (NumberFormatException e) {
                continue;
            }
            for (KeyValue bit : stat.get("bits").getChildren()) {
                String name = bit.get("name").getValue();
                if (name == null || !pending.contains(name)) continue;
                int index;
                try {
                    index = Integer.parseInt(bit.getName());
                } catch (NumberFormatException e) {
                    continue;
                }
                if (index < 0 || index > 31) continue;
                known.add(name);
                int mask = 1 << index;
                int cur = changed.containsKey(id) ? changed.get(id) : values.getOrDefault(id, 0);
                if ((cur & mask) != 0) continue;
                changed.put(id, cur | mask);
                unlocked.add(name);
                Log.i(TAG, "unlocking " + name + " (stat " + id + " bit " + index + ")");
            }
        }
        for (String n : pending) if (!known.contains(n)) Log.w(TAG, "achievement not in the Steam schema: " + n);
        if (!changed.isEmpty()) {
            ClientMsgProtobuf<CMsgClientStoreUserStats2.Builder> m = new ClientMsgProtobuf<>(CMsgClientStoreUserStats2.class, EMsg.ClientStoreUserStats2);
            m.setSourceJobID(s.client.getNextJobID());
            m.getBody().setGameId(CloudSync.APP).setSettorSteamId(s.steamId64).setSetteeSteamId(s.steamId64)
                    .setCrcStats(st.getCrcStats()).setExplicitReset(false);
            for (Map.Entry<Integer, Integer> e : changed.entrySet())
                m.getBody().addStats(CMsgClientStoreUserStats2.Stats.newBuilder().setStatId(e.getKey()).setStatValue(e.getValue()));
            CompletableFuture<CMsgClientStoreUserStatsResponse> answer = reply.expect();
            s.client.send(m);
            CMsgClientStoreUserStatsResponse r = answer.get(30, TimeUnit.SECONDS);
            if (r.getEresult() != EResult.OK.code()) throw new Exception("写入成就失败：" + EResult.from(r.getEresult()));
            Log.i(TAG, "achievements unlocked on Steam: " + unlocked);
        }
        Set<String> done = new HashSet<>(p.getStringSet("done", new HashSet<>()));
        done.addAll(pending);
        Set<String> left = new HashSet<>(p.getStringSet("pending", new HashSet<>()));
        left.removeAll(pending);
        p.edit().putStringSet("done", done).putStringSet("pending", left).apply();
        Log.i(TAG, "achievements checked: " + pending.size() + ", newly unlocked " + unlocked.size());
    }

    // Steam answers a stats store with ClientStoreUserStatsResponse, which JavaSteam has no handler for.
    private static final class StoreReply extends ClientMsgHandler {
        private CompletableFuture<CMsgClientStoreUserStatsResponse> next;

        synchronized CompletableFuture<CMsgClientStoreUserStatsResponse> expect() {
            return next = new CompletableFuture<>();
        }

        @Override
        public void handleMsg(IPacketMsg packet) {
            if (packet.getMsgType() != EMsg.ClientStoreUserStatsResponse) return;
            ClientMsgProtobuf<CMsgClientStoreUserStatsResponse.Builder> m = new ClientMsgProtobuf<>(CMsgClientStoreUserStatsResponse.class, packet);
            synchronized (this) {
                if (next != null) next.complete(m.getBody().build());
            }
        }
    }

    private void waitQuietly(long ms) {
        try {
            wait(ms);
        } catch (InterruptedException ignored) {
        }
    }

    @Override
    public synchronized void onActivityStarted(Activity a) {
        visible++;
        notifyAll();
    }

    @Override
    public synchronized void onActivityStopped(Activity a) {
        visible = Math.max(0, visible - 1);
        notifyAll();
    }

    @Override public void onActivityCreated(Activity a, Bundle b) { }
    @Override public void onActivityResumed(Activity a) { }
    @Override public void onActivityPaused(Activity a) { }
    @Override public void onActivitySaveInstanceState(Activity a, Bundle b) { }
    @Override public void onActivityDestroyed(Activity a) { }
}
