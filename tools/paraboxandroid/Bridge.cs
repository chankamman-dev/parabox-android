using System;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParaboxAndroid
{
    // Game-side half of the port, woven in by `ptx pstage` (tools/ptx/Hooks.cs). Talks to the Java side
    // (dev.kmparabox.cloud.CloudBridge): save writes queue a Steam Cloud upload, achievements go to Steam,
    // playtime runs while the game is on screen, and the launcher's settings apply here.
    public static class Bridge
    {
        const string Pkg = "dev.kmparabox.game";
        static bool booted, settingsLoaded, devLog;
        static int fpsCap = 60, renderScale = 100, touchMode, touchSize = 100;

        // End of World.Awake (every exit).
        public static void Boot()
        {
            if (booted) return;
            booted = true;
            LoadSettings();
            var go = new GameObject("ParaboxAndroid");
            UnityEngine.Object.DontDestroyOnLoad(go);
            TouchMouse.Install(go);
            TouchPad.Install(go, touchMode, touchSize);
            if (devLog) go.AddComponent<Diag>();
            // The game's own log is chatty (a line plus a stack trace per FMOD warning); traces are most
            // of its cost, and without the developer setting the log is off altogether.
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
            Debug.unityLogger.logEnabled = devLog;
            if (renderScale < 100)
            {
                int w = Display.main.systemWidth * renderScale / 100, h = Display.main.systemHeight * renderScale / 100;
                Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow);
            }
            L("boot: " + SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceName + ", screen " + Display.main.systemWidth + "x" + Display.main.systemHeight
                + ", fps cap " + fpsCap + ", render scale " + renderScale + "%, dev log " + devLog);
            Call("onGameStarted");
        }

        // FMODUnity.PlatformAndroid.StaticGetBankFolder: the banks are downloaded from Steam next to the
        // texture streams (see GameData.java), not packed in the APK.
        public static string BankFolder() => "/data/data/" + Pkg + "/files/gamedata";

        // Every Application.targetFrameRate = n of the game (60 on mobile, the refresh rate on PC).
        public static void SetTargetFrameRate(int requested)
        {
            LoadSettings();
            Application.targetFrameRate = fpsCap;
            QualitySettings.vSyncCount = 0;
            L("targetFrameRate " + requested + " -> " + fpsCap);
        }

        // Screen.fullScreen = x, QualitySettings.vSyncCount = x and Screen.SetResolution(w, h, fs, hz) of the
        // game: its PC display options, applied from prefs.txt (synced from the PC) and the pause menu.
        // On the handheld the game stays full screen at the panel size times the launcher's render
        // scale, paced by targetFrameRate.
        public static void SetFullScreen(bool on)
        {
            if (!on) L("fullScreen=false ignored");
        }

        public static void SetVSyncCount(int n)
        {
            QualitySettings.vSyncCount = 0;
        }

        public static void SetResolution(int w, int h, bool fullscreen, int hz)
        {
            L("SetResolution(" + w + "x" + h + ", fs=" + fullscreen + ", " + hz + " Hz) ignored");
        }

        // End of SaveFile.Save and Prefs.Save.
        public static void Saved() => Call("onSaved");

        // Start of Achievements.Achieve(name). The names are the Steam API names (ACH_...).
        // World.UpdateInner checks some of them every frame (e.g. 100% once the save has it), so each name
        // goes to Java once per run.
        static readonly System.Collections.Generic.HashSet<string> sent = new System.Collections.Generic.HashSet<string>();

        public static void Achieve(string name)
        {
            if (string.IsNullOrEmpty(name) || !sent.Add(name)) return;
            Call("onAchievements", name);
        }

        static void LoadSettings()
        {
            if (settingsLoaded) return;
            settingsLoaded = true;
            fpsCap = Mathf.Clamp(Setting("fps", 60), 30, 144);
            renderScale = Mathf.Clamp(Setting("scale", 100), 50, 100);
            devLog = Setting("devlog", 0) != 0;
            touchMode = Setting("touch", 0);
            touchSize = Setting("touchsize", 100);
        }

        static AndroidJavaObject Activity()
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                return player.GetStatic<AndroidJavaObject>("currentActivity");
        }

        static int Setting(string key, int def)
        {
            try
            {
                using (var activity = Activity())
                using (var bridge = new AndroidJavaClass("dev.kmparabox.cloud.CloudBridge"))
                    return bridge.CallStatic<int>("intSetting", activity, key, def);
            }
            catch (Exception e)
            {
                L("setting " + key + ": " + e.Message);
                return def;
            }
        }

        static void Call(string method, string arg = null)
        {
            L("-> " + method + (arg == null ? "" : " " + arg));
            try
            {
                using (var activity = Activity())
                using (var bridge = new AndroidJavaClass("dev.kmparabox.cloud.CloudBridge"))
                {
                    if (arg == null) bridge.CallStatic(method, activity);
                    else bridge.CallStatic(method, activity, arg);
                }
            }
            catch (Exception e)
            {
                L(method + ": " + e.Message);
            }
        }

        // The port's own lines go straight to logcat (tag "Parabox"), whatever the game's log switch says.
        static AndroidJavaClass log;

        internal static void L(string msg)
        {
            try
            {
                if (log == null) log = new AndroidJavaClass("android.util.Log");
                log.CallStatic<int>("i", "Parabox", msg);
            }
            catch
            {
            }
        }

        internal static bool DevLog => devLog;
    }

    // The PC code reads Mouse.current every frame (Controls.Update, World.ComputeMousePosition), and the
    // game's own touch buttons are hit-tested against that mouse position. Android has no mouse, so a
    // virtual one is added and driven by the primary touch: position follows the finger, left button
    // is down while it touches.
    public class TouchMouse : MonoBehaviour
    {
        Mouse mouse;

        public static void Install(GameObject go)
        {
            if (Mouse.current != null) { Bridge.L("real mouse present"); return; }
            var t = go.AddComponent<TouchMouse>();
            t.mouse = InputSystem.AddDevice<Mouse>("ParaboxTouchMouse");
        }

        void Update()
        {
            if (mouse == null || !mouse.added) return;
            // The first finger that is not on the on-screen controls (TouchPad) acts as the mouse.
            UnityEngine.InputSystem.Controls.TouchControl p = null;
            foreach (var t in TouchPad.Pressed(TouchPad.Touchscreens()))
                if (!TouchPad.OnControl(t.startPosition.ReadValue())) { p = t; break; }
            bool down = p != null;
            var pos = p != null ? p.position.ReadValue() : Vector2.zero;
            var st = new UnityEngine.InputSystem.LowLevel.MouseState { position = down || pos != Vector2.zero ? pos : mouse.position.ReadValue() };
            if (down) st = st.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, true);
            InputSystem.QueueStateEvent(mouse, st);
        }
    }

    // Developer setting only: input devices when they change, and the frame rate every 5 seconds.
    public class Diag : MonoBehaviour
    {
        string last = "";
        float next, windowStart;
        int frames;
        float worst;

        void Update()
        {
            frames++;
            worst = Mathf.Max(worst, Time.unscaledDeltaTime);
            float now = Time.unscaledTime;
            if (now - windowStart >= 5f)
            {
                Bridge.L($"fps: {frames / (now - windowStart):F1}, slowest frame {worst * 1000:F0} ms, mono heap {GC.GetTotalMemory(false) / 1048576} MB");
                windowStart = now;
                frames = 0;
                worst = 0;
            }
            if (now < next) return;
            next = now + 2f;
            string s;
            try
            {
                var devs = string.Join(", ", InputSystem.devices.Where(d => !(d is Sensor)).Select(d => d.layout + ":" + d.displayName));
                var pis = FindObjectsOfType<PlayerInput>();
                var pi = string.Join(" | ", pis.Select(p => $"scheme={p.currentControlScheme} devices={p.devices.Count}"));
                s = $"playerInputs={pis.Length} {pi} devices=[{devs}]";
            }
            catch (Exception e) { s = "diag failed: " + e.Message; }
            if (s == last) return;
            last = s;
            Bridge.L("input: " + s);
        }
    }

    public static class Safe
    {
        // Controls.ControlIcon does devices[0].ToString() on the PlayerInput, which throws while no
        // device is paired (touch-only, or before the pad is seen).
        public static string DeviceName(ref UnityEngine.InputSystem.Utilities.ReadOnlyArray<UnityEngine.InputSystem.InputDevice> devs, int i)
            => i < devs.Count ? devs[i].ToString() : "none";
    }
}
