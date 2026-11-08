using AssetsTools.NET;
using AssetsTools.NET.Extra;

// Lists materials with the shader each one uses, resolving shader names across the file's externals.
static class Materials
{
    public static void Run(string path, string filter, string tpk, string dataDir)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(tpk);
        var files = new List<AssetsFileInstance>();
        if (path.EndsWith(".bundle"))
        {
            var b = am.LoadBundleFile(path, true);
            am.LoadClassDatabaseFromPackage(b.file.Header.EngineVersion);
            for (int k = 0; k < b.file.BlockAndDirInfo.DirectoryInfos.Count; k++)
                if ((b.file.BlockAndDirInfo.DirectoryInfos[k].Flags & 4) != 0) files.Add(am.LoadAssetsFileFromBundle(b, k, false));
        }
        else
        {
            var i = am.LoadAssetsFile(path, false);
            am.LoadClassDatabaseFromPackage(i.file.Metadata.UnityVersion);
            files.Add(i);
        }
        var shaderNames = new Dictionary<(string, long), string>();
        string Resolve(AssetsFileInstance i, int fileId, long pathId)
        {
            string fname = fileId == 0 ? i.name : i.file.Metadata.Externals[fileId - 1].PathName;
            var key = (Path.GetFileName(fname), pathId);
            if (shaderNames.TryGetValue(key, out var n)) return n;
            try
            {
                var target = fileId == 0 ? i : null;
                if (target == null)
                {
                    var full = fname.StartsWith("archive:") ? null : Path.Combine(dataDir, fname.Replace("library/", "Resources/"));
                    if (full != null && File.Exists(full)) target = am.LoadAssetsFile(full, false);
                }
                if (target != null)
                {
                    var info = target.file.GetAssetInfo(pathId);
                    if (info != null) n = am.GetBaseField(target, info)["m_ParsedForm.m_Name"].AsString;
                }
            }
            catch { }
            n ??= $"{Path.GetFileName(fname)}#{pathId}";
            shaderNames[key] = n;
            return n;
        }
        string TexInfo(AssetsFileInstance i, int fileId, long pathId)
        {
            if (fileId != 0) return $"ext{fileId}#{pathId}";
            var info = i.file.GetAssetInfo(pathId);
            if (info == null) return $"#{pathId}?";
            var t = am.GetBaseField(i, info);
            if (info.TypeId != (int)AssetClassID.Texture2D) return $"{t["m_Name"].AsString}(type{info.TypeId})";
            return $"{t["m_Name"].AsString}({t["m_Width"].AsInt}x{t["m_Height"].AsInt} fmt{t["m_TextureFormat"].AsInt} mips{t["m_MipCount"].AsInt} cs{t["m_ColorSpace"].AsInt})";
        }
        foreach (var i in files)
            foreach (var a in i.file.GetAssetsOfType(AssetClassID.Material))
            {
                var b = am.GetBaseField(i, a);
                var name = b["m_Name"].AsString;
                var sh = Resolve(i, b["m_Shader.m_FileID"].AsInt, b["m_Shader.m_PathID"].AsLong);
                if (filter != "*" && !name.Contains(filter) && !sh.Contains(filter)) continue;
                var kw = b["m_ShaderKeywords"].IsDummy ? "" : b["m_ShaderKeywords"].AsString;
                var colors = string.Join(" ", b["m_SavedProperties.m_Colors.Array"].Children.Select(c => c["first"].AsString + "=(" + string.Join(",", new[] { "r", "g", "b", "a" }.Select(k => c["second." + k].AsFloat.ToString("0.##"))) + ")"));
                var floats = string.Join(" ", b["m_SavedProperties.m_Floats.Array"].Children.Select(c => c["first"].AsString + "=" + c["second"].AsFloat.ToString("0.##")));
                var texs = string.Join(" ", b["m_SavedProperties.m_TexEnvs.Array"].Children
                    .Where(c => c["second.m_Texture.m_PathID"].AsLong != 0)
                    .Select(c => c["first"].AsString + "=" + TexInfo(i, c["second.m_Texture.m_FileID"].AsInt, c["second.m_Texture.m_PathID"].AsLong)));
                Console.WriteLine($"{name} | {sh} | kw=[{kw}] | {colors} | {floats} | {texs}");
            }
    }
}
