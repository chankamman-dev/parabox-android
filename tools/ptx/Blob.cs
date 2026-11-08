using System.Text;

// Unity 2020.3 (blob version 201806140) GPU program record: the header, the code and the parameter groups.
class Param { public string Name; public int Type, Rows, Cols, IsMatrix, ArraySize, Index; }
class Group { public string Name = ""; public int Used; public List<Param> Params = new(); }
class Binding { public string Name; public int Type, Index, Extra, Extra2; }

class Prog
{
    public int Version, Type, Alu, Tex, Flow, Temp;
    public List<string> Global = new(), Local = new();
    public byte[] Code;
    public int SourceMap;
    public List<(int s, int t)> Binds = new();
    public List<Group> Groups = new();
    public List<Binding> Bindings = new();
    public byte[] Rest = Array.Empty<byte>();

    static string RS(BinaryReader r)
    {
        int l = r.ReadInt32();
        var s = Encoding.UTF8.GetString(r.ReadBytes(l));
        while (r.BaseStream.Position % 4 != 0) r.ReadByte();
        return s;
    }

    static void WS(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write(b.Length);
        w.Write(b);
        while (w.BaseStream.Position % 4 != 0) w.Write((byte)0);
    }

    public static Prog Read(byte[] d, int off, int len)
    {
        var ms = new MemoryStream(d, off, len, false);
        var r = new BinaryReader(ms);
        var p = new Prog { Version = r.ReadInt32(), Type = r.ReadInt32(), Alu = r.ReadInt32(), Tex = r.ReadInt32(), Flow = r.ReadInt32(), Temp = r.ReadInt32() };
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) p.Global.Add(RS(r));
        n = r.ReadInt32();
        for (int i = 0; i < n; i++) p.Local.Add(RS(r));
        n = r.ReadInt32();
        p.Code = r.ReadBytes(n);
        while (ms.Position % 4 != 0) r.ReadByte();
        p.SourceMap = r.ReadInt32();
        n = r.ReadInt32();
        for (int i = 0; i < n; i++) p.Binds.Add((r.ReadInt32(), r.ReadInt32()));
        n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var g = new Group { Name = RS(r), Used = r.ReadInt32() };
            int pc = r.ReadInt32();
            for (int k = 0; k < pc; k++)
                g.Params.Add(new Param { Name = RS(r), Type = r.ReadInt32(), Rows = r.ReadInt32(), Cols = r.ReadInt32(), IsMatrix = r.ReadInt32(), ArraySize = r.ReadInt32(), Index = r.ReadInt32() });
            int sc = r.ReadInt32();
            if (sc != 0) throw new InvalidDataException("struct params in " + g.Name + ": " + sc);
            p.Groups.Add(g);
        }
        n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var b = new Binding { Name = RS(r), Type = r.ReadInt32(), Index = r.ReadInt32(), Extra = r.ReadInt32() };
            if (b.Type == 0) b.Extra2 = r.ReadInt32();
            p.Bindings.Add(b);
        }
        p.Rest = r.ReadBytes((int)(ms.Length - ms.Position));
        return p;
    }

    public byte[] Write()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Version); w.Write(Type); w.Write(Alu); w.Write(Tex); w.Write(Flow); w.Write(Temp);
        w.Write(Global.Count);
        foreach (var s in Global) WS(w, s);
        w.Write(Local.Count);
        foreach (var s in Local) WS(w, s);
        w.Write(Code.Length);
        w.Write(Code);
        while (ms.Position % 4 != 0) w.Write((byte)0);
        w.Write(SourceMap);
        w.Write(Binds.Count);
        foreach (var (s, t) in Binds) { w.Write(s); w.Write(t); }
        w.Write(Groups.Count);
        foreach (var g in Groups)
        {
            WS(w, g.Name);
            w.Write(g.Used);
            w.Write(g.Params.Count);
            foreach (var q in g.Params)
            {
                WS(w, q.Name);
                w.Write(q.Type); w.Write(q.Rows); w.Write(q.Cols); w.Write(q.IsMatrix); w.Write(q.ArraySize); w.Write(q.Index);
            }
            w.Write(0);
        }
        w.Write(Bindings.Count);
        foreach (var b in Bindings)
        {
            WS(w, b.Name);
            w.Write(b.Type); w.Write(b.Index); w.Write(b.Extra);
            if (b.Type == 0) w.Write(b.Extra2);
        }
        w.Write(Rest);
        return ms.ToArray();
    }

    public override string ToString()
    {
        var sb = new StringBuilder($"type={Type} kw=[{string.Join(" ", Global)}|{string.Join(" ", Local)}] code={Code.Length} srcmap={SourceMap:x} binds={string.Join(",", Binds)}\n");
        foreach (var g in Groups)
        {
            if (g.Name == "" && g.Params.Count == 0) continue;
            sb.Append($"  group [{g.Name}] used={g.Used}: ");
            foreach (var q in g.Params) sb.Append($"{q.Name}(t{q.Type} {q.Rows}x{q.Cols} m{q.IsMatrix} a{q.ArraySize} @{q.Index}) ");
            sb.Append('\n');
        }
        foreach (var b in Bindings) sb.Append($"  bind {b.Name} type={b.Type} idx={b.Index} extra={b.Extra},{b.Extra2}\n");
        if (Rest.Length > 0) sb.Append("  REST " + Convert.ToHexString(Rest) + "\n");
        return sb.ToString();
    }

    public static List<(int off, int len, int seg)> Table(byte[] d)
    {
        var l = new List<(int, int, int)>();
        int n = BitConverter.ToInt32(d, 0);
        for (int i = 0; i < n; i++)
            l.Add((BitConverter.ToInt32(d, 4 + i * 12), BitConverter.ToInt32(d, 8 + i * 12), BitConverter.ToInt32(d, 12 + i * 12)));
        return l;
    }
}

// Parses every program in the given platform slice, and checks that it writes back byte for byte.
static class ShProbe
{
    public static void Run(string file, string shader, int plat, string tpk, string outDir)
    {
        var am = new AssetsTools.NET.Extra.AssetsManager();
        am.LoadClassPackage(tpk);
        var i = am.LoadAssetsFile(file, false);
        am.LoadClassDatabaseFromPackage(i.file.Metadata.UnityVersion);
        Directory.CreateDirectory(outDir);
        foreach (var (a, b) in Shaders.Find(am, i, shader))
        {
            var nm = b["m_ParsedForm.m_Name"].AsString;
            var d = Shaders.Slice(b, plat, out _);
            if (d == null) continue;
            var t = Prog.Table(d);
            int bad = 0;
            var ps = new List<Prog>();
            foreach (var (o, l, s) in t)
            {
                var p = Prog.Read(d, o, l);
                ps.Add(p);
                if (!p.Write().AsSpan().SequenceEqual(d.AsSpan(o, l))) bad++;
            }
            Console.WriteLine($"=== {nm} plat={plat} programs={t.Count} roundtrip-mismatch={bad}");
            foreach (var pa in b["m_ParsedForm.m_SubShaders.Array"].Children.SelectMany(s => s["m_Passes.Array"].Children))
                foreach (var stage in new[] { "progVertex", "progFragment" })
                {
                    Console.WriteLine(stage + " fields: " + string.Join(",", pa[stage].Children.Select(c => c.FieldName)));
                    var cp = pa[stage + ".m_CommonParameters"];
                    if (!cp.IsDummy)
                        foreach (var coll in cp.Children)
                            if (coll["Array"].Children.Count > 0)
                                Console.WriteLine($"  common {coll.FieldName}[{coll["Array"].Children.Count}] " + string.Join(" | ", coll["Array"].Children.Take(12).Select(e => string.Join(" ", e.Children.Where(c => c.TemplateField.HasValue).Select(c => c.FieldName.Replace("m_", "") + "=" + c.AsString)))));
                    Console.WriteLine("  nameIndices: " + string.Join(" ", pa["m_NameIndices.Array"].Children.Select(e => e["first"].AsString + "=" + e["second"].AsInt)));
                    foreach (var sp in pa[stage + ".m_SubPrograms.Array"].Children.Where(c => PlatOf(c) == plat))
                    {
                        Console.WriteLine($"{stage} blob={sp["m_BlobIndex"].AsUInt} tier={sp["m_ShaderHardwareTier"].AsSByte} type={sp["m_GpuProgramType"].AsSByte} req={sp["m_ShaderRequirements"].AsInt} kw={string.Join(",", sp["m_GlobalKeywordIndices.Array"].Children.Select(c => c.AsUShort))} chan={sp["m_Channels.m_Channels.Array"].Children.Count}");
                        foreach (var coll in sp["m_Parameters"].Children)
                        {
                            var arr = coll["Array"];
                            if (arr.Children.Count == 0) continue;
                            Console.WriteLine($"    {coll.FieldName}[{arr.Children.Count}] first: " + string.Join(" ", arr.Children[0].Children.Where(c => c.TemplateField.HasValue).Select(c => c.FieldName + "=" + c.AsString)));
                            if (coll.FieldName is "m_ConstantBuffers") foreach (var cb in arr.Children) Console.WriteLine("       cb " + cb["m_NameIndex"].AsInt + " size=" + cb["m_Size"].AsInt + " vec=" + cb["m_VectorParams.Array"].Children.Count + " mat=" + cb["m_MatrixParams.Array"].Children.Count);
                        }
                    }
                }
            for (int k = 0; k < ps.Count; k++) Console.WriteLine($"[{k}] " + ps[k]);
            for (int k = 0; k < ps.Count; k++) File.WriteAllBytes(Path.Combine(outDir, $"{Shaders.San(nm)}.{plat}.{k}.bin"), ps[k].Code);
        }
    }

    static int PlatOf(AssetsTools.NET.AssetTypeValueField sp) => sp["m_GpuProgramType"].AsSByte switch { 4 => 9, 6 => 15, 25 => 18, 23 => 14, 5 => 5, _ => 4 };
}
