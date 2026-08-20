using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// .cube 格式 LUT 的读写。达芬奇、Premiere、大部分调色软件都用这个格式交换 look。
    ///
    /// 导入：.cube 文件 -> Texture3D，接进调色管线
    /// 导出：把当前参数烘成 .cube，拿回达芬奇里用
    /// </summary>
    public static class CubeLutIO
    {
        public const int DefaultBakeSize = 33;   // 行业惯例，33 或 65

        #region 读取

        /// <summary>解析 .cube 文本，返回 Texture3D。失败返回 null 并填 error。</summary>
        public static Texture3D Load(string path, out string error)
        {
            error = null;
            if (!File.Exists(path)) { error = "文件不存在"; return null; }

            int size = 0;
            Vector3 domainMin = Vector3.zero, domainMax = Vector3.one;
            var values = new List<Vector3>();
            bool is1D = false;

            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (line.StartsWith("TITLE", System.StringComparison.OrdinalIgnoreCase)) continue;

                if (line.StartsWith("LUT_3D_SIZE", System.StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(Tail(line), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                    continue;
                }
                if (line.StartsWith("LUT_1D_SIZE", System.StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(Tail(line), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                    is1D = true;
                    continue;
                }
                if (line.StartsWith("DOMAIN_MIN", System.StringComparison.OrdinalIgnoreCase))
                {
                    domainMin = ParseVec(line); continue;
                }
                if (line.StartsWith("DOMAIN_MAX", System.StringComparison.OrdinalIgnoreCase))
                {
                    domainMax = ParseVec(line); continue;
                }

                // 剩下的应该是数据行
                var parts = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                    values.Add(new Vector3(r, g, b));
            }

            if (size <= 1) { error = "没找到 LUT_3D_SIZE / LUT_1D_SIZE"; return null; }

            if (is1D)
            {
                // 1D LUT 只有色调曲线，扩成 3D 才能走同一条采样路径
                if (values.Count < size) { error = $"1D 数据行不足：期望 {size}，实得 {values.Count}"; return null; }
                return Expand1DTo3D(values, size);
            }

            int need = size * size * size;
            if (values.Count < need)
            {
                error = $"数据行不足：期望 {need}，实得 {values.Count}";
                return null;
            }

            var tex = NewLutTexture(size);
            var px = new Color[need];

            // .cube 的排列是 r 变化最快，Texture3D 的索引是 x + y*size + z*size*size，
            // 两者恰好一致，所以可以直接顺序填
            Vector3 range = domainMax - domainMin;
            for (int i = 0; i < need; i++)
            {
                Vector3 v = values[i];
                // 归一化回 0~1，有些 LUT 的定义域不是 [0,1]
                if (range.x > 1e-6f) v.x = (v.x - domainMin.x) / range.x;
                if (range.y > 1e-6f) v.y = (v.y - domainMin.y) / range.y;
                if (range.z > 1e-6f) v.z = (v.z - domainMin.z) / range.z;
                px[i] = new Color(v.x, v.y, v.z, 1f);
            }

            tex.SetPixels(px);
            tex.Apply(false, false);
            return tex;
        }

        static Texture3D Expand1DTo3D(List<Vector3> curve, int size)
        {
            var tex = NewLutTexture(size);
            var px = new Color[size * size * size];

            for (int b = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++)
                        px[r + g * size + b * size * size] =
                            new Color(curve[r].x, curve[g].y, curve[b].z, 1f);

            tex.SetPixels(px);
            tex.Apply(false, false);
            return tex;
        }

        static Texture3D NewLutTexture(int size) => new Texture3D(size, size, size, TextureFormat.RGBAHalf, false)
        {
            name = "CubeLut",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };

        static string Tail(string line)
        {
            int i = line.IndexOf(' ');
            return i < 0 ? "" : line.Substring(i + 1).Trim();
        }

        static Vector3 ParseVec(string line)
        {
            var p = Tail(line).Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            var v = Vector3.zero;
            if (p.Length >= 3)
            {
                float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v.x);
                float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v.y);
                float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out v.z);
            }
            return v;
        }

        #endregion

        #region 烘焙与写出

        /// <summary>
        /// 生成单位 LUT 的条带图：宽 size*size，高 size。
        /// x = r + b*size，y = g。把它当成一张普通图片过一遍调色管线，
        /// 出来的就是这套参数对应的 LUT。
        /// </summary>
        public static Texture2D BuildIdentityStrip(int size)
        {
            var tex = new Texture2D(size * size, size, TextureFormat.RGBA32, false, false)
            {
                name = "IdentityLutStrip",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var px = new Color32[size * size * size];
            float inv = 1f / (size - 1);

            for (int b = 0; b < size; b++)
            {
                for (int g = 0; g < size; g++)
                {
                    for (int r = 0; r < size; r++)
                    {
                        int x = r + b * size;
                        int y = g;
                        px[y * size * size + x] = new Color32(
                            (byte)Mathf.RoundToInt(r * inv * 255f),
                            (byte)Mathf.RoundToInt(g * inv * 255f),
                            (byte)Mathf.RoundToInt(b * inv * 255f), 255);
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>把过完调色管线的条带图写成 .cube。</summary>
        public static bool WriteCube(string path, Texture2D graded, int size, string title, out string error)
        {
            error = null;
            if (graded == null || graded.width != size * size || graded.height != size)
            {
                error = "条带图尺寸和 LUT 尺寸对不上";
                return false;
            }

            try
            {
                var px = graded.GetPixels();
                var sb = new StringBuilder(size * size * size * 26 + 256);

                sb.Append("TITLE \"").Append(title).Append("\"\n");
                sb.Append("# Generated by GalgameLOVE 调色台\n");
                sb.Append("LUT_3D_SIZE ").Append(size).Append('\n');
                sb.Append("DOMAIN_MIN 0.0 0.0 0.0\n");
                sb.Append("DOMAIN_MAX 1.0 1.0 1.0\n\n");

                // .cube 要求 r 变化最快
                for (int b = 0; b < size; b++)
                {
                    for (int g = 0; g < size; g++)
                    {
                        for (int r = 0; r < size; r++)
                        {
                            Color c = px[g * size * size + (r + b * size)];
                            sb.Append(c.r.ToString("0.000000", CultureInfo.InvariantCulture)).Append(' ')
                              .Append(c.g.ToString("0.000000", CultureInfo.InvariantCulture)).Append(' ')
                              .Append(c.b.ToString("0.000000", CultureInfo.InvariantCulture)).Append('\n');
                        }
                    }
                }

                File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
                return true;
            }
            catch (System.Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        #endregion
    }
}
