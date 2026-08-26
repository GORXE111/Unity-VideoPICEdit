using System;
using System.IO;
using Love.EditorTools;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--arw2") return Arw2Test.Run(args);
        if (args.Length > 0 && args[0] == "--stream") return StreamTest.Run(args);
        if (args.Length > 0 && args[0] == "--repair") return RepairTest.Run(args);
        if (args.Length > 0 && args[0] == "--library") return LibraryTest.Run(args);
        if (args.Length > 0 && args[0] == "--autotone") return AutoToneTest.Run(args);
        if (args.Length > 0 && args[0] == "--settings") return SettingsTest.Run(args);
        if (args.Length > 0 && args[0] == "--export") return ExportTest.Run(args);
        if (args.Length > 0 && args[0] == "--snapshot") return SnapshotTest.Run(args);
        if (args.Length > 0 && args[0] == "--sky") return SkyTest.Run(args);

        string path = args.Length > 0 ? args[0] : @"C:\Users\admin\Downloads\_DSC0018.ARW";
        int step = args.Length > 1 ? int.Parse(args[1]) : 1;

        var o = SonyRawImporter.Options.Default;
        o.downscale = step;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = SonyRawImporter.Load(path, o);
        sw.Stop();

        if (!string.IsNullOrEmpty(res.error)) Console.WriteLine("ERROR: " + res.error);
        if (res.texture == null) { Console.WriteLine("没有解出贴图"); return 1; }

        Console.WriteLine("info : " + res.info);
        Console.WriteLine("尺寸 : {0} x {1}", res.texture.width, res.texture.height);
        Console.WriteLine("耗时 : {0} ms", sw.ElapsedMilliseconds);

        var px = res.texture.Pixels;
        double sr = 0, sg = 0, sb = 0;
        foreach (var c in px) { sr += c.r; sg += c.g; sb += c.b; }
        int n = px.Length;
        Console.WriteLine("平均 RGB : {0:0.0} {1:0.0} {2:0.0}", sr / n, sg / n, sb / n);
        Console.WriteLine("通道比   : R/G={0:0.000}  B/G={1:0.000}", sr / sg, sb / sg);

        // 写成 PPM，外面用 Pillow 转 png 看
        // Unity 贴图是自下而上的，PPM 是自上而下，所以要倒着写回去
        int w = res.texture.width, h = res.texture.height;
        using (var fs = new FileStream("out.ppm", FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            foreach (char ch in $"P6\n{w} {h}\n255\n") bw.Write((byte)ch);
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++)
                {
                    var c = px[y * w + x];
                    bw.Write(c.r); bw.Write(c.g); bw.Write(c.b);
                }
        }
        Console.WriteLine("已写出 out.ppm");
        return 0;
    }
}
