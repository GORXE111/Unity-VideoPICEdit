using System.Collections.Generic;
using UnityEngine;

namespace Love.Tools
{
    /// <summary>
    /// 把一行字渲进 RenderTexture。
    ///
    /// 编辑器里"渲字"的常规做法是 GUI.Label，但那只能在 OnGUI 里画到窗口上，
    /// 导出流程根本不在 OnGUI 里（也绝不能在那儿 Blit）。
    /// 所以这里自己走一遍：Font.GetCharacterInfo 拿字形在图集里的 UV，拼四边形，GL 画出去。
    /// </summary>
    public static class TextStamp
    {
        /// <summary>一个字形要画的那块四边形。位置是像素坐标、原点左上、y 向下。</summary>
        public struct Glyph
        {
            public Rect rect;                       // 屏幕位置
            public Vector2 uvBL, uvTL, uvTR, uvBR;  // 图集 UV，逆时针从左下起
        }

        /// <summary>排好版的一串字，外加它的墨迹外框。</summary>
        public class Layout
        {
            public readonly List<Glyph> glyphs = new List<Glyph>();
            /// <summary>墨迹外框的尺寸。空串或全空格时为 0。</summary>
            public Vector2 size;
            public bool Empty => glyphs.Count == 0 || size.x <= 0f || size.y <= 0f;
        }

        static Material _mat;

        static Material Mat
        {
            get
            {
                if (_mat == null)
                {
                    var sh = Shader.Find("Hidden/Love/TextStamp");
                    if (sh == null) return null;
                    _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                }
                return _mat;
            }
        }

        /// <summary>没指定字体时用这个。系统自带，不涉及分发授权。</summary>
        public static Font DefaultFont
        {
            get
            {
                var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return f != null ? f : Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
        }

        /// <summary>
        /// 排一行字，返回每个字形的位置和 UV，以及整体墨迹外框。
        ///
        /// 量的是**墨迹**外框，不是行高。水印按行高摆的话，"© 2026"（没有下伸部）
        /// 底下会空出一截，靠角的视觉边距就跟着文字内容变。
        /// </summary>
        public static Layout Measure(Font font, string text, int pixelSize)
        {
            var lay = new Layout();
            if (font == null || string.IsNullOrEmpty(text) || pixelSize <= 0) return lay;

            // 静态字体只认 size 0，取到的是烘焙时那个字号，得自己缩放
            bool dyn = font.dynamic;
            int reqSize = dyn ? pixelSize : 0;
            float k = dyn ? 1f : pixelSize / (float)Mathf.Max(font.fontSize, 1);

            // 必须先把整串请求进图集再查询：图集一旦重建，
            // 之前拿到的 CharacterInfo 里的 UV 就指向别的字了
            if (dyn) font.RequestCharactersInTexture(text, reqSize, FontStyle.Normal);

            float pen = 0f;
            float minL = float.MaxValue, maxR = float.MinValue;
            float minT = float.MaxValue, maxB = float.MinValue;

            foreach (char ch in text)
            {
                if (!font.GetCharacterInfo(ch, out CharacterInfo ci, reqSize, FontStyle.Normal))
                    continue;

                float l = pen + ci.minX * k;
                float r = pen + ci.maxX * k;
                // 基线在 y = 0，maxY 在基线之上；这里 y 向下所以取负
                float t = -ci.maxY * k;
                float b = -ci.minY * k;

                if (r > l && b > t)
                {
                    lay.glyphs.Add(new Glyph
                    {
                        rect = new Rect(l, t, r - l, b - t),
                        uvBL = ci.uvBottomLeft,
                        uvTL = ci.uvTopLeft,
                        uvTR = ci.uvTopRight,
                        uvBR = ci.uvBottomRight,
                    });

                    if (l < minL) minL = l;
                    if (r > maxR) maxR = r;
                    if (t < minT) minT = t;
                    if (b > maxB) maxB = b;
                }

                pen += ci.advance * k;
            }

            if (lay.glyphs.Count == 0) return lay;

            lay.size = new Vector2(maxR - minL, maxB - minT);

            // 把墨迹外框的左上角挪到原点，画的时候直接加偏移就行
            for (int i = 0; i < lay.glyphs.Count; i++)
            {
                var g = lay.glyphs[i];
                g.rect.x -= minL;
                g.rect.y -= minT;
                lay.glyphs[i] = g;
            }
            return lay;
        }

        /// <summary>
        /// 画到当前 RenderTexture.active 上。调用方负责设好 active 和像素矩阵。
        ///
        /// <paramref name="outlinePx"/> 大于 0 时先描一圈暗边：
        /// 亮天空上的白字基本等于没有，加一圈就稳了。
        /// </summary>
        public static void Draw(Layout lay, Font font, Vector2 origin, Color color, float outlinePx)
        {
            if (lay == null || lay.Empty || font == null) return;

            var mat = Mat;
            if (mat == null) return;

            var atlas = font.material != null ? font.material.mainTexture : null;
            if (atlas == null) return;

            mat.mainTexture = atlas;
            mat.SetPass(0);

            if (outlinePx > 0.5f)
            {
                var oc = new Color(0f, 0f, 0f, color.a);
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI * 0.25f;
                    Emit(lay, origin + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * outlinePx, oc);
                }
            }

            Emit(lay, origin, color);
        }

        static void Emit(Layout lay, Vector2 origin, Color c)
        {
            GL.Begin(GL.QUADS);

            // 线性色彩空间下写进 sRGB 的 RT 时，硬件会做 linear→sRGB 编码。
            // 直接喂界面上选的 sRGB 值会被编码两次，出来发白
            GL.Color(QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c);

            foreach (var g in lay.glyphs)
            {
                float x0 = origin.x + g.rect.x, x1 = x0 + g.rect.width;
                float y0 = origin.y + g.rect.y, y1 = y0 + g.rect.height;

                // y 向下：rect 的 y0 是字形顶部，对应字体图集里的 uvTop*
                GL.TexCoord(g.uvBL); GL.Vertex3(x0, y1, 0f);
                GL.TexCoord(g.uvBR); GL.Vertex3(x1, y1, 0f);
                GL.TexCoord(g.uvTR); GL.Vertex3(x1, y0, 0f);
                GL.TexCoord(g.uvTL); GL.Vertex3(x0, y0, 0f);
            }

            GL.End();
        }
    }
}
