using AssetsTools.NET;
using AssetsTools.NET.Extra;

// Counts particle renderers by render mode and GPU instancing. Mesh particles with instancing take
// the procedural-instancing shader variant, which the GLES3 conversion cannot produce.
static class PsrScan
{
    public static void Run(string bundleDir, string dataDir, string tpk)
    {
        var count = new Dictionary<string, int>();
        void Scan(AssetsManager am, AssetsFileInstance i, string where)
        {
            foreach (var a in i.file.GetAssetsOfType(199))
            {
                var b = am.GetBaseField(i, a);
                int mode = b["m_RenderMode"].AsInt;
                bool inst = !b["m_EnableGPUInstancing"].IsDummy && b["m_EnableGPUInstancing"].AsBool;
                bool enabled = b["m_Enabled"].AsBool;
                var key = $"mode {mode} instancing {inst} enabled {enabled}";
                count[key] = count.GetValueOrDefault(key) + 1;
                if (mode == 4 && inst && count[key] <= 5) Console.WriteLine($"  mesh+instancing in {where}");
            }
        }
        foreach (var f in Directory.GetFiles(bundleDir, "*.bundle"))
        {
            var am = new AssetsManager();
            am.LoadClassPackage(tpk);
            var bundle = am.LoadBundleFile(f, true);
            am.LoadClassDatabaseFromPackage(bundle.file.Header.EngineVersion);
            foreach (var dir in bundle.file.BlockAndDirInfo.DirectoryInfos)
            {
                if ((dir.Flags & 4) == 0) continue;
                Scan(am, am.LoadAssetsFileFromBundle(bundle, dir.Name), Path.GetFileName(f));
            }
            am.UnloadAll();
        }
        foreach (var f in Directory.GetFiles(dataDir).Where(p => Path.GetFileName(p).StartsWith("level") || p.EndsWith(".assets")).Where(p => !p.EndsWith(".resS")))
        {
            var am = new AssetsManager();
            am.LoadClassPackage(tpk);
            var i = am.LoadAssetsFile(f, false);
            am.LoadClassDatabaseFromPackage(i.file.Metadata.UnityVersion);
            Scan(am, i, Path.GetFileName(f));
            am.UnloadAll();
        }
        foreach (var kv in count.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Key}: {kv.Value}");
    }
}
