using Love.Video;
using UnityEngine;

namespace Love.Tools
{
    /// <summary>
    /// 把 <see cref="SkyDetect"/> 算出来的天空蒙版做成一张贴图，交给渲染管线。
    ///
    /// 两个坐标系问题必须在这里解决，错了都不会报错、只会"蒙版盖歪了"：
    ///
    /// 一、**贴图数据第一行是画面底部**。检测是从"顶边"往下漫延的，
    ///     直接把 GetPixels32 喂进去等于从地面开始找天空，什么也找不到。
    ///
    /// 二、**蒙版在管线里跑在几何变换之后**（几何是 Pass 5，蒙版是 Pass 6），
    ///     所以采样用的是裁剪 / 旋转之后的 uv，而缩略图还是原始构图。
    ///     这里用 DisplayUvToSource 把两边对上——和画布吸管用的是同一个函数。
    /// </summary>
    public static class SkyMaskBuilder
    {
        /// <summary>
        /// 检测用多大的图。
        ///
        /// 漫延本身是 O(n)，再大也跑得动；限制在这个量级是因为**蒙版边缘的精度
        /// 到不了那么高**——羽化 + 双线性放大之后，再多的分辨率也看不出差别。
        /// </summary>
        const int WorkSize = 512;

        /// <summary>
        /// 漫延一次，结果在**源图坐标系**。
        ///
        /// 和几何变换分开是有原因的：漫延只跟源图有关，裁剪拉直改多少次都不影响它。
        /// 合在一起写的话，拖裁剪框的每一帧都要重新漫延一遍，界面立刻发涩。
        /// </summary>
        public static SkyDetect.Result Detect(Texture2D thumb, SkyDetect.Options o)
        {
            if (thumb == null) return default;

            int sw = thumb.width, sh = thumb.height;
            if (sw < 4 || sh < 4) return default;

            var raw = thumb.GetPixels32();
            if (raw == null || raw.Length < sw * sh) return default;

            // ---- 翻成"第一行是画面顶部" ----
            var top = new Color32[sw * sh];
            for (int y = 0; y < sh; y++)
                System.Array.Copy(raw, (sh - 1 - y) * sw, top, y * sw, sw);

            return SkyDetect.Run(top, sw, sh, o);
        }

        /// <summary>把源图坐标系的结果搬到显示空间，做成贴图。几何一变只需要重跑这一步。</summary>
        public static Texture2D ToTexture(SkyDetect.Result res, VideoGradeSettings s)
        {
            if (!res.found || res.mask == null || s == null) return null;

            int sw = res.w, sh = res.h;

            // ---- 重采样到显示空间 ----
            s.OutputSize(sw, sh, out int ow, out int oh);
            float k = WorkSize / (float)Mathf.Max(ow, oh);
            if (k < 1f) { ow = Mathf.Max(4, Mathf.RoundToInt(ow * k)); oh = Mathf.Max(4, Mathf.RoundToInt(oh * k)); }

            var tex = new Texture2D(ow, oh, TextureFormat.RGBA32, false, true)
            {
                name = "天空蒙版",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            var outPx = new Color32[ow * oh];
            bool geo = s.HasGeometry;

            for (int y = 0; y < oh; y++)
            {
                // 贴图第 y 行就是 uv.y = (y+0.5)/oh，两边都是下往上，正好对上
                float v = (y + 0.5f) / oh;
                for (int x = 0; x < ow; x++)
                {
                    float u = (x + 0.5f) / ow;

                    float su = u, sv = v;
                    if (geo)
                    {
                        var src = s.DisplayUvToSource(new Vector2(u, v), sw, sh);
                        su = src.x;
                        sv = src.y;

                        // 拉直会把画面转出边界，那些地方没有源图，也就谈不上天空
                        if (su < 0f || su > 1f || sv < 0f || sv > 1f)
                        {
                            outPx[y * ow + x] = new Color32(0, 0, 0, 255);
                            continue;
                        }
                    }

                    // 源 uv 是下往上的，而 mask 第一行是画面顶部，这里要倒回去
                    int mx = Mathf.Clamp((int)(su * sw), 0, sw - 1);
                    int my = Mathf.Clamp((int)((1f - sv) * sh), 0, sh - 1);

                    byte a = (byte)Mathf.Clamp(Mathf.RoundToInt(res.mask[my * sw + mx] * 255f), 0, 255);
                    outPx[y * ow + x] = new Color32(a, a, a, 255);
                }
            }

            tex.SetPixels32(outPx);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>
        /// 几何参数的指纹。变了就得重算蒙版——裁剪一动，之前那张就对不上构图了。
        /// </summary>
        public static int GeometryKey(VideoGradeSettings s)
        {
            if (s == null) return 0;
            unchecked
            {
                int k = 17;
                k = k * 31 + s.rotate90;
                k = k * 31 + (s.flipH ? 1 : 0);
                k = k * 31 + (s.flipV ? 2 : 0);
                k = k * 31 + (s.cropEnabled ? 4 : 0);
                k = k * 31 + Mathf.RoundToInt(s.straighten * 1000f);
                k = k * 31 + Mathf.RoundToInt(s.cropX * 10000f);
                k = k * 31 + Mathf.RoundToInt(s.cropY * 10000f);
                k = k * 31 + Mathf.RoundToInt(s.cropW * 10000f);
                k = k * 31 + Mathf.RoundToInt(s.cropH * 10000f);
                return k;
            }
        }
    }
}
