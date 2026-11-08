using Mono.Cecil;

// Lists the P/Invoke entry points of an assembly ("pinv <dll>"), or with "sig" their managed signatures.
static class PInv
{
    public static void Run(string dll, bool sig = false)
    {
        var m = ModuleDefinition.ReadModule(dll);
        var ms = m.GetTypes().SelectMany(t => t.Methods).Where(x => x.IsPInvokeImpl);
        string Entry(MethodDefinition x) => string.IsNullOrEmpty(x.PInvokeInfo.EntryPoint) ? x.Name : x.PInvokeInfo.EntryPoint;
        if (!sig)
        {
            foreach (var e in ms.Select(Entry).Distinct().OrderBy(x => x)) Console.WriteLine(e);
            return;
        }
        foreach (var x in ms.OrderBy(Entry))
            Console.WriteLine(Entry(x) + "\t" + x.PInvokeInfo.Module.Name + "\t" + x.ReturnType.Name + "(" +
                string.Join(", ", x.Parameters.Select(p => p.ParameterType.FullName + " " + p.Name)) + ")");
    }
}

// "structs <dll>": the value types whose layout crosses into native code (each P/Invoke parameter's
// element type, and the types of their fields), with their fields in order.
static class Structs
{
    public static void Run(string dll)
    {
        var m = ModuleDefinition.ReadModule(dll);
        var seen = new HashSet<string>();
        var queue = new Queue<TypeDefinition>();
        void Want(TypeReference t)
        {
            while (t is TypeSpecification s) t = s.ElementType;
            var d = t.Resolve();
            if (d == null || !d.IsValueType || d.IsEnum || d.IsPrimitive || d.Module != m) return;
            if (seen.Add(d.FullName)) queue.Enqueue(d);
        }
        foreach (var x in m.GetTypes().SelectMany(t => t.Methods).Where(x => x.IsPInvokeImpl))
            foreach (var p in x.Parameters) Want(p.ParameterType);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            Console.WriteLine("struct " + d.FullName + (d.IsExplicitLayout ? " [explicit]" : ""));
            foreach (var f in d.Fields.Where(f => !f.IsStatic))
            {
                Console.WriteLine("    " + f.FieldType.FullName + " " + f.Name + (f.MarshalInfo != null ? "  [" + f.MarshalInfo.NativeType + (f.MarshalInfo is FixedArrayMarshalInfo fa ? " " + fa.Size : "") + "]" : ""));
                Want(f.FieldType);
            }
        }
    }
}
