using AssetsTools.NET;
using AssetsTools.NET.Extra;

// Points the external resource streams of the converted player data (textures, meshes, audio clips in
// *.resS / *.resource) at an absolute folder on the device. The streams themselves are byte-for-byte
// the Windows originals, so the APK does not carry them: the launcher downloads them from the Steam
// depot into that folder (cloud/.../GameData.java).
//   respath <data dir> <tpk> <device folder ending in />
static class ResPath
{
    public static void Run(string dir, string tpk, string prefix)
    {
        var names = Directory.GetFiles(dir).Select(Path.GetFileName)
            .Where(n => n is "globalgamemanagers" or "globalgamemanagers.assets" or "resources.assets"
                || (n.StartsWith("level") && !n.Contains('.')) || (n.StartsWith("sharedassets") && n.EndsWith(".assets")))
            .ToList();
        int total = 0;
        foreach (var n in names)
        {
            var am = new AssetsManager();
            am.LoadClassPackage(tpk);
            var path = Path.Combine(dir, n);
            var bytes = File.ReadAllBytes(path);
            var i = am.LoadAssetsFile(new MemoryStream(bytes), path, true);
            am.LoadClassDatabaseFromPackage(i.file.Metadata.UnityVersion);
            int changed = 0;
            foreach (var a in i.file.AssetInfos)
            {
                if (a.TypeId is not (28 or 43 or 83 or 89 or 117 or 187 or 329 or 152)) continue;
                var bf = am.GetBaseField(i, a);
                if (bf == null) continue;
                if (Rewrite(bf, prefix) == 0) continue;
                a.SetNewData(bf);
                changed++;
            }
            if (changed > 0)
            {
                using (var fs = File.Create(path + ".tmp"))
                using (var w = new AssetsFileWriter(fs))
                    i.file.Write(w);
                am.UnloadAll(true);
                File.Move(path + ".tmp", path, true);
            }
            else am.UnloadAll(true);
            total += changed;
            Console.WriteLine($"{n}: {changed} objects point at {prefix}");
        }
        Console.WriteLine($"respath: {total} objects");
    }

    // StreamingInfo.path (textures, meshes) and StreamedResource.m_Source (audio, video).
    static int Rewrite(AssetTypeValueField f, string prefix)
    {
        int n = 0;
        foreach (var c in f.Children)
        {
            if (c.TemplateField.ValueType == AssetValueType.String && (c.FieldName is "path" or "m_Source"))
            {
                var v = c.AsString;
                if (v.Length > 0 && !v.StartsWith("/") && (v.EndsWith(".resS") || v.EndsWith(".resource")))
                {
                    c.AsString = prefix + Path.GetFileName(v);
                    n++;
                }
            }
            else if (c.Children.Count > 0 && !c.TemplateField.IsArray) n += Rewrite(c, prefix);
        }
        return n;
    }
}
