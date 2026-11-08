using System.Text;

// Reads the parts of Wwise's HIRC hierarchy (bank version 140) that set loudness: each node's
// parent, its output bus and its Volume property (dB). Layout cross-checked against the hierarchy
// itself: a node is trusted only when its parent id resolves to another node (or 0).
class HircNode { public uint Id, Parent, Bus; public byte Type; public float Volume; public List<uint> Sources = new(); public byte[] Body; }

static class Hirc
{
    public static Dictionary<uint, HircNode> Load(string bankDir, out int bad)
    {
        var raw = new List<(byte type, uint id, byte[] body)>();
        foreach (var f in Directory.GetFiles(bankDir, "*.bnk"))
        {
            var b = File.ReadAllBytes(f);
            int o = 0;
            while (o + 8 <= b.Length)
            {
                var t = Encoding.ASCII.GetString(b, o, 4);
                int l = BitConverter.ToInt32(b, o + 4);
                if (t == "HIRC")
                {
                    int p = o + 8, n = BitConverter.ToInt32(b, p);
                    p += 4;
                    for (int k = 0; k < n; k++)
                    {
                        byte type = b[p];
                        int size = BitConverter.ToInt32(b, p + 1);
                        uint id = BitConverter.ToUInt32(b, p + 5);
                        raw.Add((type, id, b.AsSpan(p + 9, size - 4).ToArray()));
                        p += 5 + size;
                    }
                }
                o += 8 + l;
            }
        }
        var ids = new HashSet<uint>(raw.Select(r => r.id));
        var nodes = new Dictionary<uint, HircNode>();
        bad = 0;
        foreach (var (type, id, body) in raw)
        {
            int start;
            var node = new HircNode { Id = id, Type = type, Body = body };
            switch (type)
            {
                case 2: // Sound: source data, then the node parameters.
                    node.Sources.Add(BitConverter.ToUInt32(body, 5));
                    start = 14;
                    break;
                case 5: case 6: case 7: case 9: // containers and actor-mixer
                    start = 0;
                    break;
                case 10: case 12: case 13: // music segment / switch / playlist: one flags byte first
                    start = 1;
                    break;
                case 11: // music track
                    {
                        int p = 1;
                        int ns = BitConverter.ToInt32(body, p); p += 4;
                        for (int k = 0; k < ns; k++) { node.Sources.Add(BitConverter.ToUInt32(body, p + 5)); p += 14; }
                        int np = BitConverter.ToInt32(body, p); p += 4;
                        p += np * 44;
                        if (np > 0) p += 4;
                        int na = BitConverter.ToInt32(body, p); p += 4;
                        for (int k = 0; k < na; k++) { int pts = BitConverter.ToInt32(body, p + 8); p += 12 + pts * 12; }
                        start = p;
                        break;
                    }
                case 8: // bus: parent bus (plus an audio device when it has none), then its props
                    {
                        int p = 0;
                        node.Parent = BitConverter.ToUInt32(body, p); p += 4;
                        if (node.Parent == 0) p += 4;
                        int c = body[p++];
                        for (int k = 0; k < c; k++)
                            if (body[p + k] == 0 || body[p + k] == 5) node.Volume += BitConverter.ToSingle(body, p + c + 4 * k);
                        nodes[id] = node;
                        continue;
                    }
                case 3: case 4: // actions and events: kept whole for AudioEvents
                    nodes[id] = node;
                    continue;
                default:
                    continue;
            }
            if (!ParseBase(body, start, node, ids)) { bad++; continue; }
            nodes[id] = node;
        }
        return nodes;
    }

    // NodeBaseParams: fx params, (metadata fx, attachment flag), override bus, direct parent, flags, prop bundle.
    static bool ParseBase(byte[] b, int p, HircNode n, HashSet<uint> ids)
    {
        try
        {
            p++; // bIsOverrideParentFX
            int numFx = b[p++];
            if (numFx > 0) p += 1 + numFx * 7;
            // Bank 140 (Wwise 2021) adds metadata effects and the attachment flag; bank 135 has neither.
            foreach (bool meta in new[] { true, false })
            {
                int q = p;
                if (meta) { q++; q += b[q] * 6 + 1; q++; }
                uint bus = BitConverter.ToUInt32(b, q), parent = BitConverter.ToUInt32(b, q + 4);
                if (parent != 0 && !ids.Contains(parent)) continue;
                q += 8 + 1;
                int cProps = b[q++];
                var pid = b.AsSpan(q, cProps).ToArray();
                q += cProps;
                float vol = 0;
                for (int k = 0; k < cProps; k++)
                    if (pid[k] == 0) vol = BitConverter.ToSingle(b, q + 4 * k);
                if (Math.Abs(vol) > 96) continue;
                n.Bus = bus; n.Parent = parent; n.Volume = vol;
                return true;
            }
        }
        catch (ArgumentOutOfRangeException) { }
        catch (IndexOutOfRangeException) { }
        return false;
    }

    // Total gain (dB) from a source's node up through its parents, plus its bus chain.
    public static Dictionary<uint, float> SourceGains(Dictionary<uint, HircNode> nodes)
    {
        var res = new Dictionary<uint, float>();
        foreach (var n in nodes.Values)
            foreach (var src in n.Sources)
            {
                float db = 0;
                var seen = new HashSet<uint>();
                uint bus = 0;
                for (var cur = n; cur != null && seen.Add(cur.Id); cur = nodes.GetValueOrDefault(cur.Parent))
                {
                    db += cur.Volume;
                    if (bus == 0 && cur.Bus != 0) bus = cur.Bus;
                }
                for (var b = nodes.GetValueOrDefault(bus); b != null && seen.Add(b.Id); b = nodes.GetValueOrDefault(b.Parent)) db += b.Volume;
                if (!res.ContainsKey(src) || db > res[src]) res[src] = db;
            }
        return res;
    }

    public static void Dump(string bankDir, string tsv = null)
    {
        var nodes = Load(bankDir, out int bad);
        Console.WriteLine($"nodes={nodes.Count} unparsed={bad}");
        foreach (var g in nodes.Values.GroupBy(n => n.Type).OrderBy(g => g.Key))
            Console.WriteLine($"type {g.Key}: {g.Count()} nodes, volumes {string.Join(" ", g.Select(n => n.Volume).Distinct().OrderBy(v => v).Take(12).Select(v => v.ToString("0.#")))}");
        var gains = SourceGains(nodes);
        Console.WriteLine($"sources with gain: {gains.Count}; range {gains.Values.DefaultIfEmpty().Min():0.#} .. {gains.Values.DefaultIfEmpty().Max():0.#} dB");
        foreach (var g in gains.GroupBy(kv => kv.Value).OrderBy(g => g.Key)) Console.WriteLine($"  {g.Key,6:0.#} dB: {g.Count()}");
        if (tsv == null) return;
        foreach (var line in File.ReadAllLines(tsv))
        {
            var c = line.Split('	');
            if (c[3].Length == 0) continue;
            var dbs = c[3].Split(',').Select(i => gains.TryGetValue(uint.Parse(i), out var v) ? v.ToString("0.#") : "?");
            Console.WriteLine($"{c[0]}	{c[1]}	{string.Join(",", dbs.Distinct())}");
        }
    }

    // gain.tsv for the player: media id and the dB Wwise applies to it on the way to the speakers.
    public static void WriteGains(string bankDir, string outFile)
    {
        var gains = SourceGains(Load(bankDir, out _));
        File.WriteAllLines(outFile, gains.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}	{kv.Value:0.##}"));
        Console.WriteLine($"{gains.Count} gains -> {outFile}");
    }
}
