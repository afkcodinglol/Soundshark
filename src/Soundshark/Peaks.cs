using System.IO;
using Soundshark.Core;

namespace Soundshark;
public sealed class Peaks
{
    public const int Block = 512;
    public float[] Values { get; private set; } = [];
    public static async Task<Peaks> Load(string source, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            string path = source + ".peaks"; long expected = (new FileInfo(source).Length / 8 + Block - 1) / Block;
            if (File.Exists(path) && new FileInfo(path).Length == expected * 4)
            { byte[] bytes = File.ReadAllBytes(path); float[] cached = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, cached, 0, bytes.Length); return new Peaks { Values = cached }; }
            var peaks = new List<float>(); byte[] buffer = new byte[Block * 8];
            using var file = File.OpenRead(source);
            while (true)
            {
                ct.ThrowIfCancellationRequested(); int n = file.ReadAtLeast(buffer, buffer.Length, false); if (n == 0) break;
                float max = 0;
                for (int i = 0; i + 4 <= n; i += 4) { float v = BitConverter.ToSingle(buffer, i); if (float.IsFinite(v)) max = Math.Max(max, Math.Abs(v)); }
                peaks.Add(Math.Clamp(max, 0, 1));
            }
            byte[] all = new byte[peaks.Count * 4]; var values = peaks.ToArray(); Buffer.BlockCopy(values, 0, all, 0, all.Length); File.WriteAllBytes(path, all); return new Peaks { Values = values };
        }, ct);
    }
    public float At(Workspace w, long frame, long span)
    {
        float max = 0; long cursor = 0;
        foreach (var r in w.Ranges)
        {
            long a = Math.Clamp(frame - cursor, 0, r.Length), b = Math.Clamp(frame + span - cursor, 0, r.Length);
            if (b > a)
            {
                int first = (int)((r.Start + a) / Block), last = (int)((r.Start + b - 1) / Block);
                for (int i = first; i <= last && i < Values.Length; i++) max = Math.Max(max, Values[i]);
            }
            cursor += r.Length;
        }
        return max;
    }
}
