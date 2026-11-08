using AssetsTools.NET;
using AssetsTools.NET.Extra;
using K4os.Compression.LZ4;
using System.Text;

// Android's VideoPlayer decodes into an external OES texture and blits it with Hidden/VideoDecodeAndroid.
// A Windows build never includes that shader, and its source is hand-written GLSL, so it is assembled
// here from the already converted Hidden/VideoDecode: same parameters, two passes, new programs.
static class VideoShader
{
    const string Header = @"#define UNITY_SUPPORTS_UNIFORM_LOCATION 1
#if UNITY_SUPPORTS_UNIFORM_LOCATION
#define UNITY_LOCATION(x) layout(location = x)
#else
#define UNITY_LOCATION(x)
#endif
";

    static string Vertex(bool split) => @"#ifdef VERTEX
#version 300 es

uniform 	vec4 _MainTex_ST;
uniform 	vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
uniform 	vec4 hlslcc_mtx4x4unity_MatrixVP[4];
in highp vec4 in_POSITION0;
in highp vec2 in_TEXCOORD0;
" + (split ? "out highp vec3 vs_TEXCOORD0;\n" : "out highp vec2 vs_TEXCOORD0;\n") + @"void main()
{
    vec4 w = hlslcc_mtx4x4unity_ObjectToWorld[0] * in_POSITION0.xxxx + hlslcc_mtx4x4unity_ObjectToWorld[1] * in_POSITION0.yyyy
        + hlslcc_mtx4x4unity_ObjectToWorld[2] * in_POSITION0.zzzz + hlslcc_mtx4x4unity_ObjectToWorld[3];
    gl_Position = hlslcc_mtx4x4unity_MatrixVP[0] * w.xxxx + hlslcc_mtx4x4unity_MatrixVP[1] * w.yyyy
        + hlslcc_mtx4x4unity_MatrixVP[2] * w.zzzz + hlslcc_mtx4x4unity_MatrixVP[3] * w.wwww;
" + (split
            ? "    vs_TEXCOORD0.xz = vec2(0.5 * in_TEXCOORD0.x * _MainTex_ST.x, in_TEXCOORD0.y * _MainTex_ST.y) + _MainTex_ST.zw;\n    vs_TEXCOORD0.y = vs_TEXCOORD0.x + 0.5 * _MainTex_ST.x;\n"
            : "    vs_TEXCOORD0 = in_TEXCOORD0 * _MainTex_ST.xy + _MainTex_ST.zw;\n") + @"}

#endif
";

    static string Fragment(bool split, bool linear) => @"#ifdef FRAGMENT
#version 300 es
#extension GL_OES_EGL_image_external_essl3 : require

precision highp float;
precision highp int;
" + Header + @"UNITY_LOCATION(0) uniform mediump samplerExternalOES _MainTex;
" + (split ? "in highp vec3 vs_TEXCOORD0;\n" : "in highp vec2 vs_TEXCOORD0;\n") + @"layout(location = 0) out highp vec4 SV_Target0;
vec4 AdjustForColorSpace(vec4 c)
{
" + (linear ? "    vec3 s = c.rgb;\n    return vec4(s * (s * (s * 0.305306011 + 0.682171111) + 0.012522878), c.a);\n" : "    return c;\n") + @"}
void main()
{
" + (split
            ? "    SV_Target0.rgb = AdjustForColorSpace(texture(_MainTex, vs_TEXCOORD0.xz)).rgb;\n    SV_Target0.a = texture(_MainTex, vs_TEXCOORD0.yz).g;\n"
            : "    SV_Target0 = AdjustForColorSpace(texture(_MainTex, vs_TEXCOORD0));\n") + @"}

#endif
";

    public static void Add(string extraPath, string ggmPath, string tpk)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(tpk);
        var i = am.LoadAssetsFile(extraPath, false);
        am.LoadClassDatabaseFromPackage(i.file.Metadata.UnityVersion);
        if (Shaders.Find(am, i, "Hidden/VideoDecodeAndroid").Any()) { Console.WriteLine("already present"); return; }
        var (srcInfo, _) = Shaders.Find(am, i, "Hidden/VideoDecode").First();
        var b = am.GetBaseField(i, srcInfo);

        var blob = Shaders.Slice(b, 9, out _);
        var table = Prog.Table(blob);
        var template = Prog.Read(blob, table[0].off, table[0].len);

        var progs = new List<byte[]>();
        foreach (var split in new[] { false, true })
            foreach (var linear in new[] { false, true })
            {
                var p = Prog.Read(blob, table[0].off, table[0].len);
                p.Global = new List<string>();
                p.Local = linear ? new List<string> { "ADJUST_TO_LINEARSPACE" } : new List<string>();
                p.Code = Encoding.UTF8.GetBytes(Vertex(split) + Fragment(split, linear));
                p.Bindings = template.Bindings.Where(x => x.Name == "_MainTex").ToList();
                progs.Add(p.Write());
            }

        b["m_ParsedForm.m_Name"].AsString = "Hidden/VideoDecodeAndroid";
        b["m_Name"].AsString = "Hidden/VideoDecodeAndroid";
        var passes = b["m_ParsedForm.m_SubShaders.Array"].Children[0]["m_Passes.Array"];
        var keep = passes.Children[0];
        // Both passes share one layout; the second is a fresh read of the first.
        var second = am.GetBaseField(i, srcInfo)["m_ParsedForm.m_SubShaders.Array"].Children[0]["m_Passes.Array"].Children[0];
        passes.Children.Clear();
        passes.Children.Add(keep);
        passes.Children.Add(second);
        string[] names = { "RGBAExternal_To_RGBA", "RGBASplitExternal_To_RGBA" };
        for (int p = 0; p < 2; p++)
        {
            var pa = passes.Children[p];
            pa["m_State.m_Name"].AsString = names[p];
            var subs = pa["progVertex.m_SubPrograms.Array"].Children;
            // Keep the plain variant and the ADJUST_TO_LINEARSPACE one.
            var plain = subs.First(s => s["m_LocalKeywordIndices.Array"].Children.Count == 0 && s["m_GlobalKeywordIndices.Array"].Children.Count == 0);
            var lin = subs.First(s => s["m_LocalKeywordIndices.Array"].Children.Count == 1 && s["m_GlobalKeywordIndices.Array"].Children.Count == 0);
            plain["m_BlobIndex"].AsUInt = (uint)(p * 2);
            lin["m_BlobIndex"].AsUInt = (uint)(p * 2 + 1);
            subs.Clear();
            subs.Add(plain);
            subs.Add(lin);
        }

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(progs.Count);
        int off = 4 + progs.Count * 12;
        foreach (var e in progs) { w.Write(off); w.Write(e.Length); w.Write(0); off += e.Length; }
        foreach (var e in progs) w.Write(e);
        var raw = ms.ToArray();
        var comp = new byte[LZ4Codec.MaximumOutputSize(raw.Length)];
        int cl = LZ4Codec.Encode(raw, 0, raw.Length, comp, 0, comp.Length, LZ4Level.L09_HC);
        b["compressedBlob.Array"].AsByteArray = comp.AsSpan(0, cl).ToArray();
        b["offsets.Array"].Children[0]["Array"].Children[0].AsUInt = 0;
        b["compressedLengths.Array"].Children[0]["Array"].Children[0].AsUInt = (uint)cl;
        b["decompressedLengths.Array"].Children[0]["Array"].Children[0].AsUInt = (uint)raw.Length;

        // Built-in extra resources are looked up by fixed path id, the one the Editor's own copy uses.
        const long pathId = 16003;
        if (i.file.AssetInfos.Any(x => x.PathId == pathId)) throw new InvalidOperationException("path id 16003 already used");
        var info = AssetFileInfo.Create(i.file, pathId, (int)AssetClassID.Shader, 0xffff, am.ClassDatabase);
        info.SetNewData(b);
        i.file.Metadata.AddAssetInfo(info);
        var tmp = extraPath + ".tmp";
        using (var fs = File.Create(tmp))
        using (var aw = new AssetsFileWriter(fs))
            i.file.Write(aw);
        am.UnloadAll();
        File.Move(tmp, extraPath, true);
        Console.WriteLine($"Hidden/VideoDecodeAndroid added as path id {pathId}");

        // Register it where the player looks for built-in shaders.
        am = new AssetsManager();
        am.LoadClassPackage(tpk);
        var g = am.LoadAssetsFile(ggmPath, false);
        am.LoadClassDatabaseFromPackage(g.file.Metadata.UnityVersion);
        int fileId = g.file.Metadata.Externals.FindIndex(x => x.PathName.EndsWith("unity_builtin_extra")) + 1;
        var gs = g.file.GetAssetsOfType(AssetClassID.GraphicsSettings).First();
        var gb = am.GetBaseField(g, gs);
        var list = gb["m_AlwaysIncludedShaders.Array"];
        var entry = ValueBuilder.DefaultValueFieldFromArrayTemplate(list);
        entry["m_FileID"].AsInt = fileId;
        entry["m_PathID"].AsLong = pathId;
        list.Children.Add(entry);
        gs.SetNewData(gb);
        // Shader.Find goes through the ScriptMapper, which a Windows build left pointing at nothing.
        var sm = g.file.GetAssetsOfType(AssetClassID.ScriptMapper).First();
        var smb = am.GetBaseField(g, sm);
        int mapped = 0;
        foreach (var e in smb["m_Shaders.m_ObjectToName.Array"].Children)
            if (e["second"].AsString == "Hidden/VideoDecodeAndroid")
            {
                e["first.m_FileID"].AsInt = fileId;
                e["first.m_PathID"].AsLong = pathId;
                mapped++;
            }
        sm.SetNewData(smb);
        Console.WriteLine($"ScriptMapper: {mapped} entry pointed at the new shader");
        tmp = ggmPath + ".tmp";
        using (var fs = File.Create(tmp))
        using (var aw = new AssetsFileWriter(fs))
            g.file.Write(aw);
        am.UnloadAll();
        File.Move(tmp, ggmPath, true);
        Console.WriteLine($"GraphicsSettings: always included shaders = {list.Children.Count}, builtin extra file id {fileId}");
    }
}
