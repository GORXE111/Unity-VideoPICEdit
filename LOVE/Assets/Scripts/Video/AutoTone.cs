using System.Collections.Generic;
using UnityEngine;

namespace Love.Video
{
    /// <summary>
    /// 自动色调：从画面本身的亮度分布推一组起手参数。
    ///
    /// 只动曝光和色阶两项，别的一律不碰。"自动"应该给的是一个靠谱的起点，
    /// 而不是一键成片——把对比度、饱和度也一起改掉，用户反而不知道该从哪儿接着调。
    /// </summary>
    public static class AutoTone
    {
        /// <summary>中级灰。摄影上的 18% 反射率，线性空间的值。</summary>
        const float MiddleGrey = 0.18f;

        /// <summary>
        /// 传进来的必须是 <b>gamma 空间</b>的像素，也就是 Texture2D.GetPixels()
        /// 直接给出的那种值——Unity 不会替你做 sRGB 解码，别在外面先转一道。
        /// </summary>
        public static void Apply(Color[] gammaPixels, VideoGradeSettings s)
        {
            if (gammaPixels == null || gammaPixels.Length == 0 || s == null) return;

            // 几百万像素全排一遍没必要，隔着取到二十万个就足够有统计意义了
            int stride = Mathf.Max(1, gammaPixels.Length / 200000);
            var lum = new List<float>(gammaPixels.Length / stride + 1);
            for (int i = 0; i < gammaPixels.Length; i += stride)
            {
                var c = gammaPixels[i];
                lum.Add(0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b);
            }
            if (lum.Count < 16) return;
            lum.Sort();

            // ---- 曝光：把中位亮度推到中级灰 ----
            // 分位数和单调变换可交换，所以先在 gamma 空间取中位数再转线性，
            // 和逐像素转完再取中位数是同一个结果，但省掉一整趟转换
            float medianLin = Mathf.GammaToLinearSpace(Percentile(lum, 0.5f));
            float stops = medianLin > 1e-4f ? Mathf.Log(MiddleGrey / medianLin, 2f) : 0f;
            s.exposure = Mathf.Clamp(stops, -2f, 2f);

            float gain = Mathf.Pow(2f, s.exposure);

            // ---- 色阶：曝光之后再取两端 ----
            // 用 0.2% / 99.8% 而不是最小最大值：一两个坏点或一处镜面高光
            // 就足以把整条曲线拽歪，而它们在画面上根本看不见
            float lo = Exposed(Percentile(lum, 0.002f), gain);
            float hi = Exposed(Percentile(lum, 0.998f), gain);

            s.inBlack = Mathf.Clamp(lo, 0f, 0.45f);
            s.inWhite = Mathf.Clamp(hi, s.inBlack + 0.05f, 1f);
        }

        /// <summary>曝光是线性空间的缩放，所以要转下去乘完再转回来。</summary>
        static float Exposed(float gamma, float gain) =>
            Mathf.LinearToGammaSpace(Mathf.GammaToLinearSpace(gamma) * gain);

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
