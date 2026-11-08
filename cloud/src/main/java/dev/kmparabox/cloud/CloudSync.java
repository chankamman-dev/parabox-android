package dev.kmparabox.cloud;

import android.content.Context;

import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.TreeSet;
import java.util.regex.Pattern;

// Keeps the game's save folder (Application.persistentDataPath = the external files dir) in step
// with its Steam Cloud folder %WinAppDataLocalLow%Patrick Traynor/Patrick's Parabox/ (appinfo ufs:
// save*.txt, prefs.txt, custom_levels/*/*; not per SteamID). Names below are relative to that folder.
//
// For every file the state remembers the local content's SHA-1 and the cloud timestamp as of the
// last sync. (The SHA the cloud listing reports does not match the content, so it is not used.)
// One side changed: that side wins. Both changed: the newer one wins and the loser is backed up.
// The very first sync trusts the cloud, so saves made here before signing in never overwrite the
// PC's; whatever it replaces is backed up. Backups go to <save folder>/.cloud-backup/<time>/.
final class CloudSync {
    static final int APP = 1260520;
    static final String FOLDER = "Patrick Traynor/Patrick's Parabox/";
    private static final Pattern SYNCED = Pattern.compile("^(save[0-9]+\\.txt|prefs\\.txt|custom_levels/[^/]+/[^/]+)$");

    interface Log { void line(String s); }

    static final class Result {
        int down, up, deleted, backedUp, same;

        @Override
        public String toString() {
            return "下载 " + down + "，上传 " + up + "，删除 " + deleted + "，一致 " + same + (backedUp > 0 ? "，已备份 " + backedUp : "");
        }
    }

    static File saveDir(Context c) {
        return c.getExternalFilesDir(null);
    }

    private static File stateFile(Context c) {
        return new File(c.getFilesDir(), "cloud-state.json");
    }

    static boolean initialized(Context c) {
        return stateFile(c).isFile();
    }

    // localWrites=false is the in-game mode: only uploads and cloud-side deletes, never touching the
    // files the running game has open.
    static synchronized Result sync(Context c, Log log, boolean localWrites) throws Exception {
        TokenStore t = TokenStore.load(c);
        if (t == null) throw new Exception("尚未登录 Steam");
        File dir = saveDir(c);
        dir.mkdirs();
        JSONObject state = stateFile(c).isFile() ? new JSONObject(new String(Files.readAllBytes(stateFile(c).toPath()), "UTF-8")) : new JSONObject();
        JSONObject known = state.optJSONObject("files");
        if (known == null) known = new JSONObject();
        Result res = new Result();
        try (SteamSession s = new SteamSession()) {
            log.line("正在登录 Steam…");
            s.logOn(t.account, t.refreshToken, TokenStore.loginId(c));
            CloudClient cc = new CloudClient(s);
            log.line("正在读取云端文件列表…");
            String mine = FOLDER;
            Map<String, CloudClient.CloudFile> cloud = listMine(cc, mine);
            String prefix = null;
            for (CloudClient.CloudFile f : cloud.values()) { prefix = f.name.substring(0, f.name.lastIndexOf('/') + 1); break; }
            if (prefix == null) prefix = state.optString("prefix", "%WinAppDataLocalLow%" + FOLDER);
            boolean first = !state.optBoolean("initialized") || state.optLong("steamId") != s.steamId64;
            if (first && !localWrites) throw new Exception("首次同步要在启动时进行");

            TreeSet<String> names = new TreeSet<>(cloud.keySet());
            for (String rel : localFiles(dir)) if (SYNCED.matcher(rel).matches()) names.add(rel);

            File backup = new File(dir, ".cloud-backup/" + new SimpleDateFormat("yyyyMMdd-HHmmss", Locale.ROOT).format(new Date()));
            JSONObject next = new JSONObject();
            List<String> uploaded = new ArrayList<>();
            for (String name : names) {
                CloudClient.CloudFile cf = cloud.get(name);
                File lf = new File(dir, name);
                String lsha = lf.isFile() ? sha1(lf) : null;
                String lastL = null;
                long lastC = -1;
                if (!first) {
                    Object k = known.opt(name);
                    if (k instanceof JSONObject) { lastL = ((JSONObject) k).optString("l", null); lastC = ((JSONObject) k).optLong("c", -1); }
                    else if (k instanceof String) lastL = (String) k;
                }
                boolean wasSynced = lastL != null;
                boolean localChanged = lsha != null && !lsha.equals(lastL);
                boolean cloudChanged = cf != null && cf.timestamp != lastC;
                String keepL = lastL;
                long keepC = lastC;

                if (cf == null) {
                    if (wasSynced && !localChanged) {
                        // Unchanged here and gone from the cloud: removed on another machine.
                        if (localWrites) { copy(lf, backup, res); lf.delete(); res.deleted++; keepL = null; }
                    } else if (!first) {
                        upload(cc, prefix + name, lf, log, res);
                        uploaded.add(name);
                        keepL = lsha;
                    }
                } else if (lsha == null) {
                    if (wasSynced && !cloudChanged) {
                        cc.delete(APP, cf.name);
                        res.deleted++;
                        log.line("已从云端删除 " + name);
                        keepL = null;
                    } else if (localWrites) {
                        keepL = download(cc, cf, lf, log, res);
                        keepC = cf.timestamp;
                    }
                } else if (first) {
                    if (copy(lf, backup, res)) { /* backed up before the cloud copy replaces it */ }
                    keepL = download(cc, cf, lf, log, res);
                    keepC = cf.timestamp;
                } else if (!localChanged && !cloudChanged) {
                    res.same++;
                } else if (!localChanged) {
                    if (localWrites) { keepL = download(cc, cf, lf, log, res); keepC = cf.timestamp; }
                } else if (!cloudChanged) {
                    upload(cc, cf.name, lf, log, res);
                    uploaded.add(name);
                    keepL = lsha;
                } else {
                    boolean takeCloud = cf.timestamp > lf.lastModified() / 1000;
                    if (takeCloud && localWrites) {
                        copy(lf, backup, res);
                        keepL = download(cc, cf, lf, log, res);
                        keepC = cf.timestamp;
                    } else if (!takeCloud) {
                        backup.mkdirs();
                        Files.write(new File(backup, name.replace('/', '_') + ".cloud").toPath(), cc.download(APP, cf.name));
                        res.backedUp++;
                        upload(cc, cf.name, lf, log, res);
                        uploaded.add(name);
                        keepL = lsha;
                    }
                }
                if (keepL != null) next.put(name, new JSONObject().put("l", keepL).put("c", keepC));
            }

            // Record the timestamps Steam actually stored for what was just uploaded.
            if (!uploaded.isEmpty()) {
                Map<String, CloudClient.CloudFile> after = listMine(cc, mine);
                for (String name : uploaded) {
                    CloudClient.CloudFile f = after.get(name);
                    JSONObject e = next.optJSONObject(name);
                    if (f != null && e != null) e.put("c", f.timestamp);
                }
            }
            state.put("initialized", true);
            state.put("steamId", s.steamId64);
            state.put("prefix", prefix);
            state.put("files", next);
            state.put("lastSync", System.currentTimeMillis());
            Files.write(stateFile(c).toPath(), state.toString(1).getBytes("UTF-8"));
        }
        log.line("同步完成：" + res);
        return res;
    }

    private static Map<String, CloudClient.CloudFile> listMine(CloudClient cc, String mine) throws Exception {
        Map<String, CloudClient.CloudFile> m = new HashMap<>();
        for (CloudClient.CloudFile f : cc.list(APP)) {
            int k = f.name.indexOf(mine);
            if (k < 0) continue;
            String rel = f.name.substring(k + mine.length());
            if (SYNCED.matcher(rel).matches()) m.put(rel, f);
        }
        return m;
    }

    // Every file under the save folder, as paths relative to it ("custom_levels/pack/level.txt").
    static List<String> localFiles(File dir) {
        List<String> out = new ArrayList<>();
        walk(dir, "", out);
        return out;
    }

    private static void walk(File d, String rel, List<String> out) {
        File[] fs = d.listFiles();
        if (fs == null) return;
        for (File f : fs) {
            if (f.getName().startsWith(".")) continue;
            if (f.isDirectory()) {
                if (rel.isEmpty() && !f.getName().equals("custom_levels")) continue;
                walk(f, rel + f.getName() + "/", out);
            } else out.add(rel + f.getName());
        }
    }

    // What the launcher shows: the synced files here, with their times.
    static List<File> savedFiles(Context c) {
        List<File> out = new ArrayList<>();
        File dir = saveDir(c);
        if (dir == null) return out;
        for (String rel : localFiles(dir)) if (SYNCED.matcher(rel).matches()) out.add(new File(dir, rel));
        return out;
    }

    static long lastSync(Context c) {
        try {
            if (!stateFile(c).isFile()) return 0;
            return new JSONObject(new String(Files.readAllBytes(stateFile(c).toPath()), "UTF-8")).optLong("lastSync", 0);
        } catch (Exception e) {
            return 0;
        }
    }

    private static String download(CloudClient cc, CloudClient.CloudFile cf, File to, Log log, Result res) throws Exception {
        log.line("下载 " + to.getName());
        to.getParentFile().mkdirs();
        byte[] data = cc.download(APP, cf.name);
        File tmp = new File(to.getPath() + ".part");
        try (FileOutputStream o = new FileOutputStream(tmp)) { o.write(data); }
        if (!tmp.renameTo(to)) { to.delete(); tmp.renameTo(to); }
        to.setLastModified(cf.timestamp * 1000);
        res.down++;
        return sha1(to);
    }

    private static void upload(CloudClient cc, String cloudName, File lf, Log log, Result res) throws Exception {
        log.line("上传 " + lf.getName());
        cc.upload(APP, cloudName, Files.readAllBytes(lf.toPath()), lf.lastModified() / 1000);
        res.up++;
    }

    private static boolean copy(File f, File backup, Result res) throws Exception {
        if (!f.isFile()) return false;
        File to = new File(backup, f.getParentFile().getName().equals("files") ? f.getName() : f.getParentFile().getName() + "_" + f.getName());
        backup.mkdirs();
        Files.copy(f.toPath(), to.toPath(), java.nio.file.StandardCopyOption.REPLACE_EXISTING);
        res.backedUp++;
        return true;
    }

    static String sha1(File f) throws Exception {
        byte[] d = MessageDigest.getInstance("SHA-1").digest(Files.readAllBytes(f.toPath()));
        StringBuilder sb = new StringBuilder();
        for (byte b : d) sb.append(String.format(Locale.ROOT, "%02x", b));
        return sb.toString();
    }

    static void forget(Context c) {
        stateFile(c).delete();
    }

    private CloudSync() { }
}
