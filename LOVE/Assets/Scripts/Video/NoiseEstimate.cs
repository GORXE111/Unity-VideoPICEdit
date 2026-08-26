using UnityEngine;

namespace Love.Video
{
    /// <summary>
    /// 估计一张图有多少噪声，用来给降噪强度一个起手值。
    ///
    /// 用的是 Immerkær 那个办法：拿一个二阶差分核卷一遍，取绝对值的均值。
    /// 这个核对**线性渐变完全无响应**，所以平滑的天空、渐变的墙面不会被误当成噪声。
    ///
    /// 但它对**纹理**没有免疫力：一片草丛、一堵砖墙，二阶差分同样很大，
    /// 直接整幅取均值会把纹理当噪声，估出来偏高一大截，降噪强度跟着拉满、细节全平。
    ///
    /// 所以这里分块估，再取一个低分位数：一张照片总有几块相对平坦的地方
    /// （天空、皮肤、墙面、暗部），噪声在那些块上量得最准。
    /// </summary>
    public static class NoiseEstimate
    {
        /// <summary>分块边长。太小则每块样本不够、方差大；太大则找不到"足够平坦"的块。</summary>
        const int Patch = 32;

        /// <summary>取第几分位。0.2 意味着"最平坦的那两成块说了算"。</summary>
        const float Percentile = 0.2f;

        public struct Result
        {
            /// <summary>亮度噪声，0~1 的尺度（和像素值同单位）。</summary>
            public float luma;

            /// <summary>色度噪声。高感光下色噪往往比亮噪更扎眼，两者要分开看。</summary>
            public float chroma;

            public bool valid;
        }

        public static Result Analyze(Color32[] px, int w, int h)
        {
            var res = new Result();
            if (px == null || w < Patch || h < Patch || px.Length < w * h) return res;

            int n = w * h;
            var y = new float[n];
            var cb = new float[n];

            for (int i = 0; i < n; i++)
            {
                float r = px[i].r / 255f, g = px[i].g / 255f, b = px[i].b / 255f;
                y[i] = 0.299f * r + 0.587f * g + 0.114f * b;

                // 只取一个色差通道就够了：估的是量级不是方向
                cb[i] = 0.5f * (b - y[i]) + 0.5f;
            }

            res.luma = Estimate(y, w, h);
            res.chroma = Estimate(cb, w, h);
            res.valid = true;
            return res;
        }

        /// <summary>
        /// 分块估计，取低分位。
        /// </summary>
        static float Estimate(float[] v, int w, int h)
        {
            int cols = w / Patch, rows = h / Patch;
            if (cols < 1 || rows < 1) return 0f;

            var vals = new float[cols * rows];
            int k = 0;

            for (int row = 0; row < rows; row++)
                for (int col = 0; col < cols; col++)
                    vals[k++] = PatchSigma(v, w, col * Patch, row * Patch);

            System.Array.Sort(vals);

            // 一块也没有的极端情况上面已经挡掉了，这里至少有 1 个
            int idx = Mathf.Clamp(Mathf.FloorToInt((vals.Length - 1) * Percentile), 0, vals.Length - 1);
            return vals[idx];
        }

        /// <summary>
        /// 一块的 sigma。
        ///
        /// 核是 [[1,-2,1],[-2,4,-2],[1,-2,1]]，它是两个方向二阶差分的和。
        /// 系数 √(π/2)/6 把"绝对值的均值"折算成高斯分布的标准差：
        /// 卷积后的方差是原噪声方差的 36 倍，而半正态分布的均值是 σ√(2/π)。
        /// </summary>
        static float PatchSigma(float[] v, int stride, int x0, int y0)
        {
            double sum = 0.0;
            int count = 0;

            for (int y = y0 + 1; y < y0 + Patch - 1; y++)
            {
                for (int x = x0 + 1; x < x0 + Patch - 1; x++)
                {
                    int i = y * stride + x;
                    float c =
                        v[i - stride - 1] - 2f * v[i - stride] + v[i - stride + 1]
                        - 2f * v[i - 1] + 4f * v[i] - 2f * v[i + 1]
                        + v[i + stride - 1] - 2f * v[i + stride] + v[i + stride + 1];

                    sum += c < 0f ? -c : c;
                    count++;
                }
            }

            if (count == 0) return 0f;
            return (float)(sum / count * 0.20888568f);   // √(π/2) / 6
        }

        /// <summary>
        /// 把估出来的 sigma 折成 0~1 的降噪强度。
        ///
        /// 上限取 12/255：再往上就不是"有点噪"而是"这张废了"，
        /// 强度拉满也救不回来，反而把还剩的细节抹平。
        /// </summary>
        public static float SuggestStrength(float sigma) =>
            Mathf.Clamp01(sigma / (12f / 255f));
    }
}
