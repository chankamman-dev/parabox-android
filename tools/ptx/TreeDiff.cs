using AssetsTools.NET;
using AssetsTools.NET.Extra;

// Prints the field trees classdata.tpk gives for one class under two Unity versions, as flat paths.
static class TreeDiff
{
    static List<string> Flat(AssetsManager am, string ver, int cls)
    {
        am.LoadClassDatabaseFromPackage(ver);
        var t = am.ClassDatabase.FindAssetClassByID(cls);
        var tmp = new AssetTypeTemplateField();
        tmp.FromClassDatabase(am.ClassDatabase, t);
        var o = new List<string>();
        void W(AssetTypeTemplateField f, string p)
        {
            var path = p == "" ? f.Name : p + "." + f.Name;
            o.Add(path + " : " + f.Type + (f.IsAligned ? " (align)" : ""));
            foreach (var c in f.Children) W(c, path);
        }
        W(tmp, "");
        return o;
    }

    public static void Run(string tpk, string va, string vb, string classes)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(tpk);
        foreach (var c in classes.Split(',').Select(int.Parse))
        {
            var a = Flat(am, va, c);
            var b = Flat(am, vb, c);
            Console.WriteLine($"== class {c} {va}:{a.Count} {vb}:{b.Count}");
            foreach (var x in a.Except(b)) Console.WriteLine("  only " + va + ": " + x);
            foreach (var x in b.Except(a)) Console.WriteLine("  only " + vb + ": " + x);
        }
    }
}
