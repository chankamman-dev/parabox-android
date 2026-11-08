package dev.kmparabox.cloud;

import java.io.Closeable;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

import in.dragonbra.javasteam.enums.EResult;
import in.dragonbra.javasteam.steam.handlers.steamuser.LogOnDetails;
import in.dragonbra.javasteam.steam.handlers.steamuser.SteamUser;
import in.dragonbra.javasteam.steam.handlers.steamuser.callback.LoggedOnCallback;
import in.dragonbra.javasteam.steam.steamclient.SteamClient;
import in.dragonbra.javasteam.steam.steamclient.callbackmgr.CallbackManager;
import in.dragonbra.javasteam.steam.steamclient.callbackmgr.CallbackMsg;
import in.dragonbra.javasteam.util.compat.Consumer;
import in.dragonbra.javasteam.steam.steamclient.callbacks.ConnectedCallback;
import in.dragonbra.javasteam.steam.steamclient.callbacks.DisconnectedCallback;

// One connection to Steam, with its callback pump on a daemon thread.
final class SteamSession implements Closeable {
    final SteamClient client = new SteamClient();
    private final CallbackManager callbacks = new CallbackManager(client);
    private final AtomicBoolean running = new AtomicBoolean(true);
    long steamId64;

    SteamSession() {
        SteamCrypto.install();
        Thread pump = new Thread(() -> {
            while (running.get()) {
                try {
                    callbacks.runWaitCallbacks(100L);
                } catch (Throwable t) {
                    if (!running.get()) break;
                }
            }
        }, "ParaboxCloud-pump");
        pump.setDaemon(true);
        pump.start();
    }

    <T extends CallbackMsg> Closeable on(Class<T> type, Consumer<T> handler) {
        return callbacks.subscribe(type, handler);
    }

    void connect() throws Exception {
        CountDownLatch done = new CountDownLatch(1);
        AtomicBoolean ok = new AtomicBoolean(false);
        Closeable a = callbacks.subscribe(ConnectedCallback.class, cb -> { ok.set(true); done.countDown(); });
        Closeable b = callbacks.subscribe(DisconnectedCallback.class, cb -> done.countDown());
        try {
            client.connect();
            if (!done.await(25, TimeUnit.SECONDS)) throw new Exception("连接 Steam 超时");
            if (!ok.get()) throw new Exception("Steam 断开了连接");
        } finally {
            a.close();
            b.close();
        }
    }

    void logOn(String account, String refreshToken, int loginId) throws Exception {
        for (int attempt = 1; ; attempt++) {
            connect();
            SteamUser user = client.getHandler(SteamUser.class);
            CountDownLatch done = new CountDownLatch(1);
            AtomicReference<LoggedOnCallback> result = new AtomicReference<>();
            Closeable s = callbacks.subscribe(LoggedOnCallback.class, cb -> { result.set(cb); done.countDown(); });
            try {
                LogOnDetails d = new LogOnDetails();
                d.setUsername(account);
                d.setAccessToken(refreshToken);
                d.setShouldRememberPassword(true);
                d.setLoginID(loginId);
                user.logOn(d);
                if (!done.await(25, TimeUnit.SECONDS)) throw new Exception("登录 Steam 超时");
            } finally {
                s.close();
            }
            EResult r = result.get().getResult();
            if (r == EResult.OK) {
                steamId64 = result.get().getClientSteamID().convertToUInt64();
                return;
            }
            if (r != EResult.TryAnotherCM || attempt >= 3) throw new LogOnRejected(r);
            client.disconnect();
            Thread.sleep(500L * attempt);
        }
    }

    // Anonymous logon: only free depots (used by the PC test of the download path).
    void logOnAnonymous() throws Exception {
        connect();
        CountDownLatch done = new CountDownLatch(1);
        AtomicReference<LoggedOnCallback> result = new AtomicReference<>();
        Closeable s = callbacks.subscribe(LoggedOnCallback.class, cb -> { result.set(cb); done.countDown(); });
        try {
            client.getHandler(SteamUser.class).logOnAnonymous();
            if (!done.await(25, TimeUnit.SECONDS)) throw new Exception("匿名登录超时");
        } finally {
            s.close();
        }
        if (result.get().getResult() != EResult.OK) throw new LogOnRejected(result.get().getResult());
    }

    static final class LogOnRejected extends Exception {
        final EResult result;

        LogOnRejected(EResult r) {
            super("Steam 拒绝登录：" + r);
            result = r;
        }
    }

    @Override
    public void close() {
        running.set(false);
        try {
            client.disconnect();
        } catch (Throwable ignored) {
        }
    }
}
