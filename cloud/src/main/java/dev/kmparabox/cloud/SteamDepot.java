package dev.kmparabox.cloud;

import java.io.Closeable;
import java.io.IOException;
import java.io.RandomAccessFile;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

import in.dragonbra.javasteam.enums.EResult;
import in.dragonbra.javasteam.steam.cdn.Client;
import in.dragonbra.javasteam.steam.cdn.Server;
import in.dragonbra.javasteam.steam.handlers.steamapps.PICSProductInfo;
import in.dragonbra.javasteam.steam.handlers.steamapps.PICSRequest;
import in.dragonbra.javasteam.steam.handlers.steamapps.SteamApps;
import in.dragonbra.javasteam.steam.handlers.steamapps.callback.DepotKeyCallback;
import in.dragonbra.javasteam.steam.handlers.steamapps.callback.PICSProductInfoCallback;
import in.dragonbra.javasteam.steam.handlers.steamapps.callback.PICSTokensCallback;
import in.dragonbra.javasteam.steam.handlers.steamcontent.SteamContent;
import in.dragonbra.javasteam.types.ChunkData;
import in.dragonbra.javasteam.types.DepotManifest;
import in.dragonbra.javasteam.types.FileData;
import in.dragonbra.javasteam.types.KeyValue;
import kotlinx.coroutines.Deferred;
import kotlinx.coroutines.GlobalScope;
import kotlinx.coroutines.future.FutureKt;

// Reads one depot of an app from the Steam content network, the way DepotDownloader does:
// app info (PICS) for the manifest id, the depot key, a manifest request code, then the manifest and
// its chunks from a CDN server. Needs a logged-on session whose account owns the app (or an
// anonymous one for free depots).
final class SteamDepot implements Closeable {
    final int appId, depotId;
    private final SteamSession session;
    private final SteamApps apps;
    private final SteamContent content;
    private final Client cdn;
    private final List<Server> servers = new ArrayList<>();
    private final AtomicInteger next = new AtomicInteger();
    private byte[] depotKey;

    SteamDepot(SteamSession session, int appId, int depotId) {
        this.session = session;
        this.appId = appId;
        this.depotId = depotId;
        this.apps = session.client.getHandler(SteamApps.class);
        this.content = session.client.getHandler(SteamContent.class);
        this.cdn = new Client(session.client);
    }

    // The manifest id of the depot on a branch ("public"), from the app's PICS info.
    long branchManifest(String branch) throws Exception {
        PICSTokensCallback tokens = get(apps.picsGetAccessTokens(appId, null).toFuture());
        Long token = tokens.getAppTokens().get(appId);
        PICSRequest req = new PICSRequest(appId, token == null ? 0L : token);
        List<PICSProductInfoCallback> res = get(apps.picsGetProductInfo(req, null).toFuture()).getResults();
        for (PICSProductInfoCallback cb : res) {
            PICSProductInfo info = cb.getApps().get(appId);
            if (info == null) continue;
            KeyValue m = info.getKeyValues().get("depots").get(String.valueOf(depotId)).get("manifests").get(branch);
            String gid = m.get("gid").getValue();
            if (gid == null) gid = m.getValue();
            if (gid != null) return Long.parseLong(gid);
        }
        throw new IOException("Steam 没有返回 depot " + depotId + " 的版本信息（账号可能没有这个游戏）");
    }

    private synchronized void prepare() throws Exception {
        if (depotKey == null) {
            DepotKeyCallback k = get(apps.getDepotDecryptionKey(depotId, appId).toFuture());
            if (k.getResult() != EResult.OK) throw new IOException("拿不到游戏内容密钥（" + k.getResult() + "），请确认账号拥有这个游戏");
            depotKey = k.getDepotKey();
        }
        if (servers.isEmpty()) {
            Deferred<List<Server>> d = content.getServersForSteamPipe(null, null, GlobalScope.INSTANCE);
            List<Server> all = get(FutureKt.asCompletableFuture(d));
            for (Server s : all) {
                String t = s.getType();
                if (!"SteamCache".equals(t) && !"CDN".equals(t)) continue;
                if (s.getUseAsProxy()) continue; // needs the proxy URL template, not a plain content server
                int[] allowed = s.getAllowedAppIds();
                if (allowed != null && allowed.length > 0 && Arrays.stream(allowed).noneMatch(x -> x == appId)) continue;
                servers.add(s);
            }
            servers.sort(Comparator.comparingDouble(Server::getWeightedLoad));
            if (servers.isEmpty()) throw new IOException("没有可用的 Steam 下载服务器");
        }
    }

    private Server server(int attempt) {
        return servers.get((next.get() + attempt) % servers.size());
    }

    private void blame(Server s) {
        // Move on to the next server for everyone.
        synchronized (servers) {
            if (servers.size() > 1 && servers.get(next.get() % servers.size()) == s) next.incrementAndGet();
        }
    }

    DepotManifest manifest(long gid) throws Exception {
        prepare();
        Exception last = null;
        for (int attempt = 0; attempt < 6; attempt++) {
            Server s = server(attempt);
            try {
                Deferred<Long> code = content.getManifestRequestCode(depotId, appId, gid, "public", null, GlobalScope.INSTANCE);
                long rc = get(FutureKt.asCompletableFuture(code));
                DepotManifest m = get(cdn.downloadManifestFuture(depotId, gid, rc, s, depotKey));
                if (m.getFilenamesEncrypted() && !m.decryptFilenames(depotKey)) throw new IOException("文件名解密失败");
                return m;
            } catch (Exception e) {
                last = e;
                blame(s);
            }
        }
        throw new IOException("下载版本清单失败：" + rootMessage(last), last);
    }

    interface ChunkSink { void done(int bytes); }

    // Writes one file of the manifest to `dst` and checks its SHA-1. Chunks of a large file are fetched
    // several at a time (the game's data is a handful of big files, not thousands of small ones).
    void download(FileData f, RandomAccessFile dst, ChunkSink progress, java.util.concurrent.atomic.AtomicBoolean cancel) throws Exception {
        prepare();
        dst.setLength(f.getTotalSize());
        List<ChunkData> chunks = new ArrayList<>(f.getChunks());
        chunks.sort(Comparator.comparingLong(ChunkData::getOffset));
        int par = Math.min(6, Math.max(1, chunks.size()));
        java.util.concurrent.ExecutorService pool = java.util.concurrent.Executors.newFixedThreadPool(par, r -> {
            Thread t = new Thread(r, "ParaboxData-chunk");
            t.setDaemon(true);
            return t;
        });
        java.util.concurrent.atomic.AtomicReference<Exception> failed = new java.util.concurrent.atomic.AtomicReference<>();
        try {
            List<java.util.concurrent.Future<?>> jobs = new ArrayList<>();
            for (ChunkData c : chunks) {
                jobs.add(pool.submit(() -> {
                    if (cancel.get() || failed.get() != null) return;
                    try {
                        byte[] buf = new byte[c.getUncompressedLength()];
                        Exception last = null;
                        int n = -1;
                        for (int attempt = 0; attempt < 8 && n < 0; attempt++) {
                            if (cancel.get()) return;
                            Server s = server(attempt);
                            try {
                                n = get(cdn.downloadDepotChunkFuture(depotId, c, s, buf, depotKey));
                            } catch (Exception e) {
                                last = e;
                                blame(s);
                                if (attempt >= 3) Thread.sleep(500L * attempt);
                            }
                        }
                        if (n < 0) throw new IOException("下载失败：" + rootMessage(last), last);
                        if (n != c.getUncompressedLength()) throw new IOException("数据块长度不对");
                        synchronized (dst) {
                            dst.seek(c.getOffset());
                            dst.write(buf, 0, n);
                        }
                        progress.done(n);
                    } catch (Exception e) {
                        failed.compareAndSet(null, e);
                    }
                }));
            }
            for (java.util.concurrent.Future<?> j : jobs) j.get();
        } finally {
            pool.shutdownNow();
        }
        if (cancel.get()) throw new InterruptedException();
        if (failed.get() != null) throw failed.get();
        byte[] want = f.getFileHash();
        if (want != null && want.length == 20 && !f.getChunks().isEmpty()) {
            byte[] got = sha1(dst, f.getTotalSize());
            if (!Arrays.equals(got, want)) throw new IOException(f.getFileName() + " 校验失败");
        }
    }

    static byte[] sha1(RandomAccessFile f, long len) throws Exception {
        MessageDigest md = MessageDigest.getInstance("SHA-1");
        byte[] b = new byte[1 << 16];
        f.seek(0);
        for (long left = len; left > 0; ) {
            int r = f.read(b, 0, (int) Math.min(b.length, left));
            if (r < 0) break;
            md.update(b, 0, r);
            left -= r;
        }
        return md.digest();
    }

    private static <T> T get(CompletableFuture<T> f) throws Exception {
        try {
            return f.get(60, TimeUnit.SECONDS);
        } catch (ExecutionException e) {
            Throwable c = e.getCause();
            if (c instanceof Exception) throw (Exception) c;
            throw e;
        }
    }

    static String rootMessage(Throwable t) {
        if (t == null) return "未知错误";
        while (t.getCause() != null && t.getCause() != t) t = t.getCause();
        String m = t.getMessage();
        return m == null || m.isEmpty() ? t.getClass().getSimpleName() : m;
    }

    @Override
    public void close() {
        try {
            cdn.close();
        } catch (Throwable ignored) {
        }
    }
}
