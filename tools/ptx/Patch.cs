using Mono.Cecil;
using Mono.Cecil.Cil;

// Stages the assembly set il2cpp is given, and neutralises what has no Android counterpart yet.
//
// Composition is the one Unity itself uses: unityaot class library, the Android player's UnityEngine
// modules, and the depot only for what those two do not provide.
static class Patch
{
    static readonly string[] StubAssemblies = { "AK.Wwise.Unity.API.dll", "com.rlabrecque.steamworks.net.dll" };

    public static void Run(string depotManaged, string bcl, string androidManaged, string outDir, string extraDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(outDir, "*.dll")) File.Delete(f);
        var pool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(depotManaged, "*.dll")) pool[Path.GetFileName(f)] = f;
        foreach (var f in Directory.GetFiles(Path.Combine(bcl, "Facades"), "*.dll")) pool[Path.GetFileName(f)] = f;
        foreach (var f in Directory.GetFiles(bcl, "*.dll")) pool[Path.GetFileName(f)] = f;
        foreach (var f in Directory.GetFiles(androidManaged, "*.dll")) pool[Path.GetFileName(f)] = f;

        // Roots: the game side. Everything else is pulled in by reference.
        var roots = Directory.GetFiles(depotManaged, "*.dll").Select(Path.GetFileName)
            .Where(n => !n.StartsWith("System") && !n.StartsWith("Mono.") && n != "mscorlib.dll" && n != "netstandard.dll")
            .Concat(Directory.GetFiles(androidManaged, "UnityEngine*.dll").Select(Path.GetFileName))
            .Concat(new[] { "mscorlib.dll", "System.dll", "System.Core.dll" })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var q = new Queue<string>(roots);
        while (q.Count > 0)
        {
            var n = q.Dequeue();
            if (!seen.Add(n)) continue;
            if (!pool.TryGetValue(n, out var p)) { Console.WriteLine("missing reference " + n); continue; }
            using var m = ModuleDefinition.ReadModule(p);
            foreach (var r in m.AssemblyReferences) q.Enqueue(r.Name + ".dll");
        }
        foreach (var n in seen.Where(pool.ContainsKey)) File.Copy(pool[n], Path.Combine(outDir, n), true);
        foreach (var f in Directory.GetFiles(extraDir, "*.dll")) File.Copy(f, Path.Combine(outDir, Path.GetFileName(f)), true);
        Console.WriteLine($"staged {Directory.GetFiles(outDir, "*.dll").Length} assemblies");

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(outDir);
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false, InMemory = true };

        foreach (var n in StubAssemblies)
        {
            var path = Path.Combine(outDir, n);
            var asm = AssemblyDefinition.ReadAssembly(path, rp);
            int c = 0;
            foreach (var t in asm.MainModule.GetTypes())
                foreach (var me in t.Methods)
                    if (me.IsPInvokeImpl) { StubMethod(me); c++; }
            foreach (var r in asm.MainModule.ModuleReferences.ToList()) asm.MainModule.ModuleReferences.Remove(r);
            if (n.StartsWith("AK.")) RedirectAudio(asm, rp, outDir);
            asm.Write(path);
            Console.WriteLine($"{n}: {c} P/Invokes stubbed");
        }

        // Saves and achievements: take the game's own Local branch, which needs no Steam ID.
        {
            var path = Path.Combine(outDir, "Mortal.Core.dll");
            var asm = AssemblyDefinition.ReadAssembly(path, rp);
            var t = asm.MainModule.GetType("Mortal.Core.SaveSystem");
            int c = 0;
            foreach (var me in t.Methods.Where(x => x.HasBody))
            {
                var il = me.Body.GetILProcessor();
                foreach (var ins in me.Body.Instructions.ToList())
                    if (ins.OpCode == OpCodes.Ldfld && ins.Operand is FieldReference f && f.Name == "_savePlatformType")
                    {
                        il.InsertBefore(ins, il.Create(OpCodes.Pop));
                        il.Replace(ins, il.Create(OpCodes.Ldc_I4_0));
                        c++;
                    }
            }
            asm.Write(path);
            Console.WriteLine($"SaveSystem: {c} platform checks pinned to Local");
        }

        // Steam is never present here, so neither is a Steam Deck.
        {
            var path = Path.Combine(outDir, "Mortal.Core.dll");
            var asm = AssemblyDefinition.ReadAssembly(path, rp);
            var me = asm.MainModule.GetType("Mortal.Core.SystemSettings").Methods.First(x => x.Name == "IsRunningOnSteamDeck");
            me.Body = new MethodBody(me);
            var il = me.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ret));

            // The default is the last list entry, 960x540. Thor's panel is 1920x1080 (index 3).
            // The key is renamed so a value saved by an earlier build is not picked up.
            var settings = asm.MainModule.GetType("Mortal.Core.SystemSettings");
            var gri = settings.Methods.First(x => x.Name == "GetResolutionIndex");
            var ins = gri.Body.Instructions;
            var pat = ins.First(i => i.OpCode == OpCodes.Ldlen);
            var load = pat.Previous;
            var gil = gri.Body.GetILProcessor();
            var sub = ins.First(i => i.OpCode == OpCodes.Sub);
            for (var cur = load.Next; cur != sub; ) { var nx = cur.Next; gil.Remove(cur); cur = nx; }
            gil.Replace(sub, gil.Create(OpCodes.Ldc_I4_3));
            gil.Replace(load, gil.Create(OpCodes.Nop));
            foreach (var i in settings.Methods.First(x => x.Name == ".cctor").Body.Instructions)
                if (i.OpCode == OpCodes.Ldstr && (string)i.Operand == "System_ResolutionIndex") i.Operand = "System_ResolutionIndex_Android";

            // Every save write and delete tells the Steam Cloud uploader. Each ret becomes the call,
            // followed by a new ret, so branches that targeted the ret run the call too.
            var saved = asm.MainModule.ImportReference(AssemblyDefinition.ReadAssembly(Path.Combine(outDir, "LomAndroid.dll"), rp)
                .MainModule.GetType("LomAndroid.LomCloud").Methods.First(x => x.Name == "Saved"));
            int hooks = 0;
            foreach (var m in asm.MainModule.GetType("Mortal.Core.SaveSystem").Methods.Where(x => x.HasBody && x.Name is "ExecuteSaveData" or "ExecuteSaveJsonData" or "DeleteSaveData"))
            {
                var sil = m.Body.GetILProcessor();
                foreach (var r in m.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret).ToList())
                {
                    r.OpCode = OpCodes.Call;
                    r.Operand = saved;
                    sil.InsertAfter(r, sil.Create(OpCodes.Ret));
                    hooks++;
                }
            }
            // Achievements go to Steam through the Java side, since the Steam branch above is pinned
            // off; the library load at start starts playtime and offers achievements already held.
            var lomCloud = AssemblyDefinition.ReadAssembly(Path.Combine(outDir, "LomAndroid.dll"), rp).MainModule.GetType("LomAndroid.LomCloud");
            var ach = asm.MainModule.GetType("Mortal.Core.SaveSystem").Methods.First(x => x.Name == "SetAchivement");
            var ail = ach.Body.GetILProcessor();
            var first = ach.Body.Instructions[0];
            ail.InsertBefore(first, ail.Create(OpCodes.Ldarg_1));
            ail.InsertBefore(first, ail.Create(OpCodes.Call, asm.MainModule.ImportReference(lomCloud.Methods.First(x => x.Name == "Achievement"))));
            var libLoad = asm.MainModule.GetType("Mortal.Core.LibrarySystem").Methods.First(x => x.Name == "Load");
            var loaded = asm.MainModule.ImportReference(lomCloud.Methods.First(x => x.Name == "LibraryLoaded"));
            var lil = libLoad.Body.GetILProcessor();
            foreach (var r in libLoad.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret).ToList())
            {
                r.OpCode = OpCodes.Ldarg_0;
                r.Operand = null;
                var call = lil.Create(OpCodes.Call, loaded);
                lil.InsertAfter(r, call);
                lil.InsertAfter(call, lil.Create(OpCodes.Ret));
            }
            asm.Write(path);
            Console.WriteLine($"SystemSettings: IsRunningOnSteamDeck false, default resolution 1920x1080; SaveSystem: {hooks} cloud hooks, achievements to Steam");
        }

        // The native sound engine stays down: LomAudio plays the events instead.
        {
            var path = Path.Combine(outDir, "AK.Wwise.Unity.MonoBehaviour.dll");
            var asm = AssemblyDefinition.ReadAssembly(path, rp);
            var t = asm.MainModule.GetType("AkSoundEngineController");
            foreach (var me in t.Methods.Where(x => x.Name is "Init" or "Terminate" or "LateUpdate" or "ActivateAudio" or "OnApplicationPause" or "OnApplicationFocus" && x.HasBody))
            {
                me.Body = new MethodBody(me);
                me.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
            }
            asm.Write(path);
            Console.WriteLine("AkSoundEngineController: lifecycle disabled");
        }

        ReportSteam(outDir);
    }

    // Every Wwise member the game side touches, with its callers.
    public static void AkUse(string dir)
    {
        var skip = new[] { "AK.Wwise", "Ak.Wwise", "Mscorlib" };
        var counts = new Dictionary<string, (int n, HashSet<string> who)>();
        foreach (var f in Directory.GetFiles(dir, "*.dll"))
        {
            var n = Path.GetFileName(f);
            if (n.StartsWith("AK.") || n.StartsWith("Ak.") || n.StartsWith("System") || n.StartsWith("UnityEngine") || n.StartsWith("Unity.") || n == "mscorlib.dll" || n == "netstandard.dll") continue;
            using var m = ModuleDefinition.ReadModule(f);
            foreach (var t in m.GetTypes())
                foreach (var me in t.Methods.Where(x => x.HasBody))
                    foreach (var ins in me.Body.Instructions)
                    {
                        if (ins.Operand is not MemberReference r) continue;
                        var dt = r.DeclaringType?.FullName ?? "";
                        if (!(dt.StartsWith("Ak") || dt.StartsWith("AK.") || dt == "AKRESULT")) continue;
                        var k = dt + "::" + r.Name;
                        if (!counts.TryGetValue(k, out var v)) v = (0, new HashSet<string>());
                        v.who.Add(t.Name + "." + me.Name);
                        counts[k] = (v.n + 1, v.who);
                    }
        }
        foreach (var kv in counts.OrderByDescending(x => x.Value.n))
            Console.WriteLine($"{kv.Value.n,4} {kv.Key}  <- {string.Join(" ", kv.Value.who.Take(4))}");
    }

    // The game reaches Wwise only through these by-name calls; they are all sent to LomAudio.
    static void RedirectAudio(AssemblyDefinition asm, ReaderParameters rp, string dir)
    {
        var lom = AssemblyDefinition.ReadAssembly(Path.Combine(dir, "LomAndroid.dll"), rp).MainModule.GetType("LomAndroid.LomAudio");
        var post = asm.MainModule.ImportReference(lom.Methods.First(x => x.Name == "PostEvent"));
        var set = asm.MainModule.ImportReference(lom.Methods.First(x => x.Name == "SetRtpc"));
        var get = asm.MainModule.ImportReference(lom.Methods.First(x => x.Name == "GetRtpc"));
        var t = asm.MainModule.GetType("AkSoundEngine");
        int c = 0;
        foreach (var me in t.Methods.Where(x => x.Parameters.Count > 0 && x.Parameters[0].ParameterType.MetadataType == MetadataType.String))
        {
            var b = new MethodBody(me);
            var il = b.GetILProcessor();
            if (me.Name == "PostEvent")
            {
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, post));
            }
            else if (me.Name == "SetRTPCValue" && me.Parameters.Count > 1 && me.Parameters[1].ParameterType.MetadataType == MetadataType.Single)
            {
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldarg_1));
                var d = me.Parameters.FirstOrDefault(p => p.Name == "in_uValueChangeDuration");
                il.Append(d != null ? il.Create(OpCodes.Ldarg, d) : il.Create(OpCodes.Ldc_I4_0));
                il.Append(il.Create(OpCodes.Call, set));
                il.Append(il.Create(OpCodes.Ldc_I4_1));
            }
            else if (me.Name == "GetRTPCValue")
            {
                var o = me.Parameters.First(p => p.ParameterType.IsByReference && p.ParameterType.GetElementType().MetadataType == MetadataType.Single);
                il.Append(il.Create(OpCodes.Ldarg, o));
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, get));
                il.Append(il.Create(OpCodes.Stind_R4));
                il.Append(il.Create(OpCodes.Ldc_I4_1));
            }
            else continue;
            il.Append(il.Create(OpCodes.Ret));
            me.Body = b;
            c++;
        }
        Console.WriteLine($"AkSoundEngine: {c} by-name calls sent to LomAudio");
    }

    public static void StubMethodPublic(MethodDefinition m) => StubMethod(m);
    static void StubMethod(MethodDefinition m)
    {
        m.PInvokeInfo = null;
        m.IsPInvokeImpl = false;
        m.ImplAttributes = 0;
        m.IsPreserveSig = false;
        m.MethodReturnType.MarshalInfo = null;
        m.MethodReturnType.HasFieldMarshal = false;
        foreach (var p in m.Parameters) { p.MarshalInfo = null; p.HasFieldMarshal = false; }
        var body = new MethodBody(m);
        m.Body = body;
        var il = body.GetILProcessor();
        var rt = m.ReturnType;
        if (rt.MetadataType != MetadataType.Void)
        {
            switch (rt.MetadataType)
            {
                case MetadataType.Boolean: case MetadataType.Byte: case MetadataType.SByte: case MetadataType.Int16: case MetadataType.UInt16:
                case MetadataType.Int32: case MetadataType.UInt32: case MetadataType.Char:
                    il.Append(il.Create(OpCodes.Ldc_I4_0)); break;
                case MetadataType.Int64: case MetadataType.UInt64:
                    il.Append(il.Create(OpCodes.Ldc_I8, 0L)); break;
                case MetadataType.Single:
                    il.Append(il.Create(OpCodes.Ldc_R4, 0f)); break;
                case MetadataType.Double:
                    il.Append(il.Create(OpCodes.Ldc_R8, 0d)); break;
                case MetadataType.IntPtr: case MetadataType.UIntPtr: case MetadataType.Pointer:
                    il.Append(il.Create(OpCodes.Ldc_I4_0)); il.Append(il.Create(OpCodes.Conv_I)); break;
                default:
                    if (rt.IsValueType)
                    {
                        var v = new VariableDefinition(rt);
                        body.Variables.Add(v);
                        body.InitLocals = true;
                        il.Append(il.Create(OpCodes.Ldloc, v));
                    }
                    else il.Append(il.Create(OpCodes.Ldnull));
                    break;
            }
        }
        il.Append(il.Create(OpCodes.Ret));
    }

    static void ReportSteam(string dir)
    {
        foreach (var f in Directory.GetFiles(dir, "Mortal*.dll").Concat(new[] { Path.Combine(dir, "Assembly-CSharp.dll"), Path.Combine(dir, "OBB.Framework.dll") }))
        {
            using var m = ModuleDefinition.ReadModule(f);
            foreach (var t in m.GetTypes())
                foreach (var me in t.Methods.Where(x => x.HasBody))
                {
                    var hits = me.Body.Instructions.Where(i => i.Operand is MemberReference r && r.FullName.Contains("Steamworks.") && !r.FullName.Contains("CSteamID")).Select(i => ((MemberReference)i.Operand).Name).Distinct().ToList();
                    if (hits.Count > 0 && t.Name != "SteamManager") Console.WriteLine($"steam use: {t.FullName}.{me.Name}: {string.Join(",", hits)}");
                }
        }
    }
}
