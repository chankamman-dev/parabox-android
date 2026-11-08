using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Text;
using System.Text.RegularExpressions;

// Turns the Windows player data into an Android player image: every serialized file is stamped for
// Android, its shaders are converted, and the Addressables catalog is pointed at external storage.
static class Retarget
{
    const int Android = 13;
    const int Gles3 = 11;

    static Converter NewConverter(string tmp, string cli)
    {
        Directory.CreateDirectory(tmp);
        return new Converter { Tmp = tmp, Cli = cli };
    }

    internal static void Convert(Converter c, AssetsManager am, AssetsFileInstance i, string tag)
    {
        int seq = 0;
        var listPath = Path.Combine(c.Tmp, "list.txt");
        List<(AssetFileInfo a, AssetTypeValueField b, string name, List<Pair> pairs)> jobs;
        using (var list = new StreamWriter(listPath)) jobs = c.Prepare(am, i, list, ref seq);
        if (jobs.Count == 0) return;
        Console.WriteLine($"{tag}: {jobs.Count} shaders, {jobs.Sum(j => j.pairs.Count)} variants");
        c.RunCli(listPath);
        foreach (var (a, b, n, p) in jobs) c.Finish(a, b, n, p);
        if (Environment.GetEnvironmentVariable("LOM_KEEP") == null) foreach (var f in Directory.GetFiles(c.Tmp)) File.Delete(f); else throw new Exception("kept");
    }

    public static void Data(string src, string dst, string tmp, string cli, string tpk, string androidResources)
    {
        Directory.CreateDirectory(dst);
        Directory.CreateDirectory(Path.Combine(dst, "Resources"));
        var c = NewConverter(tmp, cli);
        var am = new AssetsManager();
        am.LoadClassPackage(tpk);
        var names = Directory.GetFiles(src).Select(Path.GetFileName).Where(n =>
            n == "globalgamemanagers" || n == "globalgamemanagers.assets" || n == "resources.assets"
            || (n.StartsWith("level") && !n.EndsWith(".resS"))
            || (n.StartsWith("sharedassets") && n.EndsWith(".assets"))).OrderBy(x => x).ToList();
        foreach (var n in names.Concat(new[] { Path.Combine("Resources", "unity_builtin_extra") }))
        {
            var i = am.LoadAssetsFile(Path.Combine(src, n), false);
            am.LoadClassDatabaseFromPackage(i.file.Metadata.UnityVersion);
            Convert(c, am, i, n);
            if (n == "globalgamemanagers")
                foreach (var a in i.file.GetAssetsOfType(AssetClassID.BuildSettings))
                {
                    var b = am.GetBaseField(i, a);
                    var arr = b["m_GraphicsAPIs.Array"];
                    arr.Children.RemoveRange(1, arr.Children.Count - 1);
                    arr.Children[0].AsInt = Gles3;
                    a.SetNewData(b);
                }
            if (n == "globalgamemanagers")
                foreach (var a in i.file.GetAssetsOfType(AssetClassID.AudioManager))
                {
                    var b = am.GetBaseField(i, a);
                    Console.WriteLine("AudioManager m_DisableAudio=" + b["m_DisableAudio"].AsBool);
                    b["m_DisableAudio"].AsBool = false;
                    a.SetNewData(b);
                }
            i.file.Metadata.TargetPlatform = Android;
            using (var fs = File.Create(Path.Combine(dst, n)))
            using (var w = new AssetsFileWriter(fs))
                i.file.Write(w);
            am.UnloadAll();
            am.LoadClassPackage(tpk);
            Console.WriteLine("wrote " + n);
        }
        foreach (var f in Directory.GetFiles(src))
        {
            var n = Path.GetFileName(f);
            if (n.EndsWith(".resS") || n.EndsWith(".resource") || n is "app.info" or "RuntimeInitializeOnLoads.json" or "ScriptingAssemblies.json")
                File.Copy(f, Path.Combine(dst, n), true);
        }
        File.Copy(androidResources, Path.Combine(dst, "Resources", "unity default resources"), true);
        var boot = File.ReadAllLines(Path.Combine(src, "boot.config")).Where(l => !l.StartsWith("scripting-backend=")).Append("scripting-backend=il2cpp");
        File.WriteAllText(Path.Combine(dst, "boot.config"), string.Join("\n", boot) + "\n");
        File.WriteAllLines(Path.Combine(dst, "shader-report.txt"), c.Log);
        VideoShader.Add(Path.Combine(dst, "Resources", "unity_builtin_extra"), Path.Combine(dst, "globalgamemanagers"), tpk);
        Console.WriteLine($"variants={c.Total} failed={c.Failed} skipped={c.Skipped}");
    }

    public static void Bundles(string src, string dst, string tmp, string cli, string tpk)
    {
        Directory.CreateDirectory(dst);
        var c = NewConverter(tmp, cli);
        int n = 0;
        foreach (var f in Directory.GetFiles(src, "*.bundle"))
        {
            var o = Path.Combine(dst, Path.GetFileName(f));
            if (File.Exists(o) && new FileInfo(o).Length > 0) continue;
            var am = new AssetsManager();
            am.LoadClassPackage(tpk);
            var bundle = am.LoadBundleFile(f, true);
            am.LoadClassDatabaseFromPackage(bundle.file.Header.EngineVersion);
            foreach (var dir in bundle.file.BlockAndDirInfo.DirectoryInfos)
            {
                if ((dir.Flags & 4) == 0) continue;
                var i = am.LoadAssetsFileFromBundle(bundle, dir.Name);
                Convert(c, am, i, Path.GetFileName(f));
                i.file.Metadata.TargetPlatform = Android;
                dir.SetNewData(i.file);
            }
            var part = o + ".part";
            using (var fs = File.Create(part))
            using (var w = new AssetsFileWriter(fs))
                bundle.file.Write(w);
            am.UnloadAll();
            File.Move(part, o, true);
            if (++n % 20 == 0) Console.WriteLine($"{n} bundles");
        }
        File.AppendAllLines(Path.Combine(dst, "..", "shader-report-bundles.txt"), c.Log);
        Console.WriteLine($"bundles={n} variants={c.Total} failed={c.Failed} skipped={c.Skipped}");
    }

    // The retargeted bundles are written uncompressed; LZ4 brings them back to the shipped size.
    public static void Pack(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        var files = Directory.GetFiles(src, "*.bundle");
        int done = 0;
        long inBytes = 0, outBytes = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 6 }, f =>
        {
            var o = Path.Combine(dst, Path.GetFileName(f));
            if (!File.Exists(o) || new FileInfo(o).Length == 0)
            {
                var am = new AssetsManager();
                var bundle = am.LoadBundleFile(f, false);
                var part = o + ".part";
                using (var fs = File.Create(part))
                using (var w = new AssetsFileWriter(fs))
                    bundle.file.Pack(w, AssetBundleCompressionType.LZ4);
                am.UnloadAll();
                File.Move(part, o, true);
            }
            Interlocked.Add(ref inBytes, new FileInfo(f).Length);
            Interlocked.Add(ref outBytes, new FileInfo(o).Length);
            if (Interlocked.Increment(ref done) % 40 == 0) Console.WriteLine($"{done} packed");
        });
        Console.WriteLine($"packed {done}: {inBytes >> 20} MB -> {outBytes >> 20} MB");
    }

    // Bundles live on external storage; the catalog stays in the package.
    public static void Catalog(string srcAa, string dstAa, string root)
    {
        Directory.CreateDirectory(dstAa);
        var t = File.ReadAllText(Path.Combine(srcAa, "catalog.json"));
        const string token = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}\\\\";
        int ids = Regex.Matches(t, Regex.Escape(token)).Count;
        t = t.Replace(token + "StandaloneWindows64\\\\", root + "/");
        // Rewritten bundles no longer match the CRCs the build recorded.
        var m = Regex.Match(t, "\"m_ExtraDataString\":\"([^\"]*)\"");
        var extra = System.Convert.FromBase64String(m.Groups[1].Value);
        int crcs = ZeroCrcs(extra);
        t = t.Remove(m.Groups[1].Index, m.Groups[1].Length).Insert(m.Groups[1].Index, System.Convert.ToBase64String(extra));
        File.WriteAllText(Path.Combine(dstAa, "catalog.json"), t);
        File.Copy(Path.Combine(srcAa, "settings.json"), Path.Combine(dstAa, "settings.json"), true);
        Console.WriteLine($"catalog: {ids} locations repointed, {crcs} CRCs cleared");
    }

    static int ZeroCrcs(byte[] d)
    {
        // The serialized request options are JSON stored as UTF-16LE.
        var key = Encoding.Unicode.GetBytes("\"m_Crc\":");
        int n = 0;
        for (int i = 0; i + key.Length + 2 <= d.Length; i++)
        {
            if (!d.AsSpan(i, key.Length).SequenceEqual(key)) continue;
            int p = i + key.Length, s = p;
            while (p + 1 < d.Length && d[p] >= '0' && d[p] <= '9' && d[p + 1] == 0) p += 2;
            if (p - s > 2 || d[s] != '0') n++;
            d[s] = (byte)'0';
            for (int k = s + 2; k < p; k += 2) d[k] = (byte)' ';
        }
        return n;
    }
}
