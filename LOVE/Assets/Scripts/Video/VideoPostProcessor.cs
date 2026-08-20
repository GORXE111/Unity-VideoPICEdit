using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace Love.Video
{
    /// <summary>
    /// 视频后处理。把 VideoScreen 输出的 RenderTexture 过一遍调色管线，再交给 RawImage 显示。
    ///
    /// 每帧都跑，哪怕视频已经暂停定格在最后一帧——这样在定格画面上拖滑条也能实时看到变化，
    /// 调色的时候不用一直重播视频。
    /// </summary>
    [DisallowMultipleComponent]
    public class VideoPostProcessor : MonoBehaviour
    {
        [Header("引用")]
        public VideoScreen videoScreen;
        [Tooltip("显示最终画面的 RawImage，通常就是 VideoScreen 上那个")]
        public RawImage target;
        public AspectRatioFitter aspectFitter;
        [Tooltip("用 Hidden/Love/VideoGrade 这个 Shader 的材质")]
        public Material material;

        [Header("参数")]
        public VideoGradeSettings settings = new VideoGradeSettings();
        [Tooltip("勾上则直接输出原片，用来和调色后的效果对比")]
        public bool bypass;

        [Header("存档")]
        [Tooltip("相对于 StreamingAssets 的路径。编辑器里保存会写到这里，随包发布")]
        public string presetFile = "Story/grade.json";
        [Tooltip("启动时自动读取上面这个文件")]
        public bool loadOnStart = true;

        [Header("分屏对比")]
        [Tooltip("开启后左半边显示原片、右半边显示调色结果，中间一条白线")]
        public bool splitCompare;
        [Range(0f, 1f)] public float splitPosition = 0.5f;

        [Header("直方图")]
        [Tooltip("实时统计画面的 RGB 分布。要回读画面，关掉能省一点开销")]
        public bool histogramEnabled = true;
        [Tooltip("每隔几帧统计一次。直方图不需要每帧都更新")]
        [Range(1, 20)] public int histogramInterval = 5;


        /// <summary>直方图的分档数。</summary>
        public const int HistogramBins = 64;

        // 直方图统计缓冲。0~2 是 RGB 三通道，3 是亮度
        readonly float[][] _histogram =
        {
            new float[HistogramBins], new float[HistogramBins],
            new float[HistogramBins], new float[HistogramBins],
        };

        RenderTexture _histSource;
        Texture2D _histReadback;
        int _histFrameCounter;

        /// <summary>红/绿/蓝/亮度四条直方图，每条 HistogramBins 个值，已归一化到 0~1。</summary>
        public float[] HistogramR => _histogram[0];
        public float[] HistogramG => _histogram[1];
        public float[] HistogramB => _histogram[2];
        public float[] HistogramLuma => _histogram[3];

        /// <summary>直方图更新时触发，UI 订阅它来重画。</summary>
        public event System.Action OnHistogramUpdated;

        RenderTexture _output;
        Texture2D _testPattern;

        /// <summary>调色后的成片。编辑器窗口拿它做画中画预览。</summary>
        public RenderTexture Output => _output;

        /// <summary>没有视频时显示一张测试卡，好让调色面板在没素材的阶段也能用。</summary>
        public bool showTestPattern;

        /// <summary>当前实际参与调色的源贴图。</summary>
        public Texture CurrentSource
        {
            get
            {
                if (showTestPattern) return EnsureTestPattern();
                return videoScreen != null ? videoScreen.SourceTexture : null;
            }
        }

        void Awake()
        {
            // 引用为空时从 UI 面板预制体上自动取
            if (target == null || aspectFitter == null)
            {
                var ui = Love.UI.GameplayUIRoot.Find();
                if (ui != null)
                {
                    if (target == null) target = ui.videoImage;
                    if (aspectFitter == null) aspectFitter = ui.videoAspectFitter;
                }
            }
            if (videoScreen == null) videoScreen = GetComponent<VideoScreen>();
            if (videoScreen == null) videoScreen = FindObjectOfType<VideoScreen>();
        }

        void Start()
        {
            if (loadOnStart) LoadPreset();
        }

        void OnDestroy()
        {
            ReleaseOutput();
            ReleaseHistogram();
            ReleaseScope();
            _renderer?.Dispose();
            _renderer = null;
            if (_testPattern != null) Destroy(_testPattern);
        }

        void LateUpdate()
        {
            var src = CurrentSource;
            if (src == null || material == null || target == null) return;

            EnsureOutput(src.width, src.height);
            Render(src, _output);

            if (!ReferenceEquals(target.texture, _output)) target.texture = _output;
            if (!target.enabled) target.enabled = true;
            if (aspectFitter != null && src.height > 0)
                aspectFitter.aspectRatio = (float)src.width / src.height;

            UpdateHistogram();
        }

        #region 直方图

        const int HistW = 128, HistH = 72;

        /// <summary>
        /// 把成片缩到 128x72 再回读统计。
        /// 直接读 1080p 会把 GPU 卡死；缩到这个尺寸只有 9216 个像素，
        /// 再配合隔几帧才统计一次，开销可以忽略。
        /// </summary>
        void UpdateHistogram()
        {
            if (!histogramEnabled || _output == null) return;

            if (++_histFrameCounter < Mathf.Max(1, histogramInterval)) return;
            _histFrameCounter = 0;

            if (_histSource == null)
            {
                // 必须是 sRGB：直方图要反映"眼睛看到的画面"，不是线性光强度。
                // 建成 Linear 的话读回来是线性值，直方图分布会和显示出来的画面完全对不上。
                _histSource = new RenderTexture(HistW, HistH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { name = "HistogramSource", filterMode = FilterMode.Bilinear };
                _histSource.Create();
            }
            if (_histReadback == null)
                _histReadback = new Texture2D(HistW, HistH, TextureFormat.RGBA32, false, true);

            Graphics.Blit(_output, _histSource);

            var prev = RenderTexture.active;
            RenderTexture.active = _histSource;
            _histReadback.ReadPixels(new Rect(0, 0, HistW, HistH), 0, 0, false);
            _histReadback.Apply(false, false);
            RenderTexture.active = prev;

            for (int ch = 0; ch < 4; ch++)
                System.Array.Clear(_histogram[ch], 0, HistogramBins);

            var pixels = _histReadback.GetRawTextureData<Color32>();
            int last = HistogramBins - 1;
            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                _histogram[0][p.r * last / 255]++;
                _histogram[1][p.g * last / 255]++;
                _histogram[2][p.b * last / 255]++;
                int luma = (p.r * 54 + p.g * 183 + p.b * 19) >> 8;   // Rec.709 的整数近似
                _histogram[3][Mathf.Clamp(luma, 0, 255) * last / 255]++;
            }

            // 归一化。用最大值而不是总数，否则纯色画面会把整张图压成一根线
            for (int ch = 0; ch < 4; ch++)
            {
                float max = 0f;
                var bins = _histogram[ch];
                for (int i = 0; i < bins.Length; i++) if (bins[i] > max) max = bins[i];
                if (max <= 0f) continue;
                for (int i = 0; i < bins.Length; i++) bins[i] /= max;
            }

            BuildScope(pixels);
            OnHistogramUpdated?.Invoke();
        }

        #region 波形图 / 矢量示波器

        public enum ScopeKind { 关闭 = 0, 波形图 = 1, 分量波形 = 2, 矢量示波器 = 3 }

        [Tooltip("要生成哪种示波器。只有选中的那种会被计算")]
        public ScopeKind scopeKind = ScopeKind.波形图;

        const int ScopeW = 256, ScopeH = 176;

        Texture2D _scopeTex;
        float[] _scopeAccum;
        Color32[] _scopePixels;

        /// <summary>示波器贴图，编辑器窗口直接拿去画。没启用时为 null。</summary>
        public Texture2D ScopeTexture => _scopeTex;

        void BuildScope(Unity.Collections.NativeArray<Color32> pixels)
        {
            if (scopeKind == ScopeKind.关闭) return;

            if (_scopeTex == null)
            {
                _scopeTex = new Texture2D(ScopeW, ScopeH, TextureFormat.RGBA32, false, true)
                { name = "ScopeTex", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                _scopeAccum = new float[ScopeW * ScopeH * 3];
                _scopePixels = new Color32[ScopeW * ScopeH];
            }

            System.Array.Clear(_scopeAccum, 0, _scopeAccum.Length);

            if (scopeKind == ScopeKind.矢量示波器) AccumulateVectorscope(pixels);
            else AccumulateWaveform(pixels, scopeKind == ScopeKind.分量波形);

            // 用平方根压一下动态范围，否则少数几个亮点会把整张图压黑
            float max = 0f;
            for (int i = 0; i < _scopeAccum.Length; i++) if (_scopeAccum[i] > max) max = _scopeAccum[i];
            float inv = max > 0f ? 1f / Mathf.Sqrt(max) : 0f;

            for (int i = 0, p = 0; i < _scopePixels.Length; i++, p += 3)
            {
                _scopePixels[i] = new Color32(
                    (byte)Mathf.Min(255f, Mathf.Sqrt(_scopeAccum[p])     * inv * 255f),
                    (byte)Mathf.Min(255f, Mathf.Sqrt(_scopeAccum[p + 1]) * inv * 255f),
                    (byte)Mathf.Min(255f, Mathf.Sqrt(_scopeAccum[p + 2]) * inv * 255f),
                    255);
            }

            _scopeTex.SetPixels32(_scopePixels);
            _scopeTex.Apply(false, false);
        }

        /// <summary>
        /// 波形图：横轴对应画面的横向位置，纵轴是亮度。
        /// parade=true 时把 R/G/B 拆成左中右三段并排，也就是分量波形。
        /// </summary>
        void AccumulateWaveform(Unity.Collections.NativeArray<Color32> pixels, bool parade)
        {
            int segments = parade ? 3 : 1;
            int segW = ScopeW / segments;

            for (int i = 0; i < pixels.Length; i++)
            {
                int sx = i % HistW;
                var p = pixels[i];

                for (int ch = 0; ch < 3; ch++)
                {
                    byte v = ch == 0 ? p.r : ch == 1 ? p.g : p.b;
                    int seg = parade ? ch : 0;
                    int x = seg * segW + sx * segW / HistW;
                    int y = v * (ScopeH - 1) / 255;
                    if ((uint)x >= ScopeW || (uint)y >= ScopeH) continue;

                    int idx = (y * ScopeW + x) * 3 + ch;
                    _scopeAccum[idx] += 1f;
                }
            }
        }

        /// <summary>
        /// 矢量示波器：把每个像素的色度（Cb/Cr）打到极坐标平面上。
        /// 圆心是无彩色，越往外饱和度越高，角度对应色相。
        /// </summary>
        void AccumulateVectorscope(Unity.Collections.NativeArray<Color32> pixels)
        {
            int cx = ScopeW / 2, cy = ScopeH / 2;
            float radius = Mathf.Min(cx, cy) - 2f;

            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                float r = p.r / 255f, g = p.g / 255f, b = p.b / 255f;

                // Rec.601 的 Cb/Cr，范围约 -0.5..0.5
                float y = 0.299f * r + 0.587f * g + 0.114f * b;
                float cb = (b - y) * 0.564f;
                float cr = (r - y) * 0.713f;

                int px = cx + Mathf.RoundToInt(cb * 2f * radius);
                int py = cy + Mathf.RoundToInt(cr * 2f * radius);
                if ((uint)px >= ScopeW || (uint)py >= ScopeH) continue;

                int idx = (py * ScopeW + px) * 3;
                _scopeAccum[idx]     += r;
                _scopeAccum[idx + 1] += g;
                _scopeAccum[idx + 2] += b;
            }
        }

        void ReleaseScope()
        {
            if (_scopeTex != null) { Destroy(_scopeTex); _scopeTex = null; }
            _scopeAccum = null;
            _scopePixels = null;
        }

        #endregion

        void ReleaseHistogram()
        {
            if (_histSource != null) { _histSource.Release(); Destroy(_histSource); _histSource = null; }
            if (_histReadback != null) { Destroy(_histReadback); _histReadback = null; }
        }

        #endregion

        #region 渲染（委托给 VideoGradeRenderer）

        VideoGradeRenderer _renderer;

        /// <summary>渲染核心。抽成独立类之后，编辑器的修图工具能直接复用同一套管线。</summary>
        public VideoGradeRenderer Renderer
        {
            get
            {
                if (_renderer == null || !_renderer.IsValid) _renderer = new VideoGradeRenderer(material);
                return _renderer;
            }
        }

        void Render(Texture src, RenderTexture dst)
        {
            var r = Renderer;
            // 颗粒每帧换种子，否则会是一张静止的噪点贴图，看着像脏了
            r.GrainSeed = Time.frameCount % 1024;
            r.Render(src, dst, settings, new VideoGradeRenderer.Options
            {
                bypass = bypass,
                splitCompare = splitCompare,
                splitPosition = splitPosition,
            });
        }

        void EnsureOutput(int w, int h)
        {
            if (_output != null && _output.width == w && _output.height == h) return;
            ReleaseOutput();
            _output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = $"VideoGradeOut_{w}x{h}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            _output.Create();
        }

        void ReleaseOutput()
        {
            if (_output == null) return;
            if (target != null && ReferenceEquals(target.texture, _output)) target.texture = null;
            _output.Release();
            Destroy(_output);
            _output = null;
        }

        #endregion

        #region 测试卡

        /// <summary>
        /// 程序生成的测试卡：上半是彩条，下半是灰阶渐变和肤色块。
        /// 没有视频素材时也能把调色管线跑起来，看清楚每个参数在干什么。
        /// </summary>
        Texture2D EnsureTestPattern()
        {
            if (_testPattern != null) return _testPattern;

            const int W = 960, H = 540;
            _testPattern = new Texture2D(W, H, TextureFormat.RGBA32, false, false)
            {
                name = "GradeTestPattern",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color[] bars =
            {
                new Color(1f, 1f, 1f), new Color(1f, 1f, 0f), new Color(0f, 1f, 1f), new Color(0f, 1f, 0f),
                new Color(1f, 0f, 1f), new Color(1f, 0f, 0f), new Color(0f, 0f, 1f), new Color(0.05f, 0.05f, 0.05f)
            };
            Color[] skin =
            {
                new Color(0.96f, 0.80f, 0.69f), new Color(0.85f, 0.63f, 0.49f),
                new Color(0.65f, 0.45f, 0.33f), new Color(0.40f, 0.26f, 0.19f)
            };

            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
            {
                float v = (float)y / (H - 1);
                for (int x = 0; x < W; x++)
                {
                    float u = (float)x / (W - 1);
                    Color c;

                    if (v > 0.45f)                       // 上半：彩条
                    {
                        c = bars[Mathf.Clamp((int)(u * bars.Length), 0, bars.Length - 1)];
                    }
                    else if (v > 0.30f)                  // 中间：肤色参考
                    {
                        c = skin[Mathf.Clamp((int)(u * skin.Length), 0, skin.Length - 1)];
                    }
                    else if (v > 0.12f)                  // 灰阶连续渐变，看色带和对比度
                    {
                        c = new Color(u, u, u);
                    }
                    else                                 // 底部：11 级阶梯灰，看黑位白位
                    {
                        float step = Mathf.Floor(u * 11f) / 10f;
                        c = new Color(step, step, step);
                    }

                    px[y * W + x] = c;
                }
            }

            _testPattern.SetPixels(px);
            _testPattern.Apply(false, false);
            return _testPattern;
        }

        #endregion

        #region 存读

        public string PresetPath => Path.Combine(Application.streamingAssetsPath, presetFile).Replace('\\', '/');

        /// <summary>把当前参数存成 JSON。编辑器里写进 StreamingAssets，跟着包一起发布。</summary>
        public bool SavePreset()
        {
            try
            {
                string path = PresetPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, settings.ToJson(), System.Text.Encoding.UTF8);
                Debug.Log($"[VideoGrade] 调色参数已保存：{path}");
                return true;
            }
            catch (System.Exception e)
            {
                // 安卓等平台 StreamingAssets 不可写，退回可写目录
                try
                {
                    string alt = Path.Combine(Application.persistentDataPath, "grade.json");
                    File.WriteAllText(alt, settings.ToJson(), System.Text.Encoding.UTF8);
                    Debug.Log($"[VideoGrade] StreamingAssets 不可写，已保存到：{alt}");
                    return true;
                }
                catch
                {
                    Debug.LogError($"[VideoGrade] 保存失败：{e.Message}");
                    return false;
                }
            }
        }

        /// <summary>读取调色参数。优先读可写目录里的覆盖版本，其次读随包发布的版本。</summary>
        public bool LoadPreset()
        {
            string persistent = Path.Combine(Application.persistentDataPath, "grade.json");
            if (File.Exists(persistent) && TryLoadFrom(persistent)) return true;

            string path = PresetPath;
            if (!path.Contains("://") && File.Exists(path) && TryLoadFrom(path)) return true;

            return false;
        }

        bool TryLoadFrom(string path)
        {
            try
            {
                var loaded = VideoGradeSettings.FromJson(File.ReadAllText(path, System.Text.Encoding.UTF8));
                if (loaded == null) return false;
                settings.CopyFrom(loaded);
                Debug.Log($"[VideoGrade] 已载入调色参数：{path}");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[VideoGrade] 载入失败 {path}：{e.Message}");
                return false;
            }
        }

        #endregion
    }
}
