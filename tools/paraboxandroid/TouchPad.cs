using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace ParaboxAndroid
{
    // On-screen controls for phones without a gamepad.
    //
    // The game shipped with a mobile button set (TouchControls) but LoadLevel.Load clears it right after
    // loading it, and its swipe code returns early without buttons, so on a touch screen nothing works.
    // This layer reads the touch screen itself and merges its state into the game's input fields at the
    // end of Controls.Update (Hooks.cs injects "Controls.x |= TouchPad.x" for each), the same fields a pad
    // or keyboard sets. That works in levels, menus and the pause menu alike, with no device pairing.
    //
    // Layout (landscape): D-pad bottom left; undo (large), confirm and back bottom right; redo, restart and
    // menu as small pills top right. A drag on free screen moves the player one cell per step, as the
    // game's own (disabled) swipe code did; holding keeps moving. Touches on the controls never reach the
    // virtual mouse, so they do not count as taps in the game's inspect mode.
    //
    // Shown per the launcher setting "touch": 0 auto (when no gamepad is connected, or the screen was
    // touched more recently than the pad was used), 1 always, 2 never. "touchsize": 85 / 100 / 120 (%).
    public class TouchPad : MonoBehaviour
    {
        // Read by the code injected into Controls.Update.
        public static bool up, down, left, right, upPressed, downPressed, leftPressed, rightPressed,
            undo, undoPressed, redo, redoPressed, restart, restartPressed, escape, escapePressed,
            confirm, confirmPressed, cancel, cancelPressed;

        enum K { Undo, Confirm, Back, Redo, Restart, Menu }

        sealed class Btn
        {
            public K k;
            public string label;
            public Rect r;
            public bool round, held, was;
        }

        static TouchPad inst;
        static int mode, sizePct = 100;
        readonly List<Btn> btns = new List<Btn>();
        Rect dpad;            // square around the D-pad
        Vector2 dpadC;
        float dpadR, unit;
        int dirHeld = -1, dirWas = -1;          // 0 up, 1 right, 2 down, 3 left
        int swipeDir = -1, swipeWas = -1;
        readonly Dictionary<int, Vector2> swipeOrigin = new Dictionary<int, Vector2>();
        float alpha, lastTouch = -100, lastPad = -100;
        bool loggedAny, loggedNoTs;
        int layoutW, layoutH;
        Texture2D circle, ring, pill, pillLine, arrow;
        GUIStyle text;

        public static void Install(GameObject go, int touchMode, int touchSize)
        {
            mode = touchMode;
            sizePct = Mathf.Clamp(touchSize, 70, 140);
            if (mode == 2) { Bridge.L("touch controls off"); return; }
            inst = go.AddComponent<TouchPad>();
            Bridge.L("touch controls: mode " + mode + ", size " + sizePct + "%, touchscreen " + (Touchscreen.current != null) + ", gamepads " + Gamepad.all.Count);
        }

        public static bool Visible => inst != null && inst.alpha > 0.05f;

        // Is this screen point (Input System coordinates, origin bottom left) on one of the controls?
        public static bool OnControl(Vector2 p)
        {
            var t = inst;
            if (t == null || !t.Shown()) return false;
            var g = new Vector2(p.x, Screen.height - p.y);
            if (Vector2.Distance(g, t.dpadC) <= t.dpadR * 1.15f) return true;
            foreach (var b in t.btns) if (Grow(b.r, t.unit * 0.04f).Contains(g)) return true;
            return false;
        }

        // Button prompts (Draw.DrawControl: "<key>  start game") name the on-screen buttons while those are
        // shown. DrawControl then passes group "Touch" (no Enter/Space picture), Controls.ControlIcon returns
        // null (no key or pad picture), and Controls.BindingDisplay returns Label(action). Other screens
        // that show bindings (the pause menu's controls page) are left alone.
        static bool inPrompt;
        public static bool InPrompt => inPrompt;

        public static string PromptGroup(string group)
        {
            inPrompt = Visible;
            // Not "": DrawControl replaces an empty group with the current control scheme.
            return inPrompt ? "Touch" : group;
        }

        // Menus ask for "MoveNR", whose branch in DrawControl only has pictures for the "Gamepad" and
        // "Keyboard&Mouse" schemes and otherwise prints the four bindings as text. While the touch prompts
        // are on, it is drawn as "Move", which shows the game's D-pad picture (Label returns "D-Pad/...").
        public static string PromptAction(string action) => inPrompt && action == "MoveNR" ? "Move" : action;

        public static void EndPrompt() => inPrompt = false;

        public static string Label(string action)
        {
            if (!inPrompt || action == null) return null;
            if (action.EndsWith("NR")) action = action.Substring(0, action.Length - 2);
            switch (action)
            {
                case "Submit": return "确认键";
                case "Cancel": return "返回键";
                case "Pause": return "菜单键";
                case "Undo": return "撤销键";
                case "Redo": return "重做键";
                case "Restart": return "重来键";
                // "Move" joins the four; this exact text makes DrawControl show its D-pad picture.
                case "Up": return "D-Pad/Up";
                case "Down": return "D-Pad/Down";
                case "Left": return "D-Pad/Left";
                case "Right": return "D-Pad/Right";
                default: return null;
            }
        }

        // Called at the end of Controls.Update, before the merge.
        public static void Poll()
        {
            if (inst == null) { Clear(); return; }
            inst.Read();
        }

        static void Clear()
        {
            up = down = left = right = upPressed = downPressed = leftPressed = rightPressed = false;
            undo = undoPressed = redo = redoPressed = restart = restartPressed = false;
            escape = escapePressed = confirm = confirmPressed = cancel = cancelPressed = false;
        }

        bool Shown()
        {
            if (mode == 1) return true;
            // Gamepads only: Android also reports keyboards and the emulator's virtual devices as joysticks.
            bool pad = Gamepad.all.Count > 0;
            return !pad || lastTouch > lastPad;
        }

        void Layout()
        {
            int w = Screen.width, h = Screen.height;
            if (w == layoutW && h == layoutH) return;
            layoutW = w;
            layoutH = h;
            unit = Mathf.Min(w, h) * sizePct / 100f;
            float m = unit * 0.06f;
            // D-pad
            dpadR = unit * 0.185f;
            dpadC = new Vector2(m + dpadR + unit * 0.04f, h - m - dpadR - unit * 0.02f);
            dpad = new Rect(dpadC.x - dpadR, dpadC.y - dpadR, dpadR * 2, dpadR * 2);
            // Face buttons
            btns.Clear();
            float big = unit * 0.19f, mid = unit * 0.145f;
            Vector2 confirmC = new Vector2(w - m - mid / 2 - unit * 0.02f, h - m - mid / 2 - unit * 0.10f);
            Vector2 backC = new Vector2(confirmC.x - unit * 0.06f, confirmC.y - mid - unit * 0.05f);
            Vector2 undoC = new Vector2(confirmC.x - mid / 2 - big / 2 - unit * 0.05f, h - m - big / 2);
            Add(K.Confirm, "确认", Circle(confirmC, mid), true);
            Add(K.Back, "返回", Circle(backC, mid * 0.82f), true);
            Add(K.Undo, "撤销", Circle(undoC, big), true);
            // Top-right pills
            float pw = unit * 0.15f, ph = unit * 0.075f, gap = unit * 0.025f;
            float x = w - m - pw, y = m;
            Add(K.Menu, "菜单", new Rect(x, y, pw, ph), false);
            x -= pw + gap;
            Add(K.Restart, "重来", new Rect(x, y, pw, ph), false);
            x -= pw + gap;
            Add(K.Redo, "重做", new Rect(x, y, pw, ph), false);
        }

        void Add(K k, string label, Rect r, bool round) => btns.Add(new Btn { k = k, label = label, r = r, round = round });

        static Rect Circle(Vector2 c, float d) => new Rect(c.x - d / 2, c.y - d / 2, d, d);

        static Rect Grow(Rect r, float by) => new Rect(r.x - by, r.y - by, r.width + by * 2, r.height + by * 2);

        void Read()
        {
            Layout();
            Clear();
            var g = Gamepad.current;
            if (g != null && (g.buttonSouth.isPressed || g.buttonEast.isPressed || g.buttonWest.isPressed || g.buttonNorth.isPressed
                || g.startButton.isPressed || g.dpad.ReadValue().sqrMagnitude > 0.25f || g.leftStick.ReadValue().sqrMagnitude > 0.25f))
                lastPad = Time.unscaledTime;

            var ts = Touchscreens();
            foreach (var b in btns) { b.was = b.held; b.held = false; }
            dirWas = dirHeld;
            dirHeld = -1;
            swipeWas = swipeDir;
            int swipeNow = -1;
            bool any = false;
            if (ts.Count > 0)
            {
                var seen = new List<int>(4);
                foreach (var t in Pressed(ts))
                {
                    any = true;
                    int id = t.touchId.ReadValue();
                    seen.Add(id);
                    Vector2 p = t.position.ReadValue(), s = t.startPosition.ReadValue();
                    Vector2 pg = new Vector2(p.x, Screen.height - p.y), sg = new Vector2(s.x, Screen.height - s.y);
                    if (!Shown()) { lastTouch = Time.unscaledTime; continue; }
                    // The D-pad owns touches that start on it; the direction follows the finger.
                    if (Vector2.Distance(sg, dpadC) <= dpadR * 1.15f)
                    {
                        Vector2 d = pg - dpadC;
                        if (d.magnitude > dpadR * 0.22f)
                            dirHeld = Mathf.Abs(d.x) > Mathf.Abs(d.y) ? (d.x > 0 ? 1 : 3) : (d.y > 0 ? 2 : 0);
                        continue;
                    }
                    Btn hit = null;
                    foreach (var b in btns) if (Grow(b.r, unit * 0.04f).Contains(sg)) { hit = b; break; }
                    if (hit != null)
                    {
                        if (Grow(hit.r, unit * 0.2f).Contains(pg)) hit.held = true;
                        continue;
                    }
                    // Free screen: a drag moves one cell each time it covers min(w, h) / 15, like the game's
                    // own swipe code; the last direction stays held while the finger is down.
                    if (!swipeOrigin.TryGetValue(id, out var o)) { swipeOrigin[id] = pg; o = pg; }
                    Vector2 dv = pg - o;
                    if (dv.magnitude > Mathf.Min(Screen.width, Screen.height) / 15f)
                    {
                        swipeDir = Mathf.Abs(dv.x) > Mathf.Abs(dv.y) ? (dv.x > 0 ? 1 : 3) : (dv.y > 0 ? 2 : 0);
                        swipeOrigin[id] = pg;
                    }
                    if (swipeDir >= 0) swipeNow = swipeDir;
                }
                if (seen.Count != swipeOrigin.Count)
                {
                    var gone = new List<int>();
                    foreach (var k in swipeOrigin.Keys) if (!seen.Contains(k)) gone.Add(k);
                    foreach (var k in gone) swipeOrigin.Remove(k);
                }
            }
            if (any) lastTouch = Time.unscaledTime;
            if (Bridge.DevLog && (any != loggedAny || (ts.Count == 0) != loggedNoTs))
            {
                loggedAny = any;
                loggedNoTs = ts.Count == 0;
                Bridge.L("touch: screens " + ts.Count + ", touching " + any + ", shown " + Shown() + ", dir " + dirHeld + "/" + swipeDir);
            }
            swipeDir = swipeNow;

            int dir = dirHeld >= 0 ? dirHeld : swipeDir;
            int was = dirWas >= 0 ? dirWas : swipeWas;
            if (Bridge.DevLog && dir != was) Bridge.L("touch dir " + was + " -> " + dir + (dirHeld >= 0 ? " (pad)" : " (swipe)"));
            up = dir == 0; right = dir == 1; down = dir == 2; left = dir == 3;
            upPressed = up && was != 0; rightPressed = right && was != 1; downPressed = down && was != 2; leftPressed = left && was != 3;
            foreach (var b in btns)
            {
                bool p = b.held && !b.was;
                switch (b.k)
                {
                    case K.Undo: undo = b.held; undoPressed = p; break;
                    case K.Redo: redo = b.held; redoPressed = p; break;
                    case K.Restart: restart = b.held; restartPressed = p; break;
                    case K.Menu: escape = b.held; escapePressed = p; break;
                    case K.Confirm: confirm = b.held; confirmPressed = p; break;
                    case K.Back: cancel = b.held; cancelPressed = p; break;
                }
            }
        }

        // Every touch screen the system reports: phones can have more than one input device of that kind
        // (and the emulator has eleven), and Touchscreen.current is only the one used last.
        static readonly List<Touchscreen> screens = new List<Touchscreen>();
        static readonly List<TouchControl> pressed = new List<TouchControl>();

        internal static List<Touchscreen> Touchscreens()
        {
            screens.Clear();
            foreach (var d in InputSystem.devices) if (d is Touchscreen t && t.added) screens.Add(t);
            return screens;
        }

        internal static List<TouchControl> Pressed(List<Touchscreen> ts)
        {
            pressed.Clear();
            foreach (var s in ts)
                foreach (var t in s.touches)
                    if (t.press.isPressed) pressed.Add(t);
            return pressed;
        }

        void Update()
        {
            float target = Shown() ? 1f : 0f;
            alpha = Mathf.MoveTowards(alpha, target, Time.unscaledDeltaTime / 0.18f);
        }

        // End of World.OnGUI (Hooks.cs), right after the game has drawn the frame, so the controls are
        // always on top of it.
        public static void DrawGUI()
        {
            if (inst != null && Event.current != null && Event.current.type == EventType.Repaint) inst.Paint();
        }

        void Paint()
        {
            if (alpha <= 0.01f) return;
            Layout();
            if (circle == null) MakeTextures();
            var keep = GUI.color;
            // D-pad: a soft disc with four arrows; the held direction lights up.
            Disc(dpad, dirHeld >= 0 ? 0.30f : 0.22f, 0.55f);
            for (int d = 0; d < 4; d++)
            {
                float ang = d * 90f;
                Vector2 c = dpadC + Rot(new Vector2(0, -dpadR * 0.62f), ang);
                float s = dpadR * (dirHeld == d ? 0.5f : 0.42f);
                var r = new Rect(c.x - s / 2, c.y - s / 2, s, s);
                var mtx = GUI.matrix;
                GUIUtility.RotateAroundPivot(ang, c);
                GUI.color = new Color(1, 1, 1, alpha * (dirHeld == d ? 1f : 0.8f));
                GUI.DrawTexture(r, arrow);
                GUI.matrix = mtx;
            }
            foreach (var b in btns)
            {
                if (b.round) Disc(b.r, b.held ? 0.45f : (b.k == K.Undo ? 0.26f : 0.2f), 0.6f);
                else
                {
                    GUI.color = new Color(0, 0, 0, alpha * (b.held ? 0.15f : 0.35f));
                    GUI.DrawTexture(b.r, pill);
                    GUI.color = new Color(1, 1, 1, alpha * (b.held ? 0.55f : 0.18f));
                    GUI.DrawTexture(b.r, pill);
                    GUI.color = new Color(1, 1, 1, alpha * 0.6f);
                    GUI.DrawTexture(b.r, pillLine);
                }
                text.fontSize = Mathf.RoundToInt(b.round ? b.r.height * (b.k == K.Undo ? 0.24f : 0.26f) : b.r.height * 0.42f);
                GUI.color = new Color(1, 1, 1, alpha);
                GUI.Label(b.r, b.label, text);
            }
            GUI.color = keep;
        }

        void Disc(Rect r, float fill, float line)
        {
            GUI.color = new Color(0, 0, 0, alpha * 0.32f);
            GUI.DrawTexture(r, circle);
            GUI.color = new Color(1, 1, 1, alpha * fill * 0.6f);
            GUI.DrawTexture(r, circle);
            GUI.color = new Color(1, 1, 1, alpha * line);
            GUI.DrawTexture(r, ring);
        }

        static Vector2 Rot(Vector2 v, float deg)
        {
            float a = deg * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        void MakeTextures()
        {
            const int n = 128;
            circle = Tex(n, (x, y) => Mathf.Clamp01(n / 2f - 1 - Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n / 2f, n / 2f)) + 0.5f));
            ring = Tex(n, (x, y) =>
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n / 2f, n / 2f));
                return Mathf.Clamp01(1.6f - Mathf.Abs(d - (n / 2f - 3f)));
            });
            // Fully rounded pill, stretched with a 9-slice style: drawn at the rect's size via DrawTexture
            // works because the pills are close to the texture's 2:1 aspect.
            pill = Tex2(128, 64, (x, y) => PillA(x, y, 128, 64, 0));
            pillLine = Tex2(128, 64, (x, y) => Mathf.Clamp01(1.6f - Mathf.Abs(PillD(x, y, 128, 64) + 2.5f)));
            arrow = Tex(64, (x, y) =>
            {
                // Upward chevron-ish triangle: apex at top center.
                float fx = (x + 0.5f) / 64f - 0.5f, fy = (y + 0.5f) / 64f;
                // y grows downward in GUI, but textures are bottom-up: flip.
                fy = 1f - fy;
                float half = fy * 0.42f;
                float inside = Mathf.Min(fy - 0.18f, 0.82f - fy) * 64f;
                float edge = (half - Mathf.Abs(fx)) * 64f;
                return Mathf.Clamp01(Mathf.Min(inside, edge) + 0.5f);
            });
            text = new GUIStyle { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            text.normal.textColor = Color.white;
        }

        static float PillD(int x, int y, int w, int h)
        {
            float r = h / 2f, px = x + 0.5f, py = y + 0.5f;
            float cx = Mathf.Clamp(px, r, w - r);
            return Vector2.Distance(new Vector2(px, py), new Vector2(cx, r)) - (r - 1);
        }

        static float PillA(int x, int y, int w, int h, int _) => Mathf.Clamp01(0.5f - PillD(x, y, w, h));

        static Texture2D Tex(int n, Func<int, int, float> a) => Tex2(n, n, a);

        static Texture2D Tex2(int w, int h, Func<int, int, float> a)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a(x, y) * 255));
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }
    }
}
