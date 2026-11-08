using AssetsTools.NET;
using AssetsTools.NET.Extra;

// Prints one class's objects as a flat field list, for poking at manager settings.
static class Inspect
{
    public static void Run(string file, int classId, string filter, string tpk)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(tpk);
        var i = am.LoadAssetsFile(file, false);
        am.LoadClassDatabaseFromPackage(i.file.Metadata.UnityVersion);
        for (int k = 0; k < i.file.Metadata.Externals.Count; k++) Console.WriteLine($"external {k + 1}: {i.file.Metadata.Externals[k].PathName}");
        foreach (var a in i.file.GetAssetsOfType(classId))
        {
            var b = am.GetBaseField(i, a);
            Walk(b, "", filter);
        }
    }

    static void Walk(AssetTypeValueField f, string path, string filter)
    {
        foreach (var c in f.Children)
        {
            var p = path + "/" + c.FieldName;
            if (c.TemplateField.HasValue && c.Value != null && !c.TemplateField.IsArray)
            {
                var line = p + " = " + c.AsString;
                if (filter == "*" || line.Contains(filter)) Console.WriteLine(line);
            }
            if (c.Children != null && c.Children.Count > 0) Walk(c, p, filter);
        }
    }
}
