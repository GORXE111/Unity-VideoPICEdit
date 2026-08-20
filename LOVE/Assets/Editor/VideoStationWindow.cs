using System;
using System.Diagnostics;
using System.IO;
using Love.Video;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;
using Debug = UnityEngine.Debug;

namespace Love.EditorTools
{
    /// <summary>
    /// 视频台：导入任意视频，逐帧查看、调色、画质处理，再导出成新的 mp4。
    ///
    /// 和「调色台」的区别：调色台是贴着游戏跑的——必须进 Play、调的是场景里
    /// 那个 VideoPostProcessor、素材得在 StreamingAssets 里。视频台不需要场景、
    /// 不需要进 Play，随便哪个路径的视频都能拖进来，定位是一个独立的后期工具。
    ///
    /// 预览和导出走两条完全不同的路，这是刻意的：
    ///   预览 —— VideoPlayer。快、能实时播、拖时间轴跟手。
    ///   导出 —— ffmpeg 解码 → Unity 调色 → ffmpeg 编码，全程管道，不落临时文件。
    /// 编辑器模式下 VideoPlayer 的逐帧步进能不能稳定触发事件是个未知数，
    /// 而导出这一步必须确定，所以它一点都不依赖 VideoPlayer。
    /// </summary>
    public class VideoStationWindow : EditorWindow
    {
        const string MaterialPath = "Assets/GameAssets/Materials/VideoGrade.mat";

        const float ToolbarH = 22f;
        const float SplitterW = 5f;
        const float TransportH = 62f;

        const string PrefRightW = "VideoStation.rightPanelW";

        [MenuItem("Tools/影视游戏/视频台", false, 7)]
        public static void Open()
        {
            var w = GetWindow<VideoStationWindow>();
            w.titleContent = new GUIContent("视频台");
            w.minSize = new Vector2(880f, 540f);
            w.Show();
        }

        // ---------------- 状态 ----------------

        [SerializeField] string _path = "";
        [SerializeField] VideoGradeSettings _settings = new VideoGradeSettings();
        [SerializeField] bool _bypass;
        [SerializeField] bool _splitCompare;
        [SerializeField] float _splitPosition = 0.5f;
        [SerializeField] int _crf = 18;
        [SerializeField] bool _keepAudio = true;
        [SerializeField] long _inFrame, _outFrame = -1;

        // 预览有两条路。VideoPlayer 快、能实时播；ffmpeg 抽帧慢一点但一定能用。
        // 编辑器模式下 VideoPlayer 不一定伺候，所以准备超时会自动倒向 ffmpeg
        public enum Decoder { VideoPlayer, Ffmpeg }
        [SerializeField] Decoder _decoder = Decoder.VideoPlayer;

        GameObject _host;
        VideoPlayer _player;
        RenderTexture _source;     // VideoPlayer 解出来的原始帧
        Texture2D _cpuFrame;       // ffmpeg 抽出来的原始帧
        byte[] _cpuBuf;
        double _prepareDeadline;
        bool _cpuSeekPending;

        RenderTexture _preview;    // 调色之后
        bool _prepared;
        string _loadError;

        int _srcW, _srcH;
        double _fps = 30.0;
        long _frameCount;
        long _frame;

        bool _playing;
        double _lastTick;
        double _playAccum;

        VideoGradeRenderer _renderer;
        Material _materialCopy;
        bool _dirty = true;

        readonly GradeCanvas _canvas = new GradeCanvas();
        readonly GradeSettingsGUI _gui = new GradeSettingsGUI();
        Vector2 _paramScroll;

        float _rightPanelW = 360f;
        bool _draggingSplit;

        Action _pendingAction;

        // 导入的 .cube。和另外两个窗口一致，视频台也该能套 LUT
        Texture3D _lut;
        [SerializeField] string _lutName = "";
        [SerializeField] float _lutAmount = 1f;

        /// <summary>当前这一帧的来源。两条解码路共用同一个下游，调色这边不用知道帧是谁给的。</summary>
        Texture FrameSource => _decoder == Decoder.Ffmpeg ? (Texture)_cpuFrame : _source;

        // 导出。整个过程由 EditorApplication.update 驱动，不阻塞编辑器
        Export _export;

        // ---------------- 生命周期 ----------------

        void OnEnable()
        {
            titleContent = new GUIContent("视频台");
            _rightPanelW = EditorPrefs.GetFloat(PrefRightW, 360f);
            EditorApplication.update += OnEditorUpdate;

            // 程序集重载会把隐藏宿主干掉，路径还在就自己接回来
            if (!string.IsNullOrEmpty(_path) && File.Exists(_path)) LoadVideo(_path);
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            AbortExport("窗口关闭");
            ReleaseAll();
            // LUT 不放进 ReleaseAll：换视频时不该把套好的 LUT 也丢掉
            if (_lut != null) { DestroyImmediate(_lut); _lut = null; }
            EditorPrefs.SetFloat(PrefRightW, _rightPanelW);
        }

        void ReleaseAll()
        {
            if (_player != null) { _player.frameReady -= OnFrameReady; _player.prepareCompleted -= OnPrepared; _player.errorReceived -= OnPlayerError; }
            if (_host != null) DestroyImmediate(_host);
            _host = null; _player = null;

            ReleaseRt(ref _source);
            ReleaseRt(ref _preview);
            if (_cpuFrame != null) { DestroyImmediate(_cpuFrame); _cpuFrame = null; }
            _cpuBuf = null;

            _renderer?.Dispose();
            _renderer = null;
            if (_materialCopy != null) { DestroyImmediate(_materialCopy); _materialCopy = null; }
            _prepared = false;
        }

        static void ReleaseRt(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            DestroyImmediate(rt);
            rt = null;
        }

        VideoGradeRenderer Renderer
        {
            get
            {
                if (_renderer != null && _renderer.IsValid) return _renderer;
                var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
                if (mat == null) return null;
                // 用材质副本，免得视频台的参数把场景那个材质资产改脏
                if (_materialCopy == null)
                    _materialCopy = new Material(mat) { hideFlags = HideFlags.HideAndDontSave };
                _renderer = new VideoGradeRenderer(_materialCopy);
                return _renderer;
            }
        }

        // ---------------- 载入 ----------------

        void OpenVideoDialog()
        {
            string p = EditorUtility.OpenFilePanel("打开视频", "", "mp4,mov,m4v,webm,avi,mkv");
            if (!string.IsNullOrEmpty(p)) LoadVideo(p);
        }

        void LoadVideo(string path)
        {
            AbortExport("重新载入");
            ReleaseAll();

            _path = path;
            _loadError = null;
            _playing = false;
            _frame = 0;
            _inFrame = 0;
            _outFrame = -1;

            // 先问 ffprobe。拿到尺寸帧率之后，就算 VideoPlayer 一直准备不好，
            // ffmpeg 那条路也能马上顶上
            if (FfmpegTool.Probe(path, out int pw, out int ph, out double pfps, out double pdur))
            {
                _srcW = pw; _srcH = ph; _fps = pfps;
                _frameCount = Math.Max(1, (long)Math.Round(pdur * pfps));
                _outFrame = _frameCount - 1;
            }

            if (_decoder == Decoder.Ffmpeg)
            {
                if (_srcW <= 0) { _loadError = "ffprobe 读不出这个文件的信息"; Repaint(); return; }
                SetupCpuPath();
                _prepared = true;
                _canvas.FitPending = true;
                SeekTo(0);
                Repaint();
                return;
            }

            _host = EditorUtility.CreateGameObjectWithHideFlags(
                "VideoStationHost", HideFlags.HideAndDontSave, typeof(VideoPlayer));
            _player = _host.GetComponent<VideoPlayer>();

            _player.playOnAwake = false;
            _player.source = VideoSource.Url;
            _player.url = path;
            _player.renderMode = VideoRenderMode.RenderTexture;
            // 编辑器模式下没有可靠的音频输出，开着只会跟场景抢 AudioSource。
            // 导出时音轨是 ffmpeg 从源文件直接复制的，和这里无关
            _player.audioOutputMode = VideoAudioOutputMode.None;
            _player.skipOnDrop = false;
            _player.isLooping = false;
            _player.waitForFirstFrame = true;
            _player.sendFrameReadyEvents = true;   // 没有它就不知道哪一帧真的解出来了，会拿着上一帧去调色

            _player.prepareCompleted += OnPrepared;
            _player.frameReady += OnFrameReady;
            _player.errorReceived += OnPlayerError;

            _player.Prepare();
            // VideoPlayer 在编辑器模式下不一定伺候。给它几秒，到点还没好就换 ffmpeg
            _prepareDeadline = EditorApplication.timeSinceStartup + 6.0;
            Repaint();
        }

        void SetupCpuPath()
        {
            if (_cpuFrame != null) DestroyImmediate(_cpuFrame);
            _cpuFrame = new Texture2D(Mathf.Max(1, _srcW), Mathf.Max(1, _srcH),
                                      TextureFormat.RGBA32, false, false)
            { name = "VideoStationCpuFrame", hideFlags = HideFlags.HideAndDontSave,
              wrapMode = TextureWrapMode.Clamp };
            _cpuBuf = new byte[(long)Mathf.Max(1, _srcW) * Mathf.Max(1, _srcH) * 4];
        }

        /// <summary>换解码器。参数和入出点都留着，只是换一条取帧的路。</summary>
        void SwitchDecoder(Decoder d)
        {
            if (_decoder == d) return;
            _decoder = d;
            if (!string.IsNullOrEmpty(_path)) LoadVideo(_path);
        }

        void FallBackToFfmpeg(string why)
        {
            if (_decoder == Decoder.Ffmpeg) return;
            if (!FfmpegTool.Available)
            {
                _loadError = why + "，而且没找到 ffmpeg 可以顶上。";
                Repaint();
                return;
            }
            Debug.LogWarning($"[视频台] {why}，改用 ffmpeg 抽帧预览。");
            _decoder = Decoder.Ffmpeg;
            LoadVideo(_path);
        }

        void OnPrepared(VideoPlayer vp)
        {
            _srcW = (int)vp.width;
            _srcH = (int)vp.height;
            _fps = vp.frameRate > 0.01 ? vp.frameRate : 30.0;
            _frameCount = vp.frameCount > 0 ? (long)vp.frameCount : 0;

            // VideoPlayer 有时报不出帧率/帧数，ffprobe 更靠得住
            if (FfmpegTool.Probe(_path, out int pw, out int ph, out double pfps, out double pdur))
            {
                if (pw > 0) { _srcW = pw; _srcH = ph; }
                if (pfps > 0.01) _fps = pfps;
                if (_frameCount <= 0 && pdur > 0) _frameCount = (long)Math.Round(pdur * _fps);
            }
            if (_frameCount <= 0) _frameCount = 1;
            if (_outFrame < 0) _outFrame = _frameCount - 1;

            ReleaseRt(ref _source);
            _source = new RenderTexture(Mathf.Max(1, _srcW), Mathf.Max(1, _srcH), 0,
                                        RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            { name = "VideoStationSource", hideFlags = HideFlags.HideAndDontSave };
            _source.Create();
            vp.targetTexture = _source;

            _prepared = true;
            _prepareDeadline = 0;     // 已经好了，别再触发兜底
            _canvas.FitPending = true;
            _dirty = true;

            vp.Pause();
            vp.frame = 0;
            Repaint();
        }

        void OnPlayerError(VideoPlayer vp, string message)
        {
            _loadError = message;
            _prepared = false;
            Repaint();
        }

        void OnFrameReady(VideoPlayer vp, long frameIdx)
        {
            _frame = frameIdx;
            _dirty = true;
            Repaint();
        }

        // ---------------- 每帧 ----------------

        void OnEditorUpdate()
        {
            if (_export != null) { PumpExport(); return; }

            if (_pendingAction != null)
            {
                var a = _pendingAction;
                _pendingAction = null;
                a();
            }

            // VideoPlayer 迟迟准备不好，说明编辑器模式下它指望不上
            if (!_prepared && _decoder == Decoder.VideoPlayer && _player != null &&
                _prepareDeadline > 0 && EditorApplication.timeSinceStartup > _prepareDeadline)
            {
                _prepareDeadline = 0;
                FallBackToFfmpeg("VideoPlayer 在编辑器模式下没能准备好");
                return;
            }

            // 播放：按真实时间累积，够一帧就步进一帧。
            // 不用 VideoPlayer 自己的时钟——编辑器模式下 player loop 不一定在跑
            if (_playing && _prepared)
            {
                double now = EditorApplication.timeSinceStartup;
                double dt = Mathf.Clamp((float)(now - _lastTick), 0f, 0.25f);
                _lastTick = now;
                _playAccum += dt;

                double step = 1.0 / Math.Max(_fps, 1.0);
                long last = Math.Min(_frameCount - 1, _outFrame);
                int guard = 0;
                // ffmpeg 那条路每帧都要起一次进程，追不上实时，一次只前进一帧免得越积越多
                int maxSteps = _decoder == Decoder.Ffmpeg ? 1 : 4;
                while (_playAccum >= step && guard++ < maxSteps)
                {
                    _playAccum -= step;
                    if (_frame >= last) { _playing = false; break; }
                    if (_decoder == Decoder.Ffmpeg) SeekTo(_frame + 1);
                    else _player?.StepForward();
                }
                if (_decoder == Decoder.Ffmpeg) _playAccum = 0.0;   // 别把追不上的时间累成债
                Repaint();
            }

            // 排在播放步进之后：这一拍要是刚步进过，就在同一拍把帧取回来
            if (_cpuSeekPending && _decoder == Decoder.Ffmpeg)
            {
                _cpuSeekPending = false;
                GrabCpuFrame();
            }

            if (_dirty) { RenderPreview(); Repaint(); }
        }

        void RenderPreview()
        {
            var src = FrameSource;
            if (!_prepared || src == null) return;
            var r = Renderer;
            if (r == null) return;

            _settings.OutputSize(src.width, src.height, out int ow, out int oh);
            if (_preview == null || _preview.width != ow || _preview.height != oh)
            {
                ReleaseRt(ref _preview);
                _preview = new RenderTexture(ow, oh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                { name = "VideoStationPreview", hideFlags = HideFlags.HideAndDontSave };
                _preview.Create();
            }

            // 颗粒种子跟着帧号走，静止画面上噪点才会动，和真实胶片一致；
            // 同一帧反复重渲染时又是稳定的，调参数时噪点不会乱跳
            r.GrainSeed = _frame * 0.017f;
            r.Render(src, _preview, _settings, new VideoGradeRenderer.Options
            {
                bypass = _bypass || _canvas.HoldCompare,
                splitCompare = _splitCompare,
                splitPosition = _splitPosition,
                lut = _lut,
                lutAmount = _lutAmount,
            });
            _dirty = false;
        }

        // ---------------- 布局 ----------------

        void OnGUI()
        {
            if (Renderer == null)
            {
                EditorGUILayout.HelpBox($"找不到调色材质：{MaterialPath}\n先跑一次「单步：生成调色材质」。", MessageType.Error);
                return;
            }

            _rightPanelW = Mathf.Clamp(_rightPanelW, 260f, Mathf.Max(300f, position.width - 320f));

            var toolbar = new Rect(0f, 0f, position.width, ToolbarH);
            var right = new Rect(position.width - _rightPanelW, ToolbarH, _rightPanelW, position.height - ToolbarH);
            var vSplit = new Rect(right.x - SplitterW, ToolbarH, SplitterW, position.height - ToolbarH);
            var left = new Rect(0f, ToolbarH, vSplit.x, position.height - ToolbarH);
            var transport = new Rect(left.x, left.yMax - TransportH, left.width, TransportH);
            var canvas = new Rect(left.x, left.y, left.width, left.height - TransportH);

            HandleSplitter(vSplit);

            DrawToolbar(toolbar);
            DrawCanvasArea(canvas);
            DrawTransport(transport);
            GradeCanvas.DrawSplitter(vSplit, true);
            DrawParamPanel(right);
        }

        void HandleSplitter(Rect vSplit)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && vSplit.Contains(e.mousePosition))
            { _draggingSplit = true; e.Use(); }
            else if (e.type == EventType.MouseDrag && _draggingSplit)
            { _rightPanelW -= e.delta.x; e.Use(); Repaint(); }
            else if (e.type == EventType.MouseUp && _draggingSplit)
            { _draggingSplit = false; EditorPrefs.SetFloat(PrefRightW, _rightPanelW); e.Use(); }
        }

        void DrawToolbar(Rect r)
        {
            GUILayout.BeginArea(r, EditorStyles.toolbar);
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("打开视频…", EditorStyles.toolbarButton, GUILayout.Width(78f)))
                OpenVideoDialog();

            using (new EditorGUI.DisabledScope(!_prepared))
            {
                GUILayout.Space(8f);

                if (GUILayout.Button("|◀", EditorStyles.toolbarButton, GUILayout.Width(30f))) SeekTo(_inFrame);
                if (GUILayout.Button("◀", EditorStyles.toolbarButton, GUILayout.Width(26f))) StepBy(-1);

                bool play = GUILayout.Toggle(_playing, _playing ? "❚❚" : "▶",
                                             EditorStyles.toolbarButton, GUILayout.Width(34f));
                if (play != _playing)
                {
                    _playing = play;
                    _lastTick = EditorApplication.timeSinceStartup;
                    _playAccum = 0.0;
                }

                if (GUILayout.Button("▶", EditorStyles.toolbarButton, GUILayout.Width(26f))) StepBy(1);
                if (GUILayout.Button("▶|", EditorStyles.toolbarButton, GUILayout.Width(30f))) SeekTo(_outFrame);

                GUILayout.Space(10f);

                bool bypass = GUILayout.Toggle(_bypass, "原图对比", EditorStyles.toolbarButton, GUILayout.Width(64f));
                if (bypass != _bypass) { _bypass = bypass; _dirty = true; }

                bool split = GUILayout.Toggle(_splitCompare, "分屏", EditorStyles.toolbarButton, GUILayout.Width(44f));
                if (split != _splitCompare) { _splitCompare = split; _dirty = true; }

                if (_splitCompare)
                {
                    float pos = GUILayout.HorizontalSlider(_splitPosition, 0f, 1f, GUILayout.Width(90f));
                    if (!Mathf.Approximately(pos, _splitPosition)) { _splitPosition = pos; _dirty = true; }
                }

                GUILayout.Space(10f);
                if (GUILayout.Button("适应", EditorStyles.toolbarButton, GUILayout.Width(40f)))
                    _canvas.FitPending = true;
                if (GUILayout.Button("100%", EditorStyles.toolbarButton, GUILayout.Width(46f)))
                    _canvas.SetZoom(1f);
            }

            GUILayout.FlexibleSpace();

            if (_prepared)
                GUILayout.Label($"{Path.GetFileName(_path)}    {_srcW}×{_srcH}    {_fps:0.###} fps    " +
                                $"{_frameCount} 帧    [{(_decoder == Decoder.Ffmpeg ? "ffmpeg 抽帧" : "VideoPlayer")}]",
                                EditorStyles.miniLabel);
            else if (!string.IsNullOrEmpty(_loadError))
                GUILayout.Label("载入失败：" + _loadError, EditorStyles.miniLabel);
            else if (!string.IsNullOrEmpty(_path))
                GUILayout.Label("正在准备…", EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(!_prepared))
            {
                if (GUILayout.Button("胶片化", EditorStyles.toolbarButton, GUILayout.Width(52f)))
                {
                    Undo.RecordObject(this, "胶片化预设");
                    _settings.ApplyFilmLook();
                    _dirty = true;
                }
                if (GUILayout.Button("重置参数", EditorStyles.toolbarButton, GUILayout.Width(64f)))
                {
                    Undo.RecordObject(this, "重置参数");
                    _settings.Reset();
                    _dirty = true;
                }
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_path) || !FfmpegTool.Available))
            {
                var d = (Decoder)EditorGUILayout.EnumPopup(_decoder, EditorStyles.toolbarPopup, GUILayout.Width(96f));
                if (d != _decoder) SwitchDecoder(d);
            }

            EditorGUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void DrawCanvasArea(Rect r)
        {
            if (!_prepared)
            {
                EditorGUI.DrawRect(r, new Color(0.13f, 0.14f, 0.16f));
                string msg = !string.IsNullOrEmpty(_loadError)
                    ? "载入失败：" + _loadError
                    : "把视频拖进来，或用左上角「打开视频」";
                EditorGUI.LabelField(new Rect(r.x, r.center.y - 20f, r.width, 40f), msg,
                    new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 13 });
                HandleDragAndDrop(r);
                return;
            }

            if (_canvas.HandleInput(r)) Repaint();
            if (_canvas.ConsumeCompareChanged()) _dirty = true;

            // 裁剪会改变输出尺寸，画布按变换后的来摆
            _settings.OutputSize(_srcW, _srcH, out int ow, out int oh);
            _canvas.Draw(r, _preview, ow, oh);

            HandleDragAndDrop(r);
        }

        void HandleDragAndDrop(Rect r)
        {
            var e = Event.current;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
            if (!r.Contains(e.mousePosition)) return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type != EventType.DragPerform) return;

            DragAndDrop.AcceptDrag();
            foreach (var p in DragAndDrop.paths)
            {
                if (!IsVideo(p)) continue;
                LoadVideo(p);
                break;      // 一次只看一个
            }
            e.Use();
        }

        static bool IsVideo(string path)
        {
            switch (Path.GetExtension(path ?? "").ToLowerInvariant())
            {
                case ".mp4": case ".mov": case ".m4v":
                case ".webm": case ".avi": case ".mkv": return true;
                default: return false;
            }
        }

        // ---------------- 时间轴 ----------------

        void DrawTransport(Rect r)
        {
            EditorGUI.DrawRect(r, new Color(0.19f, 0.19f, 0.21f));
            if (!_prepared) return;

            var bar = new Rect(r.x + 12f, r.y + 10f, r.width - 24f, 18f);
            DrawScrubber(bar);

            var row = new Rect(r.x + 12f, r.y + 34f, r.width - 24f, 18f);
            GUILayout.BeginArea(row);
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(Timecode(_frame), EditorStyles.miniLabel, GUILayout.Width(90f));

            EditorGUI.BeginChangeCheck();
            long f = (long)EditorGUILayout.LongField(_frame, GUILayout.Width(70f));
            if (EditorGUI.EndChangeCheck()) SeekTo(f);

            GUILayout.Label("/ " + (_frameCount - 1), EditorStyles.miniLabel, GUILayout.Width(60f));

            GUILayout.Space(12f);
            if (GUILayout.Button("设入点", EditorStyles.miniButton, GUILayout.Width(56f)))
            { _inFrame = Math.Min(_frame, _outFrame); Repaint(); }
            if (GUILayout.Button("设出点", EditorStyles.miniButton, GUILayout.Width(56f)))
            { _outFrame = Math.Max(_frame, _inFrame); Repaint(); }
            if (GUILayout.Button("整段", EditorStyles.miniButton, GUILayout.Width(44f)))
            { _inFrame = 0; _outFrame = _frameCount - 1; Repaint(); }

            GUILayout.Label($"区间 {_inFrame} – {_outFrame}（{_outFrame - _inFrame + 1} 帧，{Timecode(_outFrame - _inFrame + 1)}）",
                            EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void DrawScrubber(Rect bar)
        {
            EditorGUI.DrawRect(bar, new Color(0.11f, 0.11f, 0.13f));

            float Frac(long f) => _frameCount > 1 ? Mathf.Clamp01(f / (float)(_frameCount - 1)) : 0f;

            // 入出点之间高亮，一眼看出要导出哪一段
            var inX = bar.x + Frac(_inFrame) * bar.width;
            var outX = bar.x + Frac(_outFrame) * bar.width;
            EditorGUI.DrawRect(new Rect(inX, bar.y, Mathf.Max(1f, outX - inX), bar.height),
                               new Color(0.30f, 0.45f, 0.62f, 0.55f));

            // 播放头
            float px = bar.x + Frac(_frame) * bar.width;
            EditorGUI.DrawRect(new Rect(px - 1f, bar.y - 2f, 2f, bar.height + 4f), new Color(1f, 0.85f, 0.3f));

            var e = Event.current;
            var grab = new Rect(bar.x, bar.y - 4f, bar.width, bar.height + 8f);
            EditorGUIUtility.AddCursorRect(grab, MouseCursor.SlideArrow);

            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) &&
                e.button == 0 && grab.Contains(e.mousePosition))
            {
                float t = Mathf.Clamp01((e.mousePosition.x - bar.x) / Mathf.Max(bar.width, 1f));
                _playing = false;
                SeekTo((long)Math.Round(t * (_frameCount - 1)));
                e.Use();
            }
        }

        string Timecode(long frames)
        {
            double sec = frames / Math.Max(_fps, 1.0);
            var ts = TimeSpan.FromSeconds(sec);
            long ff = frames % Math.Max(1, (long)Math.Round(_fps));
            return $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}:{ff:00}";
        }

        void SeekTo(long frame)
        {
            if (!_prepared) return;
            if (_frameCount > 0) frame = Math.Max(0, Math.Min(frame, _frameCount - 1));
            _frame = frame;

            // ffmpeg 抽一帧要起一个进程，几十到几百毫秒。这个函数会被时间轴的
            // MouseDrag 调到，也就是在 OnGUI 里——同步等在这儿界面会整个僵住，
            // 所以只记个待办，真正抽帧放到 update 里
            if (_decoder == Decoder.Ffmpeg) _cpuSeekPending = true;
            else if (_player != null) _player.frame = frame;

            _dirty = true;
            Repaint();
        }

        void GrabCpuFrame()
        {
            if (_cpuFrame == null || _cpuBuf == null) return;

            // 取帧中心的时刻而不是边界，免得浮点误差把定位甩到隔壁帧
            double t = (_frame + 0.5) / Math.Max(_fps, 1.0);
            if (!FfmpegTool.GrabFrame(_path, t, _srcW, _srcH, _cpuBuf)) return;

            _cpuFrame.LoadRawTextureData(_cpuBuf);
            _cpuFrame.Apply(false, false);
            _dirty = true;
        }

        void StepBy(int delta)
        {
            if (!_prepared) return;
            _playing = false;
            if (delta == 1 && _decoder == Decoder.VideoPlayer && _player != null) _player.StepForward();
            else SeekTo(_frame + delta);
        }

        // ---------------- 参数栏 ----------------

        void DrawParamPanel(Rect r)
        {
            EditorGUI.DrawRect(r, new Color(0.22f, 0.22f, 0.24f));
            GUILayout.BeginArea(new Rect(r.x + 4f, r.y + 2f, r.width - 8f, r.height - 4f));

            float prevLabel = EditorGUIUtility.labelWidth;
            bool prevWide = EditorGUIUtility.wideMode;
            EditorGUIUtility.labelWidth = Mathf.Clamp((r.width - 8f) * 0.46f, 84f, 220f);
            EditorGUIUtility.wideMode = true;

            _paramScroll = EditorGUILayout.BeginScrollView(_paramScroll);

            DrawLutBar();
            EditorGUILayout.Space(8f);
            DrawExportBar();
            EditorGUILayout.Space(6f);

            EditorGUI.BeginChangeCheck();
            _gui.PreviewTexture = _preview;
            _gui.PanelWidth = r.width - 8f;
            _gui.SourceSize = new Vector2Int(Mathf.Max(1, _srcW), Mathf.Max(1, _srcH));
            _gui.Draw(_settings, this);
            // 转盘弹窗是跨帧的，改动落在 OnGUI 之外，BeginChangeCheck 捕捉不到
            if (EditorGUI.EndChangeCheck() | _gui.ConsumeExternalChange()) _dirty = true;

            EditorGUILayout.EndScrollView();

            EditorGUIUtility.labelWidth = prevLabel;
            EditorGUIUtility.wideMode = prevWide;
            GUILayout.EndArea();
        }

        void DrawLutBar()
        {
            EditorGUILayout.LabelField("LUT (.cube)", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("导入…")) _pendingAction = ImportLut;
            using (new EditorGUI.DisabledScope(_lut == null))
                if (GUILayout.Button("卸载"))
                {
                    if (_lut != null) { DestroyImmediate(_lut); _lut = null; }
                    _lutName = "";
                    _dirty = true;
                }
            EditorGUILayout.EndHorizontal();

            if (_lut == null) return;

            EditorGUILayout.LabelField($"已载入 {_lutName}（{_lut.width}³）", EditorStyles.miniLabel);
            EditorGUI.BeginChangeCheck();
            _lutAmount = EditorGUILayout.Slider("强度", _lutAmount, 0f, 1f);
            if (EditorGUI.EndChangeCheck()) _dirty = true;
        }

        void ImportLut()
        {
            string path = EditorUtility.OpenFilePanel("导入 .cube LUT", "", "cube");
            if (string.IsNullOrEmpty(path)) return;

            var tex = CubeLutIO.Load(path, out string err);
            if (tex == null) { Debug.LogError("[视频台] LUT 导入失败：" + err); return; }

            if (_lut != null) DestroyImmediate(_lut);
            _lut = tex;
            _lutName = Path.GetFileNameWithoutExtension(path);
            _lutAmount = 1f;
            _dirty = true;
            Debug.Log($"[视频台] 已导入 LUT：{_lutName}（{tex.width}³）");
        }

        void DrawExportBar()
        {
            EditorGUILayout.LabelField("导出", EditorStyles.boldLabel);

            if (!FfmpegTool.Available)
            {
                EditorGUILayout.HelpBox("找不到 ffmpeg。导出需要它——解码和编码都走它的管道。", MessageType.Warning);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("重新探测")) FfmpegTool.Rescan();
                if (GUILayout.Button("手动指定…"))
                {
                    string p = EditorUtility.OpenFilePanel("选择 ffmpeg 可执行文件", "", "exe");
                    if (!string.IsNullOrEmpty(p)) FfmpegTool.OverridePath = p;
                }
                EditorGUILayout.EndHorizontal();
                return;
            }

            EditorGUILayout.LabelField("ffmpeg", Path.GetFileName(FfmpegTool.Path), EditorStyles.miniLabel);

            _crf = EditorGUILayout.IntSlider(new GUIContent("质量 CRF", "越小越好越大，18 视觉无损，23 是 x264 默认"),
                                             _crf, 12, 32);
            _keepAudio = EditorGUILayout.Toggle(new GUIContent("保留原音轨", "从源文件直接复制，不重编码"), _keepAudio);

            _settings.OutputSize(Mathf.Max(1, _srcW), Mathf.Max(1, _srcH), out int ow, out int oh);
            EditorGUILayout.LabelField("输出", $"{ow} × {oh}    {_outFrame - _inFrame + 1} 帧");

            // H.264 的 yuv420p 要求宽高都是偶数，裁剪很容易裁出奇数来
            if ((ow & 1) != 0 || (oh & 1) != 0)
                EditorGUILayout.HelpBox("输出宽高必须都是偶数，H.264 的 4:2:0 色度采样才装得下。改一下裁剪尺寸。",
                                        MessageType.Warning);

            using (new EditorGUI.DisabledScope(!_prepared || _export != null || (ow & 1) != 0 || (oh & 1) != 0))
            {
                if (GUILayout.Button("导出这一段…"))
                    _pendingAction = BeginExport;
            }

            if (_export != null)
            {
                EditorGUILayout.LabelField("进度", $"{_export.done} / {_export.total} 帧");
                if (GUILayout.Button("取消导出")) AbortExport("用户取消");
            }
        }

        // ---------------- 导出 ----------------

        class Export
        {
            public Process decoder, encoder;
            public Texture2D srcTex;
            public Texture2D readback;
            public RenderTexture target;
            public byte[] frameBuf;      // 解码进来的一帧
            public byte[] outBuf;        // 回读出去的一帧
            public int inW, inH, outW, outH;
            public long total, done;
            public string path;
            public double startTime;
        }

        void BeginExport()
        {
            if (!_prepared || _export != null) return;

            string outPath = EditorUtility.SaveFilePanel("导出视频", "",
                Path.GetFileNameWithoutExtension(_path) + "_graded", "mp4");
            if (string.IsNullOrEmpty(outPath)) return;

            // 导到源文件自己身上会一边读一边写，必炸
            if (Path.GetFullPath(outPath).Equals(Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("视频台", "不能导出到源文件本身。", "好");
                return;
            }

            _playing = false;
            _settings.OutputSize(_srcW, _srcH, out int ow, out int oh);

            double inSec = _inFrame / Math.Max(_fps, 1.0);
            double durSec = (_outFrame - _inFrame + 1) / Math.Max(_fps, 1.0);

            var ex = new Export
            {
                inW = _srcW, inH = _srcH, outW = ow, outH = oh,
                total = _outFrame - _inFrame + 1,
                path = outPath,
                startTime = EditorApplication.timeSinceStartup,
            };

            try
            {
                ex.decoder = FfmpegTool.StartDecoder(_path, inSec, durSec);
                ex.encoder = FfmpegTool.StartEncoder(outPath, ow, oh, _fps, _crf,
                                                     _keepAudio ? _path : null, inSec, durSec);
                // stderr 必须有人读，否则管道写满了 ffmpeg 会卡死在那儿
                ex.decoder.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.LogWarning("[视频台/解码] " + e.Data); };
                ex.encoder.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.LogWarning("[视频台/编码] " + e.Data); };
                ex.decoder.BeginErrorReadLine();
                ex.encoder.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                Debug.LogError("[视频台] 启动 ffmpeg 失败：" + e.Message);
                return;
            }

            ex.frameBuf = new byte[(long)ex.inW * ex.inH * 4];
            ex.outBuf = new byte[(long)ex.outW * ex.outH * 4];
            ex.srcTex = new Texture2D(ex.inW, ex.inH, TextureFormat.RGBA32, false, false)
            { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            ex.readback = new Texture2D(ex.outW, ex.outH, TextureFormat.RGBA32, false, false)
            { hideFlags = HideFlags.HideAndDontSave };
            ex.target = new RenderTexture(ex.outW, ex.outH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            { hideFlags = HideFlags.HideAndDontSave };
            ex.target.Create();

            _export = ex;
            Repaint();
        }

        /// <summary>
        /// 一次 update 里连续处理若干帧，但给自己设个时间预算，
        /// 到点就交还控制权——不然编辑器整个卡住，进度条也刷不出来。
        /// </summary>
        void PumpExport()
        {
            var ex = _export;
            var r = Renderer;
            if (ex == null || r == null) { AbortExport("渲染器不可用"); return; }

            double budgetEnd = EditorApplication.timeSinceStartup + 0.20;

            while (EditorApplication.timeSinceStartup < budgetEnd)
            {
                if (ex.done >= ex.total) { FinishExport(); return; }

                if (!ReadFully(ex.decoder, ex.frameBuf))
                {
                    // 解码提前结束：源比预期的短，把已经写进去的收好就行
                    FinishExport();
                    return;
                }

                ex.srcTex.LoadRawTextureData(ex.frameBuf);
                ex.srcTex.Apply(false, false);

                r.GrainSeed = (_inFrame + ex.done) * 0.017f;
                r.Render(ex.srcTex, ex.target, _settings, new VideoGradeRenderer.Options
                {
                    lut = _lut,
                    lutAmount = _lutAmount,
                });

                var prev = RenderTexture.active;
                RenderTexture.active = ex.target;
                ex.readback.ReadPixels(new Rect(0f, 0f, ex.outW, ex.outH), 0, 0, false);
                ex.readback.Apply(false, false);
                RenderTexture.active = prev;

                NativeArray<byte> na = ex.readback.GetRawTextureData<byte>();
                na.CopyTo(ex.outBuf);

                try { ex.encoder.StandardInput.BaseStream.Write(ex.outBuf, 0, ex.outBuf.Length); }
                catch (Exception e) { AbortExport("写入编码器失败：" + e.Message); return; }

                ex.done++;
            }

            float p = ex.total > 0 ? (float)ex.done / ex.total : 1f;
            double elapsed = EditorApplication.timeSinceStartup - ex.startTime;
            double eta = p > 0.001 ? elapsed / p - elapsed : 0;
            if (EditorUtility.DisplayCancelableProgressBar(
                    "导出视频", $"{ex.done} / {ex.total} 帧，剩余约 {eta:0} 秒", p))
                AbortExport("用户取消");

            Repaint();
        }

        /// <summary>管道一次不一定给满一帧，要循环读到够。</summary>
        static bool ReadFully(Process p, byte[] buf)
        {
            var s = p.StandardOutput.BaseStream;
            int got = 0;
            while (got < buf.Length)
            {
                int n;
                try { n = s.Read(buf, got, buf.Length - got); }
                catch { return false; }
                if (n <= 0) return false;      // EOF
                got += n;
            }
            return true;
        }

        void FinishExport()
        {
            var ex = _export;
            if (ex == null) return;

            string path = ex.path;
            long done = ex.done;

            try { ex.encoder.StandardInput.BaseStream.Flush(); ex.encoder.StandardInput.Close(); } catch { }
            try { ex.encoder.WaitForExit(120000); } catch { }
            int code = -1;
            try { code = ex.encoder.ExitCode; } catch { }

            CleanupExport();
            EditorUtility.ClearProgressBar();

            if (code == 0 && File.Exists(path))
            {
                Debug.Log($"[视频台] 已导出 {done} 帧：{path}");
                if (!Application.isBatchMode) EditorUtility.RevealInFinder(path);
            }
            else
            {
                Debug.LogError($"[视频台] 导出失败，ffmpeg 退出码 {code}。看上面的 [视频台/编码] 日志。");
            }
            Repaint();
        }

        void AbortExport(string why)
        {
            if (_export == null) return;
            Debug.LogWarning("[视频台] 导出中止：" + why);
            try { _export.encoder?.StandardInput?.Close(); } catch { }
            try { if (_export.decoder != null && !_export.decoder.HasExited) _export.decoder.Kill(); } catch { }
            try { if (_export.encoder != null && !_export.encoder.HasExited) _export.encoder.Kill(); } catch { }
            CleanupExport();
            EditorUtility.ClearProgressBar();
        }

        void CleanupExport()
        {
            var ex = _export;
            _export = null;
            if (ex == null) return;

            try { ex.decoder?.Dispose(); } catch { }
            try { ex.encoder?.Dispose(); } catch { }
            if (ex.srcTex != null) DestroyImmediate(ex.srcTex);
            if (ex.readback != null) DestroyImmediate(ex.readback);
            if (ex.target != null) { ex.target.Release(); DestroyImmediate(ex.target); }
        }
    }
}
