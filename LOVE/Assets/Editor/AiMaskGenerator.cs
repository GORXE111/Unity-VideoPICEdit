#if LOVE_SENTIS
using System;
using Unity.Sentis;
using UnityEditor;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// AI 蒙版生成器。把 Sentis 推理抽出来，验证窗口和修图台共用一份。
    ///
    /// 支持两类模型：
    ///   分割（IS-Net / U^2-Net）—— 输出 0~1 的主体概率，直接就是蒙版
    ///   深度（MiDaS）—— 输出相对逆深度，没有绝对尺度，要按画面 min/max 拉伸
    ///
    /// 所有 Blit 和 ReadPixels 都不能在 OnGUI 里调用，调用方负责放到 Update。
    /// </summary>
    public class AiMaskGenerator : IDisposable
    {
        public enum Norm
        {
            ImageNet,   // (x/255 - mean) / std，MiDaS 和 U^2-Net
            HalfHalf,   // (x/255 - 0.5) / 1.0，IS-Net
        }

        public class Spec
        {
            public string label;
            public string path;
            public Norm norm;
            public bool isDepth;
        }

        /// <summary>可用模型。授权都核实过：IS-Net / U^2-Net / MiDaS 均可商用。</summary>
        public static readonly Spec[] Presets =
        {
            new Spec { label = "IS-Net 分割 (170MB, 1024px) — 细节最好",
                       path = "Assets/GameAssets/Models/isnet-general-use.onnx",
                       norm = Norm.HalfHalf, isDepth = false },
            new Spec { label = "U^2-Net 人像分割 (168MB, 320px)",
                       path = "Assets/GameAssets/Models/u2net_human_seg.onnx",
                       norm = Norm.ImageNet, isDepth = false },
            new Spec { label = "MiDaS 深度 large (397MB, 384px)",
                       path = "Assets/GameAssets/Models/MiDaS-large.onnx",
                       norm = Norm.ImageNet, isDepth = true },
            new Spec { label = "MiDaS 深度 small (64MB, 256px)",
                       path = "Assets/GameAssets/Models/MiDaS-small.onnx",
                       norm = Norm.ImageNet, isDepth = true },
        };

        static readonly Vector3 ImageNetMean = new Vector3(0.485f, 0.456f, 0.406f);
        static readonly Vector3 ImageNetStd  = new Vector3(0.229f, 0.224f, 0.225f);

        Model _model;
        Worker _worker;
        string _loadedPath;
        BackendType _loadedBackend;

        public int InputSize { get; private set; } = 256;
        public double LastMs { get; private set; }

        public void Dispose()
        {
            _worker?.Dispose();
            _worker = null;
            _model = null;
            _loadedPath = null;
        }

        /// <summary>
        /// 跑一次推理，返回灰度蒙版贴图（调用方负责销毁）。失败返回 null 并填 error。
        /// </summary>
        public Texture2D Generate(Texture2D source, Spec spec, BackendType backend,
                                  bool invertDepth, out string error)
        {
            error = null;
            if (source == null) { error = "没有源图片"; return null; }
            if (spec == null) { error = "没有选模型"; return null; }

            var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(spec.path);
            if (asset == null) { error = "找不到模型：" + spec.path; return null; }

            try
            {
                if (_model == null || _loadedPath != spec.path || _loadedBackend != backend)
                {
                    _worker?.Dispose();
                    _model = ModelLoader.Load(asset);
                    InputSize = ReadInputSize(_model);
                    _worker = new Worker(_model, backend);
                    _loadedPath = spec.path;
                    _loadedBackend = backend;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();

                using (var input = BuildInput(source, InputSize, spec.norm))
                {
                    _worker.Schedule(input);
                    using (var output = _worker.PeekOutput().ReadbackAndClone() as Tensor<float>)
                    {
                        sw.Stop();
                        LastMs = sw.Elapsed.TotalMilliseconds;

                        if (output == null) { error = "输出不是 float 张量"; return null; }
                        return BuildMaskTexture(output, spec.isDepth, invertDepth, out error);
                    }
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                Debug.LogException(e);
                return null;
            }
        }

        /// <summary>
        /// 从模型自身读输入分辨率。各模型差别很大（256 / 320 / 384 / 1024），
        /// 写死会报出很难懂的形状不匹配错误。
        /// </summary>
        public static int ReadInputSize(Model model)
        {
            if (model == null || model.inputs.Count == 0) return 256;
            var shape = model.inputs[0].shape;
            if (shape.isRankDynamic || shape.rank < 4 || !shape.IsStatic()) return 256;
            int h = shape.ToTensorShape()[2];    // NCHW
            return h > 0 ? h : 256;
        }

        /// <summary>
        /// 手工拼输入张量。归一化后的值有负数，8 位 RT 存不下；
        /// 而且这些模型都在 sRGB 图上训练，不能喂线性值。
        /// </summary>
        static Tensor<float> BuildInput(Texture2D src, int size, Norm norm)
        {
            var rt = RenderTexture.GetTemporary(size, size, 0,
                                                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var readback = new Texture2D(size, size, TextureFormat.RGBA32, false, true);

            try
            {
                Graphics.Blit(src, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0f, 0f, size, size), 0, 0, false);
                readback.Apply(false, false);
                RenderTexture.active = prev;

                Vector3 mean = norm == Norm.ImageNet ? ImageNetMean : new Vector3(0.5f, 0.5f, 0.5f);
                Vector3 std  = norm == Norm.ImageNet ? ImageNetStd  : Vector3.one;

                var px = readback.GetPixels32();
                var data = new float[3 * size * size];
                int plane = size * size;

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        // ReadPixels 原点在左下，模型要左上，纵向翻一下
                        var c = px[(size - 1 - y) * size + x];
                        int i = y * size + x;
                        data[i]             = (c.r / 255f - mean.x) / std.x;
                        data[plane + i]     = (c.g / 255f - mean.y) / std.y;
                        data[plane * 2 + i] = (c.b / 255f - mean.z) / std.z;
                    }
                }

                return new Tensor<float>(new TensorShape(1, 3, size, size), data);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(readback);
            }
        }

        Texture2D BuildMaskTexture(Tensor<float> output, bool isDepth, bool invertDepth, out string error)
        {
            error = null;
            var d = output.DownloadToArray();
            int n = d.Length;

            // 分割模型一次返回多个侧输出，第一张才是主结果
            int side = Mathf.RoundToInt(Mathf.Sqrt(n));
            if (side * side != n)
            {
                side = InputSize;
                if (side * side > n) { error = $"输出长度 {n} 和输入尺寸 {InputSize} 对不上"; return null; }
            }
            int count = side * side;

            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                if (d[i] < min) min = d[i];
                if (d[i] > max) max = d[i];
            }
            float range = Mathf.Max(1e-6f, max - min);

            var tex = new Texture2D(side, side, TextureFormat.RGBA32, false, true)
            {
                name = "AIMask",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var px = new Color32[count];
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    float raw = d[y * side + x];

                    // 深度是相对值必须拉伸；分割本来就是 0~1 的概率，
                    // 再按 min/max 拉伸只会把背景噪声放大成灰雾
                    float v = isDepth ? (raw - min) / range : Mathf.Clamp01(raw);
                    if (isDepth && !invertDepth) v = 1f - v;

                    byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
                    px[(side - 1 - y) * side + x] = new Color32(b, b, b, 255);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }
    }
}
#endif
