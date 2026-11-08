using AssetsTools.NET;
using AssetsTools.NET.Extra;

// Patrick's Parabox player data (Unity 2020.3.30f1, Windows) -> data an Android 2020.3.49f1 player loads.
//
// Of the 33 engine classes in the data, 30 have the same layout in both versions (same type hash as
// Legend of Mortal's 2020.3.49 data). GraphicsSettings, PlayerSettings and BuildSettings changed by a
// field or two; they are re-serialized with the 2020.3.49 trees from classdata.tpk. Input System 1.2.0's
// InputSettings and PlayerInput are rewritten for the 1.5.1 build used on Android (see HANDOFF.md).
static class PData
{
    const int Android = 13;
    const int Gles3 = 11;
    const string NewVer = "2020.3.49f1";
    static readonly int[] Changed = { 30, 129, 141 };

    public static void Run(string src, string dst, string tmp, string cli, string tpk, string androidRes,
        string newGgm, string managedOld, string managedNew)
    {
        Directory.CreateDirectory(Path.Combine(dst, "Resources"));
        Directory.CreateDirectory(tmp);
        var c = new Converter { Tmp = tmp, Cli = cli };

        // Type hashes the 2020.3.49 player writes for the changed classes.
        var newHash = new Dictionary<int, Hash128>();
        using (var fs = File.OpenRead(newGgm))
        {
            var af = new AssetsFile();
            af.Read(new AssetsFileReader(fs));
            foreach (var t in af.Metadata.TypeTreeTypes) if (Changed.Contains(t.TypeId)) newHash[t.TypeId] = t.TypeHash;
        }

        var amNew = new AssetsManager();
        amNew.LoadClassPackage(tpk);
        amNew.LoadClassDatabaseFromPackage(NewVer);
    

        var names = Directory.GetFiles(src).Select(Path.GetFileName).Where(n =>
            n == "globalgamemanagers" || n == "globalgamemanagers.assets" || n == "resources.assets"
            || (n.StartsWith("level") && !n.EndsWith(".resS"))
            || (n.StartsWith("sharedassets") && n.EndsWith(".assets"))).OrderBy(x => x).ToList();
        names.Add(Path.Combine("Resources", "unity_builtin_extra"));
        foreach (var n in names)
        {
            var am = new AssetsManager();
            am.LoadClassPackage(tpk);
        
            var i = am.LoadAssetsFile(Path.Combine(src, n), true);
            am.LoadClassDatabaseFromPackage(i.file.Metadata.UnityVersion);
            Retarget.Convert(c, am, i, n);

            foreach (var a in i.file.AssetInfos.ToList())
            {
                if (Changed.Contains(a.TypeId))
                {
                    var old = am.GetBaseField(i, a);
                    var tf = new AssetTypeTemplateField();
                    tf.FromClassDatabase(amNew.ClassDatabase, amNew.ClassDatabase.FindAssetClassByID(a.TypeId));
                    var nb = ValueBuilder.DefaultValueFieldFromTemplate(tf);
                    var missing = new List<string>();
                    CopyInto(old, nb, "", missing);
                    if (a.TypeId == (int)AssetClassID.BuildSettings)
                    {
                        nb["m_Version"].AsString = NewVer;
                        var arr = nb["m_GraphicsAPIs.Array"];
                        arr.Children.Clear();
                        var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
                        e.AsInt = Gles3;
                        arr.Children.Add(e);
                        arr.AsArray = new AssetTypeArrayInfo(1);
                    }
                    a.SetNewData(nb);
                    Console.WriteLine($"{n}: {(AssetClassID)a.TypeId} re-serialized for {NewVer}; new fields at default: {string.Join(", ", missing)}");
                }
                else if (a.TypeId == (int)AssetClassID.MonoBehaviour)
                {
                    var head = am.GetBaseField(i, a, AssetReadFlags.SkipMonoBehaviourFields);
                    var sc = am.GetExtAsset(i, head["m_Script"]);
                    if (sc.baseField == null) continue;
                    var cls = sc.baseField["m_ClassName"].AsString;
                    if (sc.baseField["m_AssemblyName"].AsString != "Unity.InputSystem.dll" || cls is not ("InputSettings" or "PlayerInput")) continue;
                    // Script data has no type tree here, so the two layout changes are made on the bytes.
                    // Every script bool is padded to 4 bytes. After the MonoBehaviour header (GameObject
                    // PPtr, m_Enabled, m_Script PPtr, m_Name) come the script's own fields.
                    var raw = RawMb.Bytes(i, a).ToList();
                    int nameLen = BitConverter.ToInt32(raw.ToArray(), 28);
                    int body = 32 + ((nameLen + 3) & ~3);
                    if (cls == "InputSettings")
                    {
                        // m_SupportedDevices (string[]) must be empty for the fixed offsets below.
                        if (BitConverter.ToInt32(raw.ToArray(), body) != 0) throw new Exception("InputSettings: m_SupportedDevices not empty");
                        // devices, updateMode, maxEventBytes, maxQueuedEvents, compensate(bool) -> filterNoise
                        raw.RemoveRange(body + 20, 4);          // m_FilterNoiseOnCurrent (gone in 1.5.1)
                        raw.AddRange(new byte[4]);              // m_ShortcutKeysConsumeInputs = false (new)
                    }
                    else
                    {
                        // m_Actions (PPtr), m_NotificationBehavior (int), then 1.5.1's m_UIInputModule (PPtr).
                        raw.InsertRange(body + 16, new byte[12]);
                    }
                    a.SetNewData(raw.ToArray());
                    Console.WriteLine($"{n}: {cls} rewritten for Input System 1.5.1 ({a.ByteSize} -> {raw.Count} bytes)");
                }
            }

            i.file.Metadata.TargetPlatform = Android;
            i.file.Metadata.UnityVersion = NewVer;
            foreach (var t in i.file.Metadata.TypeTreeTypes)
                if (newHash.TryGetValue(t.TypeId, out var h)) t.TypeHash = h;
            using (var fs = File.Create(Path.Combine(dst, n)))
            using (var w = new AssetsFileWriter(fs))
                i.file.Write(w);
            am.UnloadAll();
            Console.WriteLine("wrote " + n);
        }
        foreach (var f in Directory.GetFiles(src))
        {
            var n = Path.GetFileName(f);
            if (n.EndsWith(".resS") || n.EndsWith(".resource") || n is "app.info" or "RuntimeInitializeOnLoads.json" or "ScriptingAssemblies.json")
                File.Copy(f, Path.Combine(dst, n), true);
        }
        File.Copy(androidRes, Path.Combine(dst, "unity default resources"), true);
        var boot = File.ReadAllLines(Path.Combine(src, "boot.config")).Where(l => !l.StartsWith("scripting-backend=")).Append("scripting-backend=il2cpp");
        File.WriteAllText(Path.Combine(dst, "boot.config"), string.Join("\n", boot) + "\n");
        File.WriteAllLines(Path.Combine(dst, "..", "shader-report.txt"), c.Log);
        Console.WriteLine($"variants={c.Total} failed={c.Failed} skipped={c.Skipped}");
    }

    // Copies values by field name; fields the destination has and the source lacks keep their defaults.
    static void CopyInto(AssetTypeValueField s, AssetTypeValueField d, string path, List<string> missing)
    {
        var tf = d.TemplateField;
        if (tf.IsArray)
        {
            if (s.Value != null && s.Value.ValueType == AssetValueType.ByteArray) { d.Value = s.Value; return; }
            d.Children.Clear();
            foreach (var sc in s.Children)
            {
                var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(d);
                CopyInto(sc, e, path + "[]", missing);
                d.Children.Add(e);
            }
            d.AsArray = new AssetTypeArrayInfo(d.Children.Count);
            return;
        }
        if (tf.ValueType == AssetValueType.String || (d.Children.Count == 0 && tf.Children.Count == 0))
        {
            if (s.Value != null) d.Value = s.Value;
            return;
        }
        foreach (var dc in d.Children)
        {
            var sc = s[dc.FieldName];
            if (sc == null || sc.IsDummy) { missing.Add(path + "." + dc.FieldName); continue; }
            CopyInto(sc, dc, path + "." + dc.FieldName, missing);
        }
    }
}
