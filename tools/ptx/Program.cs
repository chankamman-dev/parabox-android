using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args[0] == "structs") Structs.Run(args[1]);
else if (args[0] == "pinv") PInv.Run(args[1], args.Length > 2 && args[2] == "sig");
else if (args[0] == "refcheck") RefCheck.Run(args[1], args[2]);
else if (args[0] == "pstage") PStage.Run(args[1], args[2], args[3], args[4], args[5]);
else if (args[0] == "rawmb") RawMb.Run(args[1], long.Parse(args[2]));
else if (args[0] == "pdata") PData.Run(args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]);
else if (args[0] == "respath") ResPath.Run(args[1], args[2], args[3]);
else if (args[0] == "sercmp") SerCmp.Run(args[1], args[2], args[3]);
else if (args[0] == "scripts") Scripts.Run(args[1], args[2]);
else if (args[0] == "treediff") TreeDiff.Run(args[1], args[2], args[3], args[4]);
else if (args[0] == "typecmp") TypeCmp.Run(args[1], args[2]);
else if (args[0] == "call-sites") CallSites.Run(args[1], args[2]);
else if (args[0] == "psr-scan") PsrScan.Run(args[1], args[2], args[3]);
else if (args[0] == "il-dump") IlDump.Run(args[1], args[2]);
else if (args[0] == "audio-events") AudioEvents.Write(args[1], args[2]);
else if (args[0] == "hirc-gains") Hirc.WriteGains(args[1], args[2]);
else if (args[0] == "hirc") Hirc.Dump(args[1], args.Length > 2 ? args[2] : null);
else if (args[0] == "mats") Materials.Run(args[1], args[2], args[3], args[4]);
else if (args[0] == "inspect") Inspect.Run(args[1], int.Parse(args[2]), args[3], args[4]);
else if (args[0] == "video-android") VideoShader.Add(args[1], args[2], args[3]);
else if (args[0] == "rt-pack") Retarget.Pack(args[1], args[2]);
else if (args[0] == "audio") Audio.Extract(args[1], args[2]);
else if (args[0] == "audio-pcm") Audio.Pcm(args[1], args[2]);
else if (args[0] == "akuse") Patch.AkUse(args[1]);
else if (args[0] == "rt-data") Retarget.Data(args[1], args[2], args[3], args[4], args[5], args[6]);
else if (args[0] == "rt-bundles") Retarget.Bundles(args[1], args[2], args[3], args[4], args[5]);
else if (args[0] == "rt-catalog") Retarget.Catalog(args[1], args[2], args[3]);
else if (args[0] == "stage") Patch.Run(args[1], args[2], args[3], args[4], args[5]);
else if (args[0] == "shgold") Converter.Gold(args[1], args[2], args[3], args[4], args[5]);
else if (args[0] == "shdump") Shaders.Dump(args[1], args[2], args[3], args[4]);
else if (args[0] == "shprobe") ShProbe.Run(args[1], args[2], int.Parse(args[3]), args[4], args[5]);
else if (args[0] == "assets") Assets.Run(args[1], args[2]);
else if (args[0] == "mod") Mod.Run(args[1]);
else Il.Run(args[1]);

static class Mod
{
    public static void Run(string dir)
    {
        foreach (var f in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories))
        {
            ModuleDefinition m;
            try { m = ModuleDefinition.ReadModule(f); } catch { continue; }
            var c = new Dictionary<string, int>(); int methods = 0; long il = 0;
            var calls = new Dictionary<string, int>();
            var keys = new[] { "System.Reflection.Emit", "HarmonyLib.Harmony::Patch", "HarmonyLib.Harmony::PatchAll", "HarmonyLib.Traverse", "HarmonyLib.AccessTools", "System.Net.", "UnityEngine.GUI", "IMGUI", "System.Windows.Forms", "Microsoft.Win32", "DllImport" };
            foreach (var t in m.GetTypes())
            {
                foreach (var a in t.CustomAttributes) if (a.AttributeType.Name.StartsWith("Harmony")) c[a.AttributeType.Name] = c.GetValueOrDefault(a.AttributeType.Name) + 1;
                foreach (var me in t.Methods)
                {
                    methods++;
                    foreach (var a in me.CustomAttributes) if (a.AttributeType.Name.StartsWith("Harmony")) c[a.AttributeType.Name] = c.GetValueOrDefault(a.AttributeType.Name) + 1;
                    if (me.IsPInvokeImpl) calls["DllImport:" + me.PInvokeInfo.Module.Name] = calls.GetValueOrDefault("DllImport:" + me.PInvokeInfo.Module.Name) + 1;
                    if (me.Name is "Prefix" or "Postfix" or "Transpiler" or "Finalizer") c["named:" + me.Name] = c.GetValueOrDefault("named:" + me.Name) + 1;
                    if (!me.HasBody) continue;
                    il += me.Body.CodeSize;
                    foreach (var ins in me.Body.Instructions)
                        if (ins.Operand is MemberReference r)
                            foreach (var k in keys)
                                if (r.FullName.Contains(k)) calls[k] = calls.GetValueOrDefault(k) + 1;
                }
            }
            Console.WriteLine($"## {Path.GetFileName(f)} methods={methods} il={il / 1024}KB refs=[{string.Join(",", m.AssemblyReferences.Select(a => a.Name).Take(40))}]");
            Console.WriteLine("   attrs: " + string.Join(", ", c.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")));
            Console.WriteLine("   uses:  " + string.Join(", ", calls.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")));
        }
    }
}

static class Assets
{
    class Sh { public string name; public HashSet<int> plats = new(); public long blob; public int files; }
    static Dictionary<string, Sh> shaders = new();
    static Dictionary<string, (int n, long b)> tex = new();
    static Dictionary<int, int> cls = new();
    static void TF(string k, long b) { tex.TryGetValue(k, out var v); tex[k] = (v.n + 1, v.b + b); }

    public static void Run(string data, string tpk)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(tpk);
        var loose = Directory.GetFiles(data).Where(f =>
        {
            var n = Path.GetFileName(f);
            return n == "globalgamemanagers" || n == "globalgamemanagers.assets" || n == "resources.assets"
                || (n.StartsWith("level") && !n.EndsWith(".resS"))
                || (n.StartsWith("sharedassets") && n.EndsWith(".assets"));
        }).ToList();
        var res = Path.Combine(data, "Resources");
        if (Directory.Exists(res)) loose.AddRange(Directory.GetFiles(res));
        foreach (var f in loose)
        {
            if (f.EndsWith(".resource") || f.EndsWith(".resS")) continue;
            try
            {
                var i = am.LoadAssetsFile(f, false);
                am.LoadClassDatabaseFromPackage(i.file.Metadata.UnityVersion);
                Scan(am, i, Path.GetFileName(f));
            }
            catch (Exception e) { Console.WriteLine("ERR " + f + " " + e.Message); }
            am.UnloadAll();
        }
        var aaDir = Path.Combine(data, "StreamingAssets", "aa");
        var bs = Directory.Exists(aaDir) ? Directory.GetFiles(aaDir, "*.bundle", SearchOption.AllDirectories) : new string[0];
        foreach (var f in bs)
        {
            try
            {
                var b = am.LoadBundleFile(f, true);
                var names = b.file.GetAllFileNames();
                for (int k = 0; k < names.Count; k++)
                {
                    if (names[k].EndsWith(".resS") || names[k].EndsWith(".resource")) continue;
                    var i = am.LoadAssetsFileFromBundle(b, k, false);
                    if (i == null) continue;
                    am.LoadClassDatabaseFromPackage(i.file.Metadata.UnityVersion);
                    Scan(am, i, Path.GetFileName(f));
                }
            }
            catch (Exception e) { Console.WriteLine("ERR " + f + " " + e.Message); }
            am.UnloadAll();
        }
        Console.WriteLine("== class histogram (top)");
        foreach (var kv in cls.OrderByDescending(x => x.Value).Take(25)) Console.WriteLine($"{(AssetClassID)kv.Key} {kv.Value}");
        Console.WriteLine("== textures");
        foreach (var kv in tex.OrderByDescending(x => x.Value.b)) Console.WriteLine($"{kv.Key} n={kv.Value.n} MB={kv.Value.b / 1048576}");
        Console.WriteLine($"== shaders total={shaders.Count}");
        var pc = new Dictionary<string, int>();
        foreach (var s in shaders.Values)
        {
            var k = string.Join(",", s.plats.OrderBy(x => x));
            pc[k] = pc.GetValueOrDefault(k) + 1;
        }
        foreach (var kv in pc) Console.WriteLine($"platforms[{kv.Key}] => {kv.Value} shaders");
        foreach (var s in shaders.Values.OrderByDescending(x => x.blob))
            Console.WriteLine($"{s.blob / 1024,8}KB x{s.files} [{string.Join(",", s.plats.OrderBy(x => x))}] {s.name}");
    }

    static void Scan(AssetsManager am, AssetsFileInstance i, string label)
    {
        foreach (var a in i.file.AssetInfos)
        {
            cls[a.TypeId] = cls.GetValueOrDefault(a.TypeId) + 1;
            try
            {
                if (a.TypeId == (int)AssetClassID.Shader)
                {
                    var b = am.GetBaseField(i, a);
                    var nm = b["m_ParsedForm.m_Name"].AsString;
                    if (!shaders.TryGetValue(nm, out var s)) { s = new Sh { name = nm }; shaders[nm] = s; }
                    foreach (var p in b["platforms.Array"].Children) s.plats.Add(p.AsInt);
                    var blob = b["compressedBlob.Array"];
                    long len = blob.IsDummy ? 0 : (blob.AsByteArray?.Length ?? blob.Children?.Count ?? 0);
                    s.blob = Math.Max(s.blob, len);
                    s.files++;
                }
                else if (a.TypeId == (int)AssetClassID.Texture2D)
                {
                    var b = am.GetBaseField(i, a);
                    TF("fmt" + b["m_TextureFormat"].AsInt, b["m_CompleteImageSize"].AsLong);
                }
                else if (a.TypeId == (int)AssetClassID.BuildSettings)
                {
                    var b = am.GetBaseField(i, a);
                    Console.WriteLine("BuildSettings version=" + b["m_Version"].AsString
                        + " gfx=" + string.Join(",", b["m_GraphicsAPIs.Array"].Children.Select(c => c.AsInt))
                        + " scenes=" + b["scenes.Array"].Children.Count);
                }
                else if (a.TypeId == 329)
                {
                    var b = am.GetBaseField(i, a);
                    Console.WriteLine($"VideoClip {b["m_Name"].AsString} orig={b["m_OriginalPath"].AsString} {b["Width"].AsInt}x{b["Height"].AsInt} fps={b["m_FrameRate"].AsDouble} size={b["m_ExternalResources.m_Size"].AsLong} res={b["m_ExternalResources.m_Source"].AsString}");
                }
            }
            catch (Exception e) { Console.WriteLine($"fieldErr {label} type={a.TypeId} {e.Message}"); }
        }
    }
}

static class Il
{
    static readonly string[] Pat =
    {
        "System.Reflection.Emit", "LambdaExpression::Compile", "Expression`1::Compile", "DynamicMethod",
        "Microsoft.CSharp", "System.Diagnostics.Process", "Microsoft.Win32",
        "System.Runtime.Serialization.Formatters.Binary", "Assembly::Load", "MakeGenericType", "MakeGenericMethod",
        "System.Windows", "System.Drawing", "System.IO.File::Replace", "FileSystemWatcher",
        "SteamAPI::Init", "SteamAPI::RestartAppIfNecessary", "MoonSharp",
        "Application::get_platform", "Screen::SetResolution", "Application::Quit",
        "Application::get_dataPath", "Application::get_streamingAssetsPath", "Application::get_persistentDataPath",
        "Environment::GetFolderPath", "AssetBundle::LoadFromFile", "UnityEngine.Input::", "UnityEngine.InputSystem",
        "UnityEngine.Video.VideoPlayer"
    };

    public static void Run(string dir)
    {
        var game = Directory.GetFiles(dir, "*.dll").Where(f =>
        {
            var n = Path.GetFileName(f);
            return !(n == "mscorlib.dll" || n == "netstandard.dll" || n.StartsWith("System") || n.StartsWith("Mono.")
                || (n.StartsWith("UnityEngine.") && n != "UnityEngine.UI.dll") || n == "UnityEngine.dll");
        });
        long totalIl = 0;
        foreach (var f in game.OrderBy(x => x))
        {
            using var m = ModuleDefinition.ReadModule(f);
            var hits = new Dictionary<string, HashSet<string>>();
            int methods = 0; long il = 0; var pinv = new HashSet<string>();
            foreach (var t in m.GetTypes())
                foreach (var me in t.Methods)
                {
                    methods++;
                    if (me.IsPInvokeImpl && me.PInvokeInfo != null) pinv.Add(me.PInvokeInfo.Module.Name);
                    if (!me.HasBody) continue;
                    il += me.Body.CodeSize;
                    foreach (var ins in me.Body.Instructions)
                        if (ins.Operand is MemberReference r)
                        {
                            var s = r.FullName;
                            foreach (var p in Pat)
                                if (s.Contains(p))
                                {
                                    if (!hits.TryGetValue(p, out var h)) hits[p] = h = new();
                                    h.Add(t.FullName + "." + me.Name);
                                }
                        }
                }
            totalIl += il;
            var refs = string.Join(",", m.AssemblyReferences.Select(a => a.Name).Where(n => n.StartsWith("System") || n == "netstandard" || n == "mscorlib"));
            Console.WriteLine($"## {Path.GetFileName(f)} methods={methods} il={il / 1024}KB pinvoke=[{string.Join(",", pinv)}] bcl=[{refs}]");
            foreach (var kv in hits.OrderBy(k => k.Key))
                Console.WriteLine($"   {kv.Key} ({kv.Value.Count}): {string.Join(" | ", kv.Value.Take(3))}");
        }
        Console.WriteLine("total IL KB " + totalIl / 1024);
    }
}
