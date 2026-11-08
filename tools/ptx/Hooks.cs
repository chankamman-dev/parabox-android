using Mono.Cecil;
using Mono.Cecil.Cil;

// Game-side hooks into ParaboxAndroid.dll (tools/paraboxandroid/Bridge.cs).
static class Hooks
{
    public static void Apply(string outDir, ReaderParameters rp)
    {
        var bridgeAsm = AssemblyDefinition.ReadAssembly(Path.Combine(outDir, "ParaboxAndroid.dll"), rp).MainModule;
        var bridge = bridgeAsm.GetType("ParaboxAndroid.Bridge");
        MethodDefinition BD(string n) => bridge.Methods.First(x => x.Name == n);

        var path = Path.Combine(outDir, "Assembly-CSharp.dll");
        var asm = AssemblyDefinition.ReadAssembly(path, rp);
        var mod = asm.MainModule;
        MethodReference B(string n) => mod.ImportReference(BD(n));
        MethodDefinition G(string type, string name) => mod.GetType(type).Methods.First(x => x.Name == name);

        int n = AtEveryRet(G("World", "Awake"), B("Boot"));
        Console.WriteLine($"hooks: World.Awake -> Bridge.Boot ({n} exits)");

        // ControlIcon: devices[0].ToString() -> Safe.DeviceName(ref devices, 0)
        {
            var safe = bridgeAsm.GetType("ParaboxAndroid.Safe").Methods.First(x => x.Name == "DeviceName");
            var ci = G("Controls", "ControlIcon");
            var ins = ci.Body.Instructions;
            int c = 0;
            for (int k = 0; k + 1 < ins.Count; k++)
                if (ins[k].Operand is MethodReference g && g.Name == "get_Item" && g.DeclaringType.FullName.Contains("InputDevice")
                    && ins[k + 1].Operand is MethodReference ts && ts.Name == "ToString")
                {
                    ins[k].OpCode = OpCodes.Call; ins[k].Operand = mod.ImportReference(safe);
                    ci.Body.GetILProcessor().Remove(ins[k + 1]);
                    c++;
                }
            Console.WriteLine($"hooks: ControlIcon device name guarded ({c})");
        }

        // Saves: the slot file and the settings file. Each write queues a Steam Cloud upload.
        n = AtEveryRet(G("SaveFile", "Save"), B("Saved")) + AtEveryRet(G("Prefs", "Save"), B("Saved"));
        Console.WriteLine($"hooks: SaveFile.Save / Prefs.Save -> Bridge.Saved ({n} exits)");

        // Achievements.Achieve(name): what SteamSquare.Update would hand to SteamUserStats.SetAchievement.
        {
            var m = G("Achievements", "Achieve");
            var il = m.Body.GetILProcessor();
            var first = m.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Call, B("Achieve")));
            Console.WriteLine("hooks: Achievements.Achieve -> Bridge.Achieve");
        }

        // Every Application.targetFrameRate = x goes through the launcher's frame-rate setting.
        {
            int c = 0;
            foreach (var t in mod.Types)
                foreach (var m in t.Methods.Where(x => x.HasBody))
                    foreach (var i in m.Body.Instructions)
                        if (i.Operand is MethodReference r && r.Name == "set_targetFrameRate" && r.DeclaringType.Name == "Application")
                        {
                            i.OpCode = OpCodes.Call;
                            i.Operand = B("SetTargetFrameRate");
                            c++;
                        }
            Console.WriteLine($"hooks: targetFrameRate -> Bridge.SetTargetFrameRate ({c})");
        }

        // The PC display options (from prefs.txt, which Steam Cloud brings over from the PC, and the
        // pause menu): window mode, vsync and resolution mean nothing on a handheld.
        {
            var map = new Dictionary<string, string> {
                ["set_fullScreen"] = "SetFullScreen", ["set_vSyncCount"] = "SetVSyncCount", ["SetResolution"] = "SetResolution" };
            var counts = new Dictionary<string, int>();
            foreach (var t in mod.Types)
                foreach (var m in t.Methods.Where(x => x.HasBody))
                    foreach (var i in m.Body.Instructions)
                        if (i.Operand is MethodReference r && map.TryGetValue(r.Name, out var to)
                            && (r.DeclaringType.Name == "Screen" || r.DeclaringType.Name == "QualitySettings")
                            && (r.Name != "SetResolution" || r.Parameters.Count == 4))
                        {
                            i.OpCode = OpCodes.Call;
                            i.Operand = B(to);
                            counts[to] = counts.GetValueOrDefault(to) + 1;
                        }
            Console.WriteLine("hooks: display options -> " + string.Join(", ", counts.Select(kv => kv.Key + " " + kv.Value)));
        }
        // On-screen touch controls (paraboxandroid/TouchPad.cs): at every exit of Controls.Update, poll
        // the touch screen and OR its state into the game's input fields ("Controls.up |= TouchPad.up").
        {
            var touch = bridgeAsm.GetType("ParaboxAndroid.TouchPad");
            var poll = mod.ImportReference(touch.Methods.First(x => x.Name == "Poll"));
            var ctl = mod.GetType("Controls");
            string[] fields = { "up", "down", "left", "right", "upPressed", "downPressed", "leftPressed", "rightPressed",
                "undo", "undoPressed", "redo", "redoPressed", "restart", "restartPressed", "escape", "escapePressed",
                "confirm", "confirmPressed", "cancel", "cancelPressed" };
            var m = G("Controls", "Update");
            var il = m.Body.GetILProcessor();
            int exits = 0;
            foreach (var r in m.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret).ToList())
            {
                // The ret becomes the first injected instruction, so branches to it run the merge too.
                r.OpCode = OpCodes.Call;
                r.Operand = poll;
                var at = r;
                foreach (var f in fields)
                {
                    var gf = ctl.Fields.First(x => x.Name == f && x.IsStatic);
                    var tf = mod.ImportReference(touch.Fields.First(x => x.Name == f));
                    var seq = new[] { il.Create(OpCodes.Ldsfld, gf), il.Create(OpCodes.Ldsfld, tf), il.Create(OpCodes.Or), il.Create(OpCodes.Stsfld, gf) };
                    foreach (var i in seq) { il.InsertAfter(at, i); at = i; }
                }
                il.InsertAfter(at, il.Create(OpCodes.Ret));
                exits++;
            }
            Console.WriteLine($"hooks: Controls.Update merges TouchPad ({fields.Length} fields, {exits} exits)");

            // Prompts name the on-screen buttons while they are shown: Controls.BindingDisplay(action, group)
            // returns TouchPad.Label(action) when that is not null.
            var bd = G("Controls", "BindingDisplay");
            var bil = bd.Body.GetILProcessor();
            var first = bd.Body.Instructions[0];
            var pop = bil.Create(OpCodes.Pop);
            bil.InsertBefore(first, bil.Create(OpCodes.Ldarg_0));
            bil.InsertBefore(first, bil.Create(OpCodes.Call, mod.ImportReference(touch.Methods.First(x => x.Name == "Label"))));
            bil.InsertBefore(first, bil.Create(OpCodes.Dup));
            bil.InsertBefore(first, bil.Create(OpCodes.Brfalse, pop));
            bil.InsertBefore(first, bil.Create(OpCodes.Ret));
            bil.InsertBefore(first, pop);
            Console.WriteLine("hooks: Controls.BindingDisplay -> TouchPad.Label");

            // Draw.DrawControl(action, label, group, ...): group = TouchPad.PromptGroup(group) at the start,
            // TouchPad.EndPrompt() at every exit; Controls.ControlIcon returns null while InPrompt.
            var dc = mod.GetType("Draw").Methods.First(x => x.Name == "DrawControl");
            var dil = dc.Body.GetILProcessor();
            var d0 = dc.Body.Instructions[0];
            dil.InsertBefore(d0, dil.Create(OpCodes.Ldarg_2));
            dil.InsertBefore(d0, dil.Create(OpCodes.Call, mod.ImportReference(touch.Methods.First(x => x.Name == "PromptGroup"))));
            dil.InsertBefore(d0, dil.Create(OpCodes.Starg, dc.Parameters[2]));
            dil.InsertBefore(d0, dil.Create(OpCodes.Ldarg_0));
            dil.InsertBefore(d0, dil.Create(OpCodes.Call, mod.ImportReference(touch.Methods.First(x => x.Name == "PromptAction"))));
            dil.InsertBefore(d0, dil.Create(OpCodes.Starg, dc.Parameters[0]));
            int de = AtEveryRet(dc, mod.ImportReference(touch.Methods.First(x => x.Name == "EndPrompt")));
            var ci2 = G("Controls", "ControlIcon");
            var cil = ci2.Body.GetILProcessor();
            var c0 = ci2.Body.Instructions[0];
            cil.InsertBefore(c0, cil.Create(OpCodes.Call, mod.ImportReference(touch.Methods.First(x => x.Name == "get_InPrompt"))));
            cil.InsertBefore(c0, cil.Create(OpCodes.Brfalse, c0));
            cil.InsertBefore(c0, cil.Create(OpCodes.Ldnull));
            cil.InsertBefore(c0, cil.Create(OpCodes.Ret));
            Console.WriteLine($"hooks: Draw.DrawControl prompts name the touch buttons ({de} exits)");

            n = AtEveryRet(G("World", "OnGUI"), mod.ImportReference(touch.Methods.First(x => x.Name == "DrawGUI")));
            Console.WriteLine($"hooks: World.OnGUI -> TouchPad.DrawGUI ({n} exits)");
        }
        asm.Write(path);

        // FMOD banks: PlatformAndroid.StaticGetBankFolder returns file:///android_asset (inside the APK);
        // the banks are downloaded from Steam into the app's files dir instead.
        {
            var fpath = Path.Combine(outDir, "FMODUnity.dll");
            var fasm = AssemblyDefinition.ReadAssembly(fpath, rp);
            var m = fasm.MainModule.GetType("FMODUnity.PlatformAndroid").Methods.First(x => x.Name == "StaticGetBankFolder");
            m.Body.Instructions.Clear();
            m.Body.ExceptionHandlers.Clear();
            m.Body.Variables.Clear();
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Call, fasm.MainModule.ImportReference(BD("BankFolder")));
            il.Emit(OpCodes.Ret);

            // FMOD 2.01 wrapper on the 2.03 Android libraries: the entry points whose signature or
            // structs changed go to the adapter (tools/fmodshim/pbxfmod.c, libpbxfmod.so) as "pbx_<name>".
            var adapted = new HashSet<string> {
                "FMOD5_System_Create", "FMOD_Studio_System_Create", "FMOD5_System_GetVersion",
                "FMOD5_System_GetCPUUsage", "FMOD5_System_GetCPUUsageEx", "FMOD_Studio_System_GetCPUUsage",
                "FMOD_Studio_EventDescription_GetMinimumDistance", "FMOD_Studio_EventDescription_GetMaximumDistance",
                "FMOD_Studio_EventDescription_HasCue", "FMOD_Studio_EventInstance_TriggerCue",
                "FMOD5_System_SetAdvancedSettings", "FMOD5_System_GetAdvancedSettings",
                "FMOD_Studio_EventDescription_GetParameterDescriptionByIndex", "FMOD_Studio_EventDescription_GetParameterDescriptionByName",
                "FMOD_Studio_EventDescription_GetParameterDescriptionByID", "FMOD_Studio_System_GetParameterDescriptionByName",
                "FMOD_Studio_System_GetParameterDescriptionByID", "FMOD_Studio_System_GetParameterDescriptionList",
                "FMOD_Studio_System_Update" };
            var shim = new ModuleReference("pbxfmod");
            fasm.MainModule.ModuleReferences.Add(shim);
            var done = new HashSet<string>();
            foreach (var t in fasm.MainModule.GetTypes())
                foreach (var pm in t.Methods.Where(x => x.IsPInvokeImpl && x.PInvokeInfo != null))
                {
                    string entry = string.IsNullOrEmpty(pm.PInvokeInfo.EntryPoint) ? pm.Name : pm.PInvokeInfo.EntryPoint;
                    if (!adapted.Contains(entry)) continue;
                    pm.PInvokeInfo.EntryPoint = "pbx_" + entry;
                    pm.PInvokeInfo.Module = shim;
                    done.Add(entry);
                }
            Console.WriteLine($"hooks: FMOD 2.01 -> 2.03 adapter ({done.Count} of {adapted.Count} entry points)");
            foreach (var miss in adapted.Except(done)) Console.WriteLine("  not found: " + miss);
            fasm.Write(fpath);
            Console.WriteLine("hooks: FMOD bank folder -> Bridge.BankFolder");
        }
    }

    // Each ret becomes "call target; ret", so branches that targeted the ret run the call too.
    static int AtEveryRet(MethodDefinition m, MethodReference target)
    {
        var il = m.Body.GetILProcessor();
        int c = 0;
        foreach (var r in m.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret).ToList())
        {
            r.OpCode = OpCodes.Call;
            r.Operand = target;
            il.InsertAfter(r, il.Create(OpCodes.Ret));
            c++;
        }
        return c;
    }
}
