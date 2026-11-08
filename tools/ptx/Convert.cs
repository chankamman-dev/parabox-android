using AssetsTools.NET;
using AssetsTools.NET.Extra;
using K4os.Compression.LZ4;
using System.Diagnostics;
using System.Text;

// D3D11 -> GLES3 shader slice conversion.
//
// The game's DXBC has had its RDEF chunk stripped, and HLSLcc needs one, so it is rebuilt here from
// what the player keeps: the program record's parameter groups plus the pass-level common parameters.
class CbVar { public string Name; public int Type, Rows, Cols, IsMatrix, ArraySize, Offset; }
class Cb { public string Name; public int Size; public List<CbVar> Vars = new(); public int Bind; }
class Tex { public string Name; public int Index, Sampler, Dim; }

class Refl
{
    public List<Cb> Cbs = new();
    public List<Tex> Texs = new();
    public List<(string name, int idx)> Samplers = new();
    public List<(string name, int idx)> Bufs = new();

    public Cb GetCb(string n)
    {
        var c = Cbs.FirstOrDefault(x => x.Name == n);
        if (c == null) Cbs.Add(c = new Cb { Name = n, Bind = -1 });
        return c;
    }

    // Parameters that live in the program record.
    public void AddProg(Prog p)
    {
        foreach (var g in p.Groups)
        {
            if (g.Name == "" && g.Params.Count == 0) continue;
            var c = GetCb(g.Name == "" ? "$Globals" : g.Name);
            c.Size = Math.Max(c.Size, g.Used);
            foreach (var q in g.Params) c.Vars.Add(new CbVar { Name = q.Name, Type = q.Type, Rows = q.Rows, Cols = q.Cols, IsMatrix = q.IsMatrix, ArraySize = q.ArraySize, Offset = q.Index });
        }
        foreach (var b in p.Bindings)
        {
            if (b.Type == 1) GetCb(b.Name).Bind = b.Index;
            else if (b.Type == 0) Texs.Add(new Tex { Name = b.Name, Index = b.Index, Sampler = b.Extra, Dim = b.Extra2 });
        }
    }

    // Parameters the build pipeline moved to the program level. Used by every variant, so a binding
    // is only trusted when the CB is actually present in this variant's record or the common list.
    public void AddCommon(AssetTypeValueField c, Dictionary<int, string> names, Dictionary<string, int> dimFix)
    {
        if (c.IsDummy) return;
        AddSet(c, names);
    }

    public void AddSet(AssetTypeValueField c, Dictionary<int, string> names)
    {
        foreach (var e in c["m_ConstantBuffers.Array"].Children)
        {
            var cb = GetCb(names[e["m_NameIndex"].AsInt]);
            cb.Size = Math.Max(cb.Size, e["m_Size"].AsInt);
            foreach (var v in e["m_VectorParams.Array"].Children) cb.Vars.Add(Vec(v, names));
            foreach (var v in e["m_MatrixParams.Array"].Children) cb.Vars.Add(Mat(v, names));
        }
        foreach (var e in c["m_ConstantBufferBindings.Array"].Children)
            GetCb(names[e["m_NameIndex"].AsInt]).Bind = e["m_Index"].AsInt;
        foreach (var e in c["m_TextureParams.Array"].Children)
            Texs.Add(new Tex { Name = names[e["m_NameIndex"].AsInt], Index = e["m_Index"].AsInt, Sampler = e["m_SamplerIndex"].AsInt, Dim = DimToSrv(e["m_Dim"].AsSByte) });
        foreach (var e in c["m_Samplers.Array"].Children)
            Samplers.Add(("sampler_inline" + e["bindPoint"].AsInt, e["bindPoint"].AsInt));
        foreach (var e in c["m_BufferParams.Array"].Children)
            Bufs.Add((names[e["m_NameIndex"].AsInt], e["m_Index"].AsInt));
    }

    static CbVar Vec(AssetTypeValueField v, Dictionary<int, string> names) => new CbVar
    {
        Name = names[v["m_NameIndex"].AsInt], Type = v["m_Type"].AsSByte, Rows = 1, Cols = v["m_Dim"].AsSByte,
        ArraySize = v["m_ArraySize"].AsInt, Offset = v["m_Index"].AsInt,
    };

    static CbVar Mat(AssetTypeValueField v, Dictionary<int, string> names) => new CbVar
    {
        Name = names[v["m_NameIndex"].AsInt], Type = v["m_Type"].AsSByte, Rows = v["m_RowCount"].AsSByte,
        Cols = v["m_ColumnCount"].IsDummy ? v["m_RowCount"].AsSByte : v["m_ColumnCount"].AsSByte, IsMatrix = 1, ArraySize = v["m_ArraySize"].AsInt, Offset = v["m_Index"].AsInt,
    };

    public static int DimToSrv(int unityDim) => unityDim switch { 2 => 4, 3 => 8, 4 => 9, 5 => 5, 6 => 10, _ => 4 };
    public static int SrvToUnity(int srv) => srv switch { 4 => 2, 8 => 3, 9 => 4, 5 => 5, 10 => 6, _ => 2 };

    public byte[] Rdef(uint versionToken)
    {
        // Laid out as: header, resource bindings, cbuffers, variables, types, strings.
        int major = (int)((versionToken >> 4) & 0xf);
        var strs = new MemoryStream();
        var strOff = new Dictionary<string, int>();
        int Str(string s)
        {
            if (strOff.TryGetValue(s, out var o)) return o;
            o = (int)strs.Position;
            strs.Write(Encoding.ASCII.GetBytes(s));
            strs.WriteByte(0);
            strOff[s] = o;
            return o;
        }

        var cbs = Cbs.Where(c => c.Bind >= 0 || c.Vars.Count > 0).ToList();
        var dedupVars = cbs.ToDictionary(c => c, c => c.Vars.GroupBy(v => v.Name).Select(g => g.First()).ToList());
        var binds = new List<(string name, int type, int ret, int dim, int samples, int point, int count, int flags)>();
        foreach (var c in cbs) binds.Add((c.Name, 0, 0, 0, 0, c.Bind < 0 ? 0 : c.Bind, 1, 1));
        var samplerSeen = new HashSet<int>();
        foreach (var t in Texs.GroupBy(t => t.Name).Select(g => g.First()))
        {
            binds.Add((t.Name, 2, 5, t.Dim, -1, t.Index, 1, 0x0c));
            if (t.Sampler >= 0 && t.Sampler < 32 && samplerSeen.Add(t.Sampler)) binds.Add(("sampler" + t.Name, 3, 0, 0, 0, t.Sampler, 1, 0));
        }
        foreach (var (n, i) in Samplers) if (samplerSeen.Add(i)) binds.Add((n, 3, 0, 0, 0, i, 1, 0));

        const int headerSize = 28;
        int bindSize = 32, cbSize = 24, varSize = major >= 5 ? 40 : 24, typeSize = 16;
        int bindOff = headerSize, cbOff = bindOff + binds.Count * bindSize, varOff = cbOff + cbs.Count * cbSize;
        int varCount = dedupVars.Values.Sum(v => v.Count);
        int typeOff = varOff + varCount * varSize;
        int strBase = typeOff + varCount * typeSize;

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(cbs.Count); w.Write(cbOff); w.Write(binds.Count); w.Write(bindOff); w.Write(versionToken & 0xffff0000 | (versionToken & 0xff)); w.Write(0); w.Write(strBase + Str("lomtool"));
        foreach (var b in binds)
        {
            w.Write(strBase + Str(b.name)); w.Write(b.type); w.Write(b.ret); w.Write(b.dim); w.Write(b.samples); w.Write(b.point); w.Write(b.count); w.Write(b.flags);
        }
        int vi = 0;
        foreach (var c in cbs)
        {
            w.Write(strBase + Str(c.Name)); w.Write(dedupVars[c].Count); w.Write(varOff + vi * varSize); w.Write(Math.Max(c.Size, 16)); w.Write(0); w.Write(0);
            vi += dedupVars[c].Count;
        }
        vi = 0;
        foreach (var c in cbs)
            foreach (var v in dedupVars[c])
            {
                int elems = Math.Max(1, v.ArraySize);
                int size = v.IsMatrix != 0 ? 16 * v.Rows * elems : elems > 1 ? 16 * elems : 4 * v.Cols;
                w.Write(strBase + Str(v.Name)); w.Write(v.Offset); w.Write(size); w.Write(2); w.Write(typeOff + vi * typeSize); w.Write(0);
                if (major >= 5) { w.Write(-1); w.Write(0); w.Write(-1); w.Write(0); }
                vi++;
            }
        foreach (var c in cbs)
            foreach (var v in dedupVars[c])
            {
                int svc = v.IsMatrix != 0 ? 3 : v.Cols == 1 ? 0 : 1;
                int svt = v.Type switch { 1 => 2, 2 => 1, 5 => 19, _ => 3 };
                w.Write((ushort)svc); w.Write((ushort)svt); w.Write((ushort)v.Rows); w.Write((ushort)v.Cols); w.Write((ushort)v.ArraySize); w.Write((ushort)0); w.Write(0);
            }
        w.Write(strs.ToArray());
        while (ms.Position % 4 != 0) w.Write((byte)0);
        return ms.ToArray();
    }
}

static class Dxbc
{
    // Unity prefixes the container with its own 38 byte header.
    public static byte[] Strip(byte[] code)
    {
        for (int i = 0; i + 4 < code.Length; i++)
            if (code[i] == 'D' && code[i + 1] == 'X' && code[i + 2] == 'B' && code[i + 3] == 'C') return code.AsSpan(i).ToArray();
        throw new InvalidDataException("no DXBC");
    }

    public static uint VersionToken(byte[] d)
    {
        int n = BitConverter.ToInt32(d, 28);
        for (int i = 0; i < n; i++)
        {
            int o = BitConverter.ToInt32(d, 32 + i * 4);
            var cc = Encoding.ASCII.GetString(d, o, 4);
            if (cc is "SHDR" or "SHEX") return BitConverter.ToUInt32(d, o + 8);
        }
        throw new InvalidDataException("no shader chunk");
    }

    public static byte[] WithRdef(byte[] d, byte[] rdef)
    {
        int n = BitConverter.ToInt32(d, 28);
        var chunks = new List<(string cc, byte[] data)>();
        for (int i = 0; i < n; i++)
        {
            int o = BitConverter.ToInt32(d, 32 + i * 4);
            var cc = Encoding.ASCII.GetString(d, o, 4);
            int len = BitConverter.ToInt32(d, o + 4);
            chunks.Add((cc, d.AsSpan(o + 8, len).ToArray()));
        }
        chunks.Insert(0, ("RDEF", rdef));
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(d, 0, 24);
        w.Write(0);
        w.Write(chunks.Count);
        int off = 32 + 4 * chunks.Count;
        foreach (var c in chunks) { w.Write(off); off += 8 + c.data.Length; }
        foreach (var c in chunks) { w.Write(Encoding.ASCII.GetBytes(c.cc)); w.Write(c.data.Length); w.Write(c.data); }
        var r = ms.ToArray();
        BitConverter.TryWriteBytes(r.AsSpan(24), r.Length);
        return r;
    }
}

class Pair { public string Key; public Prog V, F; public Refl RV, RF; public AssetTypeValueField Sp; public int Pass; public string Prefix; public Prog Out; public List<Target> Targets = new(); }

// One keyword set the GLES pass must answer for, and the vertex entry it is modelled on.
class Target
{
    public string Key;
    public sbyte Tier;
    public ushort[] Global, Local;
    public AssetTypeValueField Template, Array;
}

record KwSet(string Key, sbyte Tier, ushort[] Global, ushort[] Local);

class Converter
{
    public string Tmp, Cli;
    public const uint Flags = 0x20000 | 0x80 | 0x8000 | 0x800 | 0x10000 | 0x8000000 | 0x8;
    public int Total, Failed, Skipped;
    public List<string> Log = new();

    static string Key(AssetTypeValueField sp) =>
        sp["m_ShaderHardwareTier"].AsSByte + ":" + string.Join(",", sp["m_GlobalKeywordIndices.Array"].Children.Select(c => c.AsUShort).OrderBy(x => x))
        + ":" + string.Join(",", sp["m_LocalKeywordIndices.Array"].Children.Select(c => c.AsUShort).OrderBy(x => x));

    static KwSet Kw(AssetTypeValueField sp)
    {
        var g = sp["m_GlobalKeywordIndices.Array"].Children.Select(c => c.AsUShort).OrderBy(x => x).ToArray();
        var l = sp["m_LocalKeywordIndices.Array"].Children.Select(c => c.AsUShort).OrderBy(x => x).ToArray();
        return new KwSet(Key(sp), sp["m_ShaderHardwareTier"].AsSByte, g, l);
    }

    // The entry whose keywords are the largest subset of the target's, in the same tier.
    static AssetTypeValueField Best(List<AssetTypeValueField> list, KwSet t) =>
        list.Select(s => (s, k: Kw(s)))
            .Where(x => x.k.Tier == t.Tier && x.k.Global.All(t.Global.Contains) && x.k.Local.All(t.Local.Contains))
            .OrderByDescending(x => x.k.Global.Length + x.k.Local.Length)
            .Select(x => x.s).FirstOrDefault();

    static bool IsD3DVS(int t) => t == 15 || t == 16;
    static bool IsD3DFS(int t) => t == 17 || t == 18;

    // Adds every shader in the file to the batch; returns what has to be finished after hlslcc ran.
    public List<(AssetFileInfo a, AssetTypeValueField b, string name, List<Pair> pairs)> Prepare(AssetsManager am, AssetsFileInstance i, StreamWriter list, ref int seq)
    {
        var res = new List<(AssetFileInfo, AssetTypeValueField, string, List<Pair>)>();
        foreach (var a in i.file.GetAssetsOfType(AssetClassID.Shader))
        {
            var b = am.GetBaseField(i, a);
            var nm = b["m_ParsedForm.m_Name"].AsString;
            var plats = b["platforms.Array"].Children.Select(c => c.AsInt).ToList();
            if (plats.Contains(9) || !plats.Contains(4)) { Skipped++; continue; }
            var d = Shaders.Slice(b, 4, out _);
            var table = Prog.Table(d);
            var progs = new Dictionary<int, Prog>();
            Prog P(int k) => progs.TryGetValue(k, out var p) ? p : progs[k] = Prog.Read(d, table[k].off, table[k].len);
            var pairs = new List<Pair>();
            int pass = 0;
            foreach (var pa in b["m_ParsedForm.m_SubShaders.Array"].Children.SelectMany(s => s["m_Passes.Array"].Children))
            {
                var names = pa["m_NameIndices.Array"].Children.ToDictionary(e => e["second"].AsInt, e => e["first"].AsString);
                var vs = pa["progVertex.m_SubPrograms.Array"].Children;
                var fs = pa["progFragment.m_SubPrograms.Array"].Children;
                // Unity strips keywords a stage does not use, so vertex and fragment variants need not
                // share keyword sets. Like the engine, pick for every keyword set that occurs the
                // vertex and fragment programs whose sets are its largest subsets.
                var dvs = vs.Where(s => IsD3DVS(s["m_GpuProgramType"].AsSByte)).ToList();
                var dfs = fs.Where(s => IsD3DFS(s["m_GpuProgramType"].AsSByte)).ToList();
                var byPair = new Dictionary<(uint, uint), Pair>();
                foreach (var target in dvs.Concat(dfs).Select(Kw).GroupBy(k => k.Key).Select(g => g.First()))
                {
                    var sp = Best(dvs, target);
                    var fsp = Best(dfs, target);
                    if (sp == null || fsp == null) { Log.Add($"{nm} pass{pass}: no {(sp == null ? "vertex" : "fragment")} for [{target.Key}]"); continue; }
                    var tgt = new Target { Key = target.Key, Tier = target.Tier, Global = target.Global, Local = target.Local, Template = sp, Array = pa["progVertex.m_SubPrograms.Array"] };
                    var pk = (sp["m_BlobIndex"].AsUInt, fsp["m_BlobIndex"].AsUInt);
                    if (byPair.TryGetValue(pk, out var have)) { have.Targets.Add(tgt); continue; }
                    var key = target.Key;
                    var rv = new Refl(); var rf = new Refl();
                    var v = P((int)sp["m_BlobIndex"].AsUInt); var f2 = P((int)fsp["m_BlobIndex"].AsUInt);
                    rv.AddProg(v); rf.AddProg(f2);
                    rv.AddSet(sp["m_Parameters"], names); rf.AddSet(fsp["m_Parameters"], names);
                    rv.AddCommon(pa["progVertex.m_CommonParameters"], names, null);
                    rf.AddCommon(pa["progFragment.m_CommonParameters"], names, null);
                    // Structured buffers (procedural instancing) have no GLES 3.0 form.
                    if (rv.Bufs.Count + rf.Bufs.Count > 0) { Log.Add($"{nm} pass{pass}: skipped buffer variant [{key}]"); continue; }
                    var dv = Dxbc.Strip(v.Code); var df = Dxbc.Strip(f2.Code);
                    var dv2 = Dxbc.WithRdef(dv, rv.Rdef(Dxbc.VersionToken(dv)));
                    var df2 = Dxbc.WithRdef(df, rf.Rdef(Dxbc.VersionToken(df)));
                    var pre = Path.Combine(Tmp, seq++.ToString());
                    File.WriteAllBytes(pre + ".vs", dv2);
                    File.WriteAllBytes(pre + ".fs", df2);
                    list.WriteLine($"{pre}.vs {pre}.fs {pre}");
                    var pair = new Pair { Key = key, V = v, F = f2, RV = rv, RF = rf, Sp = sp, Pass = pass, Prefix = pre };
                    pair.Targets.Add(tgt);
                    byPair[pk] = pair;
                    pairs.Add(pair);
                }
                pass++;
            }
            res.Add((a, b, nm, pairs));
            Total += pairs.Count;
        }
        return res;
    }

    public void RunCli(string listFile)
    {
        // A crash on one program must not lose the rest of the batch: drop it and carry on.
        var lines = File.ReadAllLines(listFile).ToList();
        while (lines.Count > 0)
        {
            File.WriteAllLines(listFile, lines);
            var psi = new ProcessStartInfo(Cli, $"{Flags:x} 2 \"{listFile}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            var p = Process.Start(psi);
            var err = p.StandardError.ReadToEndAsync();
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var e = err.Result;
            if (e.Length > 0) Log.Add("hlslcc: " + e.Substring(0, Math.Min(e.Length, 400)));
            if (p.ExitCode >= 0) { Console.WriteLine($"hlslcc: {o.Trim()}"); return; }
            int k = lines.FindIndex(l => !File.Exists(l.Split(' ')[2] + ".meta"));
            if (k < 0) return;
            Console.WriteLine("hlslcc crashed on " + lines[k]);
            lines.RemoveRange(0, k + 1);
        }
    }

    public static Prog BuildProg(Pair p, string glsl)
    {
        var o = new Prog
        {
            Version = p.V.Version, Type = 4, Alu = p.V.Alu, Tex = p.V.Tex, Flow = p.V.Flow, Temp = p.V.Temp, SourceMap = p.V.SourceMap,
            Global = p.V.Global.Union(p.F.Global).ToList(), Local = p.V.Local.Union(p.F.Local).ToList(),
        };
        o.Code = Encoding.UTF8.GetBytes(glsl);
        var meta = File.ReadAllLines(p.Prefix + ".meta");
        var g = new Group { Name = "$Globals" };
        var empty = new Group();
        int off = 0;
        var seen = new HashSet<string>();
        void AddVars(Refl r)
        {
            foreach (var c in r.Cbs)
                foreach (var v in c.Vars.Where(v => v.IsMatrix == 0))
                    if (Used(glsl, v) && seen.Add(v.Name))
                    {
                        int elems = Math.Max(1, v.ArraySize);
                        g.Params.Add(new Param { Name = v.Name, Type = v.Type, Rows = 1, Cols = v.Cols, ArraySize = v.ArraySize, Index = off });
                        off += 16 * elems;
                    }
        }
        void AddMats(Refl r)
        {
            foreach (var c in r.Cbs)
                foreach (var v in c.Vars.Where(v => v.IsMatrix != 0))
                    if (Used(glsl, v) && seen.Add(v.Name))
                    {
                        int elems = Math.Max(1, v.ArraySize);
                        g.Params.Add(new Param { Name = v.Name, Type = v.Type, Rows = v.Rows, Cols = v.Cols, IsMatrix = 1, ArraySize = v.ArraySize, Index = off });
                        off += 16 * v.Rows * elems;
                    }
        }
        AddVars(p.RV); AddVars(p.RF); AddMats(p.RV); AddMats(p.RF);
        g.Used = off;
        o.Groups.Add(empty);
        if (g.Params.Count > 0) { o.Groups.Add(g); }
        // Textures: the location HLSLcc assigned, reported as "tex <name> <bind> <sampler> <ms> <dim> <uav>".
        foreach (var l in meta)
        {
            if (!l.StartsWith("tex ")) continue;
            var f = l.Split(' ');
            var name = f[1];
            if (o.Bindings.Any(b => b.Name == name)) continue;
            var t = p.RF.Texs.Concat(p.RV.Texs).FirstOrDefault(x => x.Name == name);
            o.Bindings.Add(new Binding { Name = name, Type = 0, Index = int.Parse(f[2]), Extra = 0, Extra2 = t?.Dim ?? 4 });
        }
        return o;
    }

    static bool Used(string glsl, CbVar v) => glsl.Contains(v.Name);

    public void Finish(AssetFileInfo a, AssetTypeValueField b, string nm, List<Pair> pairs)
    {
        var good = new List<Pair>();
        foreach (var p in pairs)
        {
            if (!File.Exists(p.Prefix + ".glsl")) { Failed++; Log.Add($"{nm}: FAILED {p.Key} -> {p.Prefix}"); continue; }
            p.Out = BuildProg(p, Fixups.Apply(nm, File.ReadAllText(p.Prefix + ".glsl"), Log));
            good.Add(p);
        }
        // One blob entry per distinct (vs, fs) text.
        var entries = new List<byte[]>();
        var byCode = new Dictionary<string, int>();
        var keep = new HashSet<AssetTypeValueField>();
        int variants = 0;
        foreach (var p in good)
        {
            var bytes = p.Out.Write();
            var k = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(bytes));
            if (!byCode.TryGetValue(k, out var idx)) { idx = entries.Count; entries.Add(bytes); byCode[k] = idx; }
            foreach (var t in p.Targets)
            {
                // The template entry itself serves its own keyword set; every other set gets a copy.
                AssetTypeValueField sp;
                if (Key(t.Template) == t.Key && !keep.Contains(t.Template)) sp = t.Template;
                else
                {
                    sp = ValueBuilder.DefaultValueFieldFromArrayTemplate(t.Array);
                    sp["m_ShaderHardwareTier"].AsSByte = t.Tier;
                    sp["m_ShaderRequirements"].AsInt = t.Template["m_ShaderRequirements"].AsInt;
                    FillKeywords(sp["m_GlobalKeywordIndices.Array"], t.Global);
                    FillKeywords(sp["m_LocalKeywordIndices.Array"], t.Local);
                    t.Array.Children.Add(sp);
                }
                sp["m_BlobIndex"].AsUInt = (uint)idx;
                sp["m_GpuProgramType"].AsSByte = 4;
                sp["m_Channels.m_Channels.Array"].Children.Clear();
                ClearSet(sp["m_Parameters"]);
                keep.Add(sp);
                variants++;
            }
        }
        foreach (var pa in b["m_ParsedForm.m_SubShaders.Array"].Children.SelectMany(s => s["m_Passes.Array"].Children))
        {
            pa["progVertex.m_SubPrograms.Array"].Children.RemoveAll(c => !keep.Contains(c));
            foreach (var st in new[] { "progFragment", "progGeometry", "progHull", "progDomain", "progRayTracing" })
                if (!pa[st].IsDummy) { pa[st + ".m_SubPrograms.Array"].Children.Clear(); ClearSet(pa[st + ".m_CommonParameters"]); }
            ClearSet(pa["progVertex.m_CommonParameters"]);
        }
        int hdr = 4 + entries.Count * 12;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(entries.Count);
        int off = hdr;
        foreach (var e in entries) { w.Write(off); w.Write(e.Length); w.Write(0); off += e.Length; }
        foreach (var e in entries) w.Write(e);
        var raw = ms.ToArray();
        var comp = new byte[LZ4Codec.MaximumOutputSize(raw.Length)];
        int cl = LZ4Codec.Encode(raw, 0, raw.Length, comp, 0, comp.Length, LZ4Level.L09_HC);
        b["compressedBlob.Array"].AsByteArray = comp.AsSpan(0, cl).ToArray();
        SetOne(b["platforms.Array"], 9);
        SetNest(b["offsets.Array"], 0);
        SetNest(b["compressedLengths.Array"], cl);
        SetNest(b["decompressedLengths.Array"], raw.Length);
        a.SetNewData(b);
        Log.Add($"{nm}: {variants} variants, {entries.Count} programs, {raw.Length / 1024} KB");
    }

    static void FillKeywords(AssetTypeValueField arr, ushort[] values)
    {
        arr.Children.Clear();
        foreach (var v in values)
        {
            var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            e.AsUShort = v;
            arr.Children.Add(e);
        }
    }

    static void ClearSet(AssetTypeValueField s)
    {
        if (s.IsDummy) return;
        foreach (var c in s.Children) c["Array"].Children.Clear();
    }
    static void SetOne(AssetTypeValueField arr, int v) { arr.Children.RemoveRange(1, arr.Children.Count - 1); arr.Children[0].AsInt = v; }
    static void SetNest(AssetTypeValueField arr, long v)
    {
        arr.Children.RemoveRange(1, arr.Children.Count - 1);
        var inner = arr.Children[0]["Array"];
        if (inner.IsDummy) { arr.Children[0].AsUInt = (uint)v; return; }
        inner.Children.RemoveRange(1, inner.Children.Count - 1);
        inner.Children[0].AsUInt = (uint)v;
    }

    // Golden test: translate a shader's D3D slice and print it beside the one Unity shipped.
    public static void Gold(string file, string shader, string tpk, string tmp, string cli)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(tpk);
        var i = am.LoadAssetsFile(file, false);
        am.LoadClassDatabaseFromPackage(i.file.Metadata.UnityVersion);
        Directory.CreateDirectory(tmp);
        var (a, b) = Shaders.Find(am, i, shader).First();
        var c = new Converter { Tmp = tmp, Cli = cli };
        var d = Shaders.Slice(b, 4, out _);
        var pa = b["m_ParsedForm.m_SubShaders.Array"].Children[0]["m_Passes.Array"].Children[0];
        var names = pa["m_NameIndices.Array"].Children.ToDictionary(e => e["second"].AsInt, e => e["first"].AsString);
        var sp = pa["progVertex.m_SubPrograms.Array"].Children.First(x => IsD3DVS(x["m_GpuProgramType"].AsSByte));
        var fsp = pa["progFragment.m_SubPrograms.Array"].Children.First(x => IsD3DFS(x["m_GpuProgramType"].AsSByte) && Key(x) == Key(sp));
        var t = Prog.Table(d);
        int vi = (int)sp["m_BlobIndex"].AsUInt, fi = (int)fsp["m_BlobIndex"].AsUInt;
        var v = Prog.Read(d, t[vi].off, t[vi].len); var f = Prog.Read(d, t[fi].off, t[fi].len);
        var rv = new Refl(); var rf = new Refl();
        rv.AddProg(v); rf.AddProg(f);
        rv.AddCommon(pa["progVertex.m_CommonParameters"], names, null);
        rf.AddCommon(pa["progFragment.m_CommonParameters"], names, null);
        var dv = Dxbc.Strip(v.Code); var df = Dxbc.Strip(f.Code);
        var pre = Path.Combine(tmp, "gold");
        File.WriteAllBytes(pre + ".vs", Dxbc.WithRdef(dv, rv.Rdef(Dxbc.VersionToken(dv))));
        File.WriteAllBytes(pre + ".fs", Dxbc.WithRdef(df, rf.Rdef(Dxbc.VersionToken(df))));
        var list = Path.Combine(tmp, "gold.txt");
        File.WriteAllText(list, $"{pre}.vs {pre}.fs {pre}\n");
        c.RunCli(list);
        Console.WriteLine(File.ReadAllText(pre + ".meta"));
        if (File.Exists(pre + ".glsl"))
        {
            var pair = new Pair { V = v, F = f, RV = rv, RF = rf, Prefix = pre };
            var txt = File.ReadAllText(pre + ".glsl");
            Console.WriteLine(txt);
            Console.WriteLine(BuildProg(pair, txt));
        }
    }
}
