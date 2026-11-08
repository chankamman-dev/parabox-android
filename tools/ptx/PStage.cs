using Mono.Cecil;
using Mono.Cecil.Cil;

// Stages the assembly set il2cpp is given for Patrick's Parabox: unityaot class library, the Android
// player's UnityEngine modules, the depot's game assemblies, and the extra folder last (the Android build
// of Input System 1.5.1 and ParaboxAndroid.dll). Steamworks.NET's P/Invokes become empty stubs, so the
// game takes its own "Steam not running" path.
static class PStage
{
    public static void Run(string depotManaged, string bcl, string androidManaged, string outDir, string extraDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(outDir, "*.dll")) File.Delete(f);
        var pool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(depotManaged, "*.dll")) pool[Path.GetFileName(f)] = f;
        foreach (var f in Directory.GetFiles(Path.Combine(bcl, "Facades"), "*.dll")) pool[Path.GetFileName(f)] = f;
        foreach (var f in Directory.GetFiles(bcl, "*.dll")) pool[Path.GetFileName(f)] = f;
        foreach (var f in Directory.GetFiles(androidManaged, "*.dll")) pool[Path.GetFileName(f)] = f;
        foreach (var f in Directory.GetFiles(extraDir, "*.dll")) pool[Path.GetFileName(f)] = f;

        var roots = Directory.GetFiles(depotManaged, "*.dll").Select(Path.GetFileName)
            .Where(n => !n.StartsWith("System") && !n.StartsWith("Mono.") && n != "mscorlib.dll" && n != "netstandard.dll")
            .Concat(Directory.GetFiles(androidManaged, "UnityEngine*.dll").Select(Path.GetFileName))
            .Concat(Directory.GetFiles(extraDir, "*.dll").Select(Path.GetFileName))
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
        Console.WriteLine($"staged {Directory.GetFiles(outDir, "*.dll").Length} assemblies");

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(outDir);
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false, InMemory = true };

        // Steamworks.NET lives in firstpass.
        {
            var path = Path.Combine(outDir, "Assembly-CSharp-firstpass.dll");
            var asm = AssemblyDefinition.ReadAssembly(path, rp);
            int c = 0;
            foreach (var t in asm.MainModule.GetTypes())
                foreach (var me in t.Methods)
                    if (me.IsPInvokeImpl && me.PInvokeInfo.Module.Name.StartsWith("steam") || me.IsPInvokeImpl && me.PInvokeInfo.Module.Name.StartsWith("sdkencrypted"))
                    { Patch.StubMethodPublic(me); c++; }
            foreach (var r in asm.MainModule.ModuleReferences.Where(r => !r.Name.StartsWith("fmod")).ToList()) asm.MainModule.ModuleReferences.Remove(r);
            // The game assumes Steam is up: SteamSquare.Init calls SteamUtils.GetAppID unguarded, and
            // Steamworks.NET throws "Steamworks is not initialized" from these checks. With them empty,
            // every call reaches its stub and returns zero/false (overlay off -> store page via OpenURL).
            foreach (var me in asm.MainModule.GetType("Steamworks.InteropHelp").Methods.Where(x => x.Name.StartsWith("TestIfAvailable")))
            {
                me.Body = new MethodBody(me);
                me.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
            }
            asm.Write(path);
            Console.WriteLine($"firstpass: {c} Steamworks P/Invokes stubbed");
        }

        // The game was compiled against Input System 1.2.0; 1.5.1 changed a few signatures (Mouse.scroll
        // now returns DeltaControl, a Vector2Control subclass). Calls that no longer resolve are pointed
        // at the method of the same name and arity, whose result fits the old use.
        {
            var path = Path.Combine(outDir, "Assembly-CSharp.dll");
            var asm = AssemblyDefinition.ReadAssembly(path, rp);
            int c = 0;
            foreach (var t in asm.MainModule.GetTypes())
                foreach (var me in t.Methods.Where(x => x.HasBody))
                    foreach (var ins in me.Body.Instructions)
                    {
                        if (ins.Operand is not MethodReference mr || mr.DeclaringType.Scope.Name != "Unity.InputSystem") continue;
                        MethodDefinition res = null;
                        try { res = mr.Resolve(); } catch { }
                        if (res != null) continue;
                        var td = mr.DeclaringType.Resolve();
                        var cand = td?.Methods.Where(x => x.Name == mr.Name && x.Parameters.Count == mr.Parameters.Count && x.HasThis == mr.HasThis).ToList();
                        if (cand == null || cand.Count != 1) throw new Exception("cannot remap " + mr.FullName);
                        ins.Operand = asm.MainModule.ImportReference(cand[0]);
                        Console.WriteLine($"remapped {mr.FullName} -> {cand[0].FullName} in {t.Name}::{me.Name}");
                        c++;
                    }
            asm.Write(path);
        }

        if (File.Exists(Path.Combine(outDir, "ParaboxAndroid.dll"))) Hooks.Apply(outDir, rp);
    }
}
