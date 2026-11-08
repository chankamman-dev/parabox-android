using System.Text;
using System.Xml.Linq;

// events.tsv for the player: what each Wwise event really does, taken from the hierarchy rather than
// from SoundbanksInfo.xml's flat file lists. Lines:
//   E <event> <action>|<action>...     play,<delayMs>,<fadeMs>,<S|P|W>:<id>  stop,<bus>,<fadeMs>
//                                      stopall,<fadeMs>  pause  resume  state,<group>,<state>
//   S <id> <bus> <file>,<file>...      one-shot; Wwise picks one at random, avoiding a repeat
//   P <id> <bus> <file> <playAt> <begin> <end> <srcDur> <entry> <exit> <loops> <fadeIn> <fadeOut>
//                                      music playlist: one segment holding one clip of <file>; all
//                                      times in ms; loops 0 = forever
//   W <id> <group> <default> <state>=<P id>,...   music switch on a state group
// Buses are music, env or sfx.
static class AudioEvents
{
    const uint MusicBus = 3991942870, EnvBus = 529726550;

    public static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (var c in Encoding.UTF8.GetBytes(s.ToLowerInvariant())) { h *= 16777619; h ^= c; }
        return h;
    }

    public static void Write(string bankDir, string outFile)
    {
        var nodes = Hirc.Load(bankDir, out _);
        var names = new Dictionary<uint, string>();
        foreach (var e in XDocument.Load(Path.Combine(bankDir, "SoundbanksInfo.xml")).Descendants())
            if (e.Attribute("Name") is { } a) names[Fnv(a.Value)] = a.Value;
        var kids = nodes.Values.Where(n => n.Parent != 0).ToLookup(n => n.Parent);
        var lines = new List<string>();
        var defs = new SortedDictionary<string, string>();
        int unknown = 0;

        string Bus(HircNode n)
        {
            for (var c = n; c != null; c = nodes.GetValueOrDefault(c.Parent))
                if (c.Bus != 0) return c.Bus == MusicBus ? "music" : c.Bus == EnvBus ? "env" : "sfx";
            return "sfx";
        }

        IEnumerable<uint> Media(HircNode n) =>
            n.Sources.Concat(kids[n.Id].SelectMany(Media)).Distinct();

        string Target(uint id)
        {
            if (!nodes.TryGetValue(id, out var n)) { unknown++; return null; }
            switch (n.Type)
            {
                case 2: case 5: case 7: case 9:
                    defs["S\t" + id] = $"S\t{id}\t{Bus(n)}\t{string.Join(",", Media(n))}";
                    return "S:" + id;
                case 13:
                    defs["P\t" + id] = Playlist(n, kids, Bus(n));
                    return "P:" + id;
                case 12:
                    {
                        var w = Switch(n, names);
                        foreach (var child in kids[id].Where(k => k.Type == 13)) defs["P\t" + child.Id] = Playlist(child, kids, Bus(child));
                        defs["W\t" + id] = w;
                        return "W:" + id;
                    }
            }
            unknown++;
            return null;
        }

        foreach (var ev in nodes.Values.Where(n => n.Type == 4))
        {
            var b = ev.Body;
            var acts = new List<string>();
            for (int i = 0; i < b[0]; i++)
            {
                if (!nodes.TryGetValue(BitConverter.ToUInt32(b, 1 + 4 * i), out var a)) continue;
                var ab = a.Body;
                int type = BitConverter.ToUInt16(ab, 0);
                uint target = BitConverter.ToUInt32(ab, 2);
                var props = Props(ab, 7);
                int delay = (int)props.GetValueOrDefault(0x0F), fade = (int)props.GetValueOrDefault(0x10);
                switch (type)
                {
                    case 0x0403:
                        if (Target(target) is { } t) acts.Add($"play,{delay},{fade},{t}");
                        break;
                    case 0x0103:
                    case 0x0102:
                        if (nodes.TryGetValue(target, out var tn) && tn.Type == 8)
                            acts.Add($"stop,{(target == MusicBus ? "music" : target == EnvBus ? "env" : "sfx")},{fade}");
                        else if (Target(target) is { } st) acts.Add($"stopnode,{fade},{st}");
                        break;
                    case 0x0105: case 0x0104: acts.Add($"stopall,{fade}"); break;
                    case 0x0205: case 0x0204: acts.Add("pause"); break;
                    case 0x0305: case 0x0304: acts.Add("resume"); break;
                    case 0x1204:
                        acts.Add($"state,{BitConverter.ToUInt32(ab, ab.Length - 8)},{BitConverter.ToUInt32(ab, ab.Length - 4)}");
                        break;
                    default:
                        acts.Add($"unknown{type:x4}");
                        break;
                }
            }
            lines.Add($"E\t{names.GetValueOrDefault(ev.Id, ev.Id.ToString())}\t{string.Join("|", acts)}");
        }
        lines.Sort(StringComparer.Ordinal);
        lines.AddRange(defs.Values);
        File.WriteAllLines(outFile, lines);
        Console.WriteLine($"{lines.Count - defs.Count} events, {defs.Count} targets, {unknown} unresolved -> {outFile}");
        foreach (var g in lines.Where(l => l.StartsWith("E\t")).SelectMany(l => l.Split('\t')[2].Split('|')).GroupBy(a => a.Split(',')[0]))
            Console.WriteLine($"  {g.Key}: {g.Count()}");
    }

    // AkPropBundle: count, ids, then 4-byte values (ints for times, floats otherwise).
    static Dictionary<int, float> Props(byte[] b, int p)
    {
        var r = new Dictionary<int, float>();
        int c = b[p++];
        for (int k = 0; k < c; k++)
        {
            int id = b[p + k];
            int v = BitConverter.ToInt32(b, p + c + 4 * k);
            r[id] = id is 0x0E or 0x0F or 0x10 ? v : BitConverter.ToSingle(b, p + c + 4 * k);
        }
        return r;
    }

    static string Playlist(HircNode pl, ILookup<uint, HircNode> kids, string bus)
    {
        var b = pl.Body;
        // Playlist items close the node: count, then 30-byte items (segment, item id, children,
        // rs type, loop, loop min, loop max, weight, avoid repeat, using weight, shuffle).
        int loops = 1;
        bool found = false;
        for (int n = 1; n <= 16 && !found; n++)
        {
            int q = b.Length - 4 - 30 * n;
            if (q < 0 || BitConverter.ToInt32(b, q) != n) continue;
            found = true;
            for (int i = 0; i < n; i++)
            {
                int loop = BitConverter.ToInt16(b, q + 4 + 30 * i + 16);
                if (loop == 0) loops = 0; else if (loops != 0) loops *= loop;
            }
        }
        if (!found) Console.WriteLine($"playlist {pl.Id}: items not found");
        var seg = kids[pl.Id].FirstOrDefault(k => k.Type == 10);
        var track = seg == null ? null : kids[seg.Id].FirstOrDefault(k => k.Type == 11);
        if (track == null) return $"P\t{pl.Id}\t{bus}\t\t0\t0\t0\t0\t0\t0\t{loops}\t0\t0";
        var (dur, entry, exit) = Segment(seg, track.Id);
        var c = Clip(track.Body);
        if (Math.Abs(dur) < 1) Console.WriteLine($"segment {seg.Id}: no duration");
        return $"P\t{pl.Id}\t{bus}\t{c.src}\t{c.playAt:0}\t{c.begin:0}\t{c.end:0}\t{c.srcDur:0}\t{entry:0}\t{exit:0}\t{loops}\t{c.fadeIn:0}\t{c.fadeOut:0}";
    }

    // After the node parameters a segment lists its tracks, then meter info, stingers, its
    // duration and its markers (the entry cue first, the exit cue last).
    static (double dur, double entry, double exit) Segment(HircNode seg, uint firstTrack)
    {
        var b = seg.Body;
        for (int q = 14; q + 8 < b.Length; q++)
        {
            int n = BitConverter.ToInt32(b, q);
            if (n < 1 || n > 8 || BitConverter.ToUInt32(b, q + 4) != firstTrack) continue;
            int p = q + 4 + 4 * n;
            p += 8 + 8 + 4 + 1 + 1 + 1;
            int stingers = BitConverter.ToInt32(b, p);
            p += 4 + stingers * 24;
            double dur = BitConverter.ToDouble(b, p);
            p += 8;
            int markers = BitConverter.ToInt32(b, p);
            p += 4;
            var pos = new List<double>();
            for (int i = 0; i < markers; i++)
            {
                pos.Add(BitConverter.ToDouble(b, p + 4));
                p += 12;
                p += 1 + b[p];
            }
            return (dur, pos.Count > 0 ? pos.Min() : 0, pos.Count > 0 ? pos.Max() : dur);
        }
        return (0, 0, 0);
    }

    static (uint src, double playAt, double begin, double end, double srcDur, double fadeIn, double fadeOut) Clip(byte[] b)
    {
        int p = 1;
        int ns = BitConverter.ToInt32(b, p);
        p += 4 + ns * 14;
        int np = BitConverter.ToInt32(b, p);
        p += 4;
        uint src = BitConverter.ToUInt32(b, p + 4);
        double playAt = BitConverter.ToDouble(b, p + 12), begin = BitConverter.ToDouble(b, p + 20);
        double end = BitConverter.ToDouble(b, p + 28), srcDur = BitConverter.ToDouble(b, p + 36);
        p += np * 44;
        if (np > 0) p += 4;
        int na = BitConverter.ToInt32(b, p);
        p += 4;
        double fadeIn = 0, fadeOut = 0;
        for (int i = 0; i < na; i++)
        {
            uint clip = BitConverter.ToUInt32(b, p), kind = BitConverter.ToUInt32(b, p + 4);
            int pts = BitConverter.ToInt32(b, p + 8);
            var t = new double[pts];
            var v = new double[pts];
            for (int k = 0; k < pts; k++) { t[k] = BitConverter.ToSingle(b, p + 12 + 12 * k); v[k] = BitConverter.ToSingle(b, p + 16 + 12 * k); }
            p += 12 + pts * 12;
            if (clip != 0 || pts < 2) continue;
            if (kind == 3 && v[0] == 0) fadeIn = (t[1] - t[0]) * 1000;
            if (kind == 4 && v[pts - 1] == 0) fadeOut = (t[pts - 1] - t[pts - 2]) * 1000;
        }
        return (src, playAt, begin, end, srcDur, fadeIn, fadeOut);
    }

    // Music switch: decision tree depth, its group ids and types, then the tree: a root with
    // children (key, audio node, weight, probability). Key 0 is the default branch.
    static string Switch(HircNode n, Dictionary<uint, string> names)
    {
        var b = n.Body;
        for (int q = 20; q + 12 < b.Length; q++)
        {
            if (BitConverter.ToInt32(b, q) != 1) continue;
            uint group = BitConverter.ToUInt32(b, q + 4);
            int size = BitConverter.ToInt32(b, q + 9);
            if (b[q + 8] > 2 || size % 12 != 0 || q + 14 + size != b.Length) continue;
            int t = q + 14;
            int first = BitConverter.ToUInt16(b, t + 4), count = BitConverter.ToUInt16(b, t + 6);
            uint def = 0;
            var map = new List<string>();
            for (int i = 0; i < count; i++)
            {
                int e = t + 12 * (first + i);
                uint key = BitConverter.ToUInt32(b, e), node = BitConverter.ToUInt32(b, e + 4);
                if (key == 0) def = node; else map.Add($"{key}={node}");
            }
            return $"W\t{n.Id}\t{group}\t{def}\t{string.Join(",", map)}";
        }
        Console.WriteLine($"switch {n.Id}: tree not found");
        return $"W\t{n.Id}\t0\t0\t";
    }
}
