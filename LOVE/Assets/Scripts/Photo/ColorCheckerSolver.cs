using UnityEngine;

namespace Love.Tools
{
    /// <summary>
    /// 24 色卡校色矩阵求解。
    ///
    /// 做法：从画面上采到 24 个色块的实测 RGB，和标准参考值做最小二乘拟合，
    /// 解出一个 3x4 矩阵（3x3 线性变换 + 偏移），让实测值尽量落到参考值上。
    ///
    /// 比手填色域矩阵可靠得多——手填一旦填错，偏色很隐蔽且极难排查，
    /// 而实测拟合是对着你这台相机、这支镜头、这个光线的真实数据算出来的。
    /// </summary>
    public static class ColorCheckerSolver
    {
        /// <summary>
        /// X-Rite ColorChecker 24 色块的标准 sRGB 值（D65），按拍摄时的行优先顺序：
        /// 第一行 深肤色 浅肤色 天蓝 树叶绿 蓝花 蓝绿
        /// 第二行 橙 紫蓝 中红 紫 黄绿 橙黄
        /// 第三行 蓝 绿 红 黄 品红 青
        /// 第四行 白 灰8 灰6.5 灰5 灰3.5 黑
        /// </summary>
        public static readonly Color32[] Reference =
        {
            new Color32(115,  82,  68, 255), new Color32(194, 150, 130, 255),
            new Color32( 98, 122, 157, 255), new Color32( 87, 108,  67, 255),
            new Color32(133, 128, 177, 255), new Color32(103, 189, 170, 255),

            new Color32(214, 126,  44, 255), new Color32( 80,  91, 166, 255),
            new Color32(193,  90,  99, 255), new Color32( 94,  60, 108, 255),
            new Color32(157, 188,  64, 255), new Color32(224, 163,  46, 255),

            new Color32( 56,  61, 150, 255), new Color32( 70, 148,  73, 255),
            new Color32(175,  54,  60, 255), new Color32(231, 199,  31, 255),
            new Color32(187,  86, 149, 255), new Color32(  8, 133, 161, 255),

            new Color32(243, 243, 242, 255), new Color32(200, 200, 200, 255),
            new Color32(160, 160, 160, 255), new Color32(122, 122, 121, 255),
            new Color32( 85,  85,  85, 255), new Color32( 52,  52,  52, 255),
        };

        public const int Columns = 6;
        public const int Rows = 4;
        public const int PatchCount = Columns * Rows;

        /// <summary>sRGB 传递函数的逆，把 gamma 值还原成线性光。</summary>
        public static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        static Vector3 ToLinear(Color c) => new Vector3(
            SrgbToLinear(Mathf.Clamp01(c.r)),
            SrgbToLinear(Mathf.Clamp01(c.g)),
            SrgbToLinear(Mathf.Clamp01(c.b)));

        /// <summary>
        /// 解 3x4 校色矩阵。measured 是 24 个实测的 sRGB 值（0~1）。
        ///
        /// 内部会把实测值和参考值都转到线性空间再拟合——
        /// shader 里矩阵是在 LOG 解码之后、线性空间应用的，
        /// 在 gamma 空间拟合出来的矩阵拿去线性空间用，结果是错的（而且错得很隐蔽）。
        ///
        /// 返回行优先的 12 个浮点，失败返回 null。
        /// </summary>
        public static float[] Solve(Color[] measured, out float residual)
        {
            residual = 0f;
            if (measured == null || measured.Length < PatchCount) return null;

            // 每个样本的自变量是 [r g b 1]，因变量是参考值的三个通道。
            // 三个通道各自独立解一个 4 元线性方程组，用正规方程 A^T A x = A^T b。
            var ata = new double[4, 4];
            var atb = new double[4, 3];

            for (int i = 0; i < PatchCount; i++)
            {
                Vector3 m0 = ToLinear(measured[i]);
                Vector3 t = ToLinear((Color)Reference[i]);
                double[] a = { m0.x, m0.y, m0.z, 1.0 };

                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 4; c++) ata[r, c] += a[r] * a[c];
                    atb[r, 0] += a[r] * t.x;
                    atb[r, 1] += a[r] * t.y;
                    atb[r, 2] += a[r] * t.z;
                }
            }

            // 轻微的岭回归：色卡采样有噪声时正规方程会病态，加一点点对角项能稳住解
            for (int r = 0; r < 4; r++) ata[r, r] += 1e-6;

            if (!Invert4(ata, out double[,] inv)) return null;

            var m = new float[12];
            for (int ch = 0; ch < 3; ch++)
            {
                for (int r = 0; r < 4; r++)
                {
                    double sum = 0;
                    for (int k = 0; k < 4; k++) sum += inv[r, k] * atb[k, ch];
                    m[ch * 4 + r] = (float)sum;
                }
            }

            // 残差：拟合后平均每个通道还差多少，用来判断这次采样靠不靠谱
            double err = 0;
            for (int i = 0; i < PatchCount; i++)
            {
                Vector3 m0 = ToLinear(measured[i]);
                Vector3 t = ToLinear((Color)Reference[i]);
                float[] v = { m0.x, m0.y, m0.z, 1f };
                for (int ch = 0; ch < 3; ch++)
                {
                    float o = m[ch * 4 + 0] * v[0] + m[ch * 4 + 1] * v[1] +
                              m[ch * 4 + 2] * v[2] + m[ch * 4 + 3] * v[3];
                    float target = ch == 0 ? t.x : ch == 1 ? t.y : t.z;
                    err += (o - target) * (o - target);
                }
            }
            residual = Mathf.Sqrt((float)(err / (PatchCount * 3)));

            return m;
        }

        /// <summary>高斯-约当求 4x4 逆矩阵。</summary>
        static bool Invert4(double[,] a, out double[,] inv)
        {
            const int N = 4;
            var m = new double[N, N * 2];
            for (int r = 0; r < N; r++)
            {
                for (int c = 0; c < N; c++) m[r, c] = a[r, c];
                m[r, N + r] = 1.0;
            }

            for (int col = 0; col < N; col++)
            {
                // 选主元，避免除以接近零的数
                int pivot = col;
                for (int r = col + 1; r < N; r++)
                    if (System.Math.Abs(m[r, col]) > System.Math.Abs(m[pivot, col])) pivot = r;

                if (System.Math.Abs(m[pivot, col]) < 1e-12) { inv = null; return false; }

                if (pivot != col)
                    for (int c = 0; c < N * 2; c++)
                    {
                        double t = m[col, c]; m[col, c] = m[pivot, c]; m[pivot, c] = t;
                    }

                double d = m[col, col];
                for (int c = 0; c < N * 2; c++) m[col, c] /= d;

                for (int r = 0; r < N; r++)
                {
                    if (r == col) continue;
                    double f = m[r, col];
                    if (f == 0) continue;
                    for (int c = 0; c < N * 2; c++) m[r, c] -= f * m[col, c];
                }
            }

            inv = new double[N, N];
            for (int r = 0; r < N; r++)
                for (int c = 0; c < N; c++) inv[r, c] = m[r, N + c];
            return true;
        }

        /// <summary>
        /// 按色卡四角在图上取 24 个色块的中心值。
        /// 角点按 左上 → 右上 → 右下 → 左下 的顺序传入，用双线性插值定位每格中心，
        /// 所以色卡有透视变形也能大致对上。
        /// </summary>
        public static Color[] SamplePatches(Texture2D img, Vector2[] corners, int sampleRadius = 3)
        {
            if (img == null || corners == null || corners.Length < 4) return null;

            var result = new Color[PatchCount];
            for (int row = 0; row < Rows; row++)
            {
                for (int col = 0; col < Columns; col++)
                {
                    // 每格中心在归一化坐标里的位置
                    float u = (col + 0.5f) / Columns;
                    float v = (row + 0.5f) / Rows;

                    Vector2 top = Vector2.Lerp(corners[0], corners[1], u);
                    Vector2 bottom = Vector2.Lerp(corners[3], corners[2], u);
                    Vector2 p = Vector2.Lerp(top, bottom, v);

                    result[row * Columns + col] = AverageAround(img, p, sampleRadius);
                }
            }
            return result;
        }

        /// <summary>取一小块的平均值，单点采样太容易踩到噪点或色卡上的脏点。</summary>
        static Color AverageAround(Texture2D img, Vector2 px, int radius)
        {
            int cx = Mathf.RoundToInt(px.x), cy = Mathf.RoundToInt(px.y);
            Color sum = Color.black;
            int n = 0;

            for (int y = -radius; y <= radius; y++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    int sx = Mathf.Clamp(cx + x, 0, img.width - 1);
                    int sy = Mathf.Clamp(cy + y, 0, img.height - 1);
                    sum += img.GetPixel(sx, sy);
                    n++;
                }
            }
            return n > 0 ? sum / n : Color.black;
        }
    }
}
