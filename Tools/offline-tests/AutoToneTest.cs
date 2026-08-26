using System;
using System.IO;
using Love.Video;
using UnityEngine;

// 拿真实像素跑 AutoTone，把它算出来的参数打出来，交给 Python 应用并度量效果。
static class AutoToneTest
{
    public static int Run(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        var mp = File.ReadAllText(Path.Combine(dir, "probe_meta.txt")).Trim().Split(' ');
        int w = int.Parse(mp[0]), h = int.Parse(mp[1]);

        byte[] raw = File.ReadAllBytes(Path.Combine(dir, "probe.raw"));
        if (raw.Length != w * h * 4) { Console.WriteLine("像素数对不上"); return 1; }

        // Texture2D.GetPixels 给的就是 gamma 空间的存储值，这里照搬
        var px = new Color[w * h];
        for (int i = 0; i < px.Length; i++)
            px[i] = new Color(raw[i * 4] / 255f, raw[i * 4 + 1] / 255f, raw[i * 4 + 2] / 255f, 1f);

        bool wb = args.Length > 2 && args[2] == "wb";

        var s = new VideoGradeSettings();
        var a = AutoTone.Analyze(px);
        var o = AutoTone.Options.Default;
        o.whiteBalance = wb;
        AutoTone.Apply(a, s, o);

        Console.WriteLine("{0:0.#####} {1:0.#####} {2:0.#####} {3:0.#####} {4:0.#####} {5:0.#####} {6:0.#####} {7:0.#####}",
                          s.exposure, s.inBlack, s.inWhite, s.highlights, s.shadows, s.contrast,
                          s.temperature, s.tint);
        Console.WriteLine("ANALYSIS p50={0:0.####} p002={1:0.####} p998={2:0.####} clipHi={3:0.####} clipLo={4:0.####}",
                          a.p50, a.p002, a.p998, a.clipHigh, a.clipLow);
        return 0;
    }
}
