using AssetsTools.NET;
using AssetsTools.NET.Extra;

static class RawMb
{
    public static byte[] Bytes(AssetsFileInstance i, AssetFileInfo a)
    {
        lock (i.file.Reader)
        {
            i.file.Reader.Position = a.GetAbsoluteByteOffset(i.file);
            return i.file.Reader.ReadBytes((int)a.ByteSize);
        }
    }
    public static void Run(string file, long pid)
    {
        var am = new AssetsManager();
        var i = am.LoadAssetsFile(file, false);
        var a = i.file.GetAssetInfo(pid);
        var b = Bytes(i, a);
        for (int k = 0; k < b.Length; k += 16)
            Console.WriteLine($"{k:x4}: {BitConverter.ToString(b, k, Math.Min(16, b.Length - k)).Replace("-", " "),-48} {new string(b.Skip(k).Take(16).Select(x => x >= 32 && x < 127 ? (char)x : '.').ToArray())}");
    }
}
