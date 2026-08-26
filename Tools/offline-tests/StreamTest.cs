using System;
using System.Diagnostics;
using Love.Tools;

// 直接跑真实的 VideoFrameStream / FfmpegTool，量三种访问模式的实际耗时。
static class StreamTest
{
    public static int Run(string[] args)
    {
        string src = args.Length > 1
            ? args[1]
            : @"C:\Users\admin\Downloads\新建文件夹 (3)\cgt-20260819185207-z4cxt.mp4";
        int div = args.Length > 2 ? int.Parse(args[2]) : 1;

        var sw = Stopwatch.StartNew();
        bool ok = FfmpegTool.Probe(src, out int w, out int h, out double fps, out double dur);
        Console.WriteLine("Probe: {0}  {1}x{2} {3:0.###}fps {4:0.##}s  ({5} ms)",
                          ok ? "OK" : "失败", w, h, fps, dur, sw.ElapsedMilliseconds);
        if (!ok) return 1;

        sw.Restart();
        string p = FfmpegTool.Path;
        Console.WriteLine("定位 ffmpeg: {0} ms  ({1})", sw.ElapsedMilliseconds,
                          p == null ? "没找到" : System.IO.Path.GetFileName(p));

        int pw = Math.Max(2, (w / div) & ~1), ph = Math.Max(2, (h / div) & ~1);
        long total = (long)Math.Round(dur * fps);
        var buf = new byte[(long)pw * ph * 4];

        using (var st = new VideoFrameStream(src, fps, pw, ph))
        {
            // --- 1) 顺序播放 ---
            sw.Restart();
            int n = 0;
            for (long f = 0; f < Math.Min(100, total); f++)
                if (st.TryGet(f, buf)) n++; else break;
            double t1 = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine("顺序播放  {0}x{1}  {2} 帧 / {3:0} ms = {4:0.0} ms/帧  ({5:0} fps)",
                              pw, ph, n, t1, t1 / Math.Max(n, 1), n * 1000.0 / t1);

            // --- 2) 小幅前跳（读掉扔掉那条路） ---
            sw.Restart();
            n = 0;
            for (long f = 0; f + 10 < total && n < 10; f += 10)
                if (st.TryGet(f, buf)) n++; else break;
            double t2 = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine("前跳 10 帧  {0} 次 / {1:0} ms = {2:0.0} ms/次", n, t2, t2 / Math.Max(n, 1));

            // --- 3) 随机定位（每次都得重开进程） ---
            var rng = new Random(7);
            sw.Restart();
            n = 0;
            for (int i = 0; i < 10; i++)
            {
                long f = rng.Next(0, (int)Math.Max(1, total));
                if (st.TryGet(f, buf)) n++; else break;
            }
            double t3 = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine("随机定位  {0} 次 / {1:0} ms = {2:0.0} ms/次", n, t3, t3 / Math.Max(n, 1));

            Console.WriteLine();
            Console.WriteLine("判定: 顺序播放 {0}（片子是 {1:0.###} fps，每帧预算 {2:0.0} ms）",
                              t1 / 100.0 < 1000.0 / fps ? "跟得上" : "跟不上", fps, 1000.0 / fps);
        }
        return 0;
    }
}
