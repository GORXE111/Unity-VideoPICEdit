using System;
using System.Diagnostics;
using System.IO;
using Love.Video;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;
using Debug = UnityEngine.Debug;
using Love.Tools;

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

        const float ToolbarH = GradeSkin.ToolbarH;
        const float SplitterW = GradeSkin.SplitterW;
        const float TransportH = 64f;
        const float DefaultPanelW = 360f;

        const string PrefRightW = "VideoStation.rightPanelW";
        const string PrefPanelOn = "VideoStation.panelVisible";
        const string PrefTransOn = "VideoStation.transportVisible";

        [MenuItem("Tools/影视游戏/视频台", false, 7)]
        public static void Open()
        {
            var w = GetWindow<VideoStationWindow>();
            w.titleContent = new GUIContent("视频台");
            // 定得小一点，左右分屏也塞得下。工具栏会自己收纳，不怕窄
            w.minSize = new Vector2(520f, 360f);
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
        public enum Decoder { Ffmpeg, VideoPlayer }

        /// <summary>
        /// 默认 ffmpeg。除了快（实测常驻流 1080p 只要 7.5ms/帧），
        /// 更重要的是它和导出用的是同一个解码器，预览的颜色和成片一定对得上——
        /// WindowsMediaFoundation 遇到 color primaries 标记缺失的文件会自己兜底，
        /// 日志里那句 "may result in color shift" 就是在说这个。
        /// </summary>
        [SerializeField] Decoder _decoder = Decoder.Ffmpeg;

        GameObject _host;
        VideoPlayer _player;
        RenderTexture _source;     // VideoPlayer 解出来的原始帧
        Texture2D _cpuFrame;       // ffmpeg 解出来的原始帧
        byte[] _cpuBuf;
        VideoFrameStream _stream;  // 常驻解码进程
        double _prepareDeadline;

        // 定位节流。时间轴一次拖拽会甩出几十个 MouseDrag，
        // 每个都真去定位一次的话，请求会越堆越多，界面就僵住了。
        // 所以只记「最后想去哪一帧」，每拍最多兑现一次
        long _wantFrame = -1;
        long _playerSeekedTo = -1;
        double _lastFetch;

        [SerializeField] int _previewDiv = 1;   // 预览降采样：1 / 2 / 4

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
        readonly MaskOverlay _maskOverlay = new MaskOverlay();
        readonly GradeSettingsGUI _gui = new GradeSettingsGUI();
        Vector2 _paramScroll;

        float _rightPanelW = DefaultPanelW;
        bool _draggingSplit;
        bool _panelVisible = true;
        bool _transportVisible = true;
        double _lastSplitClick;
        readonly GradeToolbar _tb = new GradeToolbar();

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
            _rightPanelW = EditorPrefs.GetFloat(PrefRightW, DefaultPanelW);
            _panelVisible = EditorPrefs.GetBool(PrefPanelOn, true);
            _transportVisible = EditorPrefs.GetBool(PrefTransOn, true);
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
            SaveLayout();
        }

        void SaveLayout()
        {
            EditorPrefs.SetFloat(PrefRightW, _rightPanelW);
            EditorPrefs.SetBool(PrefPanelOn, _panelVisible);
            EditorPrefs.SetBool(PrefTransOn, _transportVisible);
        }

        void ReleaseAll()
        {
            if (_player != null) { _player.prepareCompleted -= OnPrepared; _player.errorReceived -= OnPlayerError; }
            if (_host != null) DestroyImmediate(_host);
            _host = null; _player = null;

            ReleaseRt(ref _source);
            ReleaseRt(ref _preview);
            if (_cpuFrame != null) { DestroyImmediate(_cpuFrame); _cpuFrame = null; }
            _cpuBuf = null;
            _stream?.Dispose();
            _stream = null;

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
            // sendFrameReadyEvents 官方文档就写着「会带来显著的 CPU 开销」——
            // 它要求解码线程每帧和主线程同步一次。改成在 update 里轮询 player.frame，
            // 拿到的信息一样，代价小得多
            _player.sendFrameReadyEvents = false;

            _player.prepareCompleted += OnPrepared;
            _player.errorReceived += OnPlayerError;

            _player.Prepare();
            // VideoPlayer 在编辑器模式下不一定伺候。给它几秒，到点还没好就换 ffmpeg
            _prepareDeadline = EditorApplication.timeSinceStartup + 6.0;
            Repaint();
        }

        /// <summary>预览用的画面尺寸。降采样只影响预览，导出永远是全分辨率。</summary>
        void PreviewSize(out int w, out int h)
        {
            int d = Mathf.Clamp(_previewDiv, 1, 4);
            // 保持偶数，缩放滤镜和拜耳无关但奇数尺寸容易在别处出岔子
            w = Mathf.Max(2, (_srcW / d) & ~1);
            h = Mathf.Max(2, (_srcH / d) & ~1);
        }

        void SetupCpuPath()
        {
            _stream?.Dispose();
            if (_cpuFrame != null) DestroyImmediate(_cpuFrame);

            PreviewSize(out int w, out int h);
            _cpuFrame = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            { name = "VideoStationCpuFrame", hideFlags = HideFlags.HideAndDontSave,
              wrapMode = TextureWrapMode.Clamp };
            _cpuBuf = new byte[(long)w * h * 4];
            _stream = new VideoFrameStream(_path, _fps, w, h);
        }

        /// <summary>
        /// 换预览分辨率。ffmpeg 那条路要重建解码流（缩放是在管道之前做的，
        /// 省的是传输量而不只是 GPU），VideoPlayer 那条路只影响调色时的目标尺寸。
        /// </summary>
        void SetPreviewDiv(int div)
        {
            div = Mathf.Clamp(div, 1, 4);
            if (div == _previewDiv) return;
            _previewDiv = div;

            if (_decoder == Decoder.Ffmpeg && _prepared)
            {
                SetupCpuPath();
                _canvas.FitPending = true;
                _wantFrame = _frame;
            }
            _dirty = true;
            Repaint();
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

            if (!_prepared) return;

            bool repaint = false;

            // ---- 播放：只把「想去哪一帧」往前挪，真正取帧统一在下面做 ----
            if (_playing)
            {
                double now = EditorApplication.timeSinceStartup;
                _playAccum += Mathf.Clamp((float)(now - _lastTick), 0f, 0.5f);
                _lastTick = now;

                double step = 1.0 / Math.Max(_fps, 1.0);
                if (_playAccum >= step)
                {
                    // 一拍只前进一帧，跟不上就把欠的时间丢掉。
                    // 原来那版会"补齐"落后的帧数，结果是越慢越要多解码，
                    // 一旦跟不上就再也追不回来——正反馈直接把界面拖死
                    _playAccum = 0.0;

                    long last = Math.Min(_frameCount - 1, _outFrame);
                    if (_frame >= last) _playing = false;
                    else if (_decoder == Decoder.Ffmpeg) _wantFrame = _frame + 1;
                    else { _player?.StepForward(); _playerSeekedTo = -1; }
                }
                repaint = true;
            }

            // ---- 取帧：一拍最多一次，且永远只兑现最后那个请求 ----
            //
            // 拖时间轴时每次都要重开解码进程，实测约 130ms，而这是在主线程上等。
            // 不留间隔的话编辑器每一拍都被占满，输入和重绘都挤不进来。
            // 播放是顺序读（约 6ms），不需要节流
            bool throttled = !_playing && EditorApplication.timeSinceStartup - _lastFetch < 0.12;
            if (_wantFrame >= 0 && !throttled)
            {
                long want = _wantFrame;
                _wantFrame = -1;
                _lastFetch = EditorApplication.timeSinceStartup;
                if (Fetch(want)) { _frame = want; _dirty = true; repaint = true; }
            }
            else if (_wantFrame >= 0) repaint = true;   // 还欠着一次，下一拍再来

            // ---- VideoPlayer 是异步解码的，实际走到哪一帧只能轮询 ----
            if (_decoder == Decoder.VideoPlayer && _player != null)
            {
                long f = _player.frame;
                if (f >= 0 && f != _frame) { _frame = f; _dirty = true; repaint = true; }
            }

            if (_dirty && RenderPreview()) repaint = true;

            // 只在真有变化时重绘。原来那版无条件 Repaint，一旦 RenderPreview
            // 提前返回（还没准备好、渲染器拿不到），_dirty 清不掉，
            // 就变成每拍都重绘的空转，整个编辑器跟着发涩
            if (repaint) Repaint();
        }

        /// <summary>把某一帧取到贴图里。返回 false 表示这次没取到（到片尾或解码出错）。</summary>
        bool Fetch(long want)
        {
            if (_decoder == Decoder.Ffmpeg)
            {
                if (_stream == null || _cpuFrame == null || _cpuBuf == null) return false;
                if (!_stream.TryGet(want, _cpuBuf)) return false;
                _cpuFrame.LoadRawTextureData(_cpuBuf);
                _cpuFrame.Apply(false, false);
                return true;
            }

            if (_player == null) return false;
            // 同一帧不要重复定位。VideoPlayer 的 seek 会冲掉解码器状态，很贵
            if (want != _playerSeekedTo) { _player.frame = want; _playerSeekedTo = want; }
            return true;
        }

        bool RenderPreview()
        {
            var src = FrameSource;
            if (!_prepared || src == null) return false;
            var r = Renderer;
            if (r == null) return false;

            _settings.OutputSize(src.width, src.height, out int ow, out int oh);

            // ffmpeg 那条路在解码时就已经缩过了（缩在管道之前，省的是传输量），
            // 这里只需要处理 VideoPlayer 那条路
            if (_decoder == Decoder.VideoPlayer)
            {
                int div = Mathf.Clamp(_previewDiv, 1, 4);
                ow = Mathf.Max(2, ow / div);
                oh = Mathf.Max(2, oh / div);
            }
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
            return true;
        }

        // ---------------- 布局 ----------------

        void OnGUI()
        {
            if (Renderer == null)
            {
                EditorGUILayout.HelpBox($"找不到调色材质：{MaterialPath}\n先跑一次「单步：生成调色材质」。", MessageType.Error);
                return;
            }

            float panelW = _panelVisible
                ? Mathf.Clamp(_rightPanelW, 240f, Mathf.Max(240f, position.width - 220f))
                : GradeSkin.CollapsedW;

            float bodyY = ToolbarH;
            float bodyH = position.height - ToolbarH - GradeSkin.StatusH;

            var toolbar = new Rect(0f, 0f, position.width, ToolbarH);
            var right   = new Rect(position.width - panelW, bodyY, panelW, bodyH);
            var vSplit  = new Rect(right.x - SplitterW, bodyY, SplitterW, bodyH);
            var left    = new Rect(0f, bodyY, vSplit.x, bodyH);
            var status  = new Rect(0f, position.height - GradeSkin.StatusH, position.width, GradeSkin.StatusH);

            // 时间轴收起时连同它的分隔条一起让位给画布
            float transH = _transportVisible ? Mathf.Min(TransportH, left.height * 0.6f) : 0f;
            var transport = new Rect(left.x, left.yMax - transH, left.width, transH);
            var hSplit    = new Rect(left.x, transport.y - SplitterW, left.width, SplitterW);
            var canvas    = new Rect(left.x, left.y, left.width,
                                     left.height - transH - (_transportVisible ? SplitterW : 0f));

            // 分隔条的输入要先处理，否则会被下面的面板抢走
            HandleSplitter(vSplit);

            DrawToolbar(toolbar);
            DrawCanvasArea(canvas);

            if (_transportVisible)
            {
                GradeSkin.DrawSplitter(hSplit, false);
                DrawTransport(transport);
            }

            GradeSkin.DrawSplitter(vSplit, true);

            if (_panelVisible) DrawParamPanel(right);
            else DrawCollapsedPanel(right);

            DrawStatusBar(status);
        }

        void HandleSplitter(Rect vSplit)
        {
            var e = Event.current;

            if (e.type == EventType.MouseDown && e.button == 0 && vSplit.Contains(e.mousePosition))
            {
                // 双击复位到默认宽度。拖歪了想回去，总不能靠手感对齐
                double now = EditorApplication.timeSinceStartup;
                if (now - _lastSplitClick < 0.35)
                {
                    _rightPanelW = DefaultPanelW;
                    _panelVisible = true;
                    SaveLayout();
                }
                _lastSplitClick = now;

                _draggingSplit = _panelVisible;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _draggingSplit)
            { _rightPanelW -= e.delta.x; e.Use(); Repaint(); }
            else if (e.type == EventType.MouseUp && _draggingSplit)
            { _draggingSplit = false; SaveLayout(); e.Use(); }
        }

        /// <summary>参数栏收起后剩下的那条竖边，点一下再展开。</summary>
        void DrawCollapsedPanel(Rect r)
        {
            EditorGUI.DrawRect(r, GradeSkin.Panel);
            if (GUI.Button(r, new GUIContent("◀", "展开参数栏"), EditorStyles.label))
            {
                _panelVisible = true;
                SaveLayout();
            }
        }

        void DrawToolbar(Rect r)
        {
            _tb.Begin(r);
            bool on = _prepared;

            _tb.Button("打开视频…", 78f, OpenVideoDialog, priority: 100);
            _tb.Space(6f);

            _tb.Button("|◀", 28f, () => SeekTo(_inFrame), priority: 88, disabled: !on, tooltip: "回到入点");
            _tb.Button("◀", 24f, () => StepBy(-1), priority: 92, disabled: !on, tooltip: "上一帧");
            _tb.Toggle(_playing, _playing ? "❚❚" : "▶", 32f, v =>
            {
                _playing = v;
                _lastTick = EditorApplication.timeSinceStartup;
                _playAccum = 0.0;
            }, priority: 95, disabled: !on, tooltip: "播放 / 暂停");
            _tb.Button("▶", 24f, () => StepBy(1), priority: 92, disabled: !on, tooltip: "下一帧");
            _tb.Button("▶|", 28f, () => SeekTo(_outFrame), priority: 88, disabled: !on, tooltip: "跳到出点");

            _tb.Space(8f);

            _tb.Toggle(_bypass, "原图对比", 62f, v => { _bypass = v; _dirty = true; },
                       priority: 80, disabled: !on, tooltip: "按住反斜杠也可以临时看原图");
            _tb.Toggle(_splitCompare, "分屏", 42f, v => { _splitCompare = v; _dirty = true; },
                       priority: 72, disabled: !on);
            if (_splitCompare)
                // 优先级压在「分屏」开关之上：撤退是按优先级从低到高来的，
                // 这样绝不会出现"开关还在、调位置的滑条却没了"
                _tb.Slider(_splitPosition, 0f, 1f, 80f,
                           v => { _splitPosition = v; _dirty = true; }, priority: 73, disabled: !on);

            _tb.Space(8f);

            _tb.Button("适应", 38f, () => _canvas.FitPending = true, priority: 64, disabled: !on);
            _tb.Button("100%", 44f, () => _canvas.SetZoom(1f), priority: 60, disabled: !on);

            _tb.Space(6f);

            // 预览分辨率。降下来最省的是解码和管道传输，不是 GPU
            int[] divs = { 1, 2, 4 };
            int cur = Mathf.Max(0, Array.IndexOf(divs, Mathf.Clamp(_previewDiv, 1, 4)));
            _tb.Popup(cur, new[] { "预览 全分辨率", "预览 1/2", "预览 1/4" }, 96f,
                      i2 => SetPreviewDiv(divs[i2]), priority: 56, disabled: !on, label: "预览分辨率");

            _tb.Popup((int)_decoder, new[] { "ffmpeg", "VideoPlayer" }, 84f,
                      i2 => SwitchDecoder((Decoder)i2), priority: 52, label: "解码器",
                      disabled: string.IsNullOrEmpty(_path) || !FfmpegTool.Available);

            _tb.Flex();

            _tb.Button("胶片化", 50f, () =>
            {
                Undo.RecordObject(this, "胶片化预设");
                _settings.ApplyFilmLook();
                _dirty = true;
            }, priority: 30, disabled: !on);

            _tb.Button("重置参数", 62f, () =>
            {
                Undo.RecordObject(this, "重置参数");
                _settings.Reset();
                _dirty = true;
            }, priority: 30, disabled: !on);

            _tb.Space(6f);

            _tb.Toggle(_transportVisible, "时间轴", 50f, v => { _transportVisible = v; SaveLayout(); },
                       priority: 44);
            _tb.Toggle(_panelVisible, "参数栏", 50f, v => { _panelVisible = v; SaveLayout(); },
                       priority: 98, tooltip: "收起参数栏，画布占满窗口");

            _tb.End();
        }

        /// <summary>
        /// 底部状态栏。文件名、尺寸、帧率这些原来挤在工具栏中间，
        /// 一到窄窗口就把按钮顶掉——信息该待在信息该待的地方。
        /// </summary>
        void DrawStatusBar(Rect r)
        {
            EditorGUI.DrawRect(r, GradeSkin.Bar);
            GradeSkin.Line(r.x, r.y, r.width, 1f, GradeSkin.Trough);

            if (!_prepared)
            {
                string msg = !string.IsNullOrEmpty(_loadError) ? "载入失败：" + _loadError
                           : !string.IsNullOrEmpty(_path) ? "正在准备…"
                           : "未载入视频";
                GUI.Label(r, msg, GradeSkin.StatusDim);
                return;
            }

            float x = r.x;
            void Cell(string text, float w, GUIStyle st)
            {
                GUI.Label(new Rect(x, r.y, w, r.height), text, st);
                x += w;
                GradeSkin.Line(x, r.y + 3f, 1f, r.height - 6f, GradeSkin.Trough);
            }

            Cell(Path.GetFileName(_path), Mathf.Min(240f, r.width * 0.3f), GradeSkin.Status);
            Cell($"{_srcW}×{_srcH}  {_fps:0.###}fps", 150f, GradeSkin.StatusDim);
            Cell($"帧 {_frame} / {_frameCount - 1}   {Timecode(_frame)}", 190f, GradeSkin.Status);
            Cell($"缩放 {_canvas.Zoom * 100f:0}%", 84f, GradeSkin.StatusDim);
            Cell(_decoder == Decoder.Ffmpeg ? "ffmpeg" : "VideoPlayer", 84f, GradeSkin.StatusDim);
            if (_previewDiv > 1) Cell($"预览 1/{_previewDiv}", 76f, GradeSkin.StatusDim);
        }

        void DrawCanvasArea(Rect r)
        {
            if (!_prepared)
            {
                EditorGUI.DrawRect(r, GradeSkin.Canvas);
                string msg = !string.IsNullOrEmpty(_loadError)
                    ? "载入失败：" + _loadError
                    : "把视频拖进来，或用左上角「打开视频」";
                EditorGUI.LabelField(new Rect(r.x, r.center.y - 20f, r.width, 40f), msg,
                    new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 13 });
                HandleDragAndDrop(r);
                return;
            }

            if (_canvas.HandleInput(r, _maskOverlay.Dragging)) Repaint();
            if (_canvas.ConsumeCompareChanged()) _dirty = true;
            HandleTransportKeys();

            // 裁剪会改变输出尺寸，画布按变换后的来摆
            _settings.OutputSize(_srcW, _srcH, out int ow, out int oh);
            _canvas.Draw(r, _preview, ow, oh);

            var shape = _gui.Masks.EditingPart;
            _maskOverlay.Draw(r, _canvas.ImageRect, shape);
            _maskOverlay.HandleInput(_canvas.ImageRect, shape,
                                     s2 => Undo.RecordObject(this, s2), () => _dirty = true);

            HandleDragAndDrop(r);
        }

        /// <summary>方向键逐帧。空格已经被画布的抓手占了，那是和 PS 一致的约定。</summary>
        void HandleTransportKeys()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown || EditorGUIUtility.editingTextField) return;

            if (e.keyCode == KeyCode.LeftArrow) { StepBy(-1); e.Use(); }
            else if (e.keyCode == KeyCode.RightArrow) { StepBy(1); e.Use(); }
            else if (e.keyCode == KeyCode.Home) { SeekTo(_inFrame); e.Use(); }
            else if (e.keyCode == KeyCode.End) { SeekTo(_outFrame); e.Use(); }
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
            EditorGUI.DrawRect(r, GradeSkin.Bar);
            if (!_prepared) return;

            var bar = new Rect(r.x + 12f, r.y + 10f, r.width - 24f, 18f);
            DrawScrubber(bar);

            var row = new Rect(r.x + 12f, r.y + 34f, r.width - 24f, 20f);
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
            EditorGUI.DrawRect(bar, GradeSkin.Trough);

            float Frac(long f) => _frameCount > 1 ? Mathf.Clamp01(f / (float)(_frameCount - 1)) : 0f;

            // 入出点之间高亮，一眼看出要导出哪一段
            var inX = bar.x + Frac(_inFrame) * bar.width;
            var outX = bar.x + Frac(_outFrame) * bar.width;
            EditorGUI.DrawRect(new Rect(inX, bar.y, Mathf.Max(1f, outX - inX), bar.height),
                               GradeSkin.AccentDim);

            // 播放头
            float px = bar.x + Frac(_frame) * bar.width;
            EditorGUI.DrawRect(new Rect(px - 1f, bar.y - 2f, 2f, bar.height + 4f), GradeSkin.Playhead);

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

        /// <summary>
        /// 只登记「想去哪一帧」，不做任何解码。
        ///
        /// 这个函数会被时间轴的 MouseDrag 调到，也就是在 OnGUI 里。一次拖拽能甩出
        /// 几十个事件，每个都真去定位一次的话请求会越堆越多，界面就僵住了。
        /// 真正取帧在 update 里做，而且一拍最多一次、只兑现最后那个请求。
        /// </summary>
        void SeekTo(long frame)
        {
            if (!_prepared) return;
            if (_frameCount > 0) frame = Math.Max(0, Math.Min(frame, _frameCount - 1));
            _wantFrame = frame;
            _frame = frame;      // 播放头先动起来，画面等取到了再更新
            Repaint();
        }

        void StepBy(int delta)
        {
            if (!_prepared) return;
            _playing = false;
            SeekTo(_frame + delta);
        }

        // ---------------- 参数栏 ----------------

        void DrawParamPanel(Rect r)
        {
            EditorGUI.DrawRect(r, GradeSkin.Panel);
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

            // 预览降分辨率时，Bloom 半径、颗粒粗细、锐化这些跟像素尺寸挂钩的效果
            // 在预览里和成片是不一样的，得说清楚
            if (_previewDiv > 1)
                EditorGUILayout.HelpBox($"预览正在按 1/{_previewDiv} 渲染，导出仍是全分辨率。\n" +
                                        "Bloom 半径、颗粒粗细、锐化这些和像素尺寸挂钩的效果，预览会和成片有出入。",
                                        MessageType.Info);

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
