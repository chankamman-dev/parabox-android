using Mono.Cecil;

// Lists the fields Unity would serialize for every type in an assembly (instance fields that are public
// or [SerializeField] and not [NonSerialized]), so two versions of a package can be checked for layout
// compatibility with data written by the other. Player data has no type trees: layouts must match.
static class SerCmp
{
    static Dictionary<string, string> Fields(string dll)
    {
        var m = ModuleDefinition.ReadModule(dll);
        var d = new Dictionary<string, string>();
        foreach (var t in m.GetTypes())
        {
            if (t.IsInterface || t.IsEnum) continue;
            var fs = t.Fields.Where(f => !f.IsStatic && !f.IsLiteral && !f.IsInitOnly
                && !f.CustomAttributes.Any(a => a.AttributeType.Name == "NonSerializedAttribute")
                && (f.IsPublic || f.CustomAttributes.Any(a => a.AttributeType.Name == "SerializeField" || a.AttributeType.Name == "SerializeReference")))
                .Select(f => f.Name + ":" + f.FieldType.FullName);
            d[t.FullName] = (t.BaseType?.FullName ?? "") + " | " + string.Join(", ", fs);
        }
        return d;
    }

    public static void Run(string a, string b, string types)
    {
        var fa = Fields(a); var fb = Fields(b);
        var want = types == "*" ? fa.Keys.ToList() : types.Split(',').ToList();
        foreach (var t in want)
        {
            fa.TryGetValue(t, out var x); fb.TryGetValue(t, out var y);
            if (x == y) Console.WriteLine("same " + t);
            else Console.WriteLine($"DIFF {t}\n   A: {x}\n   B: {y}");
        }
    }
}

static class Scripts
{
    public static void Run(string data, string tpk)
    {
        var am = new AssetsTools.NET.Extra.AssetsManager();
        am.LoadClassPackage(tpk);
        foreach (var f in Directory.GetFiles(data).Where(f => !f.EndsWith(".resS") && !f.EndsWith(".json") && !f.EndsWith(".info") && !f.EndsWith(".config")))
        {
            try
            {
                var i = am.LoadAssetsFile(f, false);
                am.LoadClassDatabaseFromPackage(i.file.Metadata.UnityVersion);
                var cnt = i.file.AssetInfos.GroupBy(a => a.TypeId).ToDictionary(g => g.Key, g => g.Count());
                Console.WriteLine($"## {Path.GetFileName(f)} ver={i.file.Metadata.UnityVersion} types={string.Join(",", cnt.Select(k => k.Key + "x" + k.Value))}");
                foreach (var a in i.file.GetAssetsOfType(AssetsTools.NET.Extra.AssetClassID.MonoBehaviour))
                {
                    var b = am.GetBaseField(i, a, AssetsTools.NET.Extra.AssetReadFlags.SkipMonoBehaviourFields);
                    Console.WriteLine($"   mb {a.PathId} name='{b["m_Name"].AsString}' script=({b["m_Script.m_FileID"].AsInt},{b["m_Script.m_PathID"].AsLong}) size={a.ByteSize}");
                }
                if (Environment.GetEnvironmentVariable("ALLSCRIPTS") != null)
                foreach (var a in i.file.GetAssetsOfType(AssetsTools.NET.Extra.AssetClassID.MonoScript))
                {
                    var b = am.GetBaseField(i, a);
                    Console.WriteLine($"   script {a.PathId} {b["m_AssemblyName"].AsString} {b["m_Namespace"].AsString}.{b["m_ClassName"].AsString}");
                }
            }
            catch (Exception e) { Console.WriteLine("ERR " + f + " " + e.Message); }
            am.UnloadAll(); am.LoadClassPackage(tpk);
        }
    }
}
