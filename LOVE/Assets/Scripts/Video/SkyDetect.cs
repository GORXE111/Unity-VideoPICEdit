using UnityEngine;

namespace Love.Video
{
    /// <summary>
    /// 天空检测。
    ///
    /// 不是 AI。Lightroom 那套是分割模型，这里走的是"从画面顶边往下漫延"的老办法——
    /// 因为把天空和蓝衣服、蓝色湖面区分开的关键根本不是颜色，是**连通性**：
    /// 天空一定连着画面顶边，衣服不连。纯颜色阈值做不到这一点，漫延能。
    ///
    /// 三道闸门配合：
    ///   连通  —— 只能从顶边一路漫过来
    ///   相邻色差 —— 卡的是相邻像素之间的差，不是跟种子的差。
    ///              天空从天顶到地平线本身就是一大片渐变，拿顶端颜色当基准会在半空中截断
    ///   纹理  —— 树梢、屋顶、地平线都是高频，漫延到那儿就停
    ///
    /// 没有 Unity 依赖（Color32 只当数据用），所以能离线测。
    /// </summary>
    public static class SkyDetect
    {
        public struct Options
        {
            /// <summary>相邻两个像素之间允许的颜色差，0~1。</summary>
            public float localTol;

            /// <summary>纹理上限。局部亮度落差超过这个就认作边界，不再往下漫。</summary>
            public float texture;

            /// <summary>
            /// 天空至少得这么亮，量的是 max(r,g,b) 不是 luma。
            ///
            /// luma 给蓝色的权重只有 0.114，偏振过的深蓝天顶 (30,60,160) 按 luma 算才 0.24，
            /// 会被当成"太暗"整片丢掉；按 max 算是 0.63，和夜空 (10,12,25) 的 0.10 拉得很开。
            /// 夜空被挡掉是有意的——那种画面调不调都一样。
            /// </summary>
            public float minValue;

            /// <summary>最多往下吃到画面高度的百分之几。挡的是天空和水面连在一起的情况。</summary>
            public float maxDepth;

            /// <summary>绿色主导多少算"不是天空"。漏进树冠和草地是这类检测最常见的失败。</summary>
            public float greenBias;

            /// <summary>羽化几遍。0 是硬边。</summary>
            public int feather;

            public static Options Default => new Options
            {
                localTol = 0.10f,
                texture = 0.10f,
                minValue = 0.28f,
                maxDepth = 0.75f,
                greenBias = 0.05f,
                feather = 1,
            };
        }

        public struct Result
        {
            public float[] mask;      // 长度 w*h，0~1，行序和入参一致
            public int w, h;

            /// <summary>选中的面积占比。</summary>
            public float coverage;

            /// <summary>顶边根本没有像样的天空时为 false，这时候 mask 全是 0。</summary>
            public bool found;
        }

        /// <summary>顶边至少要有这么大比例像天空，才认为这张图有天空。</summary>
        const float SeedRatio = 0.08f;

        /// <summary>从顶边往下取几行当种子。只看第 0 行的话，一道压边或者一根横过画面的树枝就废了。</summary>
        const int SeedRows = 3;

        public static Result Run(Color32[] px, int w, int h, Options o)
        {
            var res = new Result { w = w, h = h };
            if (px == null || w <= 2 || h <= 2 || px.Length < w * h) return res;

            int n = w * h;
            var r = new float[n];
            var g = new float[n];
            var b = new float[n];
            var lum = new float[n];
            var val = new float[n];

            for (int i = 0; i < n; i++)
            {
                r[i] = px[i].r / 255f;
                g[i] = px[i].g / 255f;
                b[i] = px[i].b / 255f;
                lum[i] = 0.299f * r[i] + 0.587f * g[i] + 0.114f * b[i];
                val[i] = Mathf.Max(r[i], Mathf.Max(g[i], b[i]));
            }

            // 局部纹理：跟四邻的最大亮度落差
            var tex = new float[n];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float m = 0f;
                    if (x > 0) m = Mathf.Max(m, Mathf.Abs(lum[i] - lum[i - 1]));
                    if (x < w - 1) m = Mathf.Max(m, Mathf.Abs(lum[i] - lum[i + 1]));
                    if (y > 0) m = Mathf.Max(m, Mathf.Abs(lum[i] - lum[i - w]));
                    if (y < h - 1) m = Mathf.Max(m, Mathf.Abs(lum[i] - lum[i + w]));
                    tex[i] = m;
                }
            }

            float minValue = Mathf.Clamp01(o.minValue);
            float greenBias = Mathf.Max(0f, o.greenBias);

            bool SkyLike(int i)
            {
                if (val[i] < minValue) return false;

                // 绿色主导的一律排除。这一条不看蓝不蓝——夕阳是橙红的，
                // 按"蓝色才是天空"来判，落日那张图直接全丢
                return !(g[i] > r[i] + greenBias && g[i] > b[i] + greenBias);
            }

            float Dist(int a, int c)
            {
                float dr = r[a] - r[c], dg = g[a] - g[c], db = b[a] - b[c];
                return Mathf.Sqrt(dr * dr + dg * dg + db * db) * 0.57735f;   // 1/√3，归一到 0~1
            }

            // ---- 种子 ----
            var mask = new float[n];
            var queue = new int[n];
            int qh = 0, qt = 0;

            // 种子要卡得比漫延严：一片有噪点的暗色墙面，逐点看也有不少地方"够平滑"，
            // 拿漫延的阈值筛顶边会攒出足够多的种子，于是室内照片也能"找到天空"。
            // 真天空的顶端本来就极平滑，这道更严的闸门不花什么代价
            float seedTex = o.texture * 0.5f;

            int seedRows = Mathf.Min(SeedRows, h);
            int seeds = 0;
            for (int y = 0; y < seedRows; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (mask[i] > 0f || !SkyLike(i) || tex[i] > seedTex) continue;
                    mask[i] = 1f;
                    queue[qt++] = i;
                    seeds++;
                }
            }

            if (seeds < Mathf.CeilToInt(w * seedRows * SeedRatio))
            {
                res.mask = mask;   // 全 0
                return res;
            }

            // ---- 漫延 ----
            int maxY = Mathf.Clamp(Mathf.RoundToInt(h * Mathf.Clamp01(o.maxDepth)), 1, h) - 1;
            int filled = 0;

            while (qh < qt)
            {
                int i = queue[qh++];
                filled++;
                int x = i % w, y = i / w;

                void TryPush(int j, int jy)
                {
                    if (jy > maxY) return;
                    if (mask[j] > 0f) return;
                    if (tex[j] > o.texture) return;
                    if (!SkyLike(j)) return;
                    if (Dist(i, j) > o.localTol) return;
                    mask[j] = 1f;
                    queue[qt++] = j;
                }

                if (x > 0) TryPush(i - 1, y);
                if (x < w - 1) TryPush(i + 1, y);
                if (y > 0) TryPush(i - w, y - 1);
                if (y < h - 1) TryPush(i + w, y + 1);
            }

            res.coverage = filled / (float)n;
            res.found = filled > 0;

            // ---- 羽化 ----
            for (int pass = 0; pass < Mathf.Clamp(o.feather, 0, 8); pass++)
                Blur3(mask, w, h);

            res.mask = mask;
            return res;
        }

        /// <summary>3×3 均值，就地做。分两趟横竖各一次，省一个数量级的乘加。</summary>
        static void Blur3(float[] m, int w, int h)
        {
            var tmp = new float[m.Length];

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float a = m[row + Mathf.Max(x - 1, 0)];
                    float c = m[row + x];
                    float d = m[row + Mathf.Min(x + 1, w - 1)];
                    tmp[row + x] = (a + c + d) * (1f / 3f);
                }
            }

            for (int y = 0; y < h; y++)
            {
                int up = Mathf.Max(y - 1, 0) * w;
                int dn = Mathf.Min(y + 1, h - 1) * w;
                int row = y * w;
                for (int x = 0; x < w; x++)
                    m[row + x] = (tmp[up + x] + tmp[row + x] + tmp[dn + x]) * (1f / 3f);
            }
        }
    }
}
