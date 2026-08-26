using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// 一套导出配置。
    ///
    /// 挑完片要发出去的时候，"全尺寸 PNG、文件名加 _graded"基本没一次是够用的：
    /// 发网上要限长边，发客户要加水印，一次导几十张要能按序号命名。
    /// </summary>
    [Serializable]
    public class ExportPreset
    {
        public string name = "默认";

        public bool jpg = true;
        public int jpgQuality = 92;

        /// <summary>长边上限，0 表示不限制。</summary>
        public int maxLongEdge;

        /// <summary>只缩不放。原图比上限还小的时候别硬拉大，那只会糊。</summary>
        public bool noUpscale = true;

        public string nameTemplate = "{name}_graded";
        public string subfolder = "";

        // ---- 水印 ----
        // 只做图片水印。文字水印要在没有 OnGUI 的地方把字渲进贴图，
        // 编辑器里没有干净的做法；而实际用的人多半也是贴自己的 logo。
        public bool watermark;
        public string watermarkPath = "";
        public int corner = 3;            // 0 左上 1 右上 2 左下 3 右下
        public float wmScale = 0.16f;     // 相对输出长边
        public float wmOpacity = 0.75f;
        public float wmMargin = 0.03f;    // 相对输出长边

        /// <summary>重名时怎么办。0 加序号 1 覆盖 2 跳过。</summary>
        public int collision;

        public string Extension => jpg ? ".jpg" : ".png";

        public ExportPreset Clone() => (ExportPreset)MemberwiseClone();
    }

    /// <summary>命名模板要用到的一张图的信息。</summary>
    public struct ExportContext
    {
        public string sourceName;   // 不带扩展名
        public int index;           // 从 1 开始
        public int total;
        public int width, height;
        public int rating;
        public DateTime time;
    }

    /// <summary>
    /// 导出时那些纯计算的部分：命名、尺寸、重名。
    ///
    /// 单独拎出来是因为它们没有任何 Unity 依赖，可以离线测——
    /// 而这类东西恰恰最容易出"看着对、批量跑一遍才发现全叠在一个文件上"的问题。
    /// </summary>
    public static class ExportNaming
    {
        /// <summary>模板里认得的记号，界面上要列出来给人看。</summary>
        public static readonly (string token, string desc)[] Tokens =
        {
            ("{name}", "原文件名（不含扩展名）"),
            ("{index}", "序号，从 1 开始"),
            ("{index2}", "序号补到两位，01 02 …"),
            ("{index3}", "序号补到三位，001 002 …"),
            ("{total}", "这一批一共几张"),
            ("{date}", "导出日期 20260826"),
            ("{time}", "导出时刻 143052"),
            ("{rating}", "星级 0~5"),
            ("{w}", "输出宽"),
            ("{h}", "输出高"),
        };

        // Windows 不让出现在文件名里的字符，外加控制字符
        static readonly char[] Illegal = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

        public static string Expand(string template, ExportContext c)
        {
            if (string.IsNullOrEmpty(template)) template = "{name}";

            var sb = new StringBuilder(template);
            sb.Replace("{name}", c.sourceName ?? "");
            sb.Replace("{index3}", c.index.ToString("D3", CultureInfo.InvariantCulture));
            sb.Replace("{index2}", c.index.ToString("D2", CultureInfo.InvariantCulture));
            sb.Replace("{index}", c.index.ToString(CultureInfo.InvariantCulture));
            sb.Replace("{total}", c.total.ToString(CultureInfo.InvariantCulture));
            sb.Replace("{date}", c.time.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            sb.Replace("{time}", c.time.ToString("HHmmss", CultureInfo.InvariantCulture));
            sb.Replace("{rating}", c.rating.ToString(CultureInfo.InvariantCulture));
            sb.Replace("{w}", c.width.ToString(CultureInfo.InvariantCulture));
            sb.Replace("{h}", c.height.ToString(CultureInfo.InvariantCulture));

            return Sanitize(sb.ToString());
        }

        /// <summary>
        /// 洗掉文件名里不合法的字符。
        ///
        /// 洗完可能什么都不剩（模板写成 "//" 之类），那时候得给个兜底名字——
        /// 空文件名会让 File.Move 抛一个跟起因八竿子打不着的异常。
        /// </summary>
        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "untitled";

            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch < 32 || Array.IndexOf(Illegal, ch) >= 0) continue;
                sb.Append(ch);
            }

            // 结尾的点和空格在 Windows 上会被悄悄吃掉，先自己去掉免得对不上
            string r = sb.ToString().Trim().TrimEnd('.');
            return r.Length == 0 ? "untitled" : r;
        }

        /// <summary>按长边上限算输出尺寸。返回的宽高都保证 >= 1。</summary>
        public static void ComputeSize(int srcW, int srcH, ExportPreset p, out int w, out int h)
        {
            w = Mathf.Max(1, srcW);
            h = Mathf.Max(1, srcH);
            if (p == null || p.maxLongEdge <= 0) return;

            int longEdge = Mathf.Max(w, h);
            if (p.noUpscale && longEdge <= p.maxLongEdge) return;

            float k = p.maxLongEdge / (float)longEdge;
            w = Mathf.Max(1, Mathf.RoundToInt(w * k));
            h = Mathf.Max(1, Mathf.RoundToInt(h * k));
        }

        /// <summary>
        /// 处理重名。返回 null 表示"跳过这一张"。
        ///
        /// <paramref name="taken"/> 是这一批里已经用掉的名字：光看磁盘不够，
        /// 同一批里两张图算出同一个名字时，文件还没落地，第二张会直接盖掉第一张。
        /// </summary>
        public static string Resolve(string dir, string baseName, string ext,
                                     int mode, HashSet<string> taken, Func<string, bool> exists)
        {
            exists = exists ?? File.Exists;
            string first = Path.Combine(dir, baseName + ext);

            bool Used(string p) => (taken != null && taken.Contains(p)) || exists(p);

            if (!Used(first))
            {
                taken?.Add(first);
                return first;
            }

            if (mode == 1) { taken?.Add(first); return first; }   // 覆盖
            if (mode == 2) return null;                            // 跳过

            for (int i = 2; i < 10000; i++)
            {
                string p = Path.Combine(dir, baseName + "_" + i + ext);
                if (Used(p)) continue;
                taken?.Add(p);
                return p;
            }
            return null;
        }
    }
}
