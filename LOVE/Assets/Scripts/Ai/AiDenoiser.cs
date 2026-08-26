#if LOVE_SENTIS
using System;
using System.Collections.Generic;
using Love.Video;
using Unity.Sentis;
using UnityEngine;

namespace Love.Tools
{
    /// <summary>
    /// AI 降噪。
    ///
    /// 和 <see cref="AiMaskGenerator"/> 最大的不同是**不能缩图**。
    /// 蒙版缩到 1024 再放大回去没关系，蒙版本来就是低频的；
    /// 降噪缩一遍等于先把细节丢光，再拿模型去"救"一张已经没救的图。
    /// 所以必须原分辨率过——6100 万像素只能切块。
    ///
    /// 切块用 <see cref="DenoiseTiler"/>：读的时候四周多带一圈给卷积当上下文，
    /// 写回去的只有中间那块。不带上下文直接拼，块边缘会看见一格一格的网。
    ///
    /// **分步跑。** 6100 万像素切成两百多块，一口气跑完编辑器会假死好几十秒。
    /// 调用方每帧调一次 <see cref="Step"/>，中间能画进度条、能取消。
    /// </summary>
    public class AiDenoiser : IDisposable
    {
        public class Spec
        {
            public string label;
            public string path;

            /// <summary>
            /// 模型固定的输入边长。
            ///
            /// 这份权重是按固定尺寸重新折叠过的（见 Models/模型授权.md），
            /// 尺寸对不上不是"慢一点"，是直接报形状不匹配。所以块大小由它反推，
            /// 不由调用方决定。
            /// </summary>
            public int fixedInput = 576;

            /// <summary>四周留几圈给卷积当上下文。块大小 = fixedInput - 2×overlap。</summary>
            public int overlap = 32;

            /// <summary>授权。和项目里其它模型一样，逐个核实过才敢进仓库。</summary>
            public string license;
        }

        /// <summary>
        /// 可用模型。
        ///
        /// 都是可商用的授权——这条线和当初拒掉 GPL-3.0 的 RobustVideoMatting 一致。
        /// </summary>
        public static readonly Spec[] Presets =
        {
            new Spec { label = "SCUNet 盲降噪 (106MB, 576px 块)",
                       path = "Assets/GameAssets/Models/SCUNet-real.onnx",
                       fixedInput = 576, overlap = 32,
                       license = "Apache-2.0 (cszn/SCUNet)" },
        };


        /// <summary>
        /// 按路径拿模型资产。
        ///
        /// **ONNX 只能在编辑器里导入**——`Unity.Sentis.ONNX` 是编辑器程序集，
        /// 出包之后根本没有它。所以运行时只能用已经导入好的 ModelAsset，
        /// 由场景上的序列化引用带进包里。
        ///
        /// 编辑器走 AssetDatabase，独立程序走 ToolApp 上那张表。
        /// 和 AppHost 一样是注入，不用 `#if UNITY_EDITOR` ——
        /// 条件编译等于把边界交给宏去守。
        /// </summary>
        public static Func<string, ModelAsset> ResolveModel;

        static ModelAsset Resolve(string path, out string error)
        {
            error = null;
            if (ResolveModel == null)
            {
                error = "没人提供模型来源（ResolveModel 没设）";
                return null;
            }
            var a = ResolveModel(path);
            if (a == null) error = "找不到模型：" + path;
            return a;
        }

        // ---- 会话状态 ----
        Model _model;
        Worker _worker;
        string _loadedPath;
        BackendType _loadedBackend;

        List<DenoiseTiler.Tile> _tiles;
        int _next;
        int _readSize;
        RenderTexture _srcRT;
        RenderTexture _dstRT;
        Texture2D _readback;
        Texture2D _tileOut;
        Color32[] _readPx;
        float[] _inData;

        public bool Running => _tiles != null && _next < _tiles.Count;
        public float Progress => _tiles == null || _tiles.Count == 0 ? 0f : _next / (float)_tiles.Count;
        public int TileCount => _tiles?.Count ?? 0;
        public int TileDone => _next;
        public double LastMs { get; private set; }

        /// <summary>跑完之后的结果。调用方不要销毁，下一次 Begin 会接管。</summary>
        public RenderTexture Result => _dstRT;

        public void Dispose()
        {
            Cancel();
            _worker?.Dispose();
            _worker = null;
            _model = null;
            _loadedPath = null;
            ReleaseResult();
        }

        public void ReleaseResult()
        {
            if (_dstRT != null) { _dstRT.Release(); UnityEngine.Object.DestroyImmediate(_dstRT); _dstRT = null; }
        }

        /// <summary>
        /// 开一轮。成功返回 true，之后每帧调 <see cref="Step"/> 直到 <see cref="Running"/> 变假。
        /// </summary>
        public bool Begin(Texture source, Spec spec, BackendType backend, out string error)
        {
            error = null;
            Cancel();

            if (source == null) { error = "没有源图片"; return false; }
            if (spec == null) { error = "没有选模型"; return false; }

            var asset = Resolve(spec.path, out error);
            if (asset == null) return false;

            // 块大小由模型的固定输入反推，不由调用方决定
            int readSize = Mathf.Max(16, spec.fixedInput);
            int overlap = Mathf.Clamp(spec.overlap, 0, readSize / 2 - 8);
            int tile = readSize - overlap * 2;

            try
            {
                if (_model == null || _loadedPath != spec.path || _loadedBackend != backend)
                {
                    _worker?.Dispose();
                    _model = ModelLoader.Load(asset);
                    _worker = new Worker(_model, backend);
                    _loadedPath = spec.path;
                    _loadedBackend = backend;
                }
            }
            catch (Exception e)
            {
                error = "模型载入失败：" + e.Message;
                Debug.LogException(e);
                return false;
            }

            int w = source.width, h = source.height;
            _tiles = DenoiseTiler.Plan(w, h, tile, overlap);
            _next = 0;
            _readSize = readSize;

            _srcRT = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                                                RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, _srcRT);

            ReleaseResult();
            _dstRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { hideFlags = HideFlags.HideAndDontSave };
            _dstRT.Create();
            Graphics.Blit(source, _dstRT);   // 先铺一遍原图，取消到一半也不会露出空白

            _readback = new Texture2D(readSize, readSize, TextureFormat.RGBA32, false, true)
            { hideFlags = HideFlags.HideAndDontSave };
            _tileOut = new Texture2D(tile, tile, TextureFormat.RGBA32, false, true)
            { hideFlags = HideFlags.HideAndDontSave };
            _readPx = new Color32[readSize * readSize];
            _inData = new float[3 * readSize * readSize];

            return true;
        }

        /// <summary>跑一块。返回是否还有下一块。</summary>
        public bool Step()
        {
            if (!Running) { Finish(); return false; }

            var t = _tiles[_next];
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                ReadTile(t);
                RunTile(t);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Cancel();
                return false;
            }

            sw.Stop();
            LastMs = sw.Elapsed.TotalMilliseconds;

            _next++;
            if (!Running) Finish();
            return Running;
        }

        public void Cancel()
        {
            _tiles = null;
            _next = 0;
            if (_srcRT != null) { RenderTexture.ReleaseTemporary(_srcRT); _srcRT = null; }
            if (_readback != null) { UnityEngine.Object.DestroyImmediate(_readback); _readback = null; }
            if (_tileOut != null) { UnityEngine.Object.DestroyImmediate(_tileOut); _tileOut = null; }
            _readPx = null;
            _inData = null;
        }

        void Finish()
        {
            if (_srcRT != null) { RenderTexture.ReleaseTemporary(_srcRT); _srcRT = null; }
            if (_readback != null) { UnityEngine.Object.DestroyImmediate(_readback); _readback = null; }
            if (_tileOut != null) { UnityEngine.Object.DestroyImmediate(_tileOut); _tileOut = null; }
            _readPx = null;
            _inData = null;
            _tiles = null;
        }

        /// <summary>
        /// 把一块读进 <see cref="_readPx"/>，越界的部分镜像补上。
        /// </summary>
        void ReadTile(DenoiseTiler.Tile t)
        {
            int W = _srcRT.width, H = _srcRT.height;
            int n = _readSize;

            // read 的坐标是"原点左上"，RT 和 ReadPixels 是"原点左下"，纵向要倒过来
            int x0 = t.read.x;
            int yTop = t.read.y;

            int cx0 = Mathf.Clamp(x0, 0, W);
            int cx1 = Mathf.Clamp(x0 + n, 0, W);
            int cyTop0 = Mathf.Clamp(yTop, 0, H);
            int cyTop1 = Mathf.Clamp(yTop + n, 0, H);

            int cw = cx1 - cx0, ch = cyTop1 - cyTop0;
            if (cw <= 0 || ch <= 0) { Array.Clear(_readPx, 0, _readPx.Length); return; }

            var prev = RenderTexture.active;
            RenderTexture.active = _srcRT;
            // RT 里的 y：画面顶部 yTop 对应 RT 行 H - yTop - 1，整段的起点是 H - cyTop1
            _readback.ReadPixels(new Rect(cx0, H - cyTop1, cw, ch), cx0 - x0, (yTop + n) - cyTop1, false);
            _readback.Apply(false, false);
            RenderTexture.active = prev;

            var px = _readback.GetPixels32();

            // 有效区在 _readback 里的范围（同样是"原点左下"的行序）
            int vx0 = cx0 - x0, vx1 = vx0 + cw;
            int vy0 = (yTop + n) - cyTop1, vy1 = vy0 + ch;

            for (int y = 0; y < n; y++)
            {
                int sy = y;
                if (sy < vy0 || sy >= vy1)
                    sy = vy0 + DenoiseTiler.Mirror(y - vy0, ch);

                for (int x = 0; x < n; x++)
                {
                    int sx = x;
                    if (sx < vx0 || sx >= vx1)
                        sx = vx0 + DenoiseTiler.Mirror(x - vx0, cw);

                    _readPx[y * n + x] = px[sy * n + sx];
                }
            }
        }

        void RunTile(DenoiseTiler.Tile t)
        {
            int n = _readSize;
            int plane = n * n;

            // 模型要"原点左上"，_readPx 是"原点左下"，这里翻过来。
            // 降噪其实不在乎朝向（全卷积、没有"天空在上"这种先验），
            // 但翻一下不要钱，省得以后有人对着一张倒的输入怀疑人生
            for (int y = 0; y < n; y++)
            {
                int src = (n - 1 - y) * n;
                int dst = y * n;
                for (int x = 0; x < n; x++)
                {
                    var c = _readPx[src + x];
                    _inData[dst + x] = c.r / 255f;
                    _inData[plane + dst + x] = c.g / 255f;
                    _inData[plane * 2 + dst + x] = c.b / 255f;
                }
            }

            using (var input = new Tensor<float>(new TensorShape(1, 3, n, n), _inData))
            {
                _worker.Schedule(input);
                using (var output = _worker.PeekOutput().ReadbackAndClone() as Tensor<float>)
                {
                    if (output == null) throw new Exception("输出不是 float 张量");
                    WriteTile(t, output, n, plane);
                }
            }
        }

        void WriteTile(DenoiseTiler.Tile t, Tensor<float> output, int n, int plane)
        {
            int tw = t.write.width, th = t.write.height;
            var outPx = new Color32[_tileOut.width * _tileOut.height];

            for (int y = 0; y < th; y++)
            {
                // 输出是"原点左上"，_tileOut 要"原点左下"，再翻回来
                int oy = t.offsetY + y;
                int dy = th - 1 - y;

                for (int x = 0; x < tw; x++)
                {
                    int i = oy * n + (t.offsetX + x);
                    outPx[dy * _tileOut.width + x] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt(output[i] * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(output[plane + i] * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(output[plane * 2 + i] * 255f), 0, 255),
                        255);
                }
            }

            _tileOut.SetPixels32(outPx);
            _tileOut.Apply(false, false);

            // 目标里的位置同样要把 y 倒过来
            int dstY = _dstRT.height - t.write.y - th;
            Graphics.CopyTexture(_tileOut, 0, 0, 0, _tileOut.height - th, tw, th,
                                 _dstRT, 0, 0, t.write.x, dstY);
        }
    }
}
#endif
