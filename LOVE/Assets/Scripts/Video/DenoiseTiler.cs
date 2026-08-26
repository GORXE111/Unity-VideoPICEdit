using System.Collections.Generic;
using UnityEngine;

namespace Love.Video
{
    /// <summary>
    /// 把一张大图切成模型吃得下的小块。
    ///
    /// 6100 万像素没有任何降噪模型能一口吞下，只能切块。而切块的头号失败是**接缝**：
    /// 卷积在块边缘缺上下文，直接拼回去会看见一格一格的网。
    ///
    /// 办法是「读的时候带边，写的时候不带」：
    ///   读进模型的那块四周多取 overlap 圈像素，只是给卷积当上下文
    ///   写回去的只有中间那块，块与块之间严丝合缝、不重叠
    ///
    /// 读窗**恒为 tile + 2×overlap**，哪怕贴着画面边缘也不缩水——很多 ONNX 导出
    /// 是固定输入尺寸的，尺寸一变直接推理失败。越界的部分由调用方镜像补边。
    /// </summary>
    public static class DenoiseTiler
    {
        public struct Tile
        {
            /// <summary>从源图读哪块。可能越界，越界部分要镜像补边。</summary>
            public RectInt read;

            /// <summary>写回哪块。所有块的这个矩形拼起来正好铺满整幅，互不重叠。</summary>
            public RectInt write;

            /// <summary>write 在 read 里的偏移。模型输出上要从这儿开始裁。</summary>
            public int offsetX, offsetY;
        }

        /// <summary>
        /// 排块。
        /// </summary>
        /// <param name="tile">写回区域的边长。读进模型的是 tile + 2×overlap。</param>
        /// <param name="overlap">四周多取几圈当上下文。</param>
        public static List<Tile> Plan(int imgW, int imgH, int tile, int overlap)
        {
            var list = new List<Tile>();
            if (imgW <= 0 || imgH <= 0) return list;

            tile = Mathf.Max(1, tile);
            overlap = Mathf.Max(0, overlap);

            int cols = Mathf.CeilToInt(imgW / (float)tile);
            int rows = Mathf.CeilToInt(imgH / (float)tile);
            int readSize = tile + overlap * 2;

            for (int row = 0; row < rows; row++)
            {
                int wy = row * tile;
                int wh = Mathf.Min(tile, imgH - wy);

                for (int col = 0; col < cols; col++)
                {
                    int wx = col * tile;
                    int ww = Mathf.Min(tile, imgW - wx);

                    list.Add(new Tile
                    {
                        read = new RectInt(wx - overlap, wy - overlap, readSize, readSize),
                        write = new RectInt(wx, wy, ww, wh),
                        offsetX = overlap,
                        offsetY = overlap,
                    });
                }
            }

            return list;
        }

        /// <summary>
        /// 镜像补边的取样下标。
        ///
        /// 补边不能用「夹到边界」——那等于把边缘那一列复制一大片，
        /// 模型会把这片假的平坦区当成真信号，边上一圈反而更糊。
        /// 镜像至少保留了纹理的统计特征。
        /// </summary>
        public static int Mirror(int i, int n)
        {
            if (n <= 1) return 0;

            // 周期 2(n-1) 的三角波：0,1,…,n-1,n-2,…,1,0,1,…
            int period = 2 * (n - 1);
            i = ((i % period) + period) % period;
            return i < n ? i : period - i;
        }
    }
}
