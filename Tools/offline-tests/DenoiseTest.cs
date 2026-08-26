using System;
using Love.Video;
using UnityEngine;

// 切块与噪声估计。
//
// 切块的头号失败是接缝，而接缝的根源是「有的像素没人写 / 有的被写两遍」。
// 这条能用覆盖计数逐像素验死，所以下面的用例就是这么验的。
static class DenoiseTest
{
    static int _fail;

    static void True(bool ok, string what, string detail = "")
    {
        if (ok) { Console.WriteLine("  OK   " + what); return; }
        _fail++;
        Console.WriteLine("  FAIL " + what + (detail.Length > 0 ? "   " + detail : ""));
    }

    static void Near(float got, float want, float tol, string what)
    {
        bool ok = Math.Abs(got - want) <= tol;
        if (ok) { Console.WriteLine($"  OK   {what}（{got:F4}，期望 {want:F4}）"); return; }
        _fail++;
        Console.WriteLine($"  FAIL {what}   得到 {got:F4}，期望 {want:F4} ± {tol:F4}");
    }

    // ---- 固定种子的高斯噪声 ----
    static uint _rng;
    static void Seed(uint s) { _rng = s; }
    static float U()
    {
        _rng = _rng * 1664525u + 1013904223u;
        return ((_rng >> 8) + 0.5f) / 16777216f;
    }
    static float Gauss()
    {
        // Box-Muller
        float u1 = U(), u2 = U();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    static byte Cl(float v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v)));

    // ================= 切块 =================

    /// <summary>逐像素数一遍谁写了它。没人写或写了两遍，拼回去就是接缝。</summary>
    static void Coverage(int w, int h, int tile, int overlap)
    {
        var tiles = DenoiseTiler.Plan(w, h, tile, overlap);
        var count = new int[w * h];
        int readSize = tile + overlap * 2;
        bool sizeOk = true, offOk = true;

        foreach (var t in tiles)
        {
            if (t.read.width != readSize || t.read.height != readSize) sizeOk = false;
            if (t.offsetX != overlap || t.offsetY != overlap) offOk = false;

            for (int y = t.write.y; y < t.write.y + t.write.height; y++)
                for (int x = t.write.x; x < t.write.x + t.write.width; x++)
                    count[y * w + x]++;
        }

        int zero = 0, dup = 0;
        for (int i = 0; i < count.Length; i++)
        {
            if (count[i] == 0) zero++;
            else if (count[i] > 1) dup++;
        }

        string tag = $"{w}x{h} tile{tile} 边{overlap}";
        True(zero == 0 && dup == 0, $"{tag}：每个像素正好写一次",
             $"漏 {zero} 个，重 {dup} 个");
        True(sizeOk, $"{tag}：读窗恒为 {readSize}（贴边也不缩水）");
        True(offOk, $"{tag}：写回偏移就是 overlap");
    }

    public static int Run(string[] args)
    {
        Console.WriteLine("切块");

        Coverage(1000, 700, 256, 32);
        Coverage(512, 512, 256, 32);       // 正好整除
        Coverage(513, 511, 256, 32);       // 差一个像素
        Coverage(100, 80, 256, 32);        // 比一块还小
        Coverage(1920, 1080, 512, 64);
        Coverage(777, 333, 128, 0);        // 不留边

        // 比一块小的时候只该有一块，而且写回整幅
        var one = DenoiseTiler.Plan(100, 80, 256, 32);
        True(one.Count == 1, $"图比一块小时只切一块（{one.Count}）");
        True(one[0].write.width == 100 && one[0].write.height == 80, "那一块写回整幅");
        True(one[0].read.x == -32 && one[0].read.y == -32, "读窗越界到画面外，等着镜像补边");

        // 块数
        var p = DenoiseTiler.Plan(1000, 700, 256, 32);
        True(p.Count == 4 * 3, $"块数 = ceil(1000/256) x ceil(700/256) = 12（{p.Count}）");

        // 6100 万像素：逐像素数不动，改验"面积加起来正好等于整幅"
        var big = DenoiseTiler.Plan(9504, 6336, 512, 32);
        long area = 0;
        bool inside = true;
        foreach (var t in big)
        {
            area += (long)t.write.width * t.write.height;
            if (t.write.x < 0 || t.write.y < 0 ||
                t.write.x + t.write.width > 9504 || t.write.y + t.write.height > 6336) inside = false;
        }
        True(big.Count == 19 * 13, $"6100 万像素切成 {big.Count} 块（19x13）");
        True(area == 9504L * 6336L, $"写回面积正好等于整幅（{area} vs {9504L * 6336L}）");
        True(inside, "没有一块写到画面外");

        // 边界值
        True(DenoiseTiler.Plan(0, 100, 256, 32).Count == 0, "宽为 0 时不产出块");
        True(DenoiseTiler.Plan(100, 100, 0, 32).Count > 0, "块边长为 0 时不死循环");

        // ---- 镜像补边 ----
        // n=4 的下标应当是 …1,0,1,2,3,2,1,0…
        int[] want = { 1, 0, 1, 2, 3, 2, 1, 0, 1 };
        bool mok = true;
        for (int i = -1; i <= 7; i++)
            if (DenoiseTiler.Mirror(i, 4) != want[i + 1]) mok = false;
        True(mok, "镜像补边：越界下标折回画面内");
        True(DenoiseTiler.Mirror(-1000, 4) >= 0 && DenoiseTiler.Mirror(-1000, 4) < 4,
             "离谱的负下标也落在范围内");
        True(DenoiseTiler.Mirror(5, 1) == 0, "宽为 1 时恒取 0");

        // ================= 噪声估计 =================
        Console.WriteLine();
        Console.WriteLine("噪声估计");

        const int W = 256, H = 256;

        Color32[] Make(Func<int, int, float> baseVal, float sigma, float chromaSigma = 0f)
        {
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float v = baseVal(x, y) * 255f;
                    float nl = Gauss() * sigma * 255f;
                    float nc = chromaSigma > 0f ? Gauss() * chromaSigma * 255f : 0f;
                    px[y * W + x] = new Color32(Cl(v + nl - nc), Cl(v + nl), Cl(v + nl + nc), 255);
                }
            return px;
        }

        // 平坦灰底 + 已知 sigma。估出来要对得上
        foreach (float s in new[] { 2f, 5f, 12f })
        {
            Seed(99);
            var r = NoiseEstimate.Analyze(Make((x, y) => 0.5f, s / 255f), W, H);
            Near(r.luma * 255f, s, s * 0.25f + 0.4f, $"平坦面上估 sigma={s}");
        }

        // 完全没噪声
        Seed(1);
        var clean = NoiseEstimate.Analyze(Make((x, y) => 0.5f, 0f), W, H);
        True(clean.luma * 255f < 0.6f, $"干净的平坦面估出来接近 0（{clean.luma * 255f:F3}）");

        // 线性渐变，没噪声。二阶差分核对渐变无响应，这是选它的理由
        Seed(2);
        var grad = NoiseEstimate.Analyze(Make((x, y) => x / (float)W, 0f), W, H);
        True(grad.luma * 255f < 0.6f, $"渐变面不会被当成噪声（{grad.luma * 255f:F3}）");

        // 一半是强纹理、一半平坦，都带同样的噪声。
        // 整幅取均值会被纹理带偏一大截，分块取低分位才量得准
        Seed(3);
        var mixed = NoiseEstimate.Analyze(
            Make((x, y) => y < H / 2 ? (((x / 3) + (y / 3)) % 2 == 0 ? 0.25f : 0.75f) : 0.5f,
                 5f / 255f), W, H);
        Near(mixed.luma * 255f, 5f, 2f, "半幅强纹理时仍按平坦区估");

        // 色噪单独估
        Seed(4);
        var chroma = NoiseEstimate.Analyze(Make((x, y) => 0.5f, 1f / 255f, 8f / 255f), W, H);
        True(chroma.chroma > chroma.luma * 1.5f,
             $"色噪比亮噪高时能分辨出来（色 {chroma.chroma * 255f:F2} vs 亮 {chroma.luma * 255f:F2}）");

        // 边界
        True(!NoiseEstimate.Analyze(null, W, H).valid, "入参为 null 时安全返回");
        True(!NoiseEstimate.Analyze(new Color32[16], 4, 4).valid, "图太小时安全返回");

        // 强度建议
        True(NoiseEstimate.SuggestStrength(0f) == 0f, "没噪声时强度为 0");
        True(NoiseEstimate.SuggestStrength(100f) == 1f, "噪声离谱时强度封顶在 1");
        True(NoiseEstimate.SuggestStrength(6f / 255f) > 0.4f &&
             NoiseEstimate.SuggestStrength(6f / 255f) < 0.6f, "中等噪声给中等强度");

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "全部通过" : ("失败 " + _fail + " 项"));
        return _fail == 0 ? 0 : 1;
    }
}
