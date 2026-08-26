using System;
using System.Collections.Generic;
using UnityEngine;

namespace Love.Video
{
    /// <summary>
    /// 自适应起手值：分析画面本身的分布，给出一组能让这张照片先"站起来"的参数。
    ///
    /// 定位是<b>起点</b>不是成片。导入一批照片，先让每张各自站住，再谈风格——
    /// 一张一张从零拉滑条，一次拍摄根本处理不完。
    ///
    /// 每一项都可以单独关掉：日落、逆光、低调这些片子，自动的判断往往和意图相反。
    /// </summary>
    public static class AutoTone
    {
        /// <summary>中级灰。摄影上的 18% 反射率，线性空间的值。</summary>
        const float MiddleGrey = 0.18f;

        [Serializable]
        public struct Options
        {
            public bool exposure;
            public bool levels;
            public bool highlightsShadows;
            public bool contrast;

            /// <summary>白平衡默认关：日落、烛光这类片子，"中性化"恰恰是把味道抹掉。</summary>
            public bool whiteBalance;

            public static Options Default => new Options
            {
                exposure = true, levels = true, highlightsShadows = true,
                contrast = true, whiteBalance = false,
            };
        }

        /// <summary>一次统计的结果。分析和决策分开，好把"看到了什么"和"于是怎么调"分别验证。</summary>
        public struct Analysis
        {
            public bool valid;

            // 以下都是 gamma 空间的亮度分位数
            public float p002, p05, p25, p50, p75, p95, p998;

            /// <summary>贴着两端的像素占比。判断是不是已经溢出了。</summary>
            public float clipHigh, clipLow;

            /// <summary>高光区的平均色，线性。估计光源色用。</summary>
            public Vector3 brightMeanLinear;
            public int brightCount;
        }

        // ---------------- 分析 ----------------

        /// <summary>
        /// 传进来的必须是 <b>gamma 空间</b>的像素，也就是 Texture2D.GetPixels()
        /// 直接给出的那种值——Unity 不会替你做 sRGB 解码，别在外面先转一道。
        /// </summary>
        public static Analysis Analyze(Color[] gammaPixels)
        {
            var a = new Analysis();
            if (gammaPixels == null || gammaPixels.Length == 0) return a;

            // 几百万像素全排一遍没必要，隔着取到二十万个就足够有统计意义了
            int stride = Mathf.Max(1, gammaPixels.Length / 200000);
            var lum = new List<float>(gammaPixels.Length / stride + 1);

            int high = 0, low = 0, n = 0;
            Vector3 brightSum = Vector3.zero;
            int brightN = 0;

            for (int i = 0; i < gammaPixels.Length; i += stride)
            {
                var c = gammaPixels[i];
                float y = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                lum.Add(y);
                n++;

                if (y > 0.98f) high++;
                else if (y < 0.02f) low++;

                // 光源色的估计取"亮但没溢出"的那一段。全图平均（灰世界）
                // 在大面积单色的画面上会被主体带跑，比如满屏绿草
                if (y > 0.75f && y < 0.98f)
                {
                    brightSum += new Vector3(Mathf.GammaToLinearSpace(c.r),
                                             Mathf.GammaToLinearSpace(c.g),
                                             Mathf.GammaToLinearSpace(c.b));
                    brightN++;
                }
            }

            if (n < 64) return a;
            lum.Sort();

            a.valid = true;
            a.p002 = Percentile(lum, 0.002f);
            a.p05 = Percentile(lum, 0.05f);
            a.p25 = Percentile(lum, 0.25f);
            a.p50 = Percentile(lum, 0.50f);
            a.p75 = Percentile(lum, 0.75f);
            a.p95 = Percentile(lum, 0.95f);
            a.p998 = Percentile(lum, 0.998f);
            a.clipHigh = high / (float)n;
            a.clipLow = low / (float)n;
            a.brightCount = brightN;
            a.brightMeanLinear = brightN > 0 ? brightSum / brightN : Vector3.one;

            return a;
        }

        // ---------------- 决策 ----------------

        public static void Apply(Color[] gammaPixels, VideoGradeSettings s) =>
            Apply(Analyze(gammaPixels), s, Options.Default);

        public static void Apply(Analysis a, VideoGradeSettings s, Options o)
        {
            if (!a.valid || s == null) return;

            // ---- 曝光和色阶要一起解 ----
            //
            // 曝光排在色阶前面，可色阶的拉伸会把中位数再推一次。分开算的话，
            // 一张本来就正常的照片会被色阶推到过亮——第一版就是这么错的。
            // 所以迭代几轮：拿当前曝光定色阶，再反解出"过完整条链之后中位数落在中级灰"的曝光。
            float targetGamma = Mathf.LinearToGammaSpace(MiddleGrey);
            float medianLin = Mathf.GammaToLinearSpace(a.p50);
            float stops = s.exposure;
            float gain = Mathf.Pow(2f, stops);

            if (o.exposure)
            {
                stops = medianLin > 1e-4f ? Mathf.Log(MiddleGrey / medianLin, 2f) : 0f;
                stops = Mathf.Clamp(stops, -2f, 2f);
                gain = Mathf.Pow(2f, stops);
            }

            for (int iter = 0; iter < 4; iter++)
            {
                // 用 0.2% / 99.8% 而不是最小最大值：一两个坏点或一处镜面高光
                // 就足以把整条曲线拽歪，而它们在画面上根本看不见
                if (o.levels)
                {
                    float lo = Exposed(a.p002, gain);
                    float hi = Exposed(a.p998, gain);
                    s.inBlack = Mathf.Clamp(lo, 0f, 0.45f);
                    s.inWhite = Mathf.Clamp(hi, s.inBlack + 0.05f, 1f);
                }

                if (!o.exposure) break;

                // 中位数要落在色阶之前的哪个值上，才能在色阶之后正好是中级灰
                float want = o.levels
                    ? targetGamma * (s.inWhite - s.inBlack) + s.inBlack
                    : targetGamma;
                want = Mathf.Clamp(want, 0.002f, 0.998f);

                float g = Mathf.GammaToLinearSpace(want) / Mathf.Max(medianLin, 1e-5f);
                float next = Mathf.Clamp(Mathf.Log(Mathf.Max(g, 1e-5f), 2f), -2f, 2f);

                // 曝光不能把高光顶出 1.0：色阶的 saturate 在那之后，救不回来。
                // 保住高光优先于把中位数凑准——凑中位数还有色阶和阴影可用，
                // 烧掉的高光是彻底没了
                float headroom = Mathf.Log(
                    Mathf.Max(1f / Mathf.Max(Mathf.GammaToLinearSpace(a.p998), 1e-5f), 1e-5f), 2f);
                next = Mathf.Min(next, Mathf.Max(headroom, -2f));

                // 已经大片溢出了就别再往上推，那是在把细节往死里烧
                if (a.clipHigh > 0.02f) next = Mathf.Min(next, 0f);

                if (Mathf.Abs(next - stops) < 0.002f) { stops = next; gain = Mathf.Pow(2f, stops); break; }
                stops = next;
                gain = Mathf.Pow(2f, stops);
            }

            if (o.exposure) s.exposure = stops;

            // ---- 高光 / 阴影：只在真的挤住了才动 ----
            if (o.highlightsShadows)
            {
                // 高光端堆了一坨就往回收。阈值 0.5% 是经验值：
                // 正常照片总有一点点镜面反射，那不算问题
                float hiCrowd = Mathf.InverseLerp(0.005f, 0.08f, a.clipHigh);
                s.highlights = -0.7f * hiCrowd;

                float loCrowd = Mathf.InverseLerp(0.01f, 0.12f, a.clipLow);
                s.shadows = 0.6f * loCrowd;
            }

            // ---- 对比度：看拉完色阶之后中间那一段还挤不挤 ----
            if (o.contrast)
            {
                // 四分位距。色阶已经把两端拉开了，如果中间四分之二还挤在一起，
                // 说明这是张平片（雾天、阴天、低反差素材），该加反差
                float iqr = Stretched(a.p75, s, gain) - Stretched(a.p25, s, gain);
                // 正常照片的 IQR 大致在 0.2~0.35
                float t = Mathf.InverseLerp(0.30f, 0.10f, iqr);       // 越小越平
                float flat = Mathf.InverseLerp(0.45f, 0.60f, iqr);    // 太大就收一点
                s.contrast = Mathf.Clamp(1f + 0.35f * t - 0.15f * flat, 0.85f, 1.45f);
            }

            // ---- 白平衡：把高光区的平均色中性化 ----
            if (o.whiteBalance && a.brightCount > 32)
            {
                WhiteBalancePicker.Solve(a.brightMeanLinear, out float temp, out float tint);
                // 打七折。全额中性化会把暖调、冷调这些本来就想要的味道一并抹掉
                s.temperature = Mathf.Clamp(temp * 0.7f, -1f, 1f);
                s.tint = Mathf.Clamp(tint * 0.7f, -1f, 1f);
            }
        }

        /// <summary>曝光是线性空间的缩放，所以要转下去乘完再转回来。</summary>
        static float Exposed(float gamma, float gain) =>
            Mathf.LinearToGammaSpace(Mathf.GammaToLinearSpace(gamma) * gain);

        /// <summary>某个亮度过完曝光和色阶之后落在哪。判断反差要按拉伸后的分布来看。</summary>
        static float Stretched(float gamma, VideoGradeSettings s, float gain)
        {
            float v = Exposed(gamma, gain);
            float span = Mathf.Max(s.inWhite - s.inBlack, 1e-4f);
            return Mathf.Clamp01((v - s.inBlack) / span);
        }

        /// <summary>已排序序列的分位数，线性插值。</summary>
        static float Percentile(List<float> sorted, float q)
        {
            float pos = Mathf.Clamp01(q) * (sorted.Count - 1);
            int i = Mathf.FloorToInt(pos);
            int j = Mathf.Min(i + 1, sorted.Count - 1);
            return Mathf.Lerp(sorted[i], sorted[j], pos - i);
        }
    }
}
