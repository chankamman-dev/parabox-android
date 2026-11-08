package dev.kmparabox.cloud;

import android.animation.ValueAnimator;
import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.ColorMatrix;
import android.graphics.ColorMatrixColorFilter;
import android.graphics.Outline;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.TextUtils;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewOutlineProvider;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.view.animation.DecelerateInterpolator;
import android.view.animation.PathInterpolator;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import com.google.zxing.BarcodeFormat;
import com.google.zxing.EncodeHintType;
import com.google.zxing.common.BitMatrix;
import com.google.zxing.qrcode.QRCodeWriter;

import java.io.File;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Random;
import java.util.concurrent.atomic.AtomicBoolean;

// The game's home screen. Left: the title, the main button (download / update / play) and the pages;
// right: the Steam card (avatar, playtime, achievements, cloud saves, game data). Achievements, cloud
// saves, settings, account and sign-in open as cards over it. Everything works with a gamepad:
// D-pad moves the focus, A confirms, B goes back.
public final class LauncherActivity extends Activity {
    // Palette from the game: near-black space, grey drifting squares, the blue of the logo box and the
    // pink of the player.
    private static final int BG = Color.rgb(10, 11, 16), CARD = Color.argb(236, 22, 24, 33), LINE = Color.argb(34, 255, 255, 255),
            TEXT = Color.WHITE, SUB = Color.rgb(198, 203, 216), MUTED = Color.rgb(128, 134, 152),
            BLUE = Color.rgb(52, 140, 255), BLUE_D = Color.rgb(31, 102, 214), CYAN = Color.rgb(96, 200, 255), PINK = Color.rgb(255, 72, 150),
            GOLD = Color.rgb(245, 196, 81), GREEN = Color.rgb(110, 224, 160), RED = Color.rgb(255, 110, 110);
    private static final PathInterpolator EASE_OUT = new PathInterpolator(0.22f, 1f, 0.36f, 1f);

    private final Handler ui = new Handler(Looper.getMainLooper());
    private FrameLayout root, overlay;
    private LinearLayout left, card, progressBox, buttons, cardLogin;
    private TextView primary, notice, progressLabel, progressDetail, name, nameSub;
    private TextView valTime, valAch, valCloud, valData;
    private ImageView avatar;
    private Bar progressBar, achBar;
    private View cardStats;

    private volatile boolean syncing, syncedOk, installing, profileLoading;
    private volatile String syncError;
    private volatile GameData.Check check;
    private volatile String checkError;
    private volatile SteamProfile profile;
    private final AtomicBoolean cancel = new AtomicBoolean();
    private Runnable afterSync;
    private File localTest;
    private boolean leaving;
    private boolean unpacking;      // the running install reads the APK's bundled data, not Steam

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        SteamCrypto.install();
        String lt = getIntent().getStringExtra("localSource");
        if (lt != null) localTest = new File(lt);
        profile = SteamProfile.cached(this);
        buildHome();
        hideBars();
        refresh();
        enterAnimation();
        // Developer shortcut (run-emu.sh): straight into the game when the data is there.
        if (getIntent().getBooleanExtra("play", false) && GameData.ready(this)) {
            ui.postDelayed(this::enterGame, 600);
            return;
        }
        // Bundled build: unpack the game data from the APK right away (a few seconds, no login needed).
        // The Steam version check runs after it (startInstall's completion starts it).
        boolean unpack = GameData.bundled(this) && !GameData.ready(this) && localTest == null;
        if (unpack) ui.postDelayed(this::startInstall, 700);
        if (TokenStore.load(this) != null) {
            startSync(null);
            if (!unpack) startCheck();
            startProfile();
        }
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) hideBars();
    }

    // ---------------------------------------------------------------- layout

    private void buildHome() {
        root = new FrameLayout(this);
        root.setBackgroundColor(BG);
        root.addView(new BoxField(this), match());

        View vignette = new View(this);
        vignette.setBackground(new GradientDrawable(GradientDrawable.Orientation.LEFT_RIGHT,
                new int[] { Color.argb(215, 10, 11, 16), Color.argb(120, 10, 11, 16), Color.argb(40, 10, 11, 16), Color.argb(150, 10, 11, 16) }));
        root.addView(vignette, match());

        LinearLayout main = new LinearLayout(this);
        main.setOrientation(LinearLayout.HORIZONTAL);
        main.setPadding(dp(44), dp(30), dp(36), dp(30));
        main.setClipChildren(false);
        main.setClipToPadding(false);
        root.addView(main, match());

        // ---- left: title, status, main button, pages
        left = new LinearLayout(this);
        left.setOrientation(LinearLayout.VERTICAL);
        left.setGravity(Gravity.CENTER_VERTICAL);
        left.setClipChildren(false);
        main.addView(left, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1f));

        TextView pre = text(15, SUB, true);
        pre.setText("PATRICK'S");
        pre.setLetterSpacing(0.32f);
        left.addView(pre);

        LinearLayout titleRow = new LinearLayout(this);
        titleRow.setGravity(Gravity.CENTER_VERTICAL);
        TextView title = text(50, TEXT, true);
        title.setTypeface(Typeface.create("sans-serif-condensed", Typeface.BOLD));
        title.setText("PARABOX");
        title.setLetterSpacing(0.04f);
        title.setIncludeFontPadding(false);
        titleRow.addView(title);
        View mark = new NestMark(this);
        LinearLayout.LayoutParams ml = new LinearLayout.LayoutParams(dp(38), dp(38));
        ml.setMargins(dp(14), dp(4), 0, 0);
        titleRow.addView(mark, ml);
        left.addView(titleRow);

        LinearLayout tagRow = new LinearLayout(this);
        tagRow.setGravity(Gravity.CENTER_VERTICAL);
        tagRow.setPadding(0, dp(8), 0, 0);
        TextView tag = text(11, TEXT, true);
        tag.setText("STEAM · ANDROID");
        tag.setLetterSpacing(0.12f);
        tag.setPadding(dp(10), dp(3), dp(10), dp(3));
        tag.setBackground(solid(BLUE_D, dp(4)));
        tagRow.addView(tag);
        TextView sub = text(13, MUTED, false);
        sub.setText("   箱中有箱的递归推箱子  ·  移植包基于 Steam build " + GameData.port(this).build);
        tagRow.addView(sub);
        left.addView(tagRow);

        notice = text(13, GOLD, false);
        notice.setVisibility(View.GONE);
        notice.setPadding(0, dp(14), dp(24), 0);
        notice.setLineSpacing(dp(2), 1f);
        left.addView(notice);

        progressBox = new LinearLayout(this);
        progressBox.setOrientation(LinearLayout.VERTICAL);
        progressBox.setVisibility(View.GONE);
        progressBox.setPadding(0, dp(18), dp(40), 0);
        progressLabel = text(15, TEXT, true);
        progressBar = new Bar(this, BLUE, CYAN);
        progressDetail = text(12, SUB, false);
        progressBox.addView(progressLabel);
        LinearLayout.LayoutParams bp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(6));
        bp.setMargins(0, dp(8), 0, dp(6));
        progressBox.addView(progressBar, bp);
        progressBox.addView(progressDetail);
        left.addView(progressBox);

        primary = button("开始游戏", true);
        LinearLayout.LayoutParams pl = new LinearLayout.LayoutParams(dp(270), dp(56));
        pl.setMargins(0, dp(22), 0, 0);
        left.addView(primary, pl);

        buttons = new LinearLayout(this);
        buttons.setClipChildren(false);
        LinearLayout.LayoutParams bl = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        bl.setMargins(0, dp(12), 0, 0);
        left.addView(buttons, bl);
        addSecondary("成就", this::showAchievements);
        addSecondary("云存档", this::showCloud);
        addSecondary("设置", this::showSettings);

        // ---- right: the Steam card
        card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(20), dp(18), dp(20), dp(16));
        GradientDrawable cbg = solid(CARD, dp(16));
        cbg.setStroke(dp(1), LINE);
        card.setBackground(cbg);
        LinearLayout.LayoutParams cl = new LinearLayout.LayoutParams(dp(292), ViewGroup.LayoutParams.WRAP_CONTENT);
        cl.gravity = Gravity.CENTER_VERTICAL;
        main.addView(card, cl);

        LinearLayout who = new LinearLayout(this);
        who.setGravity(Gravity.CENTER_VERTICAL);
        who.setFocusable(true);
        who.setClickable(true);
        who.setBackground(rowBackground());
        who.setPadding(dp(6), dp(6), dp(6), dp(6));
        who.setOnClickListener(v -> showAccount());
        avatar = new ImageView(this);
        avatar.setScaleType(ImageView.ScaleType.CENTER_CROP);
        avatar.setOutlineProvider(new ViewOutlineProvider() {
            @Override
            public void getOutline(View v, Outline o) {
                o.setRoundRect(0, 0, v.getWidth(), v.getHeight(), dp(10));
            }
        });
        avatar.setClipToOutline(true);
        who.addView(avatar, new LinearLayout.LayoutParams(dp(48), dp(48)));
        LinearLayout names = new LinearLayout(this);
        names.setOrientation(LinearLayout.VERTICAL);
        names.setPadding(dp(12), 0, 0, 0);
        name = text(17, TEXT, true);
        name.setSingleLine(true);
        name.setEllipsize(TextUtils.TruncateAt.END);
        nameSub = text(12, MUTED, false);
        names.addView(name);
        names.addView(nameSub);
        who.addView(names, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        TextView chev = text(18, MUTED, false);
        chev.setText("›");
        who.addView(chev);
        card.addView(who);

        View sep = new View(this);
        sep.setBackgroundColor(LINE);
        LinearLayout.LayoutParams sl = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(1));
        sl.setMargins(0, dp(12), 0, dp(6));
        card.addView(sep, sl);

        LinearLayout stats = new LinearLayout(this);
        stats.setOrientation(LinearLayout.VERTICAL);
        cardStats = stats;
        valTime = statRow(stats, "游戏时长");
        valAch = statRow(stats, "成就");
        achBar = new Bar(this, PINK, GOLD);
        LinearLayout.LayoutParams abl = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(4));
        abl.setMargins(0, dp(2), 0, dp(6));
        stats.addView(achBar, abl);
        valCloud = statRow(stats, "云存档");
        card.addView(stats);

        cardLogin = new LinearLayout(this);
        cardLogin.setOrientation(LinearLayout.VERTICAL);
        TextView why = text(13, SUB, false);
        why.setText((GameData.bundled(this) ? "登录后可以同步云存档" : "登录后可以从 Steam 下载游戏数据，同步云存档") + "，记录游戏时长和成就。用手机上的 Steam 扫码即可，不需要输入密码。");
        why.setLineSpacing(dp(2), 1f);
        cardLogin.addView(why);
        TextView login = button("扫码登录 Steam", false);
        login.setOnClickListener(v -> showLogin());
        LinearLayout.LayoutParams ll = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(44));
        ll.setMargins(0, dp(14), 0, 0);
        ll.setMargins(0, dp(14), 0, dp(6));
        cardLogin.addView(login, ll);
        card.addView(cardLogin);
        LinearLayout dataRow = new LinearLayout(this);
        dataRow.setOrientation(LinearLayout.VERTICAL);
        valData = statRow(dataRow, "游戏数据");
        card.addView(dataRow);

        overlay = new FrameLayout(this);
        overlay.setBackgroundColor(Color.argb(190, 4, 5, 9));
        overlay.setVisibility(View.GONE);
        overlay.setClickable(true);
        root.addView(overlay, match());

        setContentView(root);
    }

    private Bitmap defaultAvatarBmp;

    // Patrick himself (the game's icon) until Steam's avatar is in.
    private Bitmap defaultAvatar() {
        if (defaultAvatarBmp == null) {
            try (java.io.InputStream in = getAssets().open("port/avatar.png")) {
                defaultAvatarBmp = BitmapFactory.decodeStream(in);
            } catch (Exception ignored) {
            }
        }
        return defaultAvatarBmp;
    }

    private TextView statRow(LinearLayout parent, String label) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(0, dp(7), 0, dp(7));
        TextView l = text(13, MUTED, false);
        l.setText(label);
        row.addView(l, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        TextView v = text(14, TEXT, true);
        v.setGravity(Gravity.END);
        v.setSingleLine(true);
        v.setEllipsize(TextUtils.TruncateAt.END);
        v.setCompoundDrawablePadding(dp(7));
        row.addView(v, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        parent.addView(row);
        return v;
    }

    // Recomputes every label and the main button from the current state.
    private void refresh() {
        TokenStore t = TokenStore.load(this);
        boolean ready = GameData.ready(this);
        SteamProfile p = profile;
        GameData.Check c = check;

        // Steam card
        if (t == null) {
            name.setText("未登录 Steam");
            nameSub.setText("离线也能玩，存档只在本机");
            avatar.setImageBitmap(defaultAvatar());
            cardStats.setVisibility(View.GONE);
            cardLogin.setVisibility(View.VISIBLE);
        } else {
            name.setText(p != null && !p.persona.isEmpty() ? p.persona : t.account);
            nameSub.setText(profileLoading && p == null ? "正在读取 Steam 资料…" : "Steam 已登录 · " + t.account);
            File av = SteamProfile.avatar(this);
            Bitmap bm = av.isFile() ? BitmapFactory.decodeFile(av.getPath()) : null;
            avatar.setImageBitmap(bm != null ? bm : defaultAvatar());
            cardStats.setVisibility(View.VISIBLE);
            cardLogin.setVisibility(View.GONE);
            valTime.setText(p == null ? "—" : hours(p.minutes));
            if (p == null || p.achs.isEmpty()) {
                valAch.setText(p == null ? "—" : "暂无数据");
                achBar.set(0);
            } else {
                valAch.setText(p.unlockedCount() + " / " + p.achs.size());
                achBar.set((float) p.unlockedCount() / p.achs.size());
            }
            if (syncing) dot(valCloud, CYAN, "同步中…");
            else if (syncedOk) dot(valCloud, GREEN, "已同步 " + new SimpleDateFormat("HH:mm", Locale.CHINA).format(new Date(CloudSync.lastSync(this))));
            else if (syncError != null) dot(valCloud, RED, "同步失败");
            else dot(valCloud, GOLD, "未同步");
        }
        boolean bundled = GameData.bundled(this);
        if (installing) dot(valData, CYAN, unpacking ? "解压中" : "下载中");
        else if (!ready) dot(valData, GOLD, bundled ? "待解压" : "未下载");
        else if (c != null && c.needsNewApk) dot(valData, GOLD, "Steam 有新版本");
        else if (c != null && !c.upToDate && c.target != 0) dot(valData, GOLD, "可更新 " + mb(c.bytesToGet));
        else dot(valData, GREEN, "已就绪");

        // Notice under the title
        String n = null;
        if (c != null && c.needsNewApk) n = "Steam 上的游戏已更新，这次更新改了游戏程序，需要新版安卓移植包才能跟上。现在可以继续玩当前版本，存档不受影响。";
        else if (checkError != null && !ready) n = checkError;
        else if (!ready && bundled && localTest == null) n = "游戏数据已经在安装包里，第一次启动会解压到本机（约 " + mb(GameData.port(this).dlBytes) + "，几秒钟），不需要登录和联网。";
        else if (!ready && t == null && localTest == null) n = "第一次启动需要从 Steam 下载约 " + mb(GameData.port(this).dlBytes) + " 游戏数据（贴图和音乐）。";
        notice.setText(n == null ? "" : n);
        notice.setVisibility(n == null ? View.GONE : View.VISIBLE);

        // Main button
        if (installing) setPrimary(unpacking ? "正在解压…" : "暂停下载", () -> { if (!unpacking) cancel.set(true); });
        else if (!ready) {
            if (localTest != null) setPrimary("从测试目录安装", this::startInstall);
            else if (bundled) setPrimary("解压游戏数据", this::startInstall);
            else if (t == null) setPrimary("登录 Steam 并下载", this::showLogin);
            else {
                long bytes = c != null && c.target != 0 ? c.bytesToGet : GameData.port(this).dlBytes;
                setPrimary("下载游戏数据 · " + mb(bytes), this::startInstall);
            }
        } else if (c != null && !c.upToDate && !c.needsNewApk && c.target != 0 && c.filesToGet > 0) {
            setPrimary("更新并开始 · " + mb(c.bytesToGet), this::startInstall);
        } else setPrimary("开始游戏", this::play);
        if (overlay.getVisibility() != View.VISIBLE && getCurrentFocus() == null) primary.requestFocus();
    }

    private void setPrimary(String label, Runnable r) {
        primary.setText(label);
        primary.setOnClickListener(v -> r.run());
    }

    private void addSecondary(String label, Runnable r) {
        TextView b = button(label, false);
        b.setOnClickListener(v -> r.run());
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(dp(82), dp(40));
        if (buttons.getChildCount() > 0) lp.setMargins(dp(12), 0, 0, 0);
        buttons.addView(b, lp);
    }

    private void enterAnimation() {
        int k = 0;
        for (int i = 0; i < left.getChildCount(); i++) {
            View v = left.getChildAt(i);
            v.setAlpha(0f);
            v.setTranslationY(dp(18));
            v.animate().alpha(1f).translationY(0).setStartDelay(60 + 45L * k++).setDuration(520).setInterpolator(EASE_OUT).start();
        }
        card.setAlpha(0f);
        card.setTranslationX(dp(28));
        card.animate().alpha(1f).translationX(0).setStartDelay(200).setDuration(560).setInterpolator(EASE_OUT).start();
    }

    // ---------------------------------------------------------------- Steam: profile, cloud saves

    private void startProfile() {
        if (profileLoading) return;
        profileLoading = true;
        refresh();
        new Thread(() -> {
            try {
                profile = SteamProfile.fetch(this);
            } catch (Throwable e) {
                Log.w("ParaboxCloud", "profile failed: " + e);
            }
            profileLoading = false;
            ui.post(this::refresh);
        }, "ParaboxCloud-profile").start();
    }

    private void startSync(Runnable then) {
        if (then != null) afterSync = then;
        if (syncing) return;
        syncing = true;
        refresh();
        new Thread(() -> {
            String err = null;
            try {
                CloudSync.Result r = CloudSync.sync(this, s -> { }, true);
                Log.i("ParaboxCloud", "sync: " + r);
            } catch (Throwable e) {
                err = message(e);
                Log.w("ParaboxCloud", "sync failed: " + e);
            }
            final String error = err;
            ui.post(() -> {
                syncing = false;
                syncedOk = error == null;
                syncError = error;
                refresh();
                Runnable next = afterSync;
                afterSync = null;
                if (error != null && next != null) {
                    showMessage("云存档同步失败", error + "\n本机存档没有改动。可以先离线游玩，下次启动会再同步。",
                            new String[] { "离线进入游戏", "取消" }, new Runnable[] { this::enterGame, this::closeOverlay });
                } else if (next != null) next.run();
            });
        }, "ParaboxCloud-sync").start();
    }

    private void play() {
        if (!GameData.ready(this)) return;
        if (TokenStore.load(this) == null || syncedOk) {
            enterGame();
            return;
        }
        showProgress("正在同步 Steam 云存档…", -1, "");
        startSync(this::enterGame);
    }

    private void enterGame() {
        if (leaving) return;
        leaving = true;
        root.animate().alpha(0f).setDuration(240).withEndAction(() -> {
            Intent i = new Intent();
            i.setClassName(this, "dev.kmparabox.ParaboxActivity");
            startActivity(i);
            overridePendingTransition(0, 0);
            finish();
        }).start();
    }

    // ---------------------------------------------------------------- game data

    private void startCheck() {
        new Thread(() -> {
            try {
                GameData.Check c = GameData.check(this, new GameData.Listener() {
                    @Override public void status(String s) { }
                    @Override public void progress(long done, long total, int files, int filesTotal) { }
                });
                check = c;
                checkError = null;
                Log.i("ParaboxData", "check: installed " + c.installed + " apk " + c.apkBase + " latest " + c.latest + " needsNewApk " + c.needsNewApk
                        + " files " + c.filesToGet + " bytes " + c.bytesToGet);
            } catch (Throwable e) {
                checkError = "无法向 Steam 查询游戏数据：" + message(e);
                Log.w("ParaboxData", "check failed: " + e);
            }
            ui.post(this::refresh);
        }, "ParaboxData-check").start();
    }

    private void startInstall() {
        if (installing) return;
        GameData.Check c = check;
        long apkManifest = GameData.port(this).manifest;
        boolean thenPlay = GameData.ready(this);
        long target = !thenPlay && GameData.bundled(this) ? apkManifest
                : c != null && c.target != 0 ? c.target : apkManifest;
        unpacking = localTest == null && GameData.bundled(this) && target == apkManifest;
        installing = true;
        cancel.set(false);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        refresh();
        if (unpacking) showProgress("正在解压游戏数据…", -1, "第一次启动把安装包里的贴图和音乐解压到本机，只需要一次。");
        else showProgress("正在连接 Steam…", -1, "下载时请留在这个页面；可以随时暂停，下次会接着下载。");
        final String verb = unpacking ? "正在解压游戏数据  " : "正在下载游戏数据  ";
        final long[] lastT = { System.currentTimeMillis() }, lastB = { 0 };
        final double[] speed = { 0 };
        new Thread(() -> {
            String err = null;
            boolean paused = false;
            try {
                GameData.install(this, target, localTest, new GameData.Listener() {
                    @Override
                    public void status(String s) {
                        ui.post(() -> progressLabel.setText(s));
                    }

                    @Override
                    public void progress(long done, long total, int files, int filesTotal) {
                        long now = System.currentTimeMillis();
                        if (now - lastT[0] >= 1000) {
                            double inst = (done - lastB[0]) * 1000.0 / (now - lastT[0]);
                            speed[0] = speed[0] == 0 ? inst : speed[0] * 0.7 + inst * 0.3;
                            lastT[0] = now;
                            lastB[0] = done;
                        }
                        float f = total > 0 ? (float) done / total : 0;
                        String eta = speed[0] > 1 ? "  ·  约 " + duration((long) ((total - done) / speed[0])) : "";
                        String d = String.format(Locale.CHINA, "%s / %s  ·  %s/s%s  ·  %d / %d 个文件", mb(done), mb(total), mb((long) speed[0]), eta, files, filesTotal);
                        ui.post(() -> {
                            progressLabel.setText(verb + Math.round(f * 100) + "%");
                            progressBar.set(f);
                            progressDetail.setText(d);
                        });
                    }
                }, cancel);
            } catch (InterruptedException e) {
                paused = true;
            } catch (Throwable e) {
                err = message(e);
                Log.w("ParaboxData", "install failed", e);
            }
            final String error = err;
            final boolean wasPaused = paused;
            ui.post(() -> {
                installing = false;
                boolean wasUnpacking = unpacking;
                unpacking = false;
                getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
                hideProgress();
                if (error == null && !wasPaused) {
                    check = null;
                    if (TokenStore.load(this) != null) startCheck();
                }
                refresh();
                if (error != null) showMessage(wasUnpacking ? "解压没有完成" : "下载没有完成", error, new String[] { "重试", "关闭" }, new Runnable[] { () -> { closeOverlay(); startInstall(); }, this::closeOverlay });
                else if (wasPaused) toast("下载已暂停，下次会从这里继续");
                else if (thenPlay) { toast("游戏数据已更新"); }
                else toast("游戏数据已就绪，可以开始游戏了");
            });
        }, "ParaboxData-install").start();
    }

    private void showProgress(String label, float f, String detail) {
        progressBox.setVisibility(View.VISIBLE);
        progressLabel.setText(label);
        progressBar.set(f);
        progressDetail.setText(detail);
    }

    private void hideProgress() {
        progressBox.setVisibility(View.GONE);
    }

    // ---------------------------------------------------------------- pages (cards over the home screen)

    private LinearLayout card(String title, String body, int width) {
        overlay.removeAllViews();
        LinearLayout c = new LinearLayout(this);
        c.setOrientation(LinearLayout.VERTICAL);
        c.setPadding(dp(28), dp(22), dp(28), dp(20));
        GradientDrawable bg = solid(Color.rgb(20, 22, 30), dp(18));
        bg.setStroke(dp(1), LINE);
        c.setBackground(bg);
        TextView t = text(21, TEXT, true);
        t.setText(title);
        c.addView(t);
        if (body != null) {
            TextView b = text(14, SUB, false);
            b.setText(body);
            b.setLineSpacing(dp(3), 1f);
            b.setPadding(0, dp(8), 0, 0);
            c.addView(b);
        }
        int maxH = (int) (getResources().getDisplayMetrics().heightPixels * 0.9f);
        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(Math.min(dp(width), (int) (getResources().getDisplayMetrics().widthPixels * 0.9f)),
                ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.CENTER);
        overlay.addView(c, lp);
        c.setMinimumHeight(0);
        c.post(() -> {
            if (c.getHeight() > maxH) {
                lp.height = maxH;
                c.setLayoutParams(lp);
            }
        });
        boolean wasOpen = overlay.getVisibility() == View.VISIBLE;
        overlay.setVisibility(View.VISIBLE);
        if (!wasOpen) {
            overlay.setAlpha(0f);
            overlay.animate().alpha(1f).setDuration(180).start();
        }
        c.setAlpha(0f);
        c.setScaleX(0.96f);
        c.setScaleY(0.96f);
        c.setTranslationY(dp(10));
        c.animate().alpha(1f).scaleX(1f).scaleY(1f).translationY(0).setDuration(260).setInterpolator(EASE_OUT).start();
        return c;
    }

    private LinearLayout cardButtons(LinearLayout card, String[] labels, Runnable[] actions) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.END);
        for (int k = 0; k < labels.length; k++) {
            TextView b = button(labels[k], k == 0);
            final Runnable a = actions[k];
            b.setOnClickListener(v -> a.run());
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(42));
            if (k > 0) lp.setMargins(dp(12), 0, 0, 0);
            b.setPadding(dp(24), 0, dp(24), 0);
            row.addView(b, lp);
        }
        LinearLayout.LayoutParams rl = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        rl.setMargins(0, dp(18), 0, 0);
        card.addView(row, rl);
        if (row.getChildCount() > 0) row.getChildAt(0).requestFocus();
        return row;
    }

    private ScrollView cardList(LinearLayout card, LinearLayout list) {
        ScrollView sv = new ScrollView(this);
        sv.setVerticalScrollBarEnabled(false);
        sv.addView(list);
        LinearLayout.LayoutParams sl = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f);
        sl.setMargins(0, dp(12), 0, 0);
        card.addView(sv, sl);
        return sv;
    }

    private void showMessage(String title, String body, String[] labels, Runnable[] actions) {
        LinearLayout c = card(title, body, 560);
        cardButtons(c, labels, actions);
    }

    private void closeOverlay() {
        loginCancel.set(true);
        if (overlay.getVisibility() != View.VISIBLE) return;
        View c = overlay.getChildCount() > 0 ? overlay.getChildAt(0) : null;
        if (c != null) c.animate().alpha(0f).scaleX(0.97f).scaleY(0.97f).translationY(dp(8)).setDuration(160).start();
        overlay.animate().alpha(0f).setDuration(180).withEndAction(() -> {
            overlay.setVisibility(View.GONE);
            overlay.removeAllViews();
            overlay.setAlpha(1f);
        }).start();
        refresh();
        primary.requestFocus();
    }

    private final AtomicBoolean loginCancel = new AtomicBoolean();

    private void showLogin() {
        LinearLayout c = card("用 Steam 手机应用扫码登录", "打开手机上的 Steam，点底部的「Steam 令牌」，再点扫码按钮扫描下面的二维码。不需要在这里输入密码。", 520);
        ImageView qr = new ImageView(this);
        qr.setBackground(solid(Color.WHITE, dp(12)));
        int pad = dp(12);
        qr.setPadding(pad, pad, pad, pad);
        LinearLayout.LayoutParams ql = new LinearLayout.LayoutParams(dp(196), dp(196));
        ql.gravity = Gravity.CENTER_HORIZONTAL;
        ql.setMargins(0, dp(16), 0, 0);
        c.addView(qr, ql);
        TextView st = text(13, MUTED, false);
        st.setGravity(Gravity.CENTER);
        st.setText("正在向 Steam 申请二维码…");
        st.setPadding(0, dp(10), 0, 0);
        c.addView(st);
        cardButtons(c, new String[] { "取消" }, new Runnable[] { this::closeOverlay });
        loginCancel.set(false);
        AtomicBoolean myCancel = loginCancel;
        new Thread(() -> {
            try (SteamSession s = new SteamSession()) {
                String[] r = QrLogin.run(s, url -> ui.post(() -> {
                    Bitmap bmp = qrBitmap(url);
                    if (bmp != null) qr.setImageBitmap(bmp);
                    st.setText("等待手机上确认…");
                }));
                if (myCancel.get()) return;
                TokenStore.save(this, r[0], r[1]);
                ui.post(() -> {
                    closeOverlay();
                    toast("已登录 Steam：" + r[0]);
                    startSync(null);
                    startCheck();
                    startProfile();
                });
            } catch (Throwable t) {
                if (myCancel.get()) return;
                ui.post(() -> showMessage("登录没有完成", message(t), new String[] { "重试", "关闭" }, new Runnable[] { this::showLogin, this::closeOverlay }));
            }
        }, "ParaboxCloud-login").start();
    }

    private void showAccount() {
        TokenStore t = TokenStore.load(this);
        if (t == null) {
            showLogin();
            return;
        }
        SteamProfile p = profile;
        String who = (p != null && !p.persona.isEmpty() ? p.persona + "（" + t.account + "）" : t.account);
        String last = p != null && p.lastPlayed > 0 ? "\n最近一次在 Steam 上游玩：" + new SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.CHINA).format(new Date(p.lastPlayed * 1000)) : "";
        LinearLayout c = card("Steam 账户", "已登录：" + who + last
                + "\n\n进入游戏前会先下载云存档，游戏里每次存档后自动上传；游戏时长和成就会记到这个账号上，和电脑上玩一样。", 560);
        cardButtons(c, new String[] { "关闭", "刷新资料", "退出登录" }, new Runnable[] { this::closeOverlay, () -> { closeOverlay(); startProfile(); }, () -> {
            showMessage("退出 Steam 登录？", "本机存档会保留，但不会再和云端同步，游戏时长和成就也不再记录。", new String[] { "取消", "退出登录" },
                    new Runnable[] { this::closeOverlay, () -> {
                        TokenStore.clear(this);
                        CloudSync.forget(this);
                        SteamPlay.forget(this);
                        SteamProfile.forget(this);
                        profile = null;
                        syncedOk = false;
                        syncError = null;
                        check = null;
                        closeOverlay();
                        toast("已退出 Steam 登录");
                    } });
        } });
    }

    private void showAchievements() {
        SteamProfile p = profile;
        if (TokenStore.load(this) == null) {
            showMessage("成就", "登录 Steam 后，在这里玩出的成就会解锁到你的 Steam 账号上，电脑上已有的成就也会显示在这里。",
                    new String[] { "登录 Steam", "关闭" }, new Runnable[] { this::showLogin, this::closeOverlay });
            return;
        }
        if (p == null || p.achs.isEmpty()) {
            showMessage("成就", profileLoading ? "正在从 Steam 读取成就…" : "还没有拿到成就列表，稍后再试。",
                    new String[] { "关闭", "刷新" }, new Runnable[] { this::closeOverlay, () -> { closeOverlay(); startProfile(); } });
            return;
        }
        LinearLayout c = card("成就  " + p.unlockedCount() + " / " + p.achs.size(), null, 640);
        Bar bar = new Bar(this, PINK, GOLD);
        bar.set((float) p.unlockedCount() / p.achs.size());
        LinearLayout.LayoutParams bl = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(4));
        bl.setMargins(0, dp(10), 0, 0);
        c.addView(bar, bl);
        LinearLayout list = new LinearLayout(this);
        list.setOrientation(LinearLayout.VERTICAL);
        List<SteamProfile.Ach> sorted = new ArrayList<>(p.achs);
        sorted.sort((a, b) -> {
            if ((a.unlocked > 0) != (b.unlocked > 0)) return a.unlocked > 0 ? -1 : 1;
            return Long.compare(b.unlocked, a.unlocked);
        });
        SimpleDateFormat df = new SimpleDateFormat("yyyy-MM-dd", Locale.CHINA);
        for (SteamProfile.Ach a : sorted) {
            boolean got = a.unlocked > 0;
            LinearLayout row = new LinearLayout(this);
            row.setGravity(Gravity.CENTER_VERTICAL);
            row.setPadding(dp(10), dp(8), dp(12), dp(8));
            row.setFocusable(true);
            row.setBackground(rowBackground());
            ImageView ic = new ImageView(this);
            String icon = got ? a.icon : a.iconGray;
            if (icon != null && icon.endsWith(".jpg")) icon = icon.substring(0, icon.length() - 4);
            File f = icon == null ? null : SteamProfile.icon(this, icon);
            if (f != null && f.isFile()) ic.setImageBitmap(BitmapFactory.decodeFile(f.getPath()));
            else ic.setBackground(solid(Color.argb(30, 255, 255, 255), dp(6)));
            if (!got) {
                ColorMatrix m = new ColorMatrix();
                m.setSaturation(0);
                ic.setColorFilter(new ColorMatrixColorFilter(m));
                ic.setImageAlpha(150);
            }
            ic.setOutlineProvider(new ViewOutlineProvider() {
                @Override
                public void getOutline(View v, Outline o) {
                    o.setRoundRect(0, 0, v.getWidth(), v.getHeight(), dp(6));
                }
            });
            ic.setClipToOutline(true);
            row.addView(ic, new LinearLayout.LayoutParams(dp(44), dp(44)));
            LinearLayout tx = new LinearLayout(this);
            tx.setOrientation(LinearLayout.VERTICAL);
            tx.setPadding(dp(14), 0, dp(10), 0);
            TextView nm = text(15, got ? TEXT : SUB, true);
            TextView ds = text(12, MUTED, false);
            if (!got && a.hidden) {
                nm.setText("隐藏成就");
                ds.setText("继续游戏来解锁");
            } else {
                nm.setText(a.name);
                ds.setText(a.desc);
            }
            tx.addView(nm);
            tx.addView(ds);
            row.addView(tx, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
            TextView when = text(12, got ? GOLD : MUTED, got);
            when.setText(got ? df.format(new Date(a.unlocked * 1000)) : "未解锁");
            row.addView(when);
            LinearLayout.LayoutParams rl = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            rl.setMargins(0, dp(3), 0, dp(3));
            list.addView(row, rl);
        }
        cardList(c, list);
        cardButtons(c, new String[] { "关闭" }, new Runnable[] { this::closeOverlay });
        fillHeight(c);
    }

    private void showCloud() {
        TokenStore t = TokenStore.load(this);
        long last = CloudSync.lastSync(this);
        String head;
        if (t == null) head = "未登录 Steam，存档只保存在本机。登录后会和电脑上的 Steam 云存档双向同步。";
        else if (syncing) head = "正在同步…";
        else if (syncError != null) head = "上次同步失败：" + syncError;
        else head = last > 0 ? "上次同步：" + new SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.CHINA).format(new Date(last)) : "还没有同步过";
        LinearLayout c = card("云存档", head + "\n进入游戏前下载云端存档，游戏中每次存档后自动上传；两边都改过时保留较新的一份，另一份备份在存档目录的 .cloud-backup 里。", 600);
        LinearLayout list = new LinearLayout(this);
        list.setOrientation(LinearLayout.VERTICAL);
        List<File> files = CloudSync.savedFiles(this);
        File base = CloudSync.saveDir(this);
        SimpleDateFormat df = new SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.CHINA);
        if (files.isEmpty()) {
            TextView none = text(13, MUTED, false);
            none.setText("本机还没有存档。");
            none.setPadding(dp(10), dp(10), 0, dp(4));
            list.addView(none);
        }
        for (File f : files) {
            LinearLayout row = new LinearLayout(this);
            row.setGravity(Gravity.CENTER_VERTICAL);
            row.setPadding(dp(12), dp(8), dp(12), dp(8));
            row.setFocusable(true);
            row.setBackground(rowBackground());
            String rel = base == null ? f.getName() : f.getPath().substring(base.getPath().length() + 1);
            TextView nm = text(14, TEXT, true);
            nm.setText(describeSave(rel));
            TextView fn = text(12, MUTED, false);
            fn.setText("  " + rel);
            row.addView(nm);
            row.addView(fn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
            TextView when = text(12, SUB, false);
            when.setText(df.format(new Date(f.lastModified())) + "  ·  " + kb(f.length()));
            row.addView(when);
            LinearLayout.LayoutParams rl = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            rl.setMargins(0, dp(2), 0, dp(2));
            list.addView(row, rl);
        }
        cardList(c, list);
        if (t == null) cardButtons(c, new String[] { "登录 Steam", "关闭" }, new Runnable[] { this::showLogin, this::closeOverlay });
        else cardButtons(c, new String[] { "立即同步", "关闭" }, new Runnable[] { () -> { closeOverlay(); syncedOk = false; startSync(() -> toast(syncError == null ? "云存档已同步" : "同步失败")); }, this::closeOverlay });
    }

    private static String describeSave(String rel) {
        if (rel.equals("prefs.txt")) return "游戏设置";
        if (rel.startsWith("save") && rel.endsWith(".txt")) {
            try {
                return "存档 " + (Integer.parseInt(rel.substring(4, rel.length() - 4)) + 1);
            } catch (NumberFormatException ignored) {
            }
        }
        if (rel.startsWith("custom_levels/")) return "自制关卡";
        return "文件";
    }

    private void showSettings() {
        LinearLayout c = card("设置", null, 620);
        LinearLayout list = new LinearLayout(this);
        list.setOrientation(LinearLayout.VERTICAL);
        list.addView(choiceRow("帧率上限", "游戏在手机上默认 60 帧；屏幕支持高刷新率时可以调高。", Settings.FPS, Settings.FPS_LABEL, "fps", 60));
        list.addView(choiceRow("渲染分辨率", "低于 100% 时画面略软，GPU 负担更小、更省电。", Settings.SCALE, Settings.SCALE_LABEL, "scale", 100));
        list.addView(choiceRow("屏幕按键", "自动：没接手柄时显示，用手柄时隐藏、触屏后再出现。没有按键的地方可以滑动移动。",
                new int[] { 0, 1, 2 }, new String[] { "自动", "始终显示", "隐藏" }, "touch", 0));
        list.addView(choiceRow("按键大小", "屏幕按键的大小。", new int[] { 85, 100, 120 }, new String[] { "小", "中", "大" }, "touchsize", 100));
        list.addView(choiceRow("开发者日志", "排查问题时打开：游戏日志、帧率和输入设备写进 logcat（标签 Parabox）。", new int[] { 0, 1 }, new String[] { "关", "开" }, "devlog", 0));

        GameData.State st = GameData.state(this);
        GameData.Port p = GameData.port(this);
        String data = GameData.ready(this)
                ? (st.complete ? "已安装 " + mb(GameData.installedBytes(this)) + "（Steam 清单 " + Long.toUnsignedString(st.manifest) + "）" : "已就绪（开发者推送）")
                : "未安装";
        TextView about = text(12, MUTED, false);
        about.setText("游戏数据：" + data + "\n移植包：基于 Steam build " + p.build + "，depot 清单 " + Long.toUnsignedString(p.manifest)
                + "\n可用存储空间：" + mb(GameData.freeBytes(this)) + "\n设置改动在下次进入游戏时生效。");
        about.setLineSpacing(dp(2), 1f);
        about.setPadding(dp(4), dp(12), 0, 0);
        list.addView(about);
        cardList(c, list);
        cardButtons(c, new String[] { "完成", "检查游戏数据" }, new Runnable[] { this::closeOverlay, () -> {
            closeOverlay();
            if (TokenStore.load(this) == null && localTest == null && !GameData.bundled(this)) toast("检查和修复游戏数据需要先登录 Steam");
            else startInstall();
        } });
    }

    // A card with a scrolling list takes the screen's height minus margins, so the list scrolls.
    private void fillHeight(LinearLayout c) {
        FrameLayout.LayoutParams lp = (FrameLayout.LayoutParams) c.getLayoutParams();
        lp.height = (int) (getResources().getDisplayMetrics().heightPixels * 0.88f);
        c.setLayoutParams(lp);
    }

    private View choiceRow(String title, String hint, int[] values, String[] labels, String key, int def) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(dp(14), dp(10), dp(14), dp(10));
        row.setFocusable(true);
        row.setClickable(true);
        row.setBackground(rowBackground());
        LinearLayout texts = new LinearLayout(this);
        texts.setOrientation(LinearLayout.VERTICAL);
        TextView t = text(15, TEXT, true);
        t.setText(title);
        TextView h = text(12, MUTED, false);
        h.setText(hint);
        texts.addView(t);
        texts.addView(h);
        row.addView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        TextView v = text(14, CYAN, true);
        v.setPadding(dp(12), 0, 0, 0);
        row.addView(v);
        final int[] idx = { 0 };
        int cur = Settings.get(this, key, def);
        for (int i = 0; i < values.length; i++) if (values[i] == cur) idx[0] = i;
        v.setText("‹  " + labels[idx[0]] + "  ›");
        Runnable step = () -> {
            idx[0] = (idx[0] + 1) % values.length;
            Settings.put(this, key, values[idx[0]]);
            v.setText("‹  " + labels[idx[0]] + "  ›");
        };
        row.setOnClickListener(x -> step.run());
        row.setOnKeyListener((x, code, ev) -> {
            if (ev.getAction() != KeyEvent.ACTION_DOWN) return false;
            if (code == KeyEvent.KEYCODE_DPAD_RIGHT) { step.run(); return true; }
            if (code == KeyEvent.KEYCODE_DPAD_LEFT) {
                idx[0] = (idx[0] + values.length - 2) % values.length;
                step.run();
                return true;
            }
            return false;
        });
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMargins(0, dp(3), 0, dp(3));
        row.setLayoutParams(lp);
        return row;
    }

    private void toast(String s) {
        TextView t = text(14, TEXT, false);
        t.setText(s);
        t.setPadding(dp(20), dp(10), dp(20), dp(10));
        GradientDrawable bg = solid(Color.argb(235, 30, 33, 45), dp(22));
        bg.setStroke(dp(1), LINE);
        t.setBackground(bg);
        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP | Gravity.CENTER_HORIZONTAL);
        lp.setMargins(0, dp(22), 0, 0);
        root.addView(t, lp);
        t.setAlpha(0f);
        t.setTranslationY(-dp(12));
        t.animate().alpha(1f).translationY(0).setDuration(220).setInterpolator(EASE_OUT).start();
        ui.postDelayed(() -> t.animate().alpha(0f).translationY(-dp(12)).setDuration(200).withEndAction(() -> root.removeView(t)).start(), 2600);
    }

    // ---------------------------------------------------------------- input

    @Override
    public boolean dispatchKeyEvent(KeyEvent e) {
        int code = e.getKeyCode();
        if (code == KeyEvent.KEYCODE_BUTTON_A) {
            if (e.getAction() == KeyEvent.ACTION_UP) {
                // Right after launch the window is in touch mode and nothing has focus: A then means
                // the main button (or the first button of an open card).
                View f = getCurrentFocus();
                if (f == null) f = overlay.getVisibility() == View.VISIBLE ? firstButton(overlay) : primary;
                if (f != null) {
                    f.requestFocus();
                    f.performClick();
                }
            }
            return true;
        }
        if (getCurrentFocus() == null && e.getAction() == KeyEvent.ACTION_DOWN
                && (code == KeyEvent.KEYCODE_DPAD_UP || code == KeyEvent.KEYCODE_DPAD_DOWN || code == KeyEvent.KEYCODE_DPAD_LEFT || code == KeyEvent.KEYCODE_DPAD_RIGHT)) {
            View f = overlay.getVisibility() == View.VISIBLE ? firstButton(overlay) : primary;
            if (f != null) f.requestFocus();
            return true;
        }
        if (code == KeyEvent.KEYCODE_BUTTON_B || code == KeyEvent.KEYCODE_BACK) {
            if (e.getAction() == KeyEvent.ACTION_UP) {
                if (overlay.getVisibility() == View.VISIBLE) closeOverlay();
                else if (code == KeyEvent.KEYCODE_BACK) finish();
            }
            return true;
        }
        return super.dispatchKeyEvent(e);
    }

    private static View firstButton(View v) {
        if (v instanceof TextView && v.isFocusable() && v.isClickable()) return v;
        if (v instanceof ViewGroup) {
            ViewGroup g = (ViewGroup) v;
            for (int i = 0; i < g.getChildCount(); i++) {
                View f = firstButton(g.getChildAt(i));
                if (f != null) return f;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- widgets

    private TextView text(int sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        return t;
    }

    private void dot(TextView v, int color, String label) {
        GradientDrawable d = new GradientDrawable();
        d.setShape(GradientDrawable.OVAL);
        d.setColor(color);
        d.setSize(dp(7), dp(7));
        v.setCompoundDrawablesRelativeWithIntrinsicBounds(d, null, null, null);
        v.setText(label);
    }

    private TextView button(String label, boolean main) {
        TextView b = text(main ? 17 : 14, TEXT, true);
        b.setText(label);
        b.setGravity(Gravity.CENTER);
        b.setFocusable(true);
        b.setClickable(true);
        b.setSingleLine(true);
        StateListDrawable s = new StateListDrawable();
        int r = dp(main ? 12 : 10);
        GradientDrawable normal = main ? solid(BLUE, r) : solid(Color.rgb(27, 29, 39), r);
        if (!main) normal.setStroke(dp(1), LINE);
        GradientDrawable focused = main ? solid(Color.rgb(78, 160, 255), r) : solid(Color.rgb(52, 56, 72), r);
        focused.setStroke(dp(2), Color.WHITE);
        GradientDrawable pressed = main ? solid(BLUE_D, r) : solid(Color.rgb(64, 68, 86), r);
        s.addState(new int[] { android.R.attr.state_pressed }, pressed);
        s.addState(new int[] { android.R.attr.state_focused }, focused);
        s.addState(new int[0], normal);
        b.setBackground(s);
        b.setOnFocusChangeListener((v, has) -> v.animate().scaleX(has ? 1.035f : 1f).scaleY(has ? 1.035f : 1f).setDuration(140).setInterpolator(EASE_OUT).start());
        return b;
    }

    private StateListDrawable rowBackground() {
        StateListDrawable s = new StateListDrawable();
        GradientDrawable f = solid(Color.argb(34, 255, 255, 255), dp(10));
        f.setStroke(dp(2), BLUE);
        s.addState(new int[] { android.R.attr.state_focused }, f);
        s.addState(new int[] { android.R.attr.state_pressed }, solid(Color.argb(28, 255, 255, 255), dp(10)));
        s.addState(new int[0], solid(Color.argb(0, 0, 0, 0), dp(10)));
        return s;
    }

    private static GradientDrawable solid(int c, int r) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(c);
        g.setCornerRadius(r);
        return g;
    }

    // Rounded progress bar; f < 0 shows an indeterminate sweep. Value changes ease in.
    private final class Bar extends View {
        private float f = -1, shown = 0, sweep;
        private final Paint track = new Paint(Paint.ANTI_ALIAS_FLAG), fill = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final RectF rect = new RectF();
        private final int a, b;
        private ValueAnimator anim;

        Bar(Context c, int a, int b) {
            super(c);
            this.a = a;
            this.b = b;
            track.setColor(Color.argb(40, 255, 255, 255));
        }

        void set(float v) {
            float from = f < 0 ? 0 : shown;
            f = v;
            if (anim != null) anim.cancel();
            if (v < 0) { invalidate(); return; }
            anim = ValueAnimator.ofFloat(from, v);
            anim.setDuration(Math.abs(v - from) > 0.2f ? 420 : 180);
            anim.setInterpolator(new DecelerateInterpolator());
            anim.addUpdateListener(x -> { shown = (float) x.getAnimatedValue(); invalidate(); });
            anim.start();
        }

        @Override
        protected void onDraw(Canvas c) {
            float w = getWidth(), h = getHeight(), r = h / 2;
            rect.set(0, 0, w, h);
            c.drawRoundRect(rect, r, r, track);
            fill.setShader(new android.graphics.LinearGradient(0, 0, w, 0, a, b, android.graphics.Shader.TileMode.CLAMP));
            if (f >= 0) {
                if (shown <= 0) return;
                rect.set(0, 0, Math.max(h, w * Math.min(1f, shown)), h);
                c.drawRoundRect(rect, r, r, fill);
            } else {
                sweep = (sweep + 0.012f) % 1.3f;
                float x = (sweep - 0.3f) * w;
                rect.set(Math.max(0, x), 0, Math.min(w, x + w * 0.3f), h);
                if (rect.width() > 0) c.drawRoundRect(rect, r, r, fill);
                postInvalidateOnAnimation();
            }
        }
    }

    // The title screen's backdrop: grey squares of a few sizes drifting slowly, larger ones nearer and
    // faster. Cheap to draw (a few dozen rects); stops while the page is not on screen.
    private final class BoxField extends View {
        private final int count = 46;
        private final float[] x = new float[count], y = new float[count], s = new float[count], v = new float[count];
        private final int[] col = new int[count];
        private final Paint p = new Paint();
        private long last;
        private final Random rnd = new Random(1260520);

        BoxField(Context c) {
            super(c);
        }

        @Override
        protected void onSizeChanged(int w, int h, int ow, int oh) {
            for (int i = 0; i < count; i++) {
                float depth = rnd.nextFloat();
                s[i] = dp(5) + depth * dp(22);
                v[i] = dp(3) + depth * dp(12);
                x[i] = rnd.nextFloat() * w;
                y[i] = rnd.nextFloat() * h;
                int g = 70 + (int) (depth * 40);
                col[i] = Color.argb(70 + (int) (depth * 120), g, g, g + 6);
            }
        }

        @Override
        protected void onDraw(Canvas c) {
            long now = System.nanoTime();
            float dt = last == 0 ? 0 : Math.min(0.05f, (now - last) / 1e9f);
            last = now;
            float w = getWidth(), h = getHeight();
            for (int i = 0; i < count; i++) {
                y[i] -= v[i] * dt;
                x[i] += v[i] * 0.25f * dt;
                if (y[i] < -s[i]) { y[i] = h + s[i]; x[i] = rnd.nextFloat() * w; }
                if (x[i] > w + s[i]) x[i] = -s[i];
                p.setColor(col[i]);
                c.drawRect(x[i], y[i], x[i] + s[i], y[i] + s[i], p);
            }
            if (isShown() && !leaving) postInvalidateOnAnimation();
        }
    }

    // The mark beside the title: a box holding a box holding a box.
    private final class NestMark extends View {
        private final Paint p = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final RectF r = new RectF();

        NestMark(Context c) {
            super(c);
        }

        @Override
        protected void onDraw(Canvas c) {
            float w = getWidth();
            int[] colors = { BLUE, Color.rgb(24, 64, 140), PINK };
            float inset = 0;
            for (int i = 0; i < 3; i++) {
                float sz = w - inset * 2;
                r.set(inset, inset, inset + sz, inset + sz);
                p.setColor(colors[i]);
                c.drawRoundRect(r, sz * 0.12f, sz * 0.12f, p);
                inset += w * (i == 0 ? 0.12f : 0.17f);
            }
        }
    }

    private Bitmap qrBitmap(String url) {
        try {
            Map<EncodeHintType, Object> hints = new HashMap<>();
            hints.put(EncodeHintType.MARGIN, 0);
            BitMatrix m = new QRCodeWriter().encode(url, BarcodeFormat.QR_CODE, 400, 400, hints);
            int w = m.getWidth(), h = m.getHeight();
            int[] px = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = m.get(x, y) ? Color.rgb(10, 11, 16) : Color.WHITE;
            return Bitmap.createBitmap(px, w, h, Bitmap.Config.RGB_565);
        } catch (Throwable t) {
            return null;
        }
    }

    // ---------------------------------------------------------------- helpers

    private FrameLayout.LayoutParams match() {
        return new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);
    }

    private int dp(int v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }

    static String mb(long b) {
        return b >= 1e9 ? String.format(Locale.CHINA, "%.1f GB", b / 1e9) : String.format(Locale.CHINA, "%.0f MB", b / 1e6);
    }

    static String kb(long b) {
        return b < 1000 ? b + " B" : String.format(Locale.CHINA, "%.1f KB", b / 1e3);
    }

    static String hours(int minutes) {
        if (minutes <= 0) return "还没玩过";
        if (minutes < 60) return minutes + " 分钟";
        return String.format(Locale.CHINA, "%.1f 小时", minutes / 60f);
    }

    static String duration(long s) {
        if (s < 60) return s + " 秒";
        if (s < 3600) return (s / 60) + " 分钟";
        return (s / 3600) + " 小时 " + (s % 3600 / 60) + " 分";
    }

    private static String message(Throwable t) {
        while (t.getCause() != null && (t.getMessage() == null || t.getMessage().isEmpty())) t = t.getCause();
        String m = t.getMessage();
        return m == null || m.isEmpty() ? t.getClass().getSimpleName() : m;
    }

    private void hideBars() {
        if (android.os.Build.VERSION.SDK_INT >= 30) {
            WindowInsetsController c = getWindow().getInsetsController();
            if (c != null) {
                c.hide(WindowInsets.Type.systemBars());
                c.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
            }
        } else {
            getWindow().getDecorView().setSystemUiVisibility(0x1706);
        }
    }
}
