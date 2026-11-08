using AssetsTools.NET;
using AssetsTools.NET.Extra;

// Compares the per-class type hashes stored in two sets of player files. The hash is Unity's digest of
// a class's serialized layout, so equal hashes mean a player of one version can read the other's data.
static class TypeCmp
{
    static Dictionary<int, HashSet<string>> Collect(string[] files)
    {
        var d = new Dictionary<int, HashSet<string>>();
        foreach (var f in files)
        {
            if (!File.Exists(f)) continue;
            try
            {
                using var fs = File.OpenRead(f);
                var af = new AssetsFile();
                af.Read(new AssetsFileReader(fs));
                foreach (var t in af.Metadata.TypeTreeTypes)
                {
                    if (t.TypeId == 114) continue; // MonoBehaviour hashes depend on the script, not the engine
                    if (!d.TryGetValue(t.TypeId, out var s)) d[t.TypeId] = s = new();
                    s.Add(t.TypeHash.ToString());
                }
            }
            catch (Exception e) { Console.WriteLine("ERR " + f + " " + e.Message); }
        }
        return d;
    }

    public static void Run(string listA, string listB)
    {
        var a = Collect(listA.Split(';'));
        var b = Collect(listB.Split(';'));
        int same = 0, diff = 0, only = 0;
        foreach (var kv in a.OrderBy(k => k.Key))
        {
            var name = ((AssetClassID)kv.Key).ToString();
            if (!b.TryGetValue(kv.Key, out var hb)) { only++; Console.WriteLine($"ONLY-A {kv.Key} {name} {string.Join(",", kv.Value)}"); continue; }
            if (kv.Value.SetEquals(hb)) { same++; Console.WriteLine($"same   {kv.Key} {name}"); }
            else { diff++; Console.WriteLine($"DIFF   {kv.Key} {name} A={string.Join(",", kv.Value)} B={string.Join(",", hb)}"); }
        }
        Console.WriteLine($"same={same} diff={diff} onlyA={only}");
    }
}
