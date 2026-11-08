using Mono.Cecil;

// Prints the IL of every method whose declaring type's full name contains a filter (nested
// iterator classes included), for reading game code without a decompiler.
static class IlDump
{
    public static void Run(string dll, string filter)
    {
        var m = ModuleDefinition.ReadModule(dll);
        foreach (var t in m.GetTypes().Where(t => t.FullName.Contains(filter)))
            foreach (var md in t.Methods.Where(x => x.HasBody))
            {
                Console.WriteLine($"== {t.FullName}::{md.Name}");
                foreach (var i in md.Body.Instructions) Console.WriteLine("  " + i);
            }
    }
}

// Lists every call into a namespace (e.g. Steamworks) from all assemblies in a folder, grouped by
// callee, with the calling methods.
static class CallSites
{
    public static void Run(string dir, string ns)
    {
        var hits = new SortedDictionary<string, SortedSet<string>>();
        foreach (var f in Directory.GetFiles(dir, "*.dll"))
        {
            ModuleDefinition m;
            try { m = ModuleDefinition.ReadModule(f); } catch { continue; }
            if (m.Name.StartsWith(ns, StringComparison.OrdinalIgnoreCase) || m.Name.Contains("steamworks")) continue;
            foreach (var t in m.GetTypes())
                foreach (var md in t.Methods.Where(x => x.HasBody))
                    foreach (var i in md.Body.Instructions)
                        if (i.Operand is MethodReference r && r.DeclaringType.Namespace == ns)
                        {
                            var key = r.DeclaringType.Name + "::" + r.Name;
                            if (!hits.TryGetValue(key, out var set)) hits[key] = set = new SortedSet<string>();
                            set.Add($"{m.Name} {t.FullName}::{md.Name}");
                        }
        }
        foreach (var kv in hits)
        {
            Console.WriteLine(kv.Key);
            foreach (var s in kv.Value) Console.WriteLine("    " + s);
        }
    }
}
