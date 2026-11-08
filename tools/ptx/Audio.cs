using System.Text;
using System.Xml.Linq;

// Pulls every media file out of the Windows SoundBanks and writes the event table the player reads.
// SoundbanksInfo.xml already maps each event to its files and says whether it is looping, so the
// hierarchy chunk is not needed.
static class Audio
{
    public static void Extract(string bankDir, string outDir)
    {
        var wem = Path.Combine(outDir, "wem");
        Directory.CreateDirectory(wem);
        var media = new Dictionary<uint, (string bank, int off, int len)>();
        foreach (var f in Directory.GetFiles(bankDir, "*.bnk"))
        {
            var b = File.ReadAllBytes(f);
            int o = 0, didx = -1, didxLen = 0, data = -1;
            while (o + 8 <= b.Length)
            {
                var t = Encoding.ASCII.GetString(b, o, 4);
                int l = BitConverter.ToInt32(b, o + 4);
                if (t == "DIDX") { didx = o + 8; didxLen = l; }
                if (t == "DATA") data = o + 8;
                o += 8 + l;
            }
            if (didx < 0 || data < 0) continue;
            for (int p = didx; p < didx + didxLen; p += 12)
            {
                uint id = BitConverter.ToUInt32(b, p);
                int off = BitConverter.ToInt32(b, p + 4), len = BitConverter.ToInt32(b, p + 8);
                media[id] = (f, off, len);
                File.WriteAllBytes(Path.Combine(wem, id + ".wem"), b.AsSpan(data + off, len).ToArray());
            }
        }
        var x = XDocument.Load(Path.Combine(bankDir, "SoundbanksInfo.xml"));
        var rows = new List<string>();
        var seen = new HashSet<string>();
        int missing = 0;
        foreach (var e in x.Descendants("Event"))
        {
            var name = e.Attribute("Name").Value;
            if (!seen.Add(name)) continue;
            var ids = e.Descendants("File").Select(f => uint.Parse(f.Attribute("Id").Value)).Distinct().ToList();
            missing += ids.Count(i => !media.ContainsKey(i));
            var dur = e.Attribute("DurationType")?.Value ?? "";
            rows.Add($"{name}\t{dur}\t{e.Attribute("DurationMax")?.Value ?? "0"}\t{string.Join(",", ids.Where(media.ContainsKey))}");
        }
        File.WriteAllLines(Path.Combine(outDir, "audio.tsv"), rows.OrderBy(r => r));
        Console.WriteLine($"{media.Count} media files, {rows.Count} events, {missing} references without media");
        foreach (var g in rows.Select(r => r.Split('\t')).GroupBy(r => r[0].Split('_')[0] + "/" + r[1]).OrderBy(g => g.Key))
            Console.WriteLine($"{g.Key}: {g.Count()} events, {g.Count(r => r[3].Contains(','))} with several files");
    }

    // Wwise's "PCM" is plain 16 bit samples under a private format tag ffmpeg will not read.
    public static void Pcm(string wemDir, string wavDir)
    {
        Directory.CreateDirectory(wavDir);
        int pcm = 0, other = 0;
        var rest = new Dictionary<string, int>();
        foreach (var f in Directory.GetFiles(wemDir, "*.wem"))
        {
            var b = File.ReadAllBytes(f);
            int o = 12, ch = 0, rate = 0, tag = 0, bits = 16, data = -1, len = 0;
            while (o + 8 <= b.Length)
            {
                var t = Encoding.ASCII.GetString(b, o, 4);
                int l = BitConverter.ToInt32(b, o + 4);
                if (t == "fmt ") { tag = BitConverter.ToUInt16(b, o + 8); ch = BitConverter.ToUInt16(b, o + 10); rate = BitConverter.ToInt32(b, o + 12); bits = BitConverter.ToUInt16(b, o + 22); }
                if (t == "data") { data = o + 8; len = l; }
                o += 8 + l + (l & 1);
            }
            if (tag != 0xFFFE || data < 0)
            {
                other++;
                var k = tag.ToString("x");
                rest[k] = rest.GetValueOrDefault(k) + 1;
                continue;
            }
            pcm++;
            using var w = new BinaryWriter(File.Create(Path.Combine(wavDir, Path.GetFileNameWithoutExtension(f) + ".wav")));
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + len); w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
            w.Write((ushort)1); w.Write((ushort)ch); w.Write(rate); w.Write(rate * ch * bits / 8); w.Write((ushort)(ch * bits / 8)); w.Write((ushort)bits);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(len); w.Write(b, data, Math.Min(len, b.Length - data));
        }
        Console.WriteLine($"pcm={pcm} other={other} {string.Join(" ", rest.Select(k => k.Key + ":" + k.Value))}");
    }
}
