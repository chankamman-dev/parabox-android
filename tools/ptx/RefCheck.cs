using Mono.Cecil;

// Resolves every member an assembly references against the staged set and prints what is missing.
static class RefCheck
{
    public static void Run(string dll, string dir)
    {
        var r = new DefaultAssemblyResolver(); r.AddSearchDirectory(dir);
        var m = ModuleDefinition.ReadModule(dll, new ReaderParameters { AssemblyResolver = r });
        var bad = new SortedDictionary<string, SortedSet<string>>();
        foreach (var t in m.GetTypes())
            foreach (var me in t.Methods.Where(x => x.HasBody))
                foreach (var ins in me.Body.Instructions)
                {
                    object res = null;
                    try
                    {
                        if (ins.Operand is MethodReference mr) res = mr.Resolve();
                        else if (ins.Operand is FieldReference fr) res = fr.Resolve();
                        else if (ins.Operand is TypeReference tr) res = tr.Resolve();
                        else continue;
                    }
                    catch { }
                    if (res == null)
                    {
                        var k = ((MemberReference)ins.Operand).FullName;
                        if (!bad.TryGetValue(k, out var s)) bad[k] = s = new();
                        s.Add(t.FullName + "::" + me.Name);
                    }
                }
        foreach (var kv in bad) Console.WriteLine($"{kv.Key}\n    <- {string.Join(", ", kv.Value.Take(4))}");
        Console.WriteLine($"unresolved={bad.Count}");
    }
}
