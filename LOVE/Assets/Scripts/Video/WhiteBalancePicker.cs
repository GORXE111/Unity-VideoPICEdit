using UnityEngine;

namespace Love.Video
{
    /// <summary>
    /// 白平衡吸管：点画面上一处「本该是中性灰」的地方，反解出让它真的变中性的色温 / 色调。
    ///
    /// 为什么用数值搜索而不是解析求逆：正向映射里色温经过一个带条件分支的偏移
    /// （冷暖两侧系数不同），色调又经过 StandardIlluminantY 这条 x 的二次曲线，
    /// 复合之后没有干净的闭式逆。参数只有两个、范围又只有 ±1，
    /// 粗到细扫几轮就能收敛到肉眼分辨不出的精度，几千次三维矩阵乘而已。
    /// </summary>
    public static class WhiteBalancePicker
    {
        // 和 VideoGradeCommon.cginc 里的两个常量矩阵必须逐位一致，改一边就要改另一边
        static readonly Vector3 L2M0 = new Vector3(3.90405e-1f, 5.49941e-1f, 8.92632e-3f);
        static readonly Vector3 L2M1 = new Vector3(7.08416e-2f, 9.63172e-1f, 1.35775e-3f);
        static readonly Vector3 L2M2 = new Vector3(2.31082e-2f, 1.28021e-1f, 9.36245e-1f);

        static readonly Vector3 M2L0 = new Vector3( 2.85847e+0f, -1.62879e+0f, -2.48910e-2f);
        static readonly Vector3 M2L1 = new Vector3(-2.10182e-1f,  1.15820e+0f,  3.24281e-4f);
        static readonly Vector3 M2L2 = new Vector3(-4.18120e-2f, -1.18169e-1f,  1.06867e+0f);

        /// <summary>
        /// <paramref name="linearPixel"/> 必须是<b>线性空间</b>的源像素值。
        ///
        /// 注意它取的是调色之前的原始值——如果素材开了 LOG 解码或色卡校色矩阵，
        /// 吸管算出来的结果会偏，因为那两步排在白平衡之前、这里没有复现。
        /// </summary>
        public static void Solve(Vector3 linearPixel, out float temperature, out float tint)
        {
            temperature = 0f;
            tint = 0f;

            // 全黑或接近全黑的像素没有色度信息，反解出来的是纯噪声
            if (linearPixel.x + linearPixel.y + linearPixel.z < 1e-4f) return;

            float loT = -1f, hiT = 1f, loI = -1f, hiI = 1f;
            const int N = 24;

            // 粗到细：每轮在当前最优点周围收缩到两格宽，五轮之后精度约 1e-4
            for (int pass = 0; pass < 5; pass++)
            {
                float best = float.MaxValue;
                float bt = temperature, bi = tint;

                for (int a = 0; a <= N; a++)
                {
                    float t = Mathf.Lerp(loT, hiT, a / (float)N);
                    for (int b = 0; b <= N; b++)
                    {
                        float n = Mathf.Lerp(loI, hiI, b / (float)N);
                        float c = Chroma(linearPixel, t, n);
                        if (c < best) { best = c; bt = t; bi = n; }
                    }
                }

                temperature = bt;
                tint = bi;

                float spanT = (hiT - loT) / N * 2f;
                float spanI = (hiI - loI) / N * 2f;
                loT = bt - spanT; hiT = bt + spanT;
                loI = bi - spanI; hiI = bi + spanI;
            }

            temperature = Mathf.Clamp(temperature, -1f, 1f);
            tint = Mathf.Clamp(tint, -1f, 1f);
        }

        /// <summary>这组色温色调作用到该像素后，离中性还有多远。0 = 正好中性。</summary>
        static float Chroma(Vector3 col, float temperature, float tint)
        {
            var s = new VideoGradeSettings { temperature = temperature, tint = tint };
            Vector3 bal = s.ComputeColorBalance();

            // 完整复现 shader 里那三行：线性 -> LMS -> 逐通道缩放 -> 线性
            var lms = new Vector3(Vector3.Dot(L2M0, col), Vector3.Dot(L2M1, col), Vector3.Dot(L2M2, col));
            lms = new Vector3(lms.x * bal.x, lms.y * bal.y, lms.z * bal.z);
            var o = new Vector3(Vector3.Dot(M2L0, lms), Vector3.Dot(M2L1, lms), Vector3.Dot(M2L2, lms));

            // 先按均值归一化，否则"整体变暗"也会被算成更接近中性
            float m = (o.x + o.y + o.z) / 3f;
            if (m < 1e-5f) return float.MaxValue;
            o /= m;

            return (o.x - 1f) * (o.x - 1f) + (o.y - 1f) * (o.y - 1f) + (o.z - 1f) * (o.z - 1f);
        }
    }
}
