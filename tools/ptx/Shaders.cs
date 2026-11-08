using AssetsTools.NET;
using AssetsTools.NET.Extra;
using K4os.Compression.LZ4;
using System.Text;

// Reads Unity 2020.3 Shader objects: platform slices, the sub-program table and each program's header.
static class Shaders
{
    public static IEnumerable<(AssetFileInfo a, AssetTypeValueField b)> Find(AssetsManager am, AssetsFileInstance i, string name)
    {
        foreach (var a in i.file.GetAssetsOfType(AssetClassID.Shader))
        {
            var b = am.GetBaseField(i, a);
            if (name == "*" || b["m_ParsedForm.m_Name"].AsString == name) yield return (a, b);
        }
    }

    public static byte[] Slice(AssetTypeValueField b, int plat, out int segOff)
    {
        var blob = b["compressedBlob.Array"].AsByteArray;
        var plats = b["platforms.Array"].Children.Select(c => c.AsInt).ToList();
        int k = plats.IndexOf(plat);
        segOff = 0;
        if (k < 0) return null;
        var offs = b["offsets.Array"].Children[k];
        var cl = b["compressedLengths.Array"].Children[k];
        var dl = b["decompressedLengths.Array"].Children[k];
        int off = (int)Leaf(offs, 0), c = (int)Leaf(cl, 0), d = (int)Leaf(dl, 0);
        var res = new byte[d];
        LZ4Codec.Decode(blob, off, c, res, 0, d);
        return res;
    }

    public static long Leaf(AssetTypeValueField f, int idx)
    {
        // 2020.3 nests per-tier: vector<vector<uint>>
        if (f.Children.Count == 1 && f.Children[0].Children.Count > 0 && f.TemplateField.IsArray == false) f = f.Children[0];
        return f.Children[idx].AsLong;
    }

    public static void Dump(string file, string shader, string outDir, string tpk)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(tpk);
        var i = am.LoadAssetsFile(file, false);
        am.LoadClassDatabaseFromPackage(i.file.Metadata.UnityVersion);
        Console.WriteLine($"format={i.file.Header.Version} unity={i.file.Metadata.UnityVersion} target={i.file.Metadata.TargetPlatform} typetree={i.file.Metadata.TypeTreeEnabled}");
        Directory.CreateDirectory(outDir);
        foreach (var (a, b) in Find(am, i, shader))
        {
            var nm = b["m_ParsedForm.m_Name"].AsString;
            Console.WriteLine($"--- {nm} path={a.PathId} plats={string.Join(",", b["platforms.Array"].Children.Select(c => c.AsInt))}");
            Console.WriteLine("offsets tree: " + Tree(b["offsets"], 0));
            foreach (var p in b["platforms.Array"].Children.Select(c => c.AsInt))
            {
                var d = Slice(b, p, out _);
                var fn = Path.Combine(outDir, San(nm) + "." + p + ".blob");
                File.WriteAllBytes(fn, d);
                ShowBlob(d, p, Path.Combine(outDir, San(nm) + "." + p));
            }
            var sub = b["m_ParsedForm.m_SubShaders.Array"].Children.FirstOrDefault()?["m_Passes.Array"].Children.FirstOrDefault();
            if (sub != null)
            {
                var vp = sub["progVertex.m_SubPrograms.Array"].Children;
                Console.WriteLine($"pass0 vertex subprograms={vp.Count}");
                foreach (var s in vp.Take(8))
                    Console.WriteLine("  sp: " + string.Join(" ", s.Children.Where(c => !c.TemplateField.HasValue == false).Select(c => c.FieldName + "=" + c.AsString)));
                if (vp.Count > 0) Console.WriteLine(Fields(vp[0], 0));
            }
        }
    }

    static string Tree(AssetTypeValueField f, int d) => d > 3 ? "..." : f.Children == null || f.Children.Count == 0 ? (f.Value?.AsString ?? "") : $"{f.TemplateField.Type}[{f.Children.Count}]{{{Tree(f.Children[0], d + 1)}}}";
    static string Fields(AssetTypeValueField f, int d)
    {
        var sb = new StringBuilder();
        foreach (var c in f.Children) { sb.Append(new string(' ', d * 2)).Append(c.FieldName).Append(" : ").Append(c.TemplateField.Type); if (c.TemplateField.HasValue && c.Value != null) sb.Append(" = ").Append(c.AsString); sb.Append(" n=").Append(c.Children?.Count ?? 0).Append('\n'); if (d < 2 && c.Children?.Count > 0 && !c.TemplateField.IsArray) sb.Append(Fields(c, d + 1)); }
        return sb.ToString();
    }
    public static string San(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    static void ShowBlob(byte[] d, int plat, string prefix)
    {
        using var r = new BinaryReader(new MemoryStream(d));
        int n = r.ReadInt32();
        var ents = new List<(int off, int len, int seg)>();
        for (int k = 0; k < n; k++) ents.Add((r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
        Console.WriteLine($"plat {plat}: bytes={d.Length} entries={n}");
        for (int k = 0; k < Math.Min(n, 3); k++)
        {
            var (o, l, sg) = ents[k];
            r.BaseStream.Position = o;
            int ver = r.ReadInt32(), type = r.ReadInt32();
            int alu = r.ReadInt32(), tex = r.ReadInt32(), flow = r.ReadInt32(), temp = r.ReadInt32();
            var gk = RS(r); var lk = RS(r);
            int cl = r.ReadInt32();
            Console.WriteLine($"  [{k}] off={o} len={l} seg={sg} ver={ver} type={type} keywords=[{string.Join(" ", gk)}|{string.Join(" ", lk)}] codeLen={cl}");
            var code = r.ReadBytes(cl);
            File.WriteAllBytes(prefix + "." + k + ".code", code);
            long pos = r.BaseStream.Position;
            File.WriteAllBytes(prefix + "." + k + ".tail", d.Skip((int)((pos + 3) & ~3)).Take(o + l - (int)((pos + 3) & ~3)).ToArray());
        }
    }
    static string[] RS(BinaryReader r)
    {
        int n = r.ReadInt32();
        var a = new string[n];
        for (int i = 0; i < n; i++)
        {
            int l = r.ReadInt32();
            a[i] = Encoding.UTF8.GetString(r.ReadBytes(l));
            while (r.BaseStream.Position % 4 != 0) r.ReadByte();
        }
        return a;
    }
}
