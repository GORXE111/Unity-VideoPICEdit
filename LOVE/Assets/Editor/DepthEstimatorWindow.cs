#if LOVE_SENTIS
using System.Diagnostics;
using System.IO;
using Unity.Sentis;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Love.EditorTools
{
    /// <summary>
    /// 深度估计最小验证。
    ///
    /// 目的只有三个：能不能跑通、跑多快、出来的深度图能不能用。
    /// 结论满意再谈接进修图台做景深，不满意就把 Sentis 包和模型删掉，别的代码不受影响。
    ///
    /// 用 MiDaS v2.1 small（Intel ISL，MIT 授权，可商用可随包分发）。
    /// 整个文件包在 LOVE_SENTIS 条件编译里，没装包时不会拖累工程编译。
    /// </summary>
    public class DepthEstimatorWindow : EditorWindow
    {
        [MenuItem("Tools/影视游戏/深度估计（实验）", false, 40)]
        public static void Open()
        {
            var w = GetWindow<DepthEstimatorWindow>("深度估计");
            w.minSize = new Vector2(560f, 520f);
            w.Show();
        }

        /// <summary>
        /// 归一化方式。不同模型训练时用的不一样，喂错了输出会是噪声。
        /// </summary>
        enum Norm
        {
            ImageNet,   // (x/255 - mean) / std，MiDaS 和 U^2-Net 用这个
            HalfHalf,   // (x/255 - 0.5) / 1.0，IS-Net 用这个
        }

        class ModelSpec
        {
            public string label;
            public string path;
            public Norm norm;
            public bool isDepth;      // true=相对深度（要 min/max 归一化） false=蒙版（已是 0~1）
        }

        static readonly ModelSpec[] Models =
        {
            new ModelSpec { label = "IS-Net 分割 (170MB, 1024px) — 细节最好",
                            path = "Assets/GameAssets/Models/isnet-general-use.onnx",
                            norm = Norm.HalfHalf, isDepth = false },
            new ModelSpec { label = "U^2-Net 人像分割 (168MB, 320px)",
                            path = "Assets/GameAssets/Models/u2net_human_seg.onnx",
                            norm = Norm.ImageNet, isDepth = false },
            new ModelSpec { label = "MiDaS 深度 large (397MB, 384px)",
                            path = "Assets/GameAssets/Models/MiDaS-large.onnx",
                            norm = Norm.ImageNet, isDepth = true },
            new ModelSpec { label = "MiDaS 深度 small (64MB, 256px)",
                            path = "Assets/GameAssets/Models/MiDaS-small.onnx",
                            norm = Norm.ImageNet, isDepth = true },
        };

        ModelSpec Spec => Models[Mathf.Clamp(_modelIndex, 0, Models.Length - 1)];

        int _inputSize = 256;
        static readonly Vector3 ImageNetMean = new Vector3(0.485f, 0.456f, 0.406f);
        static readonly Vector3 ImageNetStd  = new Vector3(0.229f, 0.224f, 0.225f);

        [SerializeField] int _modelIndex;
        [SerializeField] Texture2D _source;
        [SerializeField] BackendType _backend = BackendType.GPUCompute;
        [SerializeField] bool _invert = true;

        ModelAsset _modelAsset;
        Worker _worker;
        Model _model;

        Texture2D _depthTex;          // 模型原始输出，256x256
        RenderTexture _refinedRT;     // 联合双边上采样之后的全分辨率深度
        Material _refineMat;

        string _status = "尚未运行";
        double _lastMs;

        // 推理要 Blit + ReadPixels，绝不能在 OnGUI 里跑，排队到 Update
        System.Action _pendingAction;

        [SerializeField] bool _refine = true;
        [SerializeField] float _sigmaSpace = 2.2f;
        [SerializeField] float _sigmaColor = 0.12f;
        [SerializeField] float _sampleScale = 1.0f;
        [SerializeField] int _viewMode;   // 0 精修深度 1 原始深度

        /// <summary>
        /// 所有渲染和回读都在这里跑。IMGUI 正在往窗口渲染目标里画的时候
        /// 切走 RenderTexture.active，GUI 状态会乱掉（黑块、裁剪失效）。
        /// </summary>
        void Update()
        {
            if (_pendingAction == null) return;
            var a = _pendingAction;
            _pendingAction = null;
            a();
            Repaint();
        }

        void OnDisable() => ReleaseAll();

        void ReleaseModel()
        {
            _worker?.Dispose();
            _worker = null;
            _model = null;
        }

        void ReleaseAll()
        {
            ReleaseModel();
            if (_depthTex != null) { DestroyImmediate(_depthTex); _depthTex = null; }
            if (_refinedRT != null) { _refinedRT.Release(); DestroyImmediate(_refinedRT); _refinedRT = null; }
            if (_refineMat != null) { DestroyImmediate(_refineMat); _refineMat = null; }
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "这是最小验证：跑通 MiDaS 深度估计，看速度和质量是否值得接进修图台做景深。",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            _modelIndex = EditorGUILayout.Popup("模型", _modelIndex, System.Array.ConvertAll(Models, m => m.label));
            if (EditorGUI.EndChangeCheck()) ReleaseModel();   // 换模型要重建 Worker 并重读输入尺寸

            string modelPath = Spec.path;
            _modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>(modelPath);
            if (_modelAsset == null)
            {
                EditorGUILayout.HelpBox(
                    $"找不到模型：{modelPath}\n" +
                    "文件应该已经下好了。如果它还没被导入成 ModelAsset，\n" +
                    "说明 Sentis 包还没解析完，等 Unity 装完包再试。", MessageType.Error);
                return;
            }

            EditorGUI.BeginChangeCheck();
            _source = (Texture2D)EditorGUILayout.ObjectField("源图片", _source, typeof(Texture2D), false);
            if (EditorGUI.EndChangeCheck()) _status = "图片已更换，点下面按钮跑一次";

            if (GUILayout.Button("从磁盘打开图片…")) OpenFromDisk();

            _backend = (BackendType)EditorGUILayout.EnumPopup("推理后端", _backend);
            if (Spec.isDepth) _invert = EditorGUILayout.Toggle("反转（近亮远暗）", _invert);

            using (new EditorGUI.DisabledScope(_source == null))
            {
                if (GUILayout.Button("运行深度估计", GUILayout.Height(28f))) _pendingAction = Run;
            }

            EditorGUILayout.Space(4f);
            EditorGUI.BeginChangeCheck();
            _refine = EditorGUILayout.Toggle("联合双边精修", _refine);
            using (new EditorGUI.DisabledScope(!_refine))
            {
                _sigmaSpace = EditorGUILayout.Slider("  空间平滑", _sigmaSpace, 0.5f, 6f);
                _sigmaColor = EditorGUILayout.Slider("  颜色敏感度", _sigmaColor, 0.02f, 0.5f);
                _sampleScale = EditorGUILayout.Slider("  采样步长", _sampleScale, 0.25f, 3f);
            }
            _viewMode = EditorGUILayout.Popup("显示", _viewMode, new[] { "精修深度", "原始深度" });
            // 参数一动就重跑精修，但不用重新推理
            if (EditorGUI.EndChangeCheck() && _depthTex != null) _pendingAction = Refine;

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("状态", _status);
            if (_lastMs > 0) EditorGUILayout.LabelField("推理耗时", $"{_lastMs:0.0} ms");

            if (_model != null)
            {
                EditorGUILayout.LabelField("输入", $"{_model.inputs[0].name}");
                EditorGUILayout.LabelField("输出", $"{_model.outputs[0].name}");
            }

            EditorGUILayout.Space(6f);
            DrawSideBySide();

            if (_depthTex != null && GUILayout.Button("导出深度图 PNG…")) _pendingAction = ExportDepth;
        }

        void DrawSideBySide()
        {
            float w = (EditorGUIUtility.currentViewWidth - 30f) * 0.5f;
            var row = GUILayoutUtility.GetRect(0f, w * 0.75f, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;

            var left = new Rect(row.x, row.y, w, row.height);
            var right = new Rect(row.x + w + 10f, row.y, w, row.height);

            EditorGUI.DrawRect(left, new Color(0.12f, 0.13f, 0.15f));
            EditorGUI.DrawRect(right, new Color(0.12f, 0.13f, 0.15f));

            if (_source != null) GUI.DrawTexture(left, _source, ScaleMode.ScaleToFit);
            Texture shown = (_viewMode == 0 && _refinedRT != null) ? (Texture)_refinedRT : _depthTex;
            if (shown != null) GUI.DrawTexture(right, shown, ScaleMode.ScaleToFit);
        }

        #region 推理

        void Run()
        {
            if (_source == null) return;

            try
            {
                var sw = Stopwatch.StartNew();

                if (_model == null)
                {
                    _model = ModelLoader.Load(_modelAsset);
                    _inputSize = ReadInputSize(_model);
                }

                // 后端换了要重建 Worker
                _worker?.Dispose();
                _worker = new Worker(_model, _backend);

                using (var input = BuildInputTensor(_source, _inputSize, Spec.norm))
                {
                    _worker.Schedule(input);

                    // PeekOutput 拿到的还在 GPU 上，ReadbackAndClone 才是真正取回 CPU
                    using (var output = _worker.PeekOutput().ReadbackAndClone() as Tensor<float>)
                    {
                        sw.Stop();
                        _lastMs = sw.Elapsed.TotalMilliseconds;

                        if (output == null) { _status = "输出不是 float 张量，模型可能不对"; return; }
                        BuildDepthTexture(output);
                    }
                }

                Refine();
                _status = "成功";
                Repaint();
            }
            catch (System.Exception e)
            {
                _status = "失败：" + e.Message;
                Debug.LogException(e);
            }
        }

        /// <summary>
        /// 从模型自身的输入形状读出分辨率。
        /// small 是 256、large 是 384，写死会让换模型时输入尺寸对不上，
        /// 报出来的错还很难看懂（形状不匹配）。读不到就退回 256。
        /// </summary>
        static int ReadInputSize(Model model)
        {
            if (model == null || model.inputs.Count == 0) return 256;

            var shape = model.inputs[0].shape;
            if (shape.isRankDynamic || shape.rank < 4 || !shape.IsStatic()) return 256;

            var ts = shape.ToTensorShape();      // NCHW
            int h = ts[2];
            return h > 0 ? h : 256;
        }

        /// <summary>
        /// 手工构建输入张量而不是用 TextureConverter。
        ///
        /// MiDaS 要的是 ImageNet 归一化后的值，范围大致 -2.1~2.6，8 位 RT 存不下负数；
        /// 而且它是在 sRGB 图上训练的，不能喂线性值。自己在 CPU 上拼 196k 个 float
        /// 开销可以忽略，但能完全控制数值，省掉一堆色彩空间的不确定性。
        /// </summary>
        Tensor<float> BuildInputTensor(Texture2D src, int size, Norm norm)
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

                var px = readback.GetPixels32();
                var data = new float[3 * size * size];
                int plane = size * size;

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        // ReadPixels 的原点在左下，模型要的是左上，所以纵向翻一下
                        var c = px[(size - 1 - y) * size + x];
                        int i = y * size + x;
                        Vector3 mean = norm == Norm.ImageNet ? ImageNetMean : new Vector3(0.5f, 0.5f, 0.5f);
                        Vector3 std  = norm == Norm.ImageNet ? ImageNetStd  : Vector3.one;

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
                DestroyImmediate(readback);
            }
        }

        /// <summary>
        /// MiDaS 输出的是相对逆深度，没有绝对尺度，必须按当前画面的 min/max 归一化才能看。
        /// </summary>
        void BuildDepthTexture(Tensor<float> output)
        {
            var d = output.DownloadToArray();
            int n = d.Length;

            // 分割模型（U^2-Net / IS-Net）会一次返回多个侧输出，第一张才是主结果。
            // 深度模型只有一张。按输入尺寸推算单张的像素数，多出来的直接忽略。
            int side = Mathf.RoundToInt(Mathf.Sqrt(n));
            if (side * side != n)
            {
                side = _inputSize;
                if (side * side > n)
                {
                    _status = $"输出长度 {n} 和输入尺寸 {_inputSize} 对不上";
                    return;
                }
            }
            int count = side * side;

            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                if (d[i] < min) min = d[i];
                if (d[i] > max) max = d[i];
            }
            float range = Mathf.Max(1e-6f, max - min);

            if (_depthTex == null || _depthTex.width != side)
            {
                if (_depthTex != null) DestroyImmediate(_depthTex);
                _depthTex = new Texture2D(side, side, TextureFormat.RGBA32, false, true)
                { name = "AIMask", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            }

            bool depthMode = Spec.isDepth;
            var px = new Color32[count];
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    float raw = d[y * side + x];

                    // 深度是相对值，必须按当前画面 min/max 拉伸；
                    // 分割输出本来就是 0~1 的概率，再拉伸只会放大背景噪声
                    float v = depthMode ? (raw - min) / range : Mathf.Clamp01(raw);
                    if (depthMode && !_invert) v = 1f - v;

                    byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
                    // 纹理原点在左下，把行翻回来
                    px[(side - 1 - y) * side + x] = new Color32(b, b, b, 255);
                }
            }
            _depthTex.SetPixels32(px);
            _depthTex.Apply(false, false);
        }

        #endregion

        #region 精修

        const int MaxRefineSide = 2048;   // 精修分辨率上限，24MP 原图全跑没必要也慢

        /// <summary>
        /// 联合双边上采样：用全分辨率彩色原图当引导，把 256x256 的深度边界吸附到颜色边缘上。
        /// 只依赖已有的深度图，改参数时不用重新推理。
        /// </summary>
        void Refine()
        {
            if (_depthTex == null || _source == null) return;

            if (!_refine)
            {
                if (_refinedRT != null) { _refinedRT.Release(); DestroyImmediate(_refinedRT); _refinedRT = null; }
                return;
            }

            if (_refineMat == null)
            {
                var sh = Shader.Find("Hidden/Love/DepthRefine");
                if (sh == null) { _status = "找不到 Hidden/Love/DepthRefine"; return; }
                _refineMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }

            // 按原图比例定精修分辨率，但压在上限内
            float k = Mathf.Min(1f, MaxRefineSide / (float)Mathf.Max(_source.width, _source.height));
            int w = Mathf.Max(1, Mathf.RoundToInt(_source.width * k));
            int h = Mathf.Max(1, Mathf.RoundToInt(_source.height * k));

            if (_refinedRT == null || _refinedRT.width != w || _refinedRT.height != h)
            {
                if (_refinedRT != null) { _refinedRT.Release(); DestroyImmediate(_refinedRT); }
                _refinedRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                { name = "RefinedDepth", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
                _refinedRT.Create();
            }

            _refineMat.SetTexture("_DepthTex", _depthTex);
            _refineMat.SetFloat("_SigmaSpace", _sigmaSpace);
            _refineMat.SetFloat("_SigmaColor", _sigmaColor);
            _refineMat.SetFloat("_SampleScale", _sampleScale);

            // _MainTex 由 Blit 设成源图，也就是引导图
            Graphics.Blit(_source, _refinedRT, _refineMat, 0);
        }

        #endregion

        #region 杂项

        void OpenFromDisk()
        {
            string path = EditorUtility.OpenFilePanel("打开图片", "", "png,jpg,jpeg");
            if (string.IsNullOrEmpty(path)) return;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
            { name = Path.GetFileNameWithoutExtension(path), hideFlags = HideFlags.HideAndDontSave };
            if (tex.LoadImage(File.ReadAllBytes(path))) { _source = tex; _status = "图片已载入"; }
            else { DestroyImmediate(tex); Debug.LogError("[深度估计] 读不了这个文件：" + path); }
        }

        void ExportDepth()
        {
            string path = EditorUtility.SaveFilePanel("导出深度图", "",
                (_source != null ? _source.name : "depth") + "_depth", "png");
            if (string.IsNullOrEmpty(path)) return;
            File.WriteAllBytes(path, _depthTex.EncodeToPNG());
            Debug.Log("[深度估计] 已导出：" + path);
            EditorUtility.RevealInFinder(path);
        }

        #endregion
    }
}
#endif
