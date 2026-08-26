using System;
using Love.Video;
using UnityEngine;

// 天空检测。
//
// 用的是合成图。合成图当然不等于真照片，但这类检测**出问题的方式是结构性的**，
// 而结构性的失败恰恰能用合成图逼出来：
//   漏进草地（颜色闸门写成"只认蓝"）
//   抓住不连顶边的蓝色物体（没做连通性）
//   夕阳整张丢掉（拿 b>r 当天空判据）
//   天顶到地平线的渐变中途截断（拿种子颜色当基准，而不是相邻色差）
// 这几条每一条都是真实存在的写法，也每一条都在下面有一个用例盯着。
static class SkyTest
{
    static int _fail;

    static void True(bool ok, string what, string detail = "")
    {
        if (ok) { Console.WriteLine("  OK   " + what); return; }
        _fail++;
        Console.WriteLine("  FAIL " + what + (detail.Length > 0 ? "   " + detail : ""));
    }

    // 固定种子的线性同余，图每次都一样，失败可复现
    static uint _rng = 12345u;
    static void Seed() { _rng = 12345u; }
    static float Rand() { _rng = _rng * 1664525u + 1013904223u; return (_rng >> 8) / 16777216f; }

    class Img
    {
        public readonly int w, h;
        public readonly Color32[] px;
        public Img(int w, int h) { this.w = w; this.h = h; px = new Color32[w * h]; }

        public void Band(float y0, float y1, Func<int, int, (int, int, int)> f)
        {
            int a = (int)(y0 * h), b = (int)(y1 * h);
            for (int y = Math.Max(a, 0); y < Math.Min(b, h); y++)
                for (int x = 0; x < w; x++)
                {
                    var (r, g, bl) = f(x, y);
                    px[y * w + x] = new Color32(Cl(r), Cl(g), Cl(bl), 255);
                }
        }

        public void Rect(float x0, float x1, float y0, float y1, int r, int g, int b)
        {
            for (int y = (int)(y0 * h); y < (int)(y1 * h); y++)
                for (int x = (int)(x0 * w); x < (int)(x1 * w); x++)
                    px[y * w + x] = new Color32(Cl(r), Cl(g), Cl(b), 255);
        }

        static byte Cl(int v) => (byte)Math.Max(0, Math.Min(255, v));

        /// <summary>某一行的平均蒙版值。</summary>
        public float RowAvg(float[] m, float yFrac)
        {
            int y = Math.Min((int)(yFrac * h), h - 1);
            float s = 0f;
            for (int x = 0; x < w; x++) s += m[y * w + x];
            return s / w;
        }

        public float At(float[] m, float xf, float yf) =>
            m[Math.Min((int)(yf * h), h - 1) * w + Math.Min((int)(xf * w), w - 1)];
    }

    /// <summary>加一点高频噪声，用来造"有纹理的地面 / 树线"。</summary>
    static Func<int, int, (int, int, int)> Noisy(int r, int g, int b, int amp) =>
        (x, y) => { int d = (int)((Rand() - 0.5f) * 2f * amp); return (r + d, g + d, b + d); };

    public static int Run(string[] args)
    {
        Console.WriteLine("天空检测");
        var o = SkyDetect.Options.Default;

        // ---------- 1. 蓝天 + 平滑的绿草地 ----------
        // 地面是平滑的，纹理闸门帮不上忙，全靠绿色闸门。
        // 这一条挂了说明"漏进草地"那个失败模式没堵住
        Seed();
        var im = new Img(160, 120);
        im.Band(0f, 0.4f, (x, y) => (90, 140, 220));
        im.Band(0.4f, 1f, (x, y) => (70, 150, 60));
        var res = SkyDetect.Run(im.px, im.w, im.h, o);
        True(res.found, "蓝天绿地：找到天空");
        True(res.coverage > 0.3f && res.coverage < 0.5f, $"覆盖率接近天空占比（{res.coverage:F2}）");
        True(im.RowAvg(res.mask, 0.8f) < 0.02f, $"草地没被选中（{im.RowAvg(res.mask, 0.8f):F3}）");

        // ---------- 2. 天顶到地平线的大渐变 ----------
        // 顶端 (30,60,160) 到底端 (200,220,240)，整片色差远超 localTol。
        // 拿种子颜色当基准的写法会在半空中截断
        Seed();
        im = new Img(160, 120);
        im.Band(0f, 0.6f, (x, y) =>
        {
            float t = y / (0.6f * 120f);
            return ((int)(30 + t * 170), (int)(60 + t * 160), (int)(160 + t * 80));
        });
        im.Band(0.6f, 1f, Noisy(60, 55, 50, 25));
        res = SkyDetect.Run(im.px, im.w, im.h, o);
        True(im.RowAvg(res.mask, 0.05f) > 0.9f, "大渐变：天顶选中");
        True(im.RowAvg(res.mask, 0.5f) > 0.9f, $"大渐变：接近地平线处也选中（{im.RowAvg(res.mask, 0.5f):F2}）");

        // ---------- 3. 下方的蓝色湖面 ----------
        // 湖水和天空同色，且整片都在 maxDepth 以内——唯一挡住它的只有中间那条树线。
        // 这一条挂了说明连通性没起作用
        Seed();
        im = new Img(160, 120);
        im.Band(0f, 0.40f, (x, y) => (95, 145, 215));
        im.Band(0.40f, 0.46f, Noisy(35, 40, 30, 30));      // 树线：暗且高频
        im.Band(0.46f, 0.70f, (x, y) => (95, 145, 215));   // 湖面：和天空一模一样
        im.Band(0.70f, 1f, Noisy(70, 65, 60, 20));
        res = SkyDetect.Run(im.px, im.w, im.h, o);
        True(im.RowAvg(res.mask, 0.2f) > 0.9f, "湖面图：天空选中");
        True(im.RowAvg(res.mask, 0.6f) < 0.05f,
             $"同色的湖面没被选中（{im.RowAvg(res.mask, 0.6f):F3}）");

        // ---------- 4. 画面中间一件蓝衣服 ----------
        Seed();
        im = new Img(160, 120);
        im.Band(0f, 0.30f, (x, y) => (95, 145, 215));
        im.Band(0.30f, 1f, Noisy(80, 75, 70, 22));
        im.Rect(0.35f, 0.65f, 0.45f, 0.65f, 95, 145, 215);   // 和天空同色，但不连顶边
        res = SkyDetect.Run(im.px, im.w, im.h, o);
        True(im.At(res.mask, 0.5f, 0.55f) < 0.05f,
             $"不连顶边的蓝色物体没被选中（{im.At(res.mask, 0.5f, 0.55f):F3}）");

        // ---------- 5. 阴天白天空 ----------
        Seed();
        im = new Img(160, 120);
        im.Band(0f, 0.45f, (x, y) => (225, 228, 232));
        im.Band(0.45f, 1f, Noisy(55, 52, 48, 20));
        res = SkyDetect.Run(im.px, im.w, im.h, o);
        True(res.found && im.RowAvg(res.mask, 0.2f) > 0.9f, "阴天的白天空也认得出来");

        // ---------- 6. 夕阳 ----------
        // 橙红，蓝色分量最低。按"b > r 才是天空"来判的话这张整个丢掉
        Seed();
        im = new Img(160, 120);
        im.Band(0f, 0.45f, (x, y) => (235, 140, 70));
        im.Band(0.45f, 1f, Noisy(50, 45, 45, 20));
        res = SkyDetect.Run(im.px, im.w, im.h, o);
        True(res.found && im.RowAvg(res.mask, 0.2f) > 0.9f, "橙红的夕阳天空没被漏掉");

        // ---------- 7. 室内，根本没有天空 ----------
        Seed();
        im = new Img(160, 120);
        im.Band(0f, 1f, Noisy(45, 42, 40, 25));
        res = SkyDetect.Run(im.px, im.w, im.h, o);
        True(!res.found, $"没有天空时不硬找（覆盖 {res.coverage:F3}）");
        True(res.mask != null && im.RowAvg(res.mask, 0.5f) == 0f, "找不到时蒙版是全 0，不是 null");

        // ---------- 8. 整张都是天空 ----------
        Seed();
        im = new Img(160, 120);
        im.Band(0f, 1f, (x, y) => (100, 150, 220));
        res = SkyDetect.Run(im.px, im.w, im.h, o);
        True(res.coverage > 0.7f, $"整张天空时覆盖率高（{res.coverage:F2}）");

        // ---------- 9. 深度上限 ----------
        // 上一条已经说明：全蓝时也不会一路吃到底，maxDepth 把它拦在 75%
        True(res.coverage < 0.80f, $"再蓝也不会吃穿整张（{res.coverage:F2}，上限 {o.maxDepth:F2}）");

        // ---------- 10. 顶边被树枝横穿 ----------
        // 种子只剩零星几段，仍然要能从缝里漫进天空
        Seed();
        im = new Img(160, 120);
        im.Band(0f, 0.5f, (x, y) => (95, 145, 215));
        im.Band(0.5f, 1f, Noisy(60, 58, 55, 22));
        for (int x = 0; x < im.w; x++)
            if ((x / 12) % 2 == 0)
                for (int y = 0; y < 4; y++) im.px[y * im.w + x] = new Color32(30, 28, 25, 255);
        res = SkyDetect.Run(im.px, im.w, im.h, o);
        True(res.found && im.RowAvg(res.mask, 0.3f) > 0.85f,
             $"顶边被树枝挡掉一半，仍然找得到天空（{im.RowAvg(res.mask, 0.3f):F2}）");

        // ---------- 11. 边界 ----------
        True(!SkyDetect.Run(null, 100, 100, o).found, "入参为 null 时安全返回");
        True(!SkyDetect.Run(new Color32[4], 2, 2, o).found, "图太小时安全返回");
        True(!SkyDetect.Run(new Color32[10], 100, 100, o).found, "像素数对不上时安全返回");

        // ---------- 12. 羽化 ----------
        Seed();
        im = new Img(160, 120);
        im.Band(0f, 0.4f, (x, y) => (95, 145, 215));
        im.Band(0.4f, 1f, Noisy(70, 65, 60, 20));

        var hard = o; hard.feather = 0;
        var soft = o; soft.feather = 3;
        var mh = SkyDetect.Run(im.px, im.w, im.h, hard).mask;
        var ms = SkyDetect.Run(im.px, im.w, im.h, soft).mask;

        int midH = 0, midS = 0;
        for (int i = 0; i < mh.Length; i++)
        {
            if (mh[i] > 0.05f && mh[i] < 0.95f) midH++;
            if (ms[i] > 0.05f && ms[i] < 0.95f) midS++;
        }
        True(midH == 0, $"不羽化时是硬边（中间值 {midH} 个）");
        True(midS > midH, $"羽化之后边界有过渡（中间值 {midS} 个）");

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "全部通过" : ("失败 " + _fail + " 项"));
        return _fail == 0 ? 0 : 1;
    }
}
