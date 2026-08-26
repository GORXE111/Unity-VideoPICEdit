using System;
using System.IO;
using System.Reflection;
using Love.EditorTools;

// ARW2 差分测试。用反射调私有方法，免得为了测试往生产代码里开口子。
static class Arw2Test
{
    public static int Run(string[] args)
    {
        int seed = args.Length > 1 ? int.Parse(args[1]) : 1;
        int w = args.Length > 2 ? int.Parse(args[2]) : 128;
        int h = args.Length > 3 ? int.Parse(args[3]) : 8;

        var t = typeof(SonyRawImporter);
        var mCurve = t.GetMethod("BuildSonyCurve", BindingFlags.NonPublic | BindingFlags.Static);
        var mDecode = t.GetMethod("DecodeArw2", BindingFlags.NonPublic | BindingFlags.Static);
        if (mCurve == null || mDecode == null) { Console.WriteLine("反射找不到方法"); return 1; }

        // 随机码流。解码器对任意字节都必须有确定的输出，所以不需要编码器——
        // 随机数据反而能把 sh 的四个档位、imax/imin 的各种位置全扫一遍
        var rng = new Random(seed);
        var data = new byte[w * h];
        rng.NextBytes(data);

        // 曲线折点也随机，但保持单调，模拟不同机身
        var pts = new int[4];
        int acc = 0;
        for (int i = 0; i < 4; i++) { acc += rng.Next(200, 4000); pts[i] = Math.Min(acc, 16380); }

        var curve = (ushort[])mCurve.Invoke(null, new object[] { pts });
        var plane = (ushort[])mDecode.Invoke(null, new object[] { data, 0, w, h, curve });

        File.WriteAllBytes("arw2_in.bin", data);
        using (var bw = new BinaryWriter(File.Create("arw2_out.bin")))
            foreach (var v in plane) bw.Write(v);
        using (var sw = new StreamWriter("arw2_meta.txt"))
            sw.WriteLine("{0} {1} {2} {3} {4} {5} {6}", w, h, pts[0], pts[1], pts[2], pts[3], seed);

        long sum = 0; ushort mx = 0;
        foreach (var v in plane) { sum += v; if (v > mx) mx = v; }
        Console.WriteLine("seed={0} {1}x{2} 折点=[{3},{4},{5},{6}] 均值={7:0.0} 最大={8}",
                          seed, w, h, pts[0], pts[1], pts[2], pts[3], sum / (double)plane.Length, mx);
        return 0;
    }
}
