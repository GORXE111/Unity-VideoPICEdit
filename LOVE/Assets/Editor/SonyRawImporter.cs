using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// 索尼 ARW 解码。只做索尼一家、只做拜耳传感器，不追求通用 RAW 支持——
    /// 通用意味着要处理十几个厂商各自的私有压缩，那是 LibRaw 那种量级的工程。
    ///
    /// 解出来的是「已经做完黑电平、白平衡、相机色彩矩阵」的 sRGB 图，
    /// 直接喂给修图台现有的管线，后面的调色一步都不用改。
    ///
    /// 支持两种存储：未压缩（12/14/16bit）和 ARW2 有损压缩（cRAW）。
    /// 压缩格式按公开规范自己实现，没有引用 LibRaw / DNGlab 的代码——
    /// 那两个都是 LGPL，而格式本身不受版权保护。
    ///
    /// 已验证：
    ///   未压缩 —— ILCE-7RM4A 14bit，和机内 JPEG 预览逐通道比对，R/G 与 B/G 误差约 1%
    ///   ARW2   —— 和 dcraw 的参考实现做逐位差分测试，随机码流下输出完全一致
    /// </summary>
    public static class SonyRawImporter
    {
        public struct Options
        {
            /// <summary>降采样倍数：1=全尺寸，2=半尺寸。半尺寸直接用 2x2 拜耳块合成，不插值，反而更干净。</summary>
            public int downscale;

            /// <summary>把高光归一化到接近满值。纯粹是一个标量，不是曲线，关掉就是相机原始电平。</summary>
            public bool autoExposure;

            /// <summary>套用内置的相机色彩矩阵。想用色卡自己解矩阵时关掉它。</summary>
            public bool applyColorMatrix;

            public static Options Default => new Options
            {
                downscale = 1, autoExposure = true, applyColorMatrix = true,
            };
        }

        public class Result
        {
            public Texture2D texture;
            public string info;      // 成功时的说明，显示在界面上
            public string error;     // 非 null 表示失败
        }

        public static bool IsRaw(string path) =>
            path != null && Path.GetExtension(path).Equals(".arw", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 只取机内 JPEG 预览，不碰原始数据。
        ///
        /// 缩略图专用：全解一张 6100 万像素的 ARW 要十秒，而缩略图只有 128 像素，
        /// 为它跑一遍完整管线纯属浪费。预览是相机自己渲的，做缩略图绰绰有余。
        /// </summary>
        public static Texture2D LoadPreviewOnly(string path)
        {
            byte[] d;
            try { d = File.ReadAllBytes(path); }
            catch { return null; }
            if (d.Length < 16) return null;

            bool le = d[0] == 'I' && d[1] == 'I';
            if (!le && !(d[0] == 'M' && d[1] == 'M')) return null;

            var r = new TiffReader(d, le);
            var dirs = new List<Dictionary<int, TiffEntry>>();
            try { r.WalkIfds(r.U32(4), dirs, 0); }
            catch { return null; }

            var res = new Result();
            TryEmbeddedPreview(r, dirs, res);
            if (res.texture != null) res.texture.name = Path.GetFileNameWithoutExtension(path);
            return res.texture;
        }

        // ---------------- TIFF 标签 ----------------

        const int TagImageWidth = 0x0100, TagImageLength = 0x0101, TagBitsPerSample = 0x0102;
        const int TagCompression = 0x0103, TagPhotometric = 0x0106;
        const int TagStripOffsets = 0x0111, TagStripByteCounts = 0x0117;
        const int TagSubIFDs = 0x014A, TagCFAPattern = 0x828E;
        const int TagJpegOffset = 0x0201, TagJpegLength = 0x0202;
        const int TagModel = 0x0110;
        const int TagSonyBlack = 0x7310, TagSonyWB = 0x7313;
        const int TagWhiteLevel = 0xC61D, TagCropOrigin = 0xC61F, TagCropSize = 0xC620;

        const int TagSonyCurve = 0x7010;    // 11bit -> 14bit 的分段曲线，只有 ARW2 用得上

        const int PhotometricCFA = 32803;
        const int CompressionNone = 1;
        const int CompressionArw2 = 32767;  // 索尼有损压缩。同一个值也被老的 ARW1 用着

        public static Result Load(string path, Options o)
        {
            var res = new Result();
            byte[] d;
            try { d = File.ReadAllBytes(path); }
            catch (Exception e) { res.error = "读文件失败：" + e.Message; return res; }

            if (d.Length < 16) { res.error = "文件太小，不像是 ARW"; return res; }

            bool le = d[0] == 'I' && d[1] == 'I';
            if (!le && !(d[0] == 'M' && d[1] == 'M')) { res.error = "不是 TIFF 结构，无法解析"; return res; }
            var r = new TiffReader(d, le);

            // 走一遍 IFD 树，把所有目录收进来。ARW 的感光元件数据在 SubIFD 里，
            // IFD0 只放缩略图和元数据
            var dirs = new List<Dictionary<int, TiffEntry>>();
            try { r.WalkIfds(r.U32(4), dirs, 0); }
            catch (Exception e) { res.error = "解析 TIFF 目录失败：" + e.Message; return res; }

            // 找存原始拜耳数据的那个目录
            Dictionary<int, TiffEntry> cfa = null;
            foreach (var dir in dirs)
                if (dir.TryGetValue(TagPhotometric, out var pe) && r.Int(pe, 0) == PhotometricCFA) { cfa = dir; break; }

            if (cfa == null)
            {
                res.error = "文件里没有拜耳原始数据。这可能是 ARW 的某个新变体。";
                TryEmbeddedPreview(r, dirs, res);
                return res;
            }

            string model = FindString(r, dirs, TagModel) ?? "未知机身";
            int compression = cfa.TryGetValue(TagCompression, out var ce) ? r.Int(ce, 0) : -1;
            int bits = cfa.TryGetValue(TagBitsPerSample, out var be) ? r.Int(be, 0) : 0;
            int w = r.Int(cfa[TagImageWidth], 0);
            int h = r.Int(cfa[TagImageLength], 0);

            if (w <= 0 || h <= 0) { res.error = "原始图尺寸不合法"; return res; }

            long off = r.Long(cfa[TagStripOffsets], 0);
            long count = cfa.TryGetValue(TagStripByteCounts, out var sc) ? r.Long(sc, 0) : 0;

            // ---- 先把码流解成一张 16 位的平面，后面的去马赛克两条路共用 ----
            ushort[] plane;
            int black, white;

            if (compression == CompressionNone)
            {
                long need = (long)w * h * 2;
                if (count < need || off + need > d.Length)
                {
                    res.error = $"原始数据长度对不上（需要 {need}，实有 {count}）";
                    return res;
                }
                if (bits != 14 && bits != 12 && bits != 16)
                {
                    res.error = $"位深 {bits} 未支持";
                    return res;
                }

                plane = ReadUncompressed(d, (int)off, w, h);
                black = cfa.TryGetValue(TagSonyBlack, out var bl) ? r.Int(bl, 0) : 512;
                white = cfa.TryGetValue(TagWhiteLevel, out var wl) ? r.Int(wl, 0) : (1 << bits) - 1;
                if (white <= black) white = (1 << bits) - 1;
            }
            else if (compression == CompressionArw2 && count == (long)w * h)
            {
                // 平均正好 1 字节/像素，这是 ARW2 有损压缩的判据。
                // 同一个 Compression 值还被老的 ARW1 和 12bit 打包格式用着，
                // 它们的字节数对不上这个条件，会落到下面的分支
                if (!cfa.TryGetValue(TagSonyCurve, out var ce2) || ce2.count < 4)
                {
                    res.error = "ARW2 压缩数据缺少色调曲线（标签 0x7010），无法还原线性值。";
                    TryEmbeddedPreview(r, dirs, res);
                    return res;
                }
                if (off + count > d.Length)
                {
                    res.error = "压缩数据超出文件末尾";
                    return res;
                }

                var pts = new int[4];
                for (int i = 0; i < 4; i++) pts[i] = r.Int(ce2, i);

                try { plane = DecodeArw2(d, (int)off, w, h, BuildSonyCurve(pts)); }
                catch (Exception e) { res.error = "ARW2 解压失败：" + e.Message; return res; }

                // ARW2 解出来是 12 位域，和标签里那个 14 位域的黑电平不是一回事。
                // 索尼全画幅的黑电平恒为 128 << (位深-12)，12 位时就是 128
                black = 128;
                white = 4095;
                bits = 12;
            }
            else
            {
                res.error = $"这张 ARW 的压缩方式暂不支持（Compression={compression}，" +
                            $"{count} 字节 / {(long)w * h} 像素）。\n" +
                            "已知未支持：ARW1（老 A100）、12bit 打包、以及 A1 / A7 IV 之后的无损压缩。\n" +
                            "已改用机内 JPEG 预览，画质和位深都不如原始数据。";
                TryEmbeddedPreview(r, dirs, res);
                return res;
            }

            // 标签名 WB_RGGBLevels 就是顺序：R, G1, G2, B。蓝色在第四位不是第三位——
            // 这里搞错的话画面会明显偏黄，而且因为绿色是对的，第一眼还不容易断定是白平衡问题
            float gainR = 1f, gainB = 1f;
            if (cfa.TryGetValue(TagSonyWB, out var wbe) && wbe.count >= 4)
            {
                float g = r.Int(wbe, 1);
                if (g > 0.5f) { gainR = r.Int(wbe, 0) / g; gainB = r.Int(wbe, 3) / g; }
            }

            // ---- 拜耳排列 ----
            var pattern = new int[] { 0, 1, 1, 2 };   // 缺省 RGGB，索尼机身基本都是它
            if (cfa.TryGetValue(TagCFAPattern, out var cp) && cp.count >= 4)
                for (int i = 0; i < 4; i++) pattern[i] = r.Byte(cp, i);

            // ---- 有效区域。传感器边上有一圈遮光像素，不裁掉会是一条黑边 ----
            int cx = 0, cy = 0, cw = w, chh = h;
            if (cfa.TryGetValue(TagCropOrigin, out var co) && co.count >= 2 &&
                cfa.TryGetValue(TagCropSize, out var cs) && cs.count >= 2)
            {
                cx = r.Int(co, 0); cy = r.Int(co, 1);
                cw = r.Int(cs, 0); chh = r.Int(cs, 1);
                // 裁剪起点必须落在拜耳相位上，否则整幅图的红蓝会对调
                cx &= ~1; cy &= ~1;
                cw = Mathf.Clamp(cw, 2, w - cx);
                chh = Mathf.Clamp(chh, 2, h - cy);
            }

            int step = Mathf.Clamp(o.downscale, 1, 4);

            try
            {
                res.texture = Decode(plane, w, h, black, white, pattern,
                                     gainR, gainB, cx, cy, cw, chh, step, o);
            }
            catch (Exception e)
            {
                res.error = "解码失败：" + e.Message;
                return res;
            }

            res.texture.name = Path.GetFileNameWithoutExtension(path);
            string kind = compression == CompressionNone ? "未压缩" : "ARW2 有损压缩";
            res.info = $"{model}  {cw}×{chh}  {bits}bit {kind}  " +
                       $"黑电平 {black} 白电平 {white}  白平衡 R×{gainR:0.000} B×{gainB:0.000}" +
                       (step > 1 ? $"  已降采样 1/{step}" : "");
            return res;
        }

        // ---------------- 码流 -> 16 位平面 ----------------

        static ushort[] ReadUncompressed(byte[] d, int off, int w, int h)
        {
            var plane = new ushort[w * h];
            for (int i = 0; i < plane.Length; i++)
            {
                int b = off + (i << 1);
                plane[i] = (ushort)(d[b] | (d[b + 1] << 8));   // ARW 一律小端
            }
            return plane;
        }

        /// <summary>
        /// 索尼 0x7010 标签给的四个折点，展开成 4096 项的分段曲线。
        ///
        /// 五段的斜率固定是 1/2/4/8/16——暗部一个码值一个码值地走，越往亮部一个码值
        /// 跨得越大。这正是「11 位存 14 位」能成立的原因：人眼对暗部的分辨力远高于亮部。
        /// </summary>
        static ushort[] BuildSonyCurve(int[] pts)
        {
            var edge = new int[6];
            edge[0] = 0;
            for (int i = 0; i < 4; i++) edge[i + 1] = (pts[i] >> 2) & 0xFFF;
            edge[5] = 4095;

            // 折点必须单调不减，坏文件里不一定，夹一下免得下面的循环乱套
            for (int i = 1; i < 6; i++)
                edge[i] = Mathf.Clamp(edge[i], edge[i - 1], 4095);

            var curve = new ushort[4096];
            for (int i = 0; i < 5; i++)
                for (int j = edge[i] + 1; j <= edge[i + 1]; j++)
                    curve[j] = (ushort)(curve[j - 1] + (1 << i));
            return curve;
        }

        /// <summary>
        /// ARW2 有损压缩（相机菜单里的「压缩」RAW）。
        ///
        /// 一行里每 16 字节编码 16 个像素，平均正好 8 bit/像素。这 16 个像素是
        /// <b>同一奇偶列</b>上的，所以颜色相同——拜耳阵列里同一行隔一列才是同色。
        /// 于是一组 32 列要用两个 16 字节块：先偶数列，再奇数列。
        ///
        /// 16 字节的位布局（小端 32 位字，低位在前）：
        ///   bit  0..10  这 16 个像素里的最大值（11 位）
        ///   bit 11..21  最小值（11 位）
        ///   bit 22..25  最大值落在 16 个里的第几个
        ///   bit 26..29  最小值落在第几个
        ///   bit 30 起   剩下 14 个各占 7 位，存的是相对最小值的增量
        ///
        /// 增量只有 7 位而值域有 11 位，所以要按这一块的动态范围左移 sh 位。
        /// 有损就损在这里：动态范围大的块，步进跟着变粗，平滑渐变上会出现色阶断层。
        /// </summary>
        static ushort[] DecodeArw2(byte[] d, int off, int w, int h, ushort[] curve)
        {
            var plane = new ushort[w * h];
            var pix = new int[16];

            for (int row = 0; row < h; row++)
            {
                int rowOff = off + row * w;      // 每行正好 w 字节
                int rowBase = row * w;
                int dp = 0;
                int col = 0;

                while (col < w - 30)
                {
                    int b0 = rowOff + dp;
                    int head = d[b0] | (d[b0 + 1] << 8) | (d[b0 + 2] << 16) | (d[b0 + 3] << 24);

                    int max = head & 0x7FF;
                    int min = (head >> 11) & 0x7FF;
                    int imax = (head >> 22) & 0x0F;
                    int imin = (head >> 26) & 0x0F;

                    // 让 (0x7f << sh) 刚好能盖住 max-min 这个跨度
                    int sh = 0;
                    while (sh < 4 && (0x80 << sh) <= max - min) sh++;

                    int bit = 30;
                    for (int i = 0; i < 16; i++)
                    {
                        if (i == imax) { pix[i] = max; continue; }
                        if (i == imin) { pix[i] = min; continue; }

                        // 7 位增量会跨字节边界，所以读 16 位再移位取。
                        // 正常码流里最后一个增量起始于 bit 121，落在本块第 15 字节内。
                        //
                        // 但如果 imax == imin，只有一个位置是特殊值，于是要读 15 个增量
                        // 而不是 14 个，bit 走到 128，下标就越过整块了。这种码流是非法的
                        // （编码器不会让两个位置重合），可文件损坏时就会出现，
                        // 所以两个字节都得判界——否则最后一块直接抛越界异常。
                        int bp = b0 + (bit >> 3);
                        int lo = bp < d.Length ? d[bp] : 0;
                        int hi = bp + 1 < d.Length ? d[bp + 1] : 0;
                        int word = lo | (hi << 8);
                        int v = (((word >> (bit & 7)) & 0x7F) << sh) + min;
                        pix[i] = v > 0x7FF ? 0x7FF : v;
                        bit += 7;
                    }

                    // 曲线的定义域是 12 位，而解出来的是 11 位，所以左移一位再查；
                    // 查出来是 14 位域的值，右移两位回到 12 位域，和 black=128 / white=4095 对齐
                    for (int i = 0; i < 16; i++, col += 2)
                        plane[rowBase + col] = (ushort)(curve[pix[i] << 1] >> 2);

                    // 偶数列那一趟走完（col 停在偶数）就退回去从奇数列重来；
                    // 奇数列走完（col 是奇数）则前进到下一组 32 列的偶数起点
                    col -= (col & 1) != 0 ? 1 : 31;
                    dp += 16;
                }
            }

            return plane;
        }

        // ---------------- 解码本体 ----------------

        /// <summary>
        /// 相机 RGB -> 线性 sRGB。
        ///
        /// 由 dcraw / Adobe 公布的 ILCE-7RM4 系数按 dcraw 的标准流程算出来：
        /// cam_rgb = cam_xyz · xyz_rgb，每行归一化到和为 1（这一步是白能映射成白的关键），
        /// 再求逆。已经和该机身的机内 JPEG 对照验证过。
        ///
        /// 别的索尼机身共用这一组，会有偏差但方向是对的。要准确的颜色，
        /// 用修图台的「色卡校色」拍一张 24 色卡解一个属于你这台机器的矩阵。
        /// </summary>
        static readonly float[] CamToSrgb =
        {
             1.53563f, -0.35802f, -0.17761f,
            -0.13324f,  1.62857f, -0.49533f,
             0.03622f, -0.50422f,  1.46800f,
        };

        static Texture2D Decode(ushort[] plane, int w, int h, int black, int white, int[] pattern,
                                float gainR, float gainB, int cx, int cy, int cw, int ch, int step,
                                Options o)
        {
            float range = 1f / Mathf.Max(1, white - black);

            int outW = cw / step;
            int outH = ch / step;

            // ---- 曝光归一化的标量。抽样估一个高分位数就够，全排 6000 万个数没必要 ----
            float scale = 1f;
            if (o.autoExposure)
                scale = EstimateExposure(plane, w, h, cx, cy, cw, ch, black, range, pattern, gainR, gainB);

            var px = new Color32[outW * outH];

            for (int oy = 0; oy < outH; oy++)
            {
                int ry = cy + oy * step;
                // Unity 的贴图是从下往上排的，而 TIFF 的第 0 行是画面顶部
                int rowBase = (outH - 1 - oy) * outW;

                for (int ox = 0; ox < outW; ox++)
                {
                    int rx = cx + ox * step;

                    float cr, cg, cb;
                    if (step == 1) Demosaic(plane, w, h, rx, ry, pattern, black, range, out cr, out cg, out cb);
                    else           QuadAverage(plane, w, h, rx, ry, step, pattern, black, range, out cr, out cg, out cb);

                    cr *= gainR;
                    cb *= gainB;

                    float lr, lg, lb;
                    if (o.applyColorMatrix)
                    {
                        lr = CamToSrgb[0] * cr + CamToSrgb[1] * cg + CamToSrgb[2] * cb;
                        lg = CamToSrgb[3] * cr + CamToSrgb[4] * cg + CamToSrgb[5] * cb;
                        lb = CamToSrgb[6] * cr + CamToSrgb[7] * cg + CamToSrgb[8] * cb;
                    }
                    else { lr = cr; lg = cg; lb = cb; }

                    lr = Mathf.Max(lr * scale, 0f);
                    lg = Mathf.Max(lg * scale, 0f);
                    lb = Mathf.Max(lb * scale, 0f);

                    // 管线里所有中间 RT 都是 ARGB32，源图给到 8bit sRGB 就够——
                    // 给更高位深也会在第一次 Blit 时被量化掉
                    px[rowBase + ox] = new Color32(Encode(lr), Encode(lg), Encode(lb), 255);
                }
            }

            var tex = new Texture2D(outW, outH, TextureFormat.RGBA32, false, false)
            { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        static byte Encode(float linear)
        {
            float v = linear <= 0.0031308f ? linear * 12.92f
                                           : 1.055f * Mathf.Pow(Mathf.Min(linear, 1f), 1f / 2.4f) - 0.055f;
            return (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
        }

        /// <summary>
        /// 抽样估计高光位置，返回一个把它推到接近满值的标量。只是标量，不是曲线。
        ///
        /// 必须在白平衡之后统计：拜耳原始值里绿点占了一半而且没有增益，
        /// 拿它当基准会把整幅图判暗，结果是自动曝光之后红蓝双双溢出。
        /// </summary>
        static float EstimateExposure(ushort[] plane, int w, int h,
                                      int cx, int cy, int cw, int ch, int black, float range,
                                      int[] pattern, float gainR, float gainB)
        {
            var samples = new List<float>(40000);
            int sy = Mathf.Max(2, ch / 200) & ~1;
            int sx = Mathf.Max(2, cw / 200) & ~1;

            for (int y = cy; y < cy + ch; y += sy)
            for (int x = cx; x < cx + cw; x += sx)
            {
                float v = Mathf.Max(0f, (Sample(plane, w, h, x, y) - black) * range);
                int c = ColorAt(pattern, x, y);
                if (c == 0) v *= gainR;
                else if (c == 2) v *= gainB;
                samples.Add(v);
            }

            if (samples.Count < 16) return 1f;
            samples.Sort();

            // 99.5% 而不是最大值：几个热像素或一处镜面反射就能把整张图压暗一大截
            float hi = samples[Mathf.Clamp(Mathf.RoundToInt(samples.Count * 0.995f), 0, samples.Count - 1)];
            // 目标 0.75 而不是 1.0：留一档余量给后面的调色，
            // 一进来就把高光顶到满值，曝光滑条往上就没有可用行程了
            return hi > 1e-4f ? Mathf.Clamp(0.75f / hi, 0.05f, 64f) : 1f;
        }

        static int Sample(ushort[] plane, int w, int h, int x, int y)
        {
            x = x < 0 ? 0 : (x >= w ? w - 1 : x);
            y = y < 0 ? 0 : (y >= h ? h - 1 : y);
            return plane[y * w + x];
        }

        /// <summary>某个位置在拜耳阵列上是什么颜色。0=红 1=绿 2=蓝</summary>
        static int ColorAt(int[] pattern, int x, int y) => pattern[((y & 1) << 1) | (x & 1)];

        /// <summary>
        /// 双线性去马赛克。绿色取上下左右四个，红蓝按当前点的颜色分情况取。
        /// 比 AHD 之类差一档，但在细密纹理之外肉眼分不出，而且不需要额外的整幅缓冲。
        /// </summary>
        static void Demosaic(ushort[] plane, int w, int h, int x, int y, int[] pattern,
                             int black, float range, out float r, out float g, out float b)
        {
            float V(int px, int py) => Mathf.Max(0f, (Sample(plane, w, h, px, py) - black) * range);

            float self = V(x, y);
            int c = ColorAt(pattern, x, y);

            if (c == 1)
            {
                // 绿点：红和蓝各自只在一个方向上有邻居，取决于同一行的另一个颜色是什么
                g = self;
                float hor = (V(x - 1, y) + V(x + 1, y)) * 0.5f;
                float ver = (V(x, y - 1) + V(x, y + 1)) * 0.5f;
                bool redIsHorizontal = ColorAt(pattern, x - 1, y) == 0;
                r = redIsHorizontal ? hor : ver;
                b = redIsHorizontal ? ver : hor;
            }
            else
            {
                float cross = (V(x - 1, y) + V(x + 1, y) + V(x, y - 1) + V(x, y + 1)) * 0.25f;
                float diag = (V(x - 1, y - 1) + V(x + 1, y - 1) + V(x - 1, y + 1) + V(x + 1, y + 1)) * 0.25f;
                g = cross;
                if (c == 0) { r = self; b = diag; }
                else        { b = self; r = diag; }
            }
        }

        /// <summary>
        /// 降采样时不插值：一个 2x2 拜耳块本来就同时有红绿蓝，直接合成一个像素。
        /// 这条路没有任何猜测成分，所以半尺寸的输出比全尺寸插值出来的还干净。
        /// </summary>
        static void QuadAverage(ushort[] plane, int w, int h, int x, int y, int step, int[] pattern,
                                int black, float range, out float r, out float g, out float b)
        {
            float sr = 0f, sg = 0f, sb = 0f;
            int nr = 0, ng = 0, nb = 0;

            for (int dy = 0; dy < step; dy++)
            for (int dx = 0; dx < step; dx++)
            {
                float v = Mathf.Max(0f, (Sample(plane, w, h, x + dx, y + dy) - black) * range);
                switch (ColorAt(pattern, x + dx, y + dy))
                {
                    case 0: sr += v; nr++; break;
                    case 1: sg += v; ng++; break;
                    default: sb += v; nb++; break;
                }
            }

            r = nr > 0 ? sr / nr : 0f;
            g = ng > 0 ? sg / ng : 0f;
            b = nb > 0 ? sb / nb : 0f;
        }

        // ---------------- 机内 JPEG 预览（解不了原始数据时的退路）----------------

        static void TryEmbeddedPreview(TiffReader r, List<Dictionary<int, TiffEntry>> dirs, Result res)
        {
            foreach (var dir in dirs)
            {
                if (!dir.TryGetValue(TagJpegOffset, out var jo) || !dir.TryGetValue(TagJpegLength, out var jl))
                    continue;

                long off = r.Long(jo, 0), len = r.Long(jl, 0);
                if (len <= 0 || off + len > r.Data.Length) continue;

                var bytes = new byte[len];
                Array.Copy(r.Data, off, bytes, 0, len);

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
                { hideFlags = HideFlags.HideAndDontSave };
                if (tex.LoadImage(bytes, false)) { res.texture = tex; return; }
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        static string FindString(TiffReader r, List<Dictionary<int, TiffEntry>> dirs, int tag)
        {
            foreach (var dir in dirs)
                if (dir.TryGetValue(tag, out var e)) return r.Str(e);
            return null;
        }

        // ---------------- 最小 TIFF 读取器 ----------------

        struct TiffEntry
        {
            public int type;      // TIFF 类型码
            public int count;
            public long valueOffset;   // 数据在文件里的绝对位置（<=4 字节时就是内联的位置）
        }

        class TiffReader
        {
            public readonly byte[] Data;
            readonly bool _le;

            public TiffReader(byte[] d, bool littleEndian) { Data = d; _le = littleEndian; }

            public int U16(long i) => _le ? Data[i] | (Data[i + 1] << 8)
                                          : (Data[i] << 8) | Data[i + 1];

            public long U32(long i) => _le
                ? (uint)(Data[i] | (Data[i + 1] << 8) | (Data[i + 2] << 16) | (Data[i + 3] << 24))
                : (uint)((Data[i] << 24) | (Data[i + 1] << 16) | (Data[i + 2] << 8) | Data[i + 3]);

            static int TypeSize(int t)
            {
                switch (t)
                {
                    case 1: case 2: case 6: case 7: return 1;
                    case 3: case 8: return 2;
                    case 4: case 9: case 11: return 4;
                    case 5: case 10: case 12: return 8;
                    default: return 1;
                }
            }

            /// <summary>递归收集 IFD0、它的 SubIFD 以及链上的下一个 IFD。深度限一下，坏文件里链可能成环。</summary>
            public void WalkIfds(long offset, List<Dictionary<int, TiffEntry>> into, int depth)
            {
                if (depth > 4 || offset <= 0 || offset + 2 > Data.Length) return;

                int n = U16(offset);
                if (n <= 0 || n > 512) return;
                if (offset + 2 + n * 12L + 4 > Data.Length) return;

                var dir = new Dictionary<int, TiffEntry>(n);
                var subs = new List<long>();

                for (int i = 0; i < n; i++)
                {
                    long e = offset + 2 + i * 12L;
                    int tag = U16(e);
                    var entry = new TiffEntry { type = U16(e + 2), count = (int)U32(e + 4) };

                    long size = (long)TypeSize(entry.type) * entry.count;
                    entry.valueOffset = size <= 4 ? e + 8 : U32(e + 8);
                    if (entry.valueOffset < 0 || entry.valueOffset + size > Data.Length) continue;

                    dir[tag] = entry;
                    if (tag == TagSubIFDs)
                        for (int k = 0; k < entry.count; k++) subs.Add(U32(entry.valueOffset + k * 4L));
                }

                into.Add(dir);
                foreach (var sub in subs) WalkIfds(sub, into, depth + 1);
                WalkIfds(U32(offset + 2 + n * 12L), into, depth + 1);
            }

            public int Byte(TiffEntry e, int i) => Data[e.valueOffset + i];

            public int Int(TiffEntry e, int i)
            {
                long p = e.valueOffset + (long)TypeSize(e.type) * i;
                switch (e.type)
                {
                    case 1: case 2: case 6: case 7: return Data[p];
                    case 3: return U16(p);
                    case 8: return (short)U16(p);          // SSHORT 是有符号的
                    default: return (int)U32(p);
                }
            }

            public long Long(TiffEntry e, int i) => Int(e, i) & 0xFFFFFFFFL;

            public string Str(TiffEntry e)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < e.count; i++)
                {
                    byte c = Data[e.valueOffset + i];
                    if (c == 0) break;
                    sb.Append((char)c);
                }
                return sb.ToString().Trim();
            }
        }
    }
}
