using System;
using System.IO;
using System.Reflection;
using Love.EditorTools;
using UnityEngine;

// 找源算法的差分测试。
// 把同一份缩略图像素喂给 C#，输出它选的取样点，再和 Python 参考实现比。
// 用反射直接塞私有字段，免得为了测试往生产代码里开口子。
static class RepairTest
{
    public static int Run(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        string meta = File.ReadAllText(Path.Combine(dir, "probe_meta.txt")).Trim();
        var mp = meta.Split(' ');
        int w = int.Parse(mp[0]), h = int.Parse(mp[1]);

        byte[] raw = File.ReadAllBytes(Path.Combine(dir, "probe.raw"));
        if (raw.Length != w * h * 4) { Console.WriteLine("像素数对不上"); return 1; }

        var px = new Color[w * h];
        for (int i = 0; i < px.Length; i++)
            px[i] = new Color(raw[i * 4] / 255f, raw[i * 4 + 1] / 255f, raw[i * 4 + 2] / 255f, 1f);

        var rep = new ImageRepair();
        var t = typeof(ImageRepair);
        BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
        t.GetField("_probePixels", F).SetValue(rep, px);
        t.GetField("_probeW", F).SetValue(rep, w);
        t.GetField("_probeH", F).SetValue(rep, h);

        var find = t.GetMethod("FindSource", F);
        var ring = t.GetMethod("RingMean", F);

        foreach (var line in File.ReadAllLines(Path.Combine(dir, "probe_spots.txt")))
        {
            if (line.Trim().Length == 0) continue;
            var p = line.Split(' ');
            float u = float.Parse(p[0]), v = float.Parse(p[1]), r = float.Parse(p[2]);

            var uv = new Vector2(u, v);
            var src = (Vector2)find.Invoke(rep, new object[] { uv, r });
            var mt = (Color)ring.Invoke(rep, new object[] { uv, r });
            var ms = (Color)ring.Invoke(rep, new object[] { src, r });

            // 用像素偏移输出，好和 Python 直接对
            int ox = (int)Math.Round((src.x - uv.x) * (w - 1));
            int oy = (int)Math.Round((src.y - uv.y) * (h - 1));
            Console.WriteLine("{0} {1} {2:0.#####} {3:0.#####} {4:0.#####}",
                              ox, oy, mt.r - ms.r, mt.g - ms.g, mt.b - ms.b);

            if (Environment.GetEnvironmentVariable("DUMP") == "1")
                DumpCandidates(rep, t, F, uv, r, w, h);
        }
        return 0;
    }

    // 把 C# 自己算出来的候选分数全打出来，好和 Python 逐个对
    static void DumpCandidates(ImageRepair rep, Type t, BindingFlags F,
                               Vector2 uv, float r, int w, int h)
    {
        var pxM = t.GetMethod("Px", F);
        var ringF = t.GetField("Ring", BindingFlags.NonPublic | BindingFlags.Static);
        var ring = (Vector2[])ringF.GetValue(null);
        int taps = ring.Length;

        int cx0 = Mathf.RoundToInt(uv.x * (w - 1)), cy0 = Mathf.RoundToInt(uv.y * (h - 1));
        int rr = Mathf.Max(2, Mathf.RoundToInt(r * 1.6f * h));

        Func<int, int, Color> P = (x, y) => (Color)pxM.Invoke(rep, new object[] { x, y });

        var tgt = new Color[taps];
        for (int i = 0; i < taps; i++)
            tgt[i] = P(cx0 + Mathf.RoundToInt(ring[i].x * rr), cy0 + Mathf.RoundToInt(ring[i].y * rr));

        foreach (float dist in new[] { 2.2f, 3.2f, 4.5f, 6.0f })
        {
            int rad = Mathf.RoundToInt(r * h * dist);
            for (int k = 0; k < taps; k++)
            {
                int ox = Mathf.RoundToInt(ring[k].x * rad), oy = Mathf.RoundToInt(ring[k].y * rad);
                int cx = cx0 + ox, cy = cy0 + oy;
                if (cx - rr < 0 || cy - rr < 0 || cx + rr >= w || cy + rr >= h) continue;
                double ssd = 0.0;
                for (int i = 0; i < taps; i++)
                {
                    var p = P(cx + Mathf.RoundToInt(ring[i].x * rr), cy + Mathf.RoundToInt(ring[i].y * rr));
                    double dr = p.r - tgt[i].r, dg = p.g - tgt[i].g, db = p.b - tgt[i].b;
                    ssd += dr * dr + dg * dg + db * db;
                }
                Console.WriteLine("CAND {0} {1} {2:0.########}", ox, oy, ssd);
            }
        }
    }
}
