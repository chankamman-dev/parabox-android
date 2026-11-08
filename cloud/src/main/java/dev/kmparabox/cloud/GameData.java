package dev.kmparabox.cloud;

import android.content.Context;
import android.os.StatFs;
import android.util.Log;

import java.io.BufferedOutputStream;
import java.io.BufferedReader;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.OutputStreamWriter;
import java.io.RandomAccessFile;
import java.io.Writer;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

import in.dragonbra.javasteam.types.DepotManifest;
import in.dragonbra.javasteam.types.FileData;

// The game's bulk data -- texture/mesh/audio streams (*.resS) and the FMOD sound banks, about 134 MB --
// is not in the APK. It comes from the Windows depot on Steam, byte for byte: the converted player
// data in the APK points its streams at files/gamedata (ptx respath) and FMOD reads its banks there
// (Bridge.BankFolder). Everything else the game needs (code, the converted .assets with their shaders)
// is in the APK.
//
// The APK is built from one depot manifest (port.txt: "manifest"); a first install downloads exactly
// that version. When Steam has a newer one, it is installed here only if nothing the APK carries
// changed; otherwise the port needs a new APK and the data stays as it is.
// files/gamedata/.parabox-state records the manifest and the SHA-1 of every installed file, so an
// interrupted download resumes and a new manifest only fetches what changed.
public final class GameData {
    private static final String TAG = "ParaboxData";

    // ---- what the APK was built from ----
    public static final class Port {
        public int app, depot;
        public long manifest, build, dlBytes;
        public String prefix;
        final Map<String, String> base = new HashMap<>();   // depot path -> sha1 (files the APK carries, converted or as code)
        final Map<String, String> dl = new HashMap<>();     // local name -> sha1 of the files downloaded at this manifest
        final Map<String, Long> dlSize = new HashMap<>();
    }

    private static Port port;

    public static synchronized Port port(Context c) {
        if (port != null) return port;
        Port p = new Port();
        try (BufferedReader r = new BufferedReader(new InputStreamReader(c.getAssets().open("port/port.txt"), StandardCharsets.UTF_8))) {
            for (String line; (line = r.readLine()) != null; ) {
                int sp = line.indexOf(' ');
                if (sp < 0) continue;
                String k = line.substring(0, sp), v = line.substring(sp + 1);
                switch (k) {
                    case "app": p.app = Integer.parseInt(v); break;
                    case "depot": p.depot = Integer.parseInt(v); break;
                    case "manifest": p.manifest = Long.parseUnsignedLong(v); break;
                    case "build": p.build = Long.parseLong(v); break;
                    case "prefix": p.prefix = v; break;
                    case "base": p.base.put(v.substring(41), v.substring(0, 40)); break;
                    case "dl": {
                        // dl <sha1> <size> <depot path>
                        String sha = v.substring(0, 40);
                        int s2 = v.indexOf(' ', 41);
                        long size = Long.parseLong(v.substring(41, s2));
                        String name = localName(v.substring(s2 + 1));
                        p.dl.put(name, sha);
                        p.dlSize.put(name, size);
                        p.dlBytes += size;
                        break;
                    }
                    default: break;
                }
            }
        } catch (IOException e) {
            throw new IllegalStateException("APK 里缺少 port/port.txt", e);
        }
        port = p;
        return p;
    }

    // Which depot files are downloaded rather than carried by the APK: the streams next to the player
    // data, and the sound banks.
    static boolean downloaded(Port p, String depotPath) {
        if (!depotPath.startsWith(p.prefix)) return false;
        String rest = depotPath.substring(p.prefix.length());
        if (rest.indexOf('\\') < 0) return rest.endsWith(".resS") || rest.endsWith(".resource");
        return rest.startsWith("StreamingAssets\\") && rest.indexOf('\\', 16) < 0 && rest.endsWith(".bank");
    }

    // JavaSteam turns the '\' of decrypted manifest names into File.separatorChar, which is '/' on Android
    // (on a PC it stays '\', so this only shows on the device). port.txt uses the depot's own '\'.
    static String depotName(FileData f) {
        return f.getFileName().replace('/', '\\');
    }

    static String localName(String depotPath) {
        return depotPath.substring(depotPath.lastIndexOf('\\') + 1);
    }

    // ---- installed state ----
    public static final class State {
        public long manifest;          // 0 = unknown / incomplete
        public long apk;               // the APK base manifest this install was made for
        public boolean complete;
        final Map<String, String> files = new HashMap<>(); // local name -> sha1

        public int count() {
            return files.size();
        }
    }

    public static File dir(Context c) {
        return new File(c.getFilesDir(), "gamedata");
    }

    private static File stateFile(Context c) {
        return new File(dir(c), ".parabox-state");
    }

    public static State state(Context c) {
        State s = new State();
        File f = stateFile(c);
        if (f.exists()) {
            try (BufferedReader r = new BufferedReader(new InputStreamReader(new FileInputStream(f), StandardCharsets.UTF_8))) {
                for (String line; (line = r.readLine()) != null; ) {
                    if (line.startsWith("manifest ")) s.manifest = Long.parseUnsignedLong(line.substring(9));
                    else if (line.startsWith("apk ")) s.apk = Long.parseUnsignedLong(line.substring(4));
                    else if (line.equals("complete")) s.complete = true;
                    else if (line.length() > 41) s.files.put(line.substring(41), line.substring(0, 40));
                }
            } catch (Exception e) {
                Log.w(TAG, "state unreadable: " + e);
            }
        }
        return s;
    }

    private static synchronized void saveState(Context c, State s) {
        File f = stateFile(c), tmp = new File(f.getPath() + ".tmp");
        f.getParentFile().mkdirs();
        try (Writer w = new OutputStreamWriter(new BufferedOutputStream(new FileOutputStream(tmp)), StandardCharsets.UTF_8)) {
            w.write("manifest " + Long.toUnsignedString(s.manifest) + "\n");
            w.write("apk " + Long.toUnsignedString(s.apk) + "\n");
            if (s.complete) w.write("complete\n");
            synchronized (s.files) {
                for (Map.Entry<String, String> e : s.files.entrySet()) w.write(e.getValue() + " " + e.getKey() + "\n");
            }
        } catch (IOException e) {
            Log.w(TAG, "state not saved: " + e);
            return;
        }
        if (!tmp.renameTo(f)) Log.w(TAG, "state rename failed");
    }

    // Is the game playable as installed? A complete install for this APK, or files put there by the
    // developer (no state file, every file present at its expected size).
    public static boolean ready(Context c) {
        State s = state(c);
        Port p = port(c);
        if (s.complete) return s.apk == p.manifest;
        if (s.manifest == 0 && s.files.isEmpty()) {
            for (Map.Entry<String, Long> e : p.dlSize.entrySet()) {
                File f = new File(dir(c), e.getKey());
                if (!f.isFile() || f.length() != e.getValue()) return false;
            }
            return !p.dl.isEmpty();
        }
        return false;
    }

    public static long freeBytes(Context c) {
        return new StatFs(c.getFilesDir().getPath()).getAvailableBytes();
    }

    public static long installedBytes(Context c) {
        long n = 0;
        File[] fs = dir(c).listFiles();
        if (fs != null) for (File f : fs) if (f.isFile()) n += f.length();
        return n;
    }

    // ---- sources ----
    public static final class Entry {
        final String name;   // local file name
        final long size;
        String sha;          // hex sha1 of the depot file, or null when unknown (local test source)
        final Object ref;

        Entry(String name, long size, String sha, Object ref) {
            this.name = name;
            this.size = size;
            this.sha = sha;
            this.ref = ref;
        }
    }

    public interface Listener {
        void status(String s);

        void progress(long done, long total, int files, int filesTotal);
    }

    interface Source extends AutoCloseable {
        List<Entry> files(long manifest) throws Exception;

        // Every other file of the manifest (path -> sha1), to compare with what the APK carries.
        Map<String, String> others(long manifest) throws Exception;

        void fetch(Entry e, File dst, java.util.function.LongConsumer bytes, AtomicBoolean cancel) throws Exception;

        @Override
        void close();
    }

    static final class SteamSource implements Source {
        final SteamSession session;
        final SteamDepot depot;
        final Port port;
        private final Map<Long, DepotManifest> manifests = new HashMap<>();

        SteamSource(Context c, Port port) throws Exception {
            this.port = port;
            TokenStore t = TokenStore.load(c);
            if (t == null) throw new IOException("需要先登录 Steam");
            session = new SteamSession();
            try {
                session.logOn(t.account, t.refreshToken, TokenStore.loginId(c) ^ 0x20000);
            } catch (Exception e) {
                session.close();
                throw e;
            }
            depot = new SteamDepot(session, port.app, port.depot);
        }

        long publicManifest() throws Exception {
            return depot.branchManifest("public");
        }

        private DepotManifest manifest(long gid) throws Exception {
            DepotManifest m = manifests.get(gid);
            if (m == null) {
                m = depot.manifest(gid);
                manifests.put(gid, m);
            }
            return m;
        }

        @Override
        public List<Entry> files(long gid) throws Exception {
            List<Entry> out = new ArrayList<>();
            for (FileData f : manifest(gid).getFiles()) {
                String n = depotName(f);
                if (!downloaded(port, n) || f.getChunks().isEmpty()) continue;
                out.add(new Entry(localName(n), f.getTotalSize(), hex(f.getFileHash()), f));
            }
            return out;
        }

        @Override
        public Map<String, String> others(long gid) throws Exception {
            Map<String, String> out = new HashMap<>();
            for (FileData f : manifest(gid).getFiles()) {
                String n = depotName(f);
                if (downloaded(port, n) || f.getChunks().isEmpty()) continue;
                out.put(n, hex(f.getFileHash()));
            }
            return out;
        }

        @Override
        public void fetch(Entry e, File dst, java.util.function.LongConsumer bytes, AtomicBoolean cancel) throws Exception {
            try (RandomAccessFile r = new RandomAccessFile(dst, "rw")) {
                depot.download((FileData) e.ref, r, bytes::accept, cancel);
            }
        }

        @Override
        public void close() {
            depot.close();
            session.close();
        }
    }

    // Bundled build (build-apk.sh BUNDLE=1): the downloaded files are also packed in the APK under
    // assets/gamedata/, and the first start unpacks them to files/gamedata -- no Steam login, no network.
    // Same install path (state file, SHA-1 check); a later Steam update still comes from Steam.
    private static Boolean bundled;

    public static synchronized boolean bundled(Context c) {
        if (bundled != null) return bundled;
        boolean all = !port(c).dl.isEmpty();
        for (String n : port(c).dl.keySet()) {
            try (InputStream in = c.getAssets().open("gamedata/" + n)) {
                // present
            } catch (IOException e) {
                all = false;
                break;
            }
        }
        bundled = all;
        return all;
    }

    static final class AssetSource implements Source {
        final Context c;
        final Port port;

        AssetSource(Context c, Port port) {
            this.c = c.getApplicationContext();
            this.port = port;
        }

        @Override
        public List<Entry> files(long manifest) {
            List<Entry> out = new ArrayList<>();
            for (Map.Entry<String, String> e : port.dl.entrySet())
                out.add(new Entry(e.getKey(), port.dlSize.get(e.getKey()), e.getValue(), "gamedata/" + e.getKey()));
            return out;
        }

        @Override
        public Map<String, String> others(long manifest) {
            return new HashMap<>(port.base);
        }

        @Override
        public void fetch(Entry e, File dst, java.util.function.LongConsumer bytes, AtomicBoolean cancel) throws Exception {
            MessageDigest md = MessageDigest.getInstance("SHA-1");
            try (InputStream in = c.getAssets().open((String) e.ref); OutputStream out = new FileOutputStream(dst)) {
                byte[] b = new byte[1 << 16];
                for (int r; (r = in.read(b)) > 0; ) {
                    if (cancel.get()) throw new InterruptedException();
                    out.write(b, 0, r);
                    md.update(b, 0, r);
                    bytes.accept(r);
                }
            }
            if (e.sha != null && !e.sha.equals(hex(md.digest()))) throw new IOException(e.name + " 校验不符（安装包里的数据损坏）");
        }

        @Override
        public void close() { }
    }

    // Developer test: the Windows files copied to a folder on the device stand in for Steam.
    static final class LocalSource implements Source {
        final File dir;
        final Port port;

        LocalSource(File dir, Port port) {
            this.dir = dir;
            this.port = port;
        }

        @Override
        public List<Entry> files(long manifest) throws Exception {
            List<Entry> out = new ArrayList<>();
            File[] fs = dir.listFiles();
            if (fs == null) throw new IOException("测试目录不存在：" + dir);
            for (File f : fs) {
                if (!port.dl.containsKey(f.getName())) continue;
                out.add(new Entry(f.getName(), f.length(), port.dl.get(f.getName()), f));
            }
            return out;
        }

        @Override
        public Map<String, String> others(long manifest) {
            return new HashMap<>(port.base);
        }

        @Override
        public void fetch(Entry e, File dst, java.util.function.LongConsumer bytes, AtomicBoolean cancel) throws Exception {
            MessageDigest md = MessageDigest.getInstance("SHA-1");
            try (InputStream in = new FileInputStream((File) e.ref); OutputStream out = new FileOutputStream(dst)) {
                byte[] b = new byte[1 << 16];
                for (int r; (r = in.read(b)) > 0; ) {
                    if (cancel.get()) throw new InterruptedException();
                    out.write(b, 0, r);
                    md.update(b, 0, r);
                    bytes.accept(r);
                }
            }
            if (e.sha != null && !e.sha.equals(hex(md.digest()))) throw new IOException(e.name + " 校验不符（不是这个版本的文件）");
        }

        @Override
        public void close() { }
    }

    // ---- update check ----
    public static final class Check {
        public long installed, apkBase, latest;
        public boolean upToDate;       // nothing to do
        public boolean needsNewApk;    // Steam's version changed code/data the APK carries
        public int changedBaseFiles;
        public int filesToGet;
        public long bytesToGet;
        public long target;            // what install() should fetch (0 = nothing)
    }

    // Compares the installed files with the APK's manifest and Steam's current one.
    public static Check check(Context c, Listener l) throws Exception {
        Port p = port(c);
        State s = state(c);
        Check r = new Check();
        r.installed = s.complete ? s.manifest : 0;
        r.apkBase = p.manifest;
        try (SteamSource src = new SteamSource(c, p)) {
            l.status("正在向 Steam 查询最新版本…");
            r.latest = src.publicManifest();
            long target = p.manifest;
            if (r.latest != p.manifest) {
                Map<String, String> others = src.others(r.latest);
                for (Map.Entry<String, String> e : p.base.entrySet()) {
                    String now = others.get(e.getKey());
                    if (now == null || !now.equals(e.getValue())) r.changedBaseFiles++;
                }
                if (r.changedBaseFiles == 0) target = r.latest;
                else r.needsNewApk = true;
            }
            if (s.complete && s.manifest == target && s.apk == p.manifest) {
                r.upToDate = true;
                return r;
            }
            l.status("正在读取版本清单…");
            Plan plan = plan(c, src.files(target), s, p, target);
            r.filesToGet = plan.todo.size();
            r.bytesToGet = plan.totalBytes;
            r.target = target;
            if (plan.todo.isEmpty()) {
                s.manifest = target;
                s.apk = p.manifest;
                s.complete = true;
                saveState(c, s);
                r.upToDate = true;
            }
        }
        return r;
    }

    static final class Plan {
        final List<Entry> all, todo = new ArrayList<>();
        long totalBytes;

        Plan(List<Entry> all) {
            this.all = all;
        }
    }

    private static Plan plan(Context c, List<Entry> files, State s, Port p, long target) {
        Plan plan = new Plan(files);
        File d = dir(c);
        boolean adopt = s.manifest == 0 && s.files.isEmpty() && target == p.manifest;
        for (Entry e : files) {
            File f = new File(d, e.name);
            String have = s.files.get(e.name);
            if (adopt && f.isFile() && f.length() == e.size) {
                // Files put there by the developer for this exact manifest: take them as installed.
                if (e.sha != null) s.files.put(e.name, e.sha);
                continue;
            }
            if (have != null && f.isFile() && (e.sha == null || have.equals(e.sha))) continue;
            plan.todo.add(e);
            plan.totalBytes += e.size;
        }
        return plan;
    }

    // ---- install ----
    public static void install(Context c, long target, File localTest, Listener l, AtomicBoolean cancel) throws Exception {
        Port p = port(c);
        Source src = localTest != null ? new LocalSource(localTest, p)
                : bundled(c) && target == p.manifest ? new AssetSource(c, p) : new SteamSource(c, p);
        try {
            install(c, p, src, target, l, cancel);
        } finally {
            src.close();
        }
    }

    private static void install(Context c, Port p, Source src, long target, Listener l, AtomicBoolean cancel) throws Exception {
        File d = dir(c);
        d.mkdirs();
        State s = state(c);
        l.status("正在读取版本清单…");
        List<Entry> files = src.files(target);
        if (files.isEmpty()) throw new IOException("版本清单里没有找到游戏数据文件");
        if (s.manifest != target) {
            s.manifest = target;
            s.complete = false;
            saveState(c, s);
        }
        Plan plan = plan(c, files, s, p, target);
        long need = plan.totalBytes + (100L << 20);
        long free = freeBytes(c);
        if (free < need) throw new IOException(String.format("存储空间不够：需要约 %.0f MB，现在只剩 %.0f MB", need / 1e6, free / 1e6));

        AtomicLong done = new AtomicLong();
        AtomicInteger count = new AtomicInteger();
        int filesTotal = plan.todo.size();
        long total = plan.totalBytes;
        l.status(filesTotal == 0 ? "正在整理…" : "正在下载游戏数据");
        AtomicBoolean failed = new AtomicBoolean();
        final Exception[] firstError = new Exception[1];
        ExecutorService pool = Executors.newFixedThreadPool(4, r -> {
            Thread t = new Thread(r, "ParaboxData-dl");
            t.setDaemon(true);
            return t;
        });
        List<Future<?>> jobs = new ArrayList<>();
        for (Entry e : plan.todo) {
            jobs.add(pool.submit(() -> {
                if (cancel.get() || failed.get()) return;
                File out = new File(d, e.name), part = new File(d, e.name + ".part");
                AtomicLong mine = new AtomicLong();
                try {
                    src.fetch(e, part, delta -> {
                        done.addAndGet(delta);
                        mine.addAndGet(delta);
                    }, cancel);
                    if (out.exists() && !out.delete()) throw new IOException("无法替换 " + e.name);
                    if (!part.renameTo(out)) throw new IOException("无法写入 " + e.name);
                    synchronized (s.files) {
                        s.files.put(e.name, e.sha != null ? e.sha : "0000000000000000000000000000000000000000");
                    }
                    saveState(c, s);
                    l.progress(done.get(), total, count.incrementAndGet(), filesTotal);
                } catch (Exception ex) {
                    done.addAndGet(-mine.get());
                    if (cancel.get()) return;
                    part.delete();
                    if (failed.compareAndSet(false, true)) firstError[0] = ex;
                    Log.w(TAG, "failed " + e.name + ": " + ex);
                }
            }));
        }
        pool.shutdown();
        while (!pool.isTerminated()) {
            l.progress(done.get(), total, count.get(), filesTotal);
            pool.awaitTermination(250, java.util.concurrent.TimeUnit.MILLISECONDS);
        }
        saveState(c, s);
        if (cancel.get()) throw new InterruptedException("已暂停，下次会从这里继续");
        if (failed.get()) throw new IOException("下载没有完成：" + SteamDepot.rootMessage(firstError[0]) + "\n已下载完的文件会保留，重试会接着下载。", firstError[0]);

        // Files the manifest no longer has, and leftovers.
        l.status("正在整理…");
        Set<String> keep = new HashSet<>();
        for (Entry e : files) keep.add(e.name);
        String[] names = d.list();
        if (names != null)
            for (String n : names)
                if (!n.startsWith(".") && !keep.contains(n)) new File(d, n).delete();
        synchronized (s.files) {
            s.files.keySet().retainAll(keep);
        }
        s.manifest = target;
        s.apk = p.manifest;
        s.complete = true;
        saveState(c, s);
        l.progress(total, total, filesTotal, filesTotal);
    }

    // Removes the downloaded data (settings: "删除游戏数据"). Saves are elsewhere and stay.
    public static void delete(Context c) {
        File[] fs = dir(c).listFiles();
        if (fs != null) for (File f : fs) f.delete();
    }

    // ---- helpers ----
    static String hex(byte[] b) {
        if (b == null) return null;
        StringBuilder sb = new StringBuilder(b.length * 2);
        for (byte x : b) sb.append(String.format("%02x", x & 0xFF));
        return sb.toString();
    }

    private GameData() { }
}
