using System;
using System.Collections.Generic;
using System.IO;
using Love.Video;
using UnityEditor;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// 修图台。和调色台共用同一套后期管线（VideoGradeRenderer），
    /// 输入从视频换成静态图片，输出从屏幕换成文件。
    ///
    /// 布局是手算矩形而不是 GUILayout：左边大预览（可缩放平移）+ 底部胶片条 +
    /// 右边参数栏。窗口最大化之后就是一个修图软件的样子。
    ///
    /// 不依赖场景、不需要进 Play——渲染核心已经抽成普通类。
    /// </summary>
    public class PhotoGradeWindow : EditorWindow
    {
        [MenuItem("Tools/影视游戏/修图台", false, 6)]
        public static void Open()
        {
            var w = GetWindow<PhotoGradeWindow>("修图台");
            // 定得小一点，左右分屏也塞得下。工具栏会自己收纳，不怕窄
            w.minSize = new Vector2(520f, 360f);
            w.Show();
        }

        const string MaterialPath = "Assets/GameAssets/Materials/VideoGrade.mat";
        const float ToolbarH = GradeSkin.ToolbarH;
        const float DefaultPanelW = 360f;
        const float ThumbSize = 72f;
        const int ThumbPixels = 128;
        const float SplitterW = 5f;

        // 布局尺寸可拖拽调整，存 EditorPrefs 跨会话保留
        const string PrefRightW = "PhotoGrade.rightPanelW";
        const string PrefFilmH  = "PhotoGrade.filmH";
        const string PrefFilmOn = "PhotoGrade.filmVisible";
        const string PrefPanelOn = "PhotoGrade.panelVisible";

        float _rightPanelW = DefaultPanelW;
        float _filmH = 96f;
        bool _filmVisible = true;
        bool _panelVisible = true;
        double _lastSplitClick;
        readonly GradeToolbar _tb = new GradeToolbar();

        enum Splitter { None, Right, Film }
        Splitter _dragging = Splitter.None;


        [SerializeField] VideoGradeSettings _settings = new VideoGradeSettings();
        [SerializeField] bool _splitCompare;
        [SerializeField] float _splitPosition = 0.5f;
        [SerializeField] bool _bypass;

        // 图片库：排序、筛选、评级、多选都在里面。抽出去是因为那部分全是纯逻辑，能离线测
        readonly PhotoLibrary _lib = new PhotoLibrary();

        // 逐图的参数、修补、评级全存在这里，并且会落盘。
        //
        // 之前这些放在窗口的 Dictionary 字段里——Unity 序列化不了 Dictionary，
        // 于是改一行 C# 触发程序集重载、或者关掉窗口，几十张图的活就全没了。
        readonly PhotoEditStore _store = new PhotoEditStore();
        VideoGradeSettings _clipboard;
        [SerializeField] AutoTone.Options _autoOpt = AutoTone.Options.Default;

        // 上一次存快照之后参数有没有动过。没动过就别在覆盖性操作前再存一份，
        // 否则连点两次「重置」会攒出两份一模一样的
        bool _snapChanged;

        // 字体是资产引用，不能塞进 ExportPreset——那玩意要能存成 JSON。
        // 所以字体单独跟着窗口走，不属于导出预设的一部分
        [SerializeField] Font _wmFont;

        // 天空蒙版。漫延的结果按图缓存，贴图按「图 + 几何」缓存——
        // 拖裁剪框时只要重搬一次，不用重新漫延
        [SerializeField] SkyDetect.Options _skyOpt = SkyDetect.Options.Default;
        SkyDetect.Result _skyResult;
        string _skyDetected;
        Texture2D _skyTex;
        string _skyTexPath;
        int _skyTexGeo = int.MinValue;
        string _newSnapName = "";
        string _loadedPath;
        readonly List<string> _pendingImports = new List<string>();
        int _pendingSelect = -1;
        System.Action _pendingAction;
        Texture2D _full;            // 当前选中图的原图，只留一张

        VideoGradeRenderer _renderer;
        Material _materialCopy;
        RenderTexture _preview;
        bool _dirty = true;

        // 画布（棋盘底 / 缩放平移 / 硬裁剪 / PS 那套快捷键）和视频台共用一份实现。
        // 之前两个窗口各有一份，细看手感就是不一样的
        readonly GradeCanvas _canvas = new GradeCanvas();

        // 裁剪。开着的时候画布故意显示未裁剪的整幅——
        // 看不见要裁掉什么，就没法判断裁得对不对
        [SerializeField] bool _cropMode;
        int _cropDrag = -1;            // -1 没在拖 0..3 角 4..7 边 8 整体平移
        Vector2 _cropDragOrigin;
        Vector4 _cropAtDragStart;      // 按下那一刻的 x/y/w/h，全程以它为基准算增量

        // 白平衡吸管：点画面上一处本该是中性灰的地方
        bool _pickWb;

        // AI 主体蒙版。整块包在条件编译里，没装 Sentis 时修图台照常可用
#if LOVE_SENTIS
        AiMaskGenerator _maskGen;
        [SerializeField] int _maskModelIndex;
        Texture2D _maskRaw;           // 模型原始输出
        RenderTexture _maskRefined;   // 联合双边精修到全分辨率
        Material _maskRefineMat;
        [SerializeField] bool _maskRefine = true;
        [SerializeField] float _maskSigmaColor = 0.08f;
        string _maskStatus = "";

        // AI 降噪
        AiDenoiser _denoiser;
        [SerializeField] int _dnModelIndex;
        [SerializeField] float _dnStrength = 1f;
        RenderTexture _dnRaw;        // 模型出来的，不带强度
        RenderTexture _dnBlended;    // 和原图按强度混合过的，真正喂给管线的那张
        string _dnPath;              // _dnRaw 是哪张图的
        string _dnStatus = "";
        NoiseEstimate.Result _dnNoise;
        string _dnNoisePath;
#endif

        // 色卡标定：四角存在图片的 uv 空间（0~1），换图/缩放都不用重算
        [SerializeField] bool _chartMode;
        [SerializeField] Vector2[] _chartCorners = DefaultChartCorners();
        int _chartDragIndex = -1;
        string _chartStatus = "";

        static Vector2[] DefaultChartCorners() => new[]
        {
            new Vector2(0.30f, 0.65f), new Vector2(0.70f, 0.65f),
            new Vector2(0.70f, 0.35f), new Vector2(0.30f, 0.35f),
        };

        // 导入的 .cube LUT
        Texture3D _lut;
        [SerializeField] string _lutName = "";
        [SerializeField] float _lutAmount = 1f;

        // ---- 手绘蒙版 ----
        // 笔刷贴图进不了 JSON，所以由窗口持有，MaskPart 只存一个下标。
        // 尺寸按源图但封顶：蒙版不需要 6100 万像素，2048 的图 bilinear 放大反而边更柔
        const int BrushMax = 2048;

        // 存 Texture 而不是 RenderTexture：IList<T> 是不变的，
        // List<RenderTexture> 递不进 Options.brushes 那个 IList<Texture>
        readonly MaskOverlay _maskOverlay = new MaskOverlay();

        // ---- 污点修复 / 仿制图章 ----
        // 修补以一串 RepairSpot 存着、每次从原图重放，所以 _full 始终是干净的原图
        readonly ImageRepair _repair = new ImageRepair();
        [SerializeField] bool _repairMode;
        [SerializeField] bool _repairClone;        // true = 仿制图章（源自己指定）
        [SerializeField] float _repairRadius = 0.03f;
        [SerializeField] float _repairFeather = 0.35f;
        Vector2? _cloneSource;                     // 仿制图章的取样点，Alt+点击设定
        bool _repairDirty;                         // 需要重放修补


        readonly List<Texture> _brushes = new List<Texture>();
        Material _brushMat;
        [SerializeField] float _brushRadius = 0.06f;   // 相对画面短边
        [SerializeField] float _brushHardness = 0.4f;
        [SerializeField] float _brushFlow = 0.6f;

        /// <summary>笔迹只在 OnGUI 里登记，真正绘制排到 Update——GL 渲染和 Blit 一样不能出现在 OnGUI 里。</summary>
        struct Dab { public int brush; public Vector2 uv; public float radius; public bool erase; }
        readonly List<Dab> _dabs = new List<Dab>();
        Vector2 _lastDabUv;
        bool _dabbing;

        Vector2 _paramScroll, _filmScroll;
        readonly GradeSettingsGUI _gui = new GradeSettingsGUI();

        void OnEnable()
        {
            titleContent = new GUIContent("修图台");
            wantsMouseMove = true;

            _rightPanelW = EditorPrefs.GetFloat(PrefRightW, 360f);
            _filmH = EditorPrefs.GetFloat(PrefFilmH, 96f);
            _filmVisible = EditorPrefs.GetBool(PrefFilmOn, true);
            _panelVisible = EditorPrefs.GetBool(PrefPanelOn, true);

            _store.Load();
            _lib.MetaLoader = e =>
            {
                var rec = _store.Get(e.path);
                if (rec == null) return;
                e.rating = Mathf.Clamp(rec.rating, 0, 5);
                e.flag = Mathf.Clamp(rec.flag, -1, 1);
            };
            _lib.MetaSaver = e => _store.PutMeta(e.path, e.rating, e.flag);
        }

        void SaveLayout()
        {
            EditorPrefs.SetFloat(PrefRightW, _rightPanelW);
            EditorPrefs.SetFloat(PrefFilmH, _filmH);
            EditorPrefs.SetBool(PrefFilmOn, _filmVisible);
            EditorPrefs.SetBool(PrefPanelOn, _panelVisible);
        }

        /// <summary>
        /// 所有渲染都放在这里，绝对不能放进 OnGUI。
        ///
        /// IMGUI 正在往窗口的渲染目标里画的时候，Graphics.Blit 会把 RenderTexture.active 切走，
        /// 哪怕之后切回来，GUI 的渲染状态也已经乱了——表现是边缘干净的黑块、
        /// 图像画到 UI 上、BeginClip 失效。Update 在 GUI 之外跑，没有这个问题。
        /// </summary>
        void Update()
        {
            bool needRepaint = false;

            // 导出同样要 Blit + ReadPixels，也不能在 GUI 里跑
            if (_repairDirty)
            {
                _repairDirty = false;
                _repair.Rebuild(_full);      // 里面有 Blit，只能在这里做
                StashRepairs();
                _dirty = true;
            }

            // 限流落盘。拖滑条时不会每帧写文件，但崩了最多丢八秒
            if (_store.Dirty) { StashSettings(); _store.Save(); }

            FlushDabs();

            if (_pendingAction != null)
            {
                var action = _pendingAction;
                _pendingAction = null;
                action();
                needRepaint = true;
            }

#if LOVE_SENTIS
            // 一帧只跑一块。一口气跑完编辑器要假死好几分钟，
            // 分开跑的话每块之间还能响应输入、还能取消
            if (_denoiser != null && _denoiser.Running)
            {
                _denoiser.Step();
                _dnStatus = $"降噪中 {_denoiser.TileDone}/{_denoiser.TileCount} 块" +
                            $"（每块 {_denoiser.LastMs:0} ms）";
                if (!_denoiser.Running) FinishDenoise();
                needRepaint = true;
            }
#endif

            // 待导入的文件也在这里处理：生成缩略图要 Blit
            if (_pendingImports.Count > 0)
            {
                ProcessPendingImports();
                needRepaint = true;
            }

            if (_dirty && _full != null)
            {
                RenderPreview();
                needRepaint = true;
            }

            if (needRepaint) Repaint();
        }

        void OnDisable()
        {
            _renderer?.Dispose();
            _renderer = null;
            if (_materialCopy != null) { DestroyImmediate(_materialCopy); _materialCopy = null; }
            // 关窗前把当前这张收好再落盘，否则最后改的那点就白改了
            StashSettings();
            StashRepairs();
            _store.Save(force: true);

            ReleasePreview();
            ReleaseBrushes();
            ReleaseSky();
            _repair.Dispose();
            ClearEntries();
            if (_lut != null) { DestroyImmediate(_lut); _lut = null; }
#if LOVE_SENTIS
            ReleaseMask();
            ReleaseDenoise();
            _denoiser?.Dispose();
            _denoiser = null;
            _maskGen?.Dispose();
            _maskGen = null;
#endif
        }

        VideoGradeRenderer Renderer
        {
            get
            {
                if (_renderer != null && _renderer.IsValid) return _renderer;
                var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
                if (mat == null) return null;
                // 用材质副本，免得修图的参数把场景那个材质资产改脏
                if (_materialCopy == null)
                    _materialCopy = new Material(mat) { hideFlags = HideFlags.HideAndDontSave };
                _renderer = new VideoGradeRenderer(_materialCopy);
                return _renderer;
            }
        }

        #region 主布局

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

            _filmH = Mathf.Clamp(_filmH, 64f, Mathf.Max(80f, bodyH * 0.5f));
            float filmH = _filmVisible ? _filmH : 0f;

            var toolbar = new Rect(0f, 0f, position.width, ToolbarH);
            var right   = new Rect(position.width - panelW, bodyY, panelW, bodyH);
            var vSplit  = new Rect(right.x - SplitterW, bodyY, SplitterW, bodyH);
            var left    = new Rect(0f, bodyY, vSplit.x, bodyH);
            var film    = new Rect(left.x, left.yMax - filmH, left.width, filmH);
            var hSplit  = new Rect(left.x, film.y - SplitterW, left.width, SplitterW);
            var canvas  = new Rect(left.x, left.y, left.width,
                                   left.height - filmH - (_filmVisible ? SplitterW : 0f));
            var status  = new Rect(0f, position.height - GradeSkin.StatusH, position.width, GradeSkin.StatusH);

            // 分隔条的输入要先处理，否则会被下面的面板抢走
            HandleSplitters(vSplit, hSplit);

            DrawToolbar(toolbar);
            DrawCanvas(canvas);

            if (_filmVisible) { GradeSkin.DrawSplitter(hSplit, false); DrawFilmstrip(film); }

            GradeSkin.DrawSplitter(vSplit, true);

            if (_panelVisible) DrawParamPanel(right);
            else DrawCollapsedPanel(right);

            DrawStatusBar(status);
        }

        #region 可拖拽分隔条

        void HandleSplitters(Rect vSplit, Rect hSplit)
        {
            var e = Event.current;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (vSplit.Contains(e.mousePosition))
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

                    _dragging = _panelVisible ? Splitter.Right : Splitter.None;
                    e.Use();
                }
                else if (_filmVisible && hSplit.Contains(e.mousePosition))
                { _dragging = Splitter.Film; e.Use(); }
            }
            else if (e.type == EventType.MouseDrag && _dragging != Splitter.None)
            {
                if (_dragging == Splitter.Right) _rightPanelW -= e.delta.x;
                else _filmH -= e.delta.y;
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseUp && _dragging != Splitter.None)
            {
                _dragging = Splitter.None;
                SaveLayout();
                e.Use();
            }
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

        #endregion

        void DrawToolbar(Rect r)
        {
            _tb.Begin(r);
            bool has = _full != null;

            _tb.Button("打开图片…", 74f, OpenFiles, priority: 100);
            _tb.Button("文件夹…", 64f, OpenFolder, priority: 86);
            _tb.Button("清空", 44f, ClearEntries, priority: 40, disabled: _lib.Count == 0);

            _tb.Space(8f);

            _tb.Toggle(_bypass, "原图对比", 62f, v => { _bypass = v; _dirty = true; },
                       priority: 80, disabled: !has, tooltip: "按住反斜杠也可以临时看原图");
            _tb.Toggle(_splitCompare, "分屏", 42f, v => { _splitCompare = v; _dirty = true; },
                       priority: 72, disabled: !has);
            if (_splitCompare)
                // 优先级压在「分屏」开关之上：撤退是按优先级从低到高来的，
                // 这样绝不会出现"开关还在、调位置的滑条却没了"
                _tb.Slider(_splitPosition, 0f, 1f, 80f,
                           v => { _splitPosition = v; _dirty = true; }, priority: 73, disabled: !has);

            _tb.Space(8f);

            _tb.Button("适应", 38f, () => _canvas.FitPending = true, priority: 64, disabled: !has);
            _tb.Button("100%", 44f, () => _canvas.SetZoom(1f), priority: 60, disabled: !has);

            _tb.Space(8f);

            _tb.Toggle(_cropMode, "裁剪", 42f, v =>
            {
                _cropMode = v;
                // 进裁剪模式时如果还没框过，先给一个整幅的框，不然没有东西可拖
                if (v && !_settings.cropEnabled)
                {
                    Undo.RecordObject(this, "开始裁剪");
                    _settings.cropEnabled = true;
                    _settings.ResetCrop();
                }
                _canvas.FitPending = true;
                _dirty = true;
            }, priority: 78, disabled: !has);

            // 吸管是一次性的：取完一次就自己关掉，免得下一次平移画面被当成取色
            _tb.Toggle(_repairMode, "修复", 42f, v =>
            {
                _repairMode = v;
                if (v) { _pickWb = false; _cropMode = false; }
                Repaint();
            }, priority: 77, disabled: !has, tooltip: "点一下去掉污点；按住 Alt 点是设仿制取样点");

            _tb.Toggle(_pickWb, "白平衡吸管", 76f, v => { _pickWb = v; Repaint(); },
                       priority: 76, disabled: !has);
            _tb.Button("自动色调", 62f, () => _pendingAction = AutoToneCurrent,
                       priority: 74, disabled: !has);

            _tb.Flex();

            _tb.Button("胶片化", 50f, () =>
            {
                AutoSnapshot("胶片化之前");
                Undo.RecordObject(this, "胶片化预设");
                _settings.ApplyFilmLook();
                _dirty = true;
            }, priority: 30);

            _tb.Button("预设", 42f, PresetMenu, priority: 34);

            _tb.Button("重置参数", 62f, () =>
            {
                AutoSnapshot("重置之前");
                Undo.RecordObject(this, "重置参数");
                _settings.Reset();
                _dirty = true;
            }, priority: 30);

            _tb.Space(6f);

            _tb.Toggle(_filmVisible, "胶片条", 50f, v => { _filmVisible = v; SaveLayout(); }, priority: 44);
            _tb.Toggle(_panelVisible, "参数栏", 50f, v => { _panelVisible = v; SaveLayout(); },
                       priority: 98, tooltip: "收起参数栏，画布占满窗口");

            _tb.End();
        }

        /// <summary>
        /// 底部状态栏。图片信息原来挤在工具栏中间，一到窄窗口就把按钮顶掉——
        /// 信息该待在信息该待的地方。
        /// </summary>
        void DrawStatusBar(Rect r)
        {
            EditorGUI.DrawRect(r, GradeSkin.Bar);
            GradeSkin.Line(r.x, r.y, r.width, 1f, GradeSkin.Trough);

            if (_full == null)
            {
                GUI.Label(r, _lib.Count > 0 ? "未选中图片" : "未载入图片", GradeSkin.StatusDim);
                return;
            }

            float x = r.x;
            void Cell(string text, float w, GUIStyle st)
            {
                GUI.Label(new Rect(x, r.y, w, r.height), text, st);
                x += w;
                GradeSkin.Line(x, r.y + 3f, 1f, r.height - 6f, GradeSkin.Trough);
            }

            Cell(_full.name, Mathf.Min(240f, r.width * 0.3f), GradeSkin.Status);
            Cell($"{_full.width}×{_full.height}", 110f, GradeSkin.StatusDim);

            _settings.OutputSize(_full.width, _full.height, out int ow, out int oh);
            if (ow != _full.width || oh != _full.height)
                Cell($"输出 {ow}×{oh}", 120f, GradeSkin.Status);

            Cell($"缩放 {_canvas.Zoom * 100f:0}%", 84f, GradeSkin.StatusDim);
                        int vis = _lib.Visible.Count, idx = _lib.IndexOfVisible(_lib.Current);
            Cell($"第 {idx + 1} / {vis} 张" + (vis != _lib.Count ? $"（共 {_lib.Count}）" : ""),
                 130f, GradeSkin.StatusDim);
            if (_lib.Selected.Count > 1) Cell($"选中 {_lib.Selected.Count} 张", 84f, GradeSkin.Status);
            if (_lut != null) Cell($"LUT {_lutName}", 140f, GradeSkin.StatusDim);
        }

        void DrawParamPanel(Rect r)
        {
            EditorGUI.DrawRect(r, GradeSkin.Panel);
            GUILayout.BeginArea(new Rect(r.x + 4f, r.y + 2f, r.width - 8f, r.height - 4f));

            // 标签宽度跟着面板走。用 Unity 的固定默认值时，
            // 面板一窄「反向（选窗口外）」这种标签就被截成「反向（选窗…」
            float prevLabel = EditorGUIUtility.labelWidth;
            bool prevWide = EditorGUIUtility.wideMode;
            EditorGUIUtility.labelWidth = Mathf.Clamp((r.width - 8f) * 0.46f, 84f, 220f);
            EditorGUIUtility.wideMode = true;

            _paramScroll = EditorGUILayout.BeginScrollView(_paramScroll);

            DrawMaskBar();
            EditorGUILayout.Space(8f);
            DrawSnapshotBar();
            EditorGUILayout.Space(8f);
            DrawSyncBar();
            EditorGUILayout.Space(8f);
            DrawAutoToneBar();
            EditorGUILayout.Space(8f);
            DrawRepairBar();
            EditorGUILayout.Space(8f);
            DrawChartBar();
            EditorGUILayout.Space(8f);
            DrawLutBar();
            EditorGUILayout.Space(8f);
            DrawRawBar();
            EditorGUILayout.Space(8f);
            DrawExportBar();
            EditorGUILayout.Space(6f);

            EditorGUI.BeginChangeCheck();
            _gui.Masks.RequestBrush = NewBrush;
            _gui.Masks.DrawBrushOptions = DrawBrushOptions;
            _gui.Masks.HasSubjectMask = CurrentMask != null;
            _gui.Masks.HasDepthMap = CurrentMask != null;
            _gui.PreviewTexture = _preview;
            _gui.PanelWidth = r.width - 8f;
            _gui.SourceSize = _full != null ? new Vector2Int(_full.width, _full.height) : Vector2Int.zero;
            _gui.Draw(_settings, this);
            // 只有参数真的动了才重渲染。缩放平移不该触发全分辨率重算。
            // 转盘弹窗是跨帧的，改动落在 OnGUI 之外，BeginChangeCheck 捕捉不到，所以要单独问一次
            bool changed = EditorGUI.EndChangeCheck();
            if (changed | _gui.ConsumeExternalChange()) { _dirty = true; _snapChanged = true; }

            EditorGUILayout.EndScrollView();

            EditorGUIUtility.labelWidth = prevLabel;
            EditorGUIUtility.wideMode = prevWide;
            GUILayout.EndArea();
        }

        #endregion

        #region 预览：缩放与平移

        void DrawCanvas(Rect r)
        {
            if (_full == null)
            {
                EditorGUI.DrawRect(r, GradeSkin.Canvas);
                EditorGUI.LabelField(new Rect(r.x, r.center.y - 20f, r.width, 40f),
                    "把图片拖进来，或用左上角「打开图片 / 打开文件夹」", GradeSkin.Placeholder);
                HandleDragAndDrop(r);
                return;
            }

            // 裁剪框 / 色卡角点 / 笔刷正在用时，别让画布平移抢走事件
            bool block = (_chartMode && _chartDragIndex >= 0) || (_cropMode && _cropDrag >= 0)
                         || _gui.Masks.PaintingPart != null || _maskOverlay.Dragging || _repairMode;
            if (_canvas.HandleInput(r, block)) Repaint();
            // 按住反斜杠看原图。不直接写 _bypass，那会把用户自己按下的对比按钮状态冲掉
            if (_canvas.ConsumeCompareChanged()) _dirty = true;

            CanvasSize(out int cw, out int chh);
            _canvas.Draw(r, _preview, cw, chh);
            var img = _canvas.ImageRect;

            if (_cropMode) DrawCropOverlay(r, img);
            if (_chartMode) DrawChartOverlay(r, img);

            // 裁剪模式下画布显示的是未裁剪的整幅，而蒙版坐标是裁剪之后的，两者对不上
            var shape = _cropMode ? null : _gui.Masks.EditingPart;
            _maskOverlay.Draw(r, img, shape);
            bool taken = _maskOverlay.HandleInput(img, shape, s => Undo.RecordObject(this, s),
                                                  () => _dirty = true);

            if (_repairMode) DrawRepairCursor(r, img);

            if (!taken && !HandleRepairInput(img) && !HandleBrushInput(img)) HandlePickInput(img);
            HandleDragAndDrop(r);
        }

        /// <summary>
        /// 画布上这一幅的像素尺寸。裁剪模式下故意返回未裁剪的整幅——
        /// 那时候要让用户看见自己正在切掉哪一块。
        /// </summary>
        void CanvasSize(out int w, out int h)
        {
            if (_full == null) { w = h = 1; return; }
            bool prev = _settings.cropEnabled;
            if (_cropMode) _settings.cropEnabled = false;
            _settings.OutputSize(_full.width, _full.height, out w, out h);
            _settings.cropEnabled = prev;
        }

        /// <summary>画布 uv -> 源图 uv。裁剪模式下画布显示的是整幅，换算要跟着变。</summary>
        Vector2 CanvasUvToSource(Vector2 uv)
        {
            bool prev = _settings.cropEnabled;
            if (_cropMode) _settings.cropEnabled = false;
            var srcUv = _settings.DisplayUvToSource(uv, _full.width, _full.height);
            _settings.cropEnabled = prev;
            return srcUv;
        }

        void HandleDragAndDrop(Rect r)
        {
            var e = Event.current;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
            if (!r.Contains(e.mousePosition)) return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type != EventType.DragPerform) return;

            DragAndDrop.AcceptDrag();
            foreach (var path in DragAndDrop.paths)
            {
                if (Directory.Exists(path)) AddFolder(path);
                else if (IsImage(path)) AddFile(path);
            }
            e.Use();
        }

        /// <summary>按原分辨率渲染。缩略图预览的话，Bloom 半径和颗粒尺寸都会和导出结果对不上。</summary>
        /// <summary>喂给调色管线的那张图。有修补就用修补后的，没有就用原图。</summary>
        Texture GradeSource
        {
            get
            {
#if LOVE_SENTIS
                // 降噪排在修补前面：修补是拿周围像素补窟窿，
                // 在带噪的图上找取样源，补上去的那块也是带噪的
                if (_dnBlended != null && _lib.Current != null && _dnPath == _lib.Current.path)
                    return _dnBlended;
#endif
                return _repair.Result != null ? (Texture)_repair.Result : _full;
            }
        }

        void RenderPreview()
        {
            if (_full == null) return;

            CanvasSize(out int pw, out int ph);
            bool sizeChanged = _preview == null || _preview.width != pw || _preview.height != ph;

            if (sizeChanged)
            {
                ReleasePreview();
                _preview = new RenderTexture(pw, ph, 0,
                                             RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                { name = "PhotoGradePreview", hideFlags = HideFlags.HideAndDontSave };
                _preview.Create();
            }

            var r = Renderer;
            if (r == null) return;

            r.GrainSeed = 7f;   // 图片用固定种子，否则每次重绘噪点都在跳，导出也和预览对不上

            // 裁剪模式下临时关掉裁剪：画布要显示整幅，裁剪框以叠加层的形式画在上面
            bool prevCrop = _settings.cropEnabled;
            if (_cropMode) _settings.cropEnabled = false;

            r.Render(GradeSource, _preview, _settings, new VideoGradeRenderer.Options
            {
                bypass = _bypass || _canvas.HoldCompare,
                splitCompare = _splitCompare,
                splitPosition = _splitPosition,
                externalMask = CurrentMask,
                lut = _lut,
                lutAmount = _lutAmount,
                brushes = _brushes,
                depthMap = CurrentMask,   // 选了 MiDaS 时生成的就是深度图，和主体蒙版共用一张
                skyMask = EnsureSky(),
            });

            _settings.cropEnabled = prevCrop;
            _dirty = false;
        }

        #region 天空蒙版

        /// <summary>有没有哪个还开着的蒙版组用到了天空。没人用就一点活都别干。</summary>
        bool NeedsSky()
        {
            var gs = _settings != null ? _settings.maskGroups : null;
            if (gs == null) return false;

            for (int i = 0; i < gs.Count; i++)
            {
                var g = gs[i];
                if (g == null || !g.enabled || g.parts == null) continue;
                for (int j = 0; j < g.parts.Count; j++)
                    if (g.parts[j] != null && g.parts[j].Shape == MaskShape.Sky) return true;
            }
            return false;
        }

        /// <summary>
        /// 取当前这张的天空蒙版，必要时算一遍。
        ///
        /// 只在 Update 里调：GetPixels32 和 SetPixels32 都不便宜，
        /// 而且这条路最终要产出一张贴图给渲染用，OnGUI 里碰不得。
        /// </summary>
        Texture2D EnsureSky()
        {
            var e = _lib.Current;
            if (e == null || e.thumb == null || !NeedsSky())
            {
                _gui.Masks.HasSky = false;
                return null;
            }

            if (_skyDetected != e.path)
            {
                _skyResult = SkyMaskBuilder.Detect(e.thumb, _skyOpt);
                _skyDetected = e.path;
                ReleaseSkyTexture();
            }

            _gui.Masks.HasSky = _skyResult.found;
            _gui.Masks.SkyCoverage = _skyResult.coverage;
            if (!_skyResult.found) return null;

            int geo = SkyMaskBuilder.GeometryKey(_settings);
            if (_skyTex != null && _skyTexPath == e.path && _skyTexGeo == geo) return _skyTex;

            ReleaseSkyTexture();
            _skyTex = SkyMaskBuilder.ToTexture(_skyResult, _settings);
            _skyTexPath = e.path;
            _skyTexGeo = geo;
            return _skyTex;
        }

        void ReleaseSkyTexture()
        {
            if (_skyTex != null) { DestroyImmediate(_skyTex); _skyTex = null; }
            _skyTexPath = null;
            _skyTexGeo = int.MinValue;
        }

        void ReleaseSky()
        {
            ReleaseSkyTexture();
            _skyResult = default;
            _skyDetected = null;
        }

        /// <summary>
        /// 批量导出别的图时现算一张。
        ///
        /// 和 AI 主体蒙版不同，天空是能逐图便宜地算出来的，所以批量导出时也照算——
        /// 否则"压天空"这套参数套到整批图上会悄悄什么都不做，那比报错还难查。
        /// </summary>
        Texture2D BuildSkyFor(Texture2D src)
        {
            if (src == null || !NeedsSky()) return null;

            // GetPixels32 在大图上是内存炸弹，先缩到工作尺寸再回读
            int max = Mathf.Max(src.width, src.height);
            Texture2D small = src;
            RenderTexture rt = null;
            Texture2D tmp = null;

            if (max > 512)
            {
                float k = 512f / max;
                int w = Mathf.Max(4, Mathf.RoundToInt(src.width * k));
                int h = Mathf.Max(4, Mathf.RoundToInt(src.height * k));
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt);

                tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
                    { hideFlags = HideFlags.HideAndDontSave };
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tmp.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
                tmp.Apply(false, false);
                RenderTexture.active = prev;
                small = tmp;
            }

            var res = SkyMaskBuilder.Detect(small, _skyOpt);
            var tex = SkyMaskBuilder.ToTexture(res, _settings);

            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            if (tmp != null) DestroyImmediate(tmp);
            return tex;
        }

        #endregion

        void ReleaseBrushes()
        {
            foreach (var t in _brushes)
                if (t is RenderTexture rt) { rt.Release(); DestroyImmediate(rt); }
            _brushes.Clear();
            if (_brushMat != null) { DestroyImmediate(_brushMat); _brushMat = null; }
        }

        /// <summary>新开一张笔刷画布，返回它的下标。</summary>
        int NewBrush()
        {
            int w = 1024, h = 1024;
            if (_full != null)
            {
                float k = Mathf.Min(1f, BrushMax / (float)Mathf.Max(_full.width, _full.height));
                w = Mathf.Max(16, Mathf.RoundToInt(_full.width * k));
                h = Mathf.Max(16, Mathf.RoundToInt(_full.height * k));
            }

            // 蒙版是数据不是颜色，必须 Linear：走 sRGB 的话写进去 0.5 读出来就不是 0.5
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            { name = "MaskBrush" + _brushes.Count, hideFlags = HideFlags.HideAndDontSave,
              wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            rt.Create();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);      // 从全空起步
            RenderTexture.active = prev;

            _brushes.Add(rt);
            return _brushes.Count - 1;
        }

        Material BrushMaterial
        {
            get
            {
                if (_brushMat != null) return _brushMat;
                var sh = Shader.Find("Hidden/Love/MaskBrush");
                if (sh == null) return null;
                _brushMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                return _brushMat;
            }
        }

        /// <summary>把登记下来的笔迹一次性画掉。只能在 Update 里调。</summary>
        void FlushDabs()
        {
            if (_dabs.Count == 0) return;
            var mat = BrushMaterial;
            if (mat == null) { _dabs.Clear(); return; }

            foreach (var d in _dabs)
            {
                if (d.brush < 0 || d.brush >= _brushes.Count) continue;
                if (!(_brushes[d.brush] is RenderTexture rt) || rt == null) continue;

                mat.SetFloat("_Hardness", _brushHardness);
                mat.SetFloat("_Flow", _brushFlow);

                // 半径按短边算，这样在长宽比不同的图上手感一致
                float rx = d.radius * Mathf.Min(rt.width, rt.height) / rt.width;
                float ry = d.radius * Mathf.Min(rt.width, rt.height) / rt.height;

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                mat.SetPass(d.erase ? 1 : 0);

                GL.PushMatrix();
                GL.LoadOrtho();
                GL.Begin(GL.QUADS);
                GL.TexCoord2(0f, 0f); GL.Vertex3(d.uv.x - rx, d.uv.y - ry, 0f);
                GL.TexCoord2(1f, 0f); GL.Vertex3(d.uv.x + rx, d.uv.y - ry, 0f);
                GL.TexCoord2(1f, 1f); GL.Vertex3(d.uv.x + rx, d.uv.y + ry, 0f);
                GL.TexCoord2(0f, 1f); GL.Vertex3(d.uv.x - rx, d.uv.y + ry, 0f);
                GL.End();
                GL.PopMatrix();

                RenderTexture.active = prev;
            }

            _dabs.Clear();
            _dirty = true;
        }

        #region 快照

        /// <summary>
        /// 覆盖性操作之前留一份。
        ///
        /// Unity 的 Ctrl+Z 是会话级的，程序集一重载就没了；而这些操作
        /// （重置、套预设、自动色调、粘贴）一下就把整套参数换掉。
        /// 存一份的成本是几 KB，找不回来的成本是重调一遍。
        /// </summary>
        void AutoSnapshot(string label)
        {
            if (_lib.Current == null || !_snapChanged) return;
            var rec = _store.GetOrCreate(_lib.Current.path);
            Snapshots.Add(rec.snapshots, _settings, label, true, DateTime.Now);
            _store.MarkDirty();
            _snapChanged = false;
        }

        void TakeSnapshot(string name)
        {
            if (_lib.Current == null) return;
            var rec = _store.GetOrCreate(_lib.Current.path);
            Snapshots.Add(rec.snapshots, _settings, name, false, DateTime.Now);
            _store.MarkDirty();
            _snapChanged = false;
            Repaint();
        }

        void RestoreSnapshot(GradeSnapshot snap)
        {
            if (snap?.settings == null) return;

            // 恢复本身也是覆盖性的，先把现在这套留住
            AutoSnapshot("恢复之前");

            Undo.RecordObject(this, "恢复快照");
            _settings.CopyFrom(snap.settings);
            StashSettings();
            _dirty = true;
            _snapChanged = false;
            Repaint();
        }

        void DrawSnapshotBar()
        {
            EditorGUILayout.LabelField("快照", EditorStyles.boldLabel);

            var rec = _lib.Current != null ? _store.Get(_lib.Current.path) : null;
            var list = rec?.snapshots;

            EditorGUILayout.BeginHorizontal();
            _newSnapName = EditorGUILayout.TextField(_newSnapName);
            using (new EditorGUI.DisabledScope(_lib.Current == null))
                if (GUILayout.Button("存一份", EditorStyles.miniButton, GUILayout.Width(56f)))
                {
                    TakeSnapshot(string.IsNullOrWhiteSpace(_newSnapName)
                        ? "快照 " + DateTime.Now.ToString("HH:mm:ss") : _newSnapName.Trim());
                    _newSnapName = "";
                    GUI.FocusControl(null);
                }
            EditorGUILayout.EndHorizontal();

            if (list == null || list.Count == 0)
            {
                EditorGUILayout.HelpBox("还没有快照。重置、套预设、自动色调、粘贴这些覆盖性操作之前会自动存一份，" +
                                        "也可以在这里手动存。跟着逐图记录一起落盘，重启也还在。",
                                        MessageType.None);
                return;
            }

            // 新的在上面：刚存的那份最可能马上要用
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var snap = list[i];
                var row = EditorGUILayout.GetControlRect(GUILayout.Height(18f));

                if (Event.current.type == EventType.Repaint && snap.auto)
                    EditorGUI.DrawRect(row, new Color(1f, 1f, 1f, 0.03f));

                float x = row.x;
                // 自动存的用一道灰色条区分，手动存的用强调色——手动的才是用户真正在意的
                GradeSkin.Line(x, row.y + 2f, 2f, row.height - 4f,
                               snap.auto ? GradeSkin.Grip : GradeSkin.Accent);
                x += 6f;

                EditorGUI.BeginChangeCheck();
                string nm = EditorGUI.TextField(new Rect(x, row.y, row.width - 160f, 17f), snap.name);
                if (EditorGUI.EndChangeCheck()) { snap.name = nm; _store.MarkDirty(); }
                x = row.xMax - 152f;

                GUI.Label(new Rect(x, row.y, 76f, 17f), snap.TimeText, EditorStyles.miniLabel);
                x += 78f;

                if (GUI.Button(new Rect(x, row.y, 48f, 17f), "恢复", EditorStyles.miniButton))
                    RestoreSnapshot(snap);
                x += 50f;

                if (GUI.Button(new Rect(x, row.y, 22f, 17f), "×", EditorStyles.miniButton))
                {
                    list.RemoveAt(i);
                    _store.MarkDirty();
                    Repaint();
                }
            }

            EditorGUILayout.LabelField(
                $"{list.Count} / {Snapshots.MaxPerPhoto} 份　满了先挤最老的自动快照",
                EditorStyles.miniLabel);
        }

        #endregion

        /// <summary>
        /// 参数的复制 / 粘贴 / 同步。
        ///
        /// 每张图有自己的一套参数，所以才需要这些。之前是一套管所有图，
        /// 换图参数不变——开发 look 时顺手，但一批照片各自曝光不同时就不对了。
        /// </summary>
        void DrawSyncBar()
        {
            EditorGUILayout.LabelField("参数（逐图）", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_full == null))
                if (GUILayout.Button(new GUIContent("复制", "把当前这张的全部参数记下来")))
                    _clipboard = _settings.Clone();

            using (new EditorGUI.DisabledScope(_clipboard == null || _full == null))
                if (GUILayout.Button(new GUIContent("粘贴", "套到当前这张")))
                {
                    AutoSnapshot("粘贴之前");
                    Undo.RecordObject(this, "粘贴参数");
                    _settings.CopyFrom(_clipboard);
                    StashSettings();
                    _dirty = true;
                }
            EditorGUILayout.EndHorizontal();

            int sel = _lib.Selected.Count;
            using (new EditorGUI.DisabledScope(sel < 2))
                if (GUILayout.Button($"同步到选中的 {Mathf.Max(sel, 0)} 张"))
                    SyncSettings(new List<PhotoEntry>(_lib.Selected));

            using (new EditorGUI.DisabledScope(_lib.Visible.Count < 2))
                if (GUILayout.Button($"同步到当前筛选下的 {_lib.Visible.Count} 张"))
                    SyncSettings(new List<PhotoEntry>(_lib.Visible));

            if (_clipboard != null)
                EditorGUILayout.LabelField("剪贴板", "已存一套参数", EditorStyles.miniLabel);

            EditorGUILayout.Space(2f);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"已记录 {_store.Count} 张的编辑", EditorStyles.miniLabel);
            if (GUILayout.Button(new GUIContent("清除全部记录", PhotoEditStore.FilePath),
                                 EditorStyles.miniButton, GUILayout.Width(84f)))
            {
                if (EditorUtility.DisplayDialog("清除编辑记录",
                        $"删掉全部 {_store.Count} 张图的参数、修补和评级？\n\n这个操作不可撤销。",
                        "删除", "取消"))
                {
                    _store.Clear();
                    Debug.Log("[修图台] 编辑记录已清除");
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        void SyncSettings(List<PhotoEntry> targets)
        {
            if (targets.Count == 0) return;
            if (!EditorUtility.DisplayDialog("同步参数",
                    $"把当前这张的参数套到 {targets.Count} 张图上？\n\n它们各自原来的参数会被覆盖。",
                    "同步", "取消"))
                return;

            StashSettings();
            foreach (var e in targets) _store.PutSettings(e.path, _settings);
            Debug.Log($"[修图台] 参数已同步到 {targets.Count} 张");
        }

        #region 污点修复

        /// <summary>画布上的光标环，让人看清这一笔会盖住多大一块。</summary>
        void DrawRepairCursor(Rect canvas, Rect img)
        {
            if (Event.current.type != EventType.Repaint || _full == null) return;

            var m = Event.current.mousePosition;
            if (!canvas.Contains(m)) return;

            GUI.BeginGroup(canvas);
            var off = new Vector2(canvas.x, canvas.y);
            Color prev = Handles.color;

            // 半径是以画面高为单位的，屏幕上就是 img.height 倍
            float rpx = _repairRadius * img.height;
            Handles.color = new Color(1f, 1f, 1f, 0.9f);
            DrawCircle(m - off, rpx);
            Handles.color = new Color(1f, 1f, 1f, 0.35f);
            DrawCircle(m - off, rpx * (1f + _repairFeather));

            // 仿制模式下把取样点也画出来，不然不知道会从哪儿抄
            if (_repairClone && _cloneSource.HasValue)
            {
                var sp = new Vector2(img.x + _cloneSource.Value.x * img.width,
                                     img.y + (1f - _cloneSource.Value.y) * img.height) - off;
                Handles.color = GradeSkin.Playhead;
                DrawCircle(sp, rpx);
                Handles.DrawAAPolyLine(1.2f, new Vector3(sp.x, sp.y, 0f),
                                       new Vector3(m.x - off.x, m.y - off.y, 0f));
            }

            Handles.color = prev;
            GUI.EndGroup();
        }

        static void DrawCircle(Vector2 c, float r)
        {
            const int N = 40;
            var pts = new Vector3[N + 1];
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f;
                pts[i] = new Vector3(c.x + Mathf.Cos(a) * r, c.y + Mathf.Sin(a) * r, 0f);
            }
            Handles.DrawAAPolyLine(1.4f, pts);
        }

        /// <summary>返回 true 表示事件被修复工具吃掉了。</summary>
        bool HandleRepairInput(Rect img)
        {
            if (!_repairMode || _full == null) return false;

            EditorGUIUtility.AddCursorRect(img, MouseCursor.ArrowPlus);
            Repaint();   // 光标环要跟着鼠标走

            var e = Event.current;
            if (e.type == EventType.ScrollWheel && img.Contains(e.mousePosition))
            {
                // 滚轮改笔尖大小，和 PS 的 [ ] 一个意思但更顺手
                _repairRadius = Mathf.Clamp(_repairRadius * (1f - e.delta.y * 0.08f), 0.003f, 0.3f);
                e.Use();
                return true;
            }

            if (e.type != EventType.MouseDown || e.button != 0 || !img.Contains(e.mousePosition))
                return false;

            float u = (e.mousePosition.x - img.x) / Mathf.Max(img.width, 1f);
            float v = 1f - (e.mousePosition.y - img.y) / Mathf.Max(img.height, 1f);
            var uv = CanvasUvToSource(new Vector2(u, v));

            // Alt+点击 = 设仿制取样点，和 PS 一致
            if (e.alt)
            {
                _cloneSource = uv;
                _repairClone = true;
                e.Use();
                return true;
            }

            Undo.RecordObject(this, "污点修复");

            Vector2? manual = null;
            if (_repairClone && _cloneSource.HasValue)
            {
                manual = _cloneSource.Value;
            }

            _repair.Add(_full, uv, _repairRadius, _repairFeather, _repairClone, manual);
            _repairDirty = true;
            e.Use();
            return true;
        }

        void DrawRepairBar()
        {
            EditorGUILayout.LabelField("污点修复 / 仿制图章", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(_full == null))
            {
                bool on = EditorGUILayout.Toggle(
                    new GUIContent("修复模式", "点画面去掉污点。按住 Alt 点是设仿制取样点，滚轮改笔尖大小"),
                    _repairMode);
                if (on != _repairMode)
                {
                    _repairMode = on;
                    if (on) { _pickWb = false; _cropMode = false; }
                }

                EditorGUI.BeginChangeCheck();
                _repairRadius = EditorGUILayout.Slider(
                    new GUIContent("笔尖大小", "以画面高为单位。画布上滚轮也能改"), _repairRadius, 0.003f, 0.3f);
                _repairFeather = EditorGUILayout.Slider(
                    new GUIContent("羽化", "从笔尖边缘往外扩的过渡带"), _repairFeather, 0.02f, 1f);
                if (EditorGUI.EndChangeCheck()) Repaint();

                bool clone = EditorGUILayout.Toggle(
                    new GUIContent("仿制图章", "关掉是自动找源的污点修复；打开要自己 Alt+点击指定取样点"),
                    _repairClone);
                if (clone != _repairClone) { _repairClone = clone; Repaint(); }

                if (_repairClone && !_cloneSource.HasValue)
                    EditorGUILayout.HelpBox("还没设取样点：按住 Alt 在画面上点一下。", MessageType.Warning);
            }

            EditorGUILayout.LabelField("已修补", $"{_repair.Spots.Count} 处");

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_repair.Spots.Count == 0))
            {
                if (GUILayout.Button("撤销上一处"))
                {
                    Undo.RecordObject(this, "撤销修补");
                    _repair.Spots.RemoveAt(_repair.Spots.Count - 1);
                    _repairDirty = true;
                }
                if (GUILayout.Button("全部清除"))
                {
                    Undo.RecordObject(this, "清除修补");
                    _repair.Spots.Clear();
                    _repairDirty = true;
                }
            }
            EditorGUILayout.EndHorizontal();

            if (_repair.Spots.Count > 40)
                EditorGUILayout.HelpBox("修补处数很多。每处都要重放一趟全分辨率的 Blit，" +
                                        "改动之后的重建会明显变慢。", MessageType.None);
        }

        #endregion

        /// <summary>笔刷的大小 / 硬度 / 流量。画在蒙版面板里那个笔刷部件下面。</summary>
        void DrawBrushOptions()
        {
            EditorGUI.BeginChangeCheck();
            _brushRadius = EditorGUILayout.Slider(
                new GUIContent("笔刷大小", "相对画面短边"), _brushRadius, 0.005f, 0.5f);
            _brushHardness = EditorGUILayout.Slider(
                new GUIContent("硬度", "0 全羽化，1 硬边"), _brushHardness, 0f, 1f);
            _brushFlow = EditorGUILayout.Slider(
                new GUIContent("流量", "单笔的强度。调低了可以一层层叠出柔和的过渡"), _brushFlow, 0.02f, 1f);
            if (EditorGUI.EndChangeCheck()) Repaint();

            if (GUILayout.Button("清空这支笔刷"))
                _pendingAction = ClearCurrentBrush;
        }

        /// <summary>排队到 Update：清空是往 RT 上画东西，不能在 OnGUI 里做。</summary>
        void ClearCurrentBrush()
        {
            var part = _gui.Masks.PaintingPart;
            if (part == null || part.brushId < 0 || part.brushId >= _brushes.Count) return;
            if (!(_brushes[part.brushId] is RenderTexture rt) || rt == null) return;

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
            _dirty = true;
            Repaint();
        }

        /// <summary>画布上的涂抹。返回 true 表示这次事件被笔刷吃掉了，画布别再拿去平移。</summary>
        bool HandleBrushInput(Rect img)
        {
            var part = _gui.Masks.PaintingPart;
            if (part == null || _full == null || part.brushId < 0) return false;

            EditorGUIUtility.AddCursorRect(img, MouseCursor.ArrowPlus);

            var e = Event.current;
            bool down = e.type == EventType.MouseDown && e.button == 0 && img.Contains(e.mousePosition);
            bool drag = e.type == EventType.MouseDrag && e.button == 0 && _dabbing;
            if (!down && !drag)
            {
                if (e.type == EventType.MouseUp) _dabbing = false;
                return _dabbing;
            }

            float u = (e.mousePosition.x - img.x) / Mathf.Max(img.width, 1f);
            float v = 1f - (e.mousePosition.y - img.y) / Mathf.Max(img.height, 1f);
            var uv = CanvasUvToSource(new Vector2(u, v));

            var dab = new Dab { brush = part.brushId, radius = _brushRadius, erase = e.alt };

            if (down) { _dabbing = true; _lastDabUv = uv; dab.uv = uv; _dabs.Add(dab); }
            else
            {
                // 拖快了两个采样点之间会断开，中间补几笔
                float dist = Vector2.Distance(uv, _lastDabUv);
                int steps = Mathf.Clamp(Mathf.CeilToInt(dist / Mathf.Max(_brushRadius * 0.4f, 0.002f)), 1, 64);
                for (int i = 1; i <= steps; i++)
                {
                    dab.uv = Vector2.Lerp(_lastDabUv, uv, i / (float)steps);
                    _dabs.Add(dab);
                }
                _lastDabUv = uv;
            }

            e.Use();
            Repaint();
            return true;
        }

        void ReleasePreview()
        {
            if (_preview == null) return;
            _preview.Release();
            DestroyImmediate(_preview);
            _preview = null;
        }

        #endregion

        #region 胶片条

        static readonly string[] SortNames = { "文件名", "日期", "星级" };
        static readonly string[] FilterNames =
            { "全部", "只看留用", "不看排除", "★1+", "★2+", "★3+", "★4+", "★5" };

        void DrawFilmstrip(Rect r)
        {
            EditorGUI.DrawRect(r, GradeSkin.Trough);

            var bar = new Rect(r.x + 4f, r.y + 2f, r.width - 8f, 16f);
            DrawFilmBar(bar);

            if (_lib.Count == 0)
            {
                EditorGUI.LabelField(new Rect(r.x, r.y + 20f, r.width, r.height - 20f),
                                     "图片列表为空", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            HandleFilmKeys();

            var view = new Rect(r.x, r.y + 20f, r.width, r.height - 22f);
            var vis = _lib.Visible;

            if (vis.Count == 0)
            {
                EditorGUI.LabelField(view, "当前筛选下没有图片", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            float rowW = vis.Count * (ThumbSize + 8f) + 8f;
            _filmScroll = GUI.BeginScrollView(view, _filmScroll,
                new Rect(0f, 0f, rowW, view.height - 16f), false, false);

            for (int i2 = 0; i2 < vis.Count; i2++)
            {
                var e = vis[i2];
                var cell = new Rect(8f + i2 * (ThumbSize + 8f), 2f, ThumbSize, ThumbSize);
                bool isCurrent = ReferenceEquals(e, _lib.Current);
                bool isSelected = _lib.Selected.Contains(e);

                // 当前那张用实心边框，同选中的其它张用半透明——
                // 多选时必须一眼看出"大图在看哪一张"
                if (isCurrent || isSelected)
                {
                    var ring = new Rect(cell.x - 3f, cell.y - 3f, cell.width + 6f, cell.height + 6f);
                    EditorGUI.DrawRect(ring, isCurrent
                        ? GradeSkin.Accent
                        : new Color(GradeSkin.Accent.r, GradeSkin.Accent.g, GradeSkin.Accent.b, 0.45f));
                }

                EditorGUI.DrawRect(cell, new Color(0.06f, 0.07f, 0.08f));
                if (e.thumb != null && Event.current.type == EventType.Repaint)
                    GUI.DrawTexture(cell, e.thumb, ScaleMode.ScaleToFit);

                DrawThumbBadges(cell, e);

                if (GUI.Button(cell, GUIContent.none, GUIStyle.none)) ClickThumb(e);
            }

            GUI.EndScrollView();
        }

        /// <summary>缩略图上的角标：留用/排除在左上，星级在底边。</summary>
        void DrawThumbBadges(Rect cell, PhotoEntry e)
        {
            if (Event.current.type != EventType.Repaint) return;

            if (e.flag != 0)
            {
                var b = new Rect(cell.x + 2f, cell.y + 2f, 12f, 12f);
                EditorGUI.DrawRect(b, e.flag > 0 ? new Color(0.35f, 0.75f, 0.4f)
                                                 : new Color(0.8f, 0.3f, 0.3f));
                GUI.Label(new Rect(b.x, b.y - 2f, 14f, 16f), e.flag > 0 ? "✓" : "✕",
                          EditorStyles.miniLabel);
            }

            if (e.rating <= 0) return;

            // 底边一条压暗的带子衬星星，亮图上才看得清
            var strip = new Rect(cell.x, cell.yMax - 13f, cell.width, 13f);
            EditorGUI.DrawRect(strip, new Color(0f, 0f, 0f, 0.55f));
            GUI.Label(new Rect(strip.x + 2f, strip.y - 2f, strip.width, 16f),
                      new string('★', e.rating), EditorStyles.miniLabel);
        }

        void DrawFilmBar(Rect r)
        {
            float x = r.x;

            int sort = EditorGUI.Popup(new Rect(x, r.y, 60f, 16f), (int)_lib.Sort, SortNames,
                                       EditorStyles.miniButton);
            if (sort != (int)_lib.Sort) _lib.Sort = (PhotoSort)sort;
            x += 62f;

            if (GUI.Button(new Rect(x, r.y, 22f, 16f),
                           new GUIContent(_lib.Descending ? "▼" : "▲", "升序 / 降序"),
                           EditorStyles.miniButton))
                _lib.Descending = !_lib.Descending;
            x += 24f;

            int filt = EditorGUI.Popup(new Rect(x, r.y, 74f, 16f), (int)_lib.Filter, FilterNames,
                                       EditorStyles.miniButton);
            if (filt != (int)_lib.Filter) _lib.Filter = (PhotoFilter)filt;
            x += 78f;

            using (new EditorGUI.DisabledScope(_lib.Visible.Count == 0))
                if (GUI.Button(new Rect(x, r.y, 44f, 16f), "全选", EditorStyles.miniButton))
                    _lib.SelectAllVisible();
            x += 46f;

            using (new EditorGUI.DisabledScope(_lib.Selected.Count == 0))
                if (GUI.Button(new Rect(x, r.y, 58f, 16f),
                               new GUIContent("移出列表", "只从列表拿掉，不动磁盘上的文件"),
                               EditorStyles.miniButton))
                    _pendingAction = RemoveSelected;
            x += 62f;

            GUI.Label(new Rect(x, r.y - 1f, r.xMax - x, 18f),
                      $"{_lib.IndexOfVisible(_lib.Current) + 1} / {_lib.Visible.Count}" +
                      (_lib.Selected.Count > 1 ? $"　选中 {_lib.Selected.Count}" : "") +
                      "　←→ 切换 · Ctrl/Shift 多选 · 0~5 打分 · P 留用 X 排除 U 取消",
                      EditorStyles.miniLabel);
        }

        void ClickThumb(PhotoEntry e)
        {
            var ev = Event.current;
            if (ev.shift) _lib.SelectRange(e);
            else if (ev.control || ev.command) _lib.Toggle(e);
            else _lib.SelectOnly(e);

            LoadEntry(_lib.Current);
            Repaint();
        }

        void HandleFilmKeys()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown || EditorGUIUtility.editingTextField) return;

            if (e.keyCode == KeyCode.LeftArrow) { LoadEntry(_lib.Step(-1)); e.Use(); return; }
            if (e.keyCode == KeyCode.RightArrow) { LoadEntry(_lib.Step(1)); e.Use(); return; }

            // 打分和标记按 Lightroom 的键位，成批作用于所有选中的
            int stars = -1;
            if (e.keyCode >= KeyCode.Alpha0 && e.keyCode <= KeyCode.Alpha5) stars = e.keyCode - KeyCode.Alpha0;
            else if (e.keyCode >= KeyCode.Keypad0 && e.keyCode <= KeyCode.Keypad5) stars = e.keyCode - KeyCode.Keypad0;

            if (stars >= 0)
            {
                _lib.ApplyToSelection(x => _lib.SetRating(x, stars));
                e.Use(); Repaint(); return;
            }

            int flag = 99;
            if (e.keyCode == KeyCode.P) flag = 1;
            else if (e.keyCode == KeyCode.X) flag = -1;
            else if (e.keyCode == KeyCode.U) flag = 0;

            if (flag != 99)
            {
                _lib.ApplyToSelection(x => _lib.SetFlag(x, flag));
                e.Use(); Repaint();
            }
        }

        /// <summary>只从列表拿掉，不动磁盘。真删文件是另一回事，不该藏在这种按钮后面。</summary>
        void RemoveSelected()
        {
            var gone = new List<PhotoEntry>(_lib.Selected);
            if (gone.Count == 0) return;

            bool hadCurrent = gone.Contains(_lib.Current);
            foreach (var e in gone)
            {
                if (e.thumb != null) DestroyImmediate(e.thumb);
                _store.Remove(e.path);
                _lib.Remove(e);
            }

            if (hadCurrent)
            {
                if (_full != null) { DestroyImmediate(_full); _full = null; }
                _repair.Spots.Clear();
                _repairDirty = true;
                _loadedPath = null;
                if (_lib.Visible.Count > 0) Select(_lib.Visible[0]);
            }
            Repaint();
        }

        /// <summary>单选一张并载入。</summary>
        void Select(PhotoEntry e)
        {
            if (e == null) return;
            _lib.SelectOnly(e);
            LoadEntry(e);
        }

        /// <summary>
        /// 把某张载进大图。只管载入，不碰选中状态——
        /// 多选的时候选中集已经由 PhotoLibrary 定好了，这里再动一次就会把它清掉。
        /// </summary>
        void LoadEntry(PhotoEntry e)
        {
            if (e == null) return;
            if (e.path == _loadedPath && _full != null) return;

            // 换图之前先把上一张的修补和参数收好
            StashRepairs();
            StashSettings();
            _loadedPath = e.path;

            if (_full != null) { DestroyImmediate(_full); _full = null; }
            _full = LoadTextureFromFile(e.path);

            // 取回这张图自己的参数。没有就沿用当前这套当起点——
            // 正在开发一个 look 时，翻到下一张不该被打回默认值
            var rec = _store.GetOrCreate(e.path);
            if (rec.hasSettings && rec.settings != null) _settings.CopyFrom(rec.settings);
            _store.PutSettings(e.path, _settings);

            // 取回这张图自己的修补。找源用的缩略图也得作废，
            // 否则会拿上一张图去搜取样点
            _repair.Spots.Clear();
            if (rec.repairs != null) _repair.Spots.AddRange(rec.repairs);
            _repair.InvalidateProbe();
            _cloneSource = null;
            _repairDirty = true;

            // 换图要复位「改过没」，否则上一张的改动会让这一张也存一份冗余快照
            _snapChanged = false;
            ReleaseSky();

            _canvas.FitPending = true;
            _dirty = true;
#if LOVE_SENTIS
            ReleaseMask();   // 换图了，旧蒙版不再对应
#endif
            Repaint();
        }

        #endregion

        #region 导入

        void OpenFiles()
        {
            string path = EditorUtility.OpenFilePanel("打开图片", "", "png,jpg,jpeg");
            if (string.IsNullOrEmpty(path)) return;
            AddFile(path);
        }

        void OpenFolder()
        {
            string dir = EditorUtility.OpenFolderPanel("打开文件夹", "", "");
            if (string.IsNullOrEmpty(dir)) return;
            AddFolder(dir);
        }

        void AddFolder(string dir)
        {
            var files = new List<string>();
            foreach (var pattern in new[] { "*.png", "*.jpg", "*.jpeg", "*.arw" })
                files.AddRange(Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly));
            files.Sort();
            foreach (var f in files) AddFile(f);
        }

        /// <summary>只排队，真正的载入放到 Update 里做——生成缩略图要 Blit，不能在 GUI 里跑。</summary>
        void AddFile(string path, bool selectIt = true)
        {
            if (_lib.Contains(path)) return;
            if (_pendingImports.Contains(path)) return;
            _pendingImports.Add(path);
            if (selectIt) _pendingSelect = -2;   // -2 = 导入完选中第一张新加的
        }

        void ProcessPendingImports()
        {
            int firstNew = _lib.Count;
            PhotoEntry firstAdded = null;

            try
            {
                for (int i = 0; i < _pendingImports.Count; i++)
                {
                    string path = _pendingImports[i];
                    if (_pendingImports.Count > 3 && EditorUtility.DisplayCancelableProgressBar(
                            "载入图片", Path.GetFileName(path), (float)i / _pendingImports.Count)) break;

                    // 缩略图不需要全解 RAW：一张 6100 万像素的 ARW 要十秒，
                    // 而机内预览是现成的，128 像素的缩略图用它完全够
                    var full = SonyRawImporter.IsRaw(path)
                        ? SonyRawImporter.LoadPreviewOnly(path) ?? LoadTextureFromFile(path)
                        : LoadTextureFromFile(path);
                    if (full == null) { Debug.LogError($"[修图台] 读不了这个文件：{path}"); continue; }

                    var added = _lib.Add(path, Path.GetFileNameWithoutExtension(path),
                                         MakeThumbnail(full, ThumbPixels));
                    if (firstAdded == null) firstAdded = added;
                    // 缩略图做完就把原图丢掉，选中时再按需加载
                    DestroyImmediate(full);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _pendingImports.Clear();
            }

            if (_lib.Count > firstNew && (_lib.Current == null || _pendingSelect == -2))
                Select(firstAdded);
            _pendingSelect = -1;
        }

        /// <summary>把当前图的参数记到路径下。换图前要调，否则改动会跟着丢。</summary>
        void StashSettings()
        {
            if (!string.IsNullOrEmpty(_loadedPath)) _store.PutSettings(_loadedPath, _settings);
        }

        /// <summary>把当前图的修补记到路径下。换图和关窗前都要调。</summary>
        void StashRepairs()
        {
            if (string.IsNullOrEmpty(_loadedPath)) return;
            _store.PutRepairs(_loadedPath, _repair.Spots);
        }

        void ClearEntries()
        {
            foreach (var e in _lib.All) if (e.thumb != null) DestroyImmediate(e.thumb);
            // 只清列表，不清编辑记录——下次把同一批图拖回来，调过的还在
            _lib.Clear();
            _loadedPath = null;
            if (_full != null) { DestroyImmediate(_full); _full = null; }
            ReleasePreview();
            Repaint();
        }

        static bool IsImage(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".arw";
        }

        static Texture2D LoadTextureFromFile(string path)
        {
            if (SonyRawImporter.IsRaw(path)) return LoadRaw(path);

            try
            {
                // linear:false —— png/jpg 里存的是 sRGB，按 sRGB 采样管线才对
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
                { name = Path.GetFileNameWithoutExtension(path), hideFlags = HideFlags.HideAndDontSave };
                if (tex.LoadImage(File.ReadAllBytes(path))) return tex;
                DestroyImmediate(tex);
            }
            catch (System.Exception e) { Debug.LogError($"[修图台] 读取失败：{e.Message}"); }
            return null;
        }

        // RAW 的解码选项。存 EditorPrefs：这是「怎么读文件」的偏好，
        // 不属于调色参数，不该跟着预设走
        const string PrefRawHalf = "PhotoGrade.rawHalfSize";
        const string PrefRawAuto = "PhotoGrade.rawAutoExposure";
        const string PrefRawMat  = "PhotoGrade.rawColorMatrix";

        static SonyRawImporter.Options RawOptions => new SonyRawImporter.Options
        {
            downscale = EditorPrefs.GetBool(PrefRawHalf, false) ? 2 : 1,
            autoExposure = EditorPrefs.GetBool(PrefRawAuto, true),
            applyColorMatrix = EditorPrefs.GetBool(PrefRawMat, true),
        };

        static Texture2D LoadRaw(string path)
        {
            var res = SonyRawImporter.Load(path, RawOptions);

            // 解不了原始数据时会退回机内 JPEG 预览，那时 texture 和 error 会同时有值。
            // 这种情况必须说出来——不然用户拿到的是一张 8bit 的小图却以为是 RAW
            if (!string.IsNullOrEmpty(res.error))
            {
                if (res.texture != null) Debug.LogWarning($"[修图台] {Path.GetFileName(path)}：{res.error}");
                else Debug.LogError($"[修图台] {Path.GetFileName(path)}：{res.error}");
            }
            else if (!string.IsNullOrEmpty(res.info))
            {
                Debug.Log($"[修图台] RAW 已解码 {Path.GetFileName(path)}：{res.info}");
            }

            return res.texture;
        }

        void DrawRawBar()
        {
            EditorGUILayout.LabelField("RAW 解码（索尼 ARW）", EditorStyles.boldLabel);

            PrefToggle("半尺寸导入", PrefRawHalf, false,
                       "6100 万像素的全尺寸会让每一步都很吃内存。半尺寸直接用 2x2 拜耳块合成，" +
                       "不做插值，细节反而比全尺寸更干净。");
            PrefToggle("自动曝光归一化", PrefRawAuto, true,
                       "把高光推到接近满值。只是一个标量，不是曲线，关掉就是相机的原始电平。");
            PrefToggle("套用相机色彩矩阵", PrefRawMat, true,
                       "内置的是 ILCE-7RM4 的系数，其它索尼机身方向对但有偏差。" +
                       "要准确的颜色，关掉它，用上面的色卡校色解一个属于你这台机器的矩阵。");

            EditorGUILayout.HelpBox("改完要重新导入才生效。压缩 ARW 暂不支持，会退回机内 JPEG 预览。",
                                    MessageType.None);
        }

        static void PrefToggle(string label, string key, bool def, string tip)
        {
            bool cur = EditorPrefs.GetBool(key, def);
            bool v = EditorGUILayout.Toggle(new GUIContent(label, tip), cur);
            if (v != cur) EditorPrefs.SetBool(key, v);
        }

        static Texture2D MakeThumbnail(Texture2D src, int maxSize)
        {
            float k = Mathf.Min(1f, maxSize / (float)Mathf.Max(src.width, src.height));
            int tw = Mathf.Max(1, Mathf.RoundToInt(src.width * k));
            int th = Mathf.Max(1, Mathf.RoundToInt(src.height * k));

            var rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Graphics.Blit(src, rt);

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var thumb = new Texture2D(tw, th, TextureFormat.RGBA32, false, false)
            { hideFlags = HideFlags.HideAndDontSave };
            thumb.ReadPixels(new Rect(0f, 0f, tw, th), 0, 0, false);
            thumb.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            return thumb;
        }

        #endregion

        #region LUT 导入导出

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

            if (_lut != null)
            {
                EditorGUILayout.LabelField($"已载入 {_lutName}（{_lut.width}³）", EditorStyles.miniLabel);
                EditorGUI.BeginChangeCheck();
                _lutAmount = EditorGUILayout.Slider("强度", _lutAmount, 0f, 1f);
                if (EditorGUI.EndChangeCheck()) _dirty = true;
            }

            using (new EditorGUI.DisabledScope(Renderer == null))
                if (GUILayout.Button("把当前参数烘成 .cube…")) _pendingAction = ExportLut;
        }

        void ImportLut()
        {
            string path = EditorUtility.OpenFilePanel("导入 .cube LUT", "", "cube");
            if (string.IsNullOrEmpty(path)) return;

            var tex = CubeLutIO.Load(path, out string err);
            if (tex == null) { Debug.LogError("[修图台] LUT 导入失败：" + err); return; }

            if (_lut != null) DestroyImmediate(_lut);
            _lut = tex;
            _lutName = Path.GetFileNameWithoutExtension(path);
            _lutAmount = 1f;
            _dirty = true;
            Debug.Log($"[修图台] 已导入 LUT：{_lutName}（{tex.width}³）");
        }

        /// <summary>
        /// 把当前调色参数烘成 .cube。
        ///
        /// 做法是拿一张单位 LUT 条带图当普通图片过一遍管线，读回来就是查找表。
        /// 但只能烘"逐像素的颜色变换"——暗角、颗粒、模糊、蒙版这些依赖坐标的效果
        /// 不是颜色映射，塞进 LUT 没有意义，所以烘之前先把它们清零。
        /// </summary>
        void ExportLut()
        {
            var r = Renderer;
            if (r == null) return;

            string path = EditorUtility.SaveFilePanel("导出 .cube", "", "look", "cube");
            if (string.IsNullOrEmpty(path)) return;

            int size = CubeLutIO.DefaultBakeSize;
            var strip = CubeLutIO.BuildIdentityStrip(size);
            var rt = RenderTexture.GetTemporary(size * size, size, 0,
                                                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var readback = new Texture2D(size * size, size, TextureFormat.RGBAFloat, false, true)
            { hideFlags = HideFlags.HideAndDontSave };

            try
            {
                var baked = _settings.Clone();
                // 清掉所有依赖坐标的效果，只留颜色变换
                baked.bloomIntensity = 0f; baked.blur = 0f; baked.backgroundBlur = 0f;
                baked.vignetteIntensity = 0f; baked.grain = 0f; baked.chromatic = 0f;
                baked.dither = 0f; baked.sharpen = 0f; baked.denoise = 0f;
                baked.clarity = 0f; baked.texture = 0f;
                baked.distortK1 = 0f; baked.distortK2 = 0f; baked.distortScale = 1f;
                baked.secondaryEnabled = false; baked.showMask = false;
                baked.zebraHigh = 0f; baked.zebraLow = 0f;

                r.Render(strip, rt, baked, new VideoGradeRenderer.Options
                {
                    lut = _lut,
                    lutAmount = _lutAmount,
                });

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0f, 0f, size * size, size), 0, 0, false);
                readback.Apply(false, false);
                RenderTexture.active = prev;

                if (CubeLutIO.WriteCube(path, readback, size, Path.GetFileNameWithoutExtension(path), out string err))
                {
                    Debug.Log($"[修图台] 已导出 LUT：{path}（{size}³）");
                    EditorUtility.RevealInFinder(path);
                }
                else Debug.LogError("[修图台] LUT 导出失败：" + err);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
                DestroyImmediate(readback);
                DestroyImmediate(strip);
                _dirty = true;   // 烘焙改过材质参数，让预览重算
            }
        }

        #endregion

        #region 色卡校色

        /// <summary>图片 uv (0~1, 左下原点) 转屏幕坐标。</summary>
        static Vector2 ChartUvToScreen(Rect img, Vector2 uv) =>
            new Vector2(img.x + uv.x * img.width, img.yMax - uv.y * img.height);

        static Vector2 ChartScreenToUv(Rect img, Vector2 p) => new Vector2(
            Mathf.Clamp01((p.x - img.x) / Mathf.Max(1f, img.width)),
            Mathf.Clamp01((img.yMax - p.y) / Mathf.Max(1f, img.height)));

        /// <summary>在画布上叠加色卡的 6x4 网格和四个可拖角点。</summary>
        void DrawChartOverlay(Rect canvas, Rect img)
        {
            var e = Event.current;

            // 先处理输入，再绘制——绘制只在 Repaint，输入在别的事件
            if (e.type == EventType.MouseDown && e.button == 0 && canvas.Contains(e.mousePosition))
            {
                for (int i = 0; i < 4; i++)
                {
                    if (Vector2.Distance(e.mousePosition, ChartUvToScreen(img, _chartCorners[i])) > 12f) continue;
                    _chartDragIndex = i;
                    e.Use();
                    break;
                }
            }
            else if (e.type == EventType.MouseDrag && _chartDragIndex >= 0)
            {
                _chartCorners[_chartDragIndex] = ChartScreenToUv(img, e.mousePosition);
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseUp && _chartDragIndex >= 0)
            {
                _chartDragIndex = -1;
                e.Use();
            }

            if (e.type != EventType.Repaint) return;

            GUI.BeginGroup(canvas);
            Color prev = Handles.color;

            // 6x4 网格：让你直观确认每格是不是对准了色块
            Handles.color = new Color(1f, 1f, 1f, 0.5f);
            for (int row = 0; row <= ColorCheckerSolver.Rows; row++)
            {
                float v = row / (float)ColorCheckerSolver.Rows;
                Vector2 a = Vector2.Lerp(_chartCorners[0], _chartCorners[3], v);
                Vector2 b = Vector2.Lerp(_chartCorners[1], _chartCorners[2], v);
                DrawChartLine(canvas, img, a, b);
            }
            for (int col = 0; col <= ColorCheckerSolver.Columns; col++)
            {
                float u = col / (float)ColorCheckerSolver.Columns;
                Vector2 a = Vector2.Lerp(_chartCorners[0], _chartCorners[1], u);
                Vector2 b = Vector2.Lerp(_chartCorners[3], _chartCorners[2], u);
                DrawChartLine(canvas, img, a, b);
            }

            // 四角手柄，左上角标个记号提示顺序
            for (int i = 0; i < 4; i++)
            {
                Vector2 p = ChartUvToScreen(img, _chartCorners[i]) - new Vector2(canvas.x, canvas.y);
                var box = new Rect(p.x - 5f, p.y - 5f, 10f, 10f);
                EditorGUI.DrawRect(box, i == 0 ? GradeSkin.Playhead : Color.white);
                EditorGUI.DrawRect(new Rect(box.x + 2f, box.y + 2f, 6f, 6f), new Color(0f, 0f, 0f, 0.6f));
            }

            Handles.color = prev;
            GUI.EndGroup();
        }

        void DrawChartLine(Rect canvas, Rect img, Vector2 uvA, Vector2 uvB)
        {
            Vector2 a = ChartUvToScreen(img, uvA) - new Vector2(canvas.x, canvas.y);
            Vector2 b = ChartUvToScreen(img, uvB) - new Vector2(canvas.x, canvas.y);
            Handles.DrawAAPolyLine(1.5f, new Vector3(a.x, a.y, 0f), new Vector3(b.x, b.y, 0f));
        }

        void DrawChartBar()
        {
            EditorGUILayout.LabelField("色卡校色", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _chartMode = EditorGUILayout.Toggle("标定模式", _chartMode);
            if (EditorGUI.EndChangeCheck()) Repaint();

            if (_chartMode)
            {
                EditorGUILayout.HelpBox(
                    "拖四个角点框住色卡，黄色手柄是左上角（深肤色那一格）。\n" +
                    "网格对准 24 格之后点解算。", MessageType.Info);

                if (GUILayout.Button("重置角点")) { _chartCorners = DefaultChartCorners(); Repaint(); }

                using (new EditorGUI.DisabledScope(_full == null))
                {
                    if (GUILayout.Button("解算校色矩阵", GUILayout.Height(24f)))
                        _pendingAction = SolveColorChart;
                }
            }

            if (_settings.colorMatrixEnabled && GUILayout.Button("清除矩阵"))
            {
                Undo.RecordObject(this, "清除校色矩阵");
                _settings.ResetColorMatrix();
                _chartStatus = "";
                _dirty = true;
            }

            // 角点存在源图 uv 里，而画布显示的是变换后的画面，两者对不上号
            if (_chartMode && _settings.HasGeometry)
                EditorGUILayout.HelpBox("画面有裁剪或旋转，色卡角点会和画布对不上。先把「裁剪与旋转」重置再标定。",
                                        MessageType.Warning);

            if (!string.IsNullOrEmpty(_chartStatus))
                EditorGUILayout.LabelField(_chartStatus, EditorStyles.miniLabel);
        }

        void SolveColorChart()
        {
            if (_full == null) return;

            // uv 原点在左下，GetPixel 也是左下，所以直接乘尺寸即可
            var px = new Vector2[4];
            for (int i = 0; i < 4; i++)
                px[i] = new Vector2(_chartCorners[i].x * _full.width, _chartCorners[i].y * _full.height);

            // 采样半径按格子大小走，太大就会采到邻格
            float cellW = Vector2.Distance(px[0], px[1]) / ColorCheckerSolver.Columns;
            float cellH = Vector2.Distance(px[0], px[3]) / ColorCheckerSolver.Rows;
            int radius = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(cellW, cellH) * 0.25f), 1, 24);

            var measured = ColorCheckerSolver.SamplePatches(_full, px, radius);
            if (measured == null) { _chartStatus = "采样失败"; return; }

            var m = ColorCheckerSolver.Solve(measured, out float residual);
            if (m == null) { _chartStatus = "求解失败：矩阵奇异，检查角点是否框对"; return; }

            Undo.RecordObject(this, "解算校色矩阵");
            _settings.colorMatrix = m;
            _settings.colorMatrixEnabled = true;
            _dirty = true;

            // 残差是线性空间的均方根误差。经验上 0.02 以内算好，
            // 超过 0.06 基本说明角点没对准、色卡过曝或者光线不均
            string quality = residual < 0.02f ? "很好" : residual < 0.06f ? "可用" : "偏差大，检查角点和曝光";
            _chartStatus = $"已解算，采样半径 {radius}px，残差 {residual:0.0000}（{quality}）";
        }

        #endregion

        #region AI 主体蒙版

#if LOVE_SENTIS
        Texture CurrentMask => _maskRefined != null ? (Texture)_maskRefined : _maskRaw;

        void ReleaseMask()
        {
            if (_maskRaw != null) { DestroyImmediate(_maskRaw); _maskRaw = null; }
            if (_maskRefined != null) { _maskRefined.Release(); DestroyImmediate(_maskRefined); _maskRefined = null; }
            _maskStatus = "";
            _dirty = true;
        }

        void DrawMaskBar()
        {
            EditorGUILayout.LabelField("AI 主体蒙版", EditorStyles.boldLabel);

            _maskModelIndex = EditorGUILayout.Popup(_maskModelIndex,
                System.Array.ConvertAll(AiMaskGenerator.Presets, m => m.label));

            using (new EditorGUI.DisabledScope(_full == null))
            {
                if (GUILayout.Button(_maskRaw == null ? "生成蒙版" : "重新生成"))
                    _pendingAction = GenerateMask;
            }

            if (_maskRaw != null)
            {
                EditorGUI.BeginChangeCheck();
                _maskRefine = EditorGUILayout.Toggle("边缘精修", _maskRefine);
                using (new EditorGUI.DisabledScope(!_maskRefine))
                    _maskSigmaColor = EditorGUILayout.Slider("  贴边程度", _maskSigmaColor, 0.02f, 0.4f);
                if (EditorGUI.EndChangeCheck()) _pendingAction = RefineMask;

                if (GUILayout.Button("清除蒙版")) _pendingAction = ReleaseMask;
            }

            if (!string.IsNullOrEmpty(_maskStatus))
                EditorGUILayout.LabelField(_maskStatus, EditorStyles.miniLabel);

            EditorGUILayout.Space(8f);
            DrawDenoiseBar();
        }

        void DrawDenoiseBar()
        {
            EditorGUILayout.LabelField("AI 降噪", EditorStyles.boldLabel);

            _dnModelIndex = EditorGUILayout.Popup(_dnModelIndex,
                System.Array.ConvertAll(AiDenoiser.Presets, m => m.label));

            // 先量一下这张有多噪，给个起手强度。量的是缩略图，够用了
            var e = _lib.Current;
            if (e != null && e.thumb != null && _dnNoisePath != e.path)
            {
                _dnNoise = NoiseEstimate.Analyze(e.thumb.GetPixels32(), e.thumb.width, e.thumb.height);
                _dnNoisePath = e.path;
            }

            if (_dnNoise.valid)
                EditorGUILayout.LabelField(
                    $"实测噪声：亮 {_dnNoise.luma * 255f:F1}／色 {_dnNoise.chroma * 255f:F1}（0~255 的尺度）",
                    EditorStyles.miniLabel);

            bool busy = _denoiser != null && _denoiser.Running;

            using (new EditorGUI.DisabledScope(_full == null || busy))
            {
                if (GUILayout.Button(_dnRaw == null ? "生成降噪" : "重新生成"))
                    _pendingAction = StartDenoise;
            }

            if (busy)
            {
                var r = EditorGUILayout.GetControlRect(GUILayout.Height(18f));
                EditorGUI.ProgressBar(r, _denoiser.Progress, $"{_denoiser.Progress * 100f:F0}%");
                if (GUILayout.Button("取消"))
                {
                    _denoiser.Cancel();
                    _dnStatus = "已取消";
                }
            }
            else if (_dnRaw != null)
            {
                EditorGUI.BeginChangeCheck();
                _dnStrength = EditorGUILayout.Slider(
                    new GUIContent("强度", "和原图混合的比例。模型总会抹掉一点东西，" +
                                          "干净的图上强度拉满反而丢细节"),
                    _dnStrength, 0f, 1f);
                if (EditorGUI.EndChangeCheck()) _pendingAction = BlendDenoise;

                if (GUILayout.Button("清除降噪")) _pendingAction = ReleaseDenoise;
            }

            if (!string.IsNullOrEmpty(_dnStatus))
                EditorGUILayout.LabelField(_dnStatus, EditorStyles.miniLabel);
        }

        void StartDenoise()
        {
            if (_full == null || _lib.Current == null) return;
            if (_denoiser == null) _denoiser = new AiDenoiser();

            var spec = AiDenoiser.Presets[Mathf.Clamp(_dnModelIndex, 0, AiDenoiser.Presets.Length - 1)];

            if (!_denoiser.Begin(_full, spec, Unity.Sentis.BackendType.GPUCompute, out string err))
            {
                _dnStatus = "失败：" + err;
                return;
            }

            _dnPath = _lib.Current.path;
            _dnStatus = $"切成 {_denoiser.TileCount} 块，开始…";

            // 起手强度按实测噪声给，别让人对着一张本来就干净的图拉满
            if (_dnNoise.valid) _dnStrength = NoiseEstimate.SuggestStrength(_dnNoise.luma);
        }

        void FinishDenoise()
        {
            if (_denoiser == null) return;

            if (_dnRaw != null) { _dnRaw.Release(); DestroyImmediate(_dnRaw); }
            _dnRaw = _denoiser.Result;
            _denoiser.ReleaseResult();   // 所有权交出来，下一轮 Begin 不要再动它

            _dnStatus = _dnRaw != null ? $"降噪完成，{_denoiser.TileCount} 块" : "没有结果";
            BlendDenoise();
        }

        /// <summary>
        /// 按强度把降噪结果和原图混起来。
        ///
        /// 用 DrawTexture 的 alpha 混合直接做 lerp，省一个 shader——
        /// 和贴水印是同一个路子。
        /// </summary>
        void BlendDenoise()
        {
            if (_dnRaw == null || _full == null) return;

            if (_dnBlended == null || _dnBlended.width != _dnRaw.width || _dnBlended.height != _dnRaw.height)
            {
                if (_dnBlended != null) { _dnBlended.Release(); DestroyImmediate(_dnBlended); }
                _dnBlended = new RenderTexture(_dnRaw.width, _dnRaw.height, 0,
                                               RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { hideFlags = HideFlags.HideAndDontSave };
                _dnBlended.Create();
            }

            Graphics.Blit(_full, _dnBlended);

            float a = Mathf.Clamp01(_dnStrength);
            if (a > 0.001f)
            {
                var prev = RenderTexture.active;
                RenderTexture.active = _dnBlended;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, _dnBlended.width, _dnBlended.height, 0f);
                Graphics.DrawTexture(new Rect(0f, 0f, _dnBlended.width, _dnBlended.height), _dnRaw,
                                     new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0,
                                     new Color(1f, 1f, 1f, a));
                GL.PopMatrix();
                RenderTexture.active = prev;
            }

            _dirty = true;
            Repaint();
        }

        void ReleaseDenoise()
        {
            _denoiser?.Cancel();
            if (_dnRaw != null) { _dnRaw.Release(); DestroyImmediate(_dnRaw); _dnRaw = null; }
            if (_dnBlended != null) { _dnBlended.Release(); DestroyImmediate(_dnBlended); _dnBlended = null; }
            _dnPath = null;
            _dnStatus = "";
            _dirty = true;
        }

        void GenerateMask()
        {
            if (_full == null) return;
            if (_maskGen == null) _maskGen = new AiMaskGenerator();

            var spec = AiMaskGenerator.Presets[Mathf.Clamp(_maskModelIndex, 0, AiMaskGenerator.Presets.Length - 1)];

            if (_maskRaw != null) { DestroyImmediate(_maskRaw); _maskRaw = null; }
            _maskRaw = _maskGen.Generate(_full, spec, Unity.Sentis.BackendType.GPUCompute, true, out string err);

            _maskStatus = _maskRaw != null
                ? $"已生成 {_maskRaw.width}px，{_maskGen.LastMs:0} ms"
                : "失败：" + err;

            if (_maskRaw != null) RefineMask();
            _dirty = true;
        }

        /// <summary>
        /// 用彩色原图当引导把蒙版边界吸附到颜色边缘，并放大到接近原图的分辨率。
        /// 模型输出只有 1024px，直接拉到 4000px 会糊。
        /// </summary>
        void RefineMask()
        {
            if (_maskRaw == null || _full == null) return;

            if (!_maskRefine)
            {
                if (_maskRefined != null) { _maskRefined.Release(); DestroyImmediate(_maskRefined); _maskRefined = null; }
                _dirty = true;
                return;
            }

            if (_maskRefineMat == null)
            {
                var sh = Shader.Find("Hidden/Love/DepthRefine");
                if (sh == null) { _maskStatus = "找不到 Hidden/Love/DepthRefine"; return; }
                _maskRefineMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }

            const int MaxSide = 2048;
            float k = Mathf.Min(1f, MaxSide / (float)Mathf.Max(_full.width, _full.height));
            int w = Mathf.Max(1, Mathf.RoundToInt(_full.width * k));
            int h = Mathf.Max(1, Mathf.RoundToInt(_full.height * k));

            if (_maskRefined == null || _maskRefined.width != w || _maskRefined.height != h)
            {
                if (_maskRefined != null) { _maskRefined.Release(); DestroyImmediate(_maskRefined); }
                _maskRefined = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                { name = "RefinedMask", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
                _maskRefined.Create();
            }

            _maskRefineMat.SetTexture("_DepthTex", _maskRaw);
            _maskRefineMat.SetFloat("_SigmaSpace", 2.0f);
            _maskRefineMat.SetFloat("_SigmaColor", _maskSigmaColor);
            _maskRefineMat.SetFloat("_SampleScale", 1.0f);
            Graphics.Blit(_full, _maskRefined, _maskRefineMat, 0);

            _dirty = true;
        }
#else
        Texture CurrentMask => null;
        void DrawMaskBar()
        {
            EditorGUILayout.LabelField("AI 主体蒙版", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("需要安装 com.unity.sentis 才能用。", MessageType.None);
        }
#endif

        #endregion

        #region 裁剪叠加层

        const float CropHandle = 8f;     // 控制点的半边长，像素
        const float CropGrab = 11f;      // 命中判定半径，比控制点大一圈才好点中

        void DrawCropOverlay(Rect canvas, Rect img)
        {
            var box = CropToScreen(img);
            var e = Event.current;

            if (e.type == EventType.Repaint)
            {
                GUI.BeginGroup(canvas);
                var b = new Rect(box.x - canvas.x, box.y - canvas.y, box.width, box.height);
                var m = new Rect(img.x - canvas.x, img.y - canvas.y, img.width, img.height);

                // 框外压暗，一眼看出要裁掉哪些。四块拼起来而不是画一个带洞的矩形——
                // IMGUI 没有洞这种东西
                var dim = GradeSkin.Dim;
                EditorGUI.DrawRect(new Rect(m.x, m.y, m.width, b.y - m.y), dim);
                EditorGUI.DrawRect(new Rect(m.x, b.yMax, m.width, m.yMax - b.yMax), dim);
                EditorGUI.DrawRect(new Rect(m.x, b.y, b.x - m.x, b.height), dim);
                EditorGUI.DrawRect(new Rect(b.xMax, b.y, m.xMax - b.xMax, b.height), dim);

                // 三分线：构图参考，Lightroom 的裁剪框也是这个
                var thin = GradeSkin.Guide;
                for (int i = 1; i <= 2; i++)
                {
                    EditorGUI.DrawRect(new Rect(b.x + b.width * i / 3f, b.y, 1f, b.height), thin);
                    EditorGUI.DrawRect(new Rect(b.x, b.y + b.height * i / 3f, b.width, 1f), thin);
                }

                var line = GradeSkin.Outline;
                EditorGUI.DrawRect(new Rect(b.x, b.y, b.width, 1f), line);
                EditorGUI.DrawRect(new Rect(b.x, b.yMax - 1f, b.width, 1f), line);
                EditorGUI.DrawRect(new Rect(b.x, b.y, 1f, b.height), line);
                EditorGUI.DrawRect(new Rect(b.xMax - 1f, b.y, 1f, b.height), line);

                foreach (var h in HandlePoints(b))
                    EditorGUI.DrawRect(new Rect(h.x - CropHandle * 0.5f, h.y - CropHandle * 0.5f,
                                                CropHandle, CropHandle), line);

                GUI.EndGroup();
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0 && canvas.Contains(e.mousePosition))
            {
                int hit = HitTest(box, e.mousePosition);
                if (hit < 0) return;

                // Undo 要在改之前记，记在后面 Ctrl+Z 撤不回来
                Undo.RecordObject(this, "裁剪");
                _cropDrag = hit;
                _cropDragOrigin = e.mousePosition;
                _cropAtDragStart = new Vector4(_settings.cropX, _settings.cropY,
                                               _settings.cropW, _settings.cropH);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _cropDrag >= 0)
            {
                // 屏幕 y 向下、uv y 向上，所以纵向要取反
                var d = e.mousePosition - _cropDragOrigin;
                ApplyCropDrag(new Vector2(d.x / Mathf.Max(img.width, 1f),
                                          -d.y / Mathf.Max(img.height, 1f)));
                _dirty = true;
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseUp && _cropDrag >= 0)
            {
                _cropDrag = -1;
                e.Use();
            }
        }

        /// <summary>裁剪框（源图归一化，y 向上）换算成屏幕矩形（y 向下）。</summary>
        Rect CropToScreen(Rect img)
        {
            float x = img.x + _settings.cropX * img.width;
            float y = img.y + (1f - _settings.cropY - _settings.cropH) * img.height;
            return new Rect(x, y, _settings.cropW * img.width, _settings.cropH * img.height);
        }

        /// <summary>八个控制点，顺序和 HitTest / ApplyCropDrag 里的编号一一对应。</summary>
        static Vector2[] HandlePoints(Rect b) => new[]
        {
            new Vector2(b.x, b.yMax), new Vector2(b.xMax, b.yMax),      // 0 左下 1 右下
            new Vector2(b.xMax, b.y), new Vector2(b.x, b.y),            // 2 右上 3 左上
            new Vector2(b.x, b.center.y), new Vector2(b.xMax, b.center.y),   // 4 左 5 右
            new Vector2(b.center.x, b.yMax), new Vector2(b.center.x, b.y),   // 6 下 7 上
        };

        static int HitTest(Rect box, Vector2 mouse)
        {
            var pts = HandlePoints(box);
            for (int i = 0; i < pts.Length; i++)
                if (Vector2.Distance(pts[i], mouse) <= CropGrab) return i;
            return box.Contains(mouse) ? 8 : -1;
        }

        void ApplyCropDrag(Vector2 d)
        {
            const float MinSize = 0.03f;
            var c = _cropAtDragStart;
            float x = c.x, y = c.y, w = c.z, h = c.w;

            if (_cropDrag == 8)
            {
                // 整体平移：框大小不变，只把位置夹在画面内
                x = Mathf.Clamp(c.x + d.x, 0f, 1f - w);
                y = Mathf.Clamp(c.y + d.y, 0f, 1f - h);
            }
            else
            {
                bool left   = _cropDrag == 0 || _cropDrag == 3 || _cropDrag == 4;
                bool right  = _cropDrag == 1 || _cropDrag == 2 || _cropDrag == 5;
                bool bottom = _cropDrag == 0 || _cropDrag == 1 || _cropDrag == 6;
                bool top    = _cropDrag == 2 || _cropDrag == 3 || _cropDrag == 7;

                // 拖左/下边时对边固定，所以要同时改起点和尺寸
                if (left)   { float nx = Mathf.Clamp(c.x + d.x, 0f, c.x + c.z - MinSize); w = c.x + c.z - nx; x = nx; }
                if (right)  { w = Mathf.Clamp(c.z + d.x, MinSize, 1f - c.x); }
                if (bottom) { float ny = Mathf.Clamp(c.y + d.y, 0f, c.y + c.w - MinSize); h = c.y + c.w - ny; y = ny; }
                if (top)    { h = Mathf.Clamp(c.w + d.y, MinSize, 1f - c.y); }
            }

            _settings.cropX = x; _settings.cropY = y;
            _settings.cropW = w; _settings.cropH = h;
        }

        #endregion

        #region 白平衡吸管与自动色调

        void HandlePickInput(Rect img)
        {
            if (!_pickWb || _full == null) return;

            EditorGUIUtility.AddCursorRect(img, MouseCursor.ArrowPlus);

            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0 || !img.Contains(e.mousePosition)) return;

            float u = (e.mousePosition.x - img.x) / Mathf.Max(img.width, 1f);
            float v = 1f - (e.mousePosition.y - img.y) / Mathf.Max(img.height, 1f);

            PickWhiteBalance(new Vector2(u, v));

            _pickWb = false;      // 取一次就退出，免得接下来平移画面又被当成取色
            e.Use();
            Repaint();
        }

        void PickWhiteBalance(Vector2 canvasUv)
        {
            var uv = CanvasUvToSource(canvasUv);
            if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) return;

            int px = Mathf.Clamp(Mathf.RoundToInt(uv.x * (_full.width - 1)), 0, _full.width - 1);
            int py = Mathf.Clamp(Mathf.RoundToInt(uv.y * (_full.height - 1)), 0, _full.height - 1);

            // 取 5x5 的平均。单个像素上的噪点足以让解出来的色温差出几百 K，
            // 而用户点的时候本来就是在指一片区域而不是一个点
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int x = px + dx, y = py + dy;
                if (x < 0 || y < 0 || x >= _full.width || y >= _full.height) continue;
                var c = _full.GetPixel(x, y);
                // GetPixel 给的是贴图里存的值，也就是 gamma 空间；白平衡是线性空间的运算
                sum += new Vector3(Mathf.GammaToLinearSpace(c.r),
                                   Mathf.GammaToLinearSpace(c.g),
                                   Mathf.GammaToLinearSpace(c.b));
                n++;
            }
            if (n == 0) return;

            WhiteBalancePicker.Solve(sum / n, out float temp, out float tint);

            Undo.RecordObject(this, "白平衡吸管");
            _settings.temperature = temp;
            _settings.tint = tint;
            _dirty = true;
        }

        /// <summary>
        /// 分析用的像素。直接拿缩略图，不重新回读原图。
        ///
        /// 缩略图是 128 像素，一张 6000 像素的照片相当于每个缩略图像素平均了两千多个源像素。
        /// 分位数在一万六千个样本上已经很稳，而<b>孤立的镜面高光会被平均掉</b>——
        /// 这恰恰是想要的：几个反光点不该触发高光回收，大片过曝的天空才该。
        ///
        /// 好处是批量自动色调不用把每张原图读一遍，几百张也是瞬时的。
        /// </summary>
        static Color[] AnalysisPixels(PhotoEntry e) =>
            e != null && e.thumb != null ? e.thumb.GetPixels() : null;

        /// <summary>排队到 Update 里跑：Undo 和重渲染都要在 GUI 之外做。</summary>
        void AutoToneCurrent()
        {
            var px = AnalysisPixels(_lib.Current);
            if (px == null) return;

            AutoSnapshot("自动色调之前");
            Undo.RecordObject(this, "自动色调");
            AutoTone.Apply(AutoTone.Analyze(px), _settings, _autoOpt);
            StashSettings();
            _dirty = true;
            Repaint();
        }

        /// <summary>
        /// 对选中的每一张各自算一套。注意不是"把当前这张的参数抄过去"——
        /// 那是同步；自动色调是让每张<b>按自己的分布</b>各站各的。
        /// </summary>
        void AutoToneSelection()
        {
            var list = new List<PhotoEntry>(_lib.Selected.Count > 1 ? _lib.Selected
                                                                    : (IEnumerable<PhotoEntry>)_lib.Visible);
            if (list.Count == 0) return;

            StashSettings();
            int done = 0;

            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (list.Count > 8 && EditorUtility.DisplayCancelableProgressBar(
                            "自动色调", e.name, (float)i / list.Count)) break;

                    var px = AnalysisPixels(e);
                    if (px == null) continue;

                    // 在这张自己已有的参数上改，风格化那些不动
                    var rec = _store.GetOrCreate(e.path);
                    if (!rec.hasSettings || rec.settings == null) _store.PutSettings(e.path, _settings);
                    AutoTone.Apply(AutoTone.Analyze(px), rec.settings, _autoOpt);
                    _store.MarkDirty();
                    done++;
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            // 当前这张的参数要同步回界面上那一套，否则滑条还是旧值
            var curRec = _lib.Current != null ? _store.Get(_lib.Current.path) : null;
            if (curRec != null && curRec.hasSettings && curRec.settings != null)
                _settings.CopyFrom(curRec.settings);

            Debug.Log($"[修图台] 自动色调已应用到 {done} 张");
            _dirty = true;
            Repaint();
        }

        void DrawAutoToneBar()
        {
            EditorGUILayout.LabelField("自适应起手值", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _autoOpt.exposure = EditorGUILayout.Toggle(
                new GUIContent("曝光", "把中位亮度推到中级灰。和色阶一起解——色阶的拉伸会把中位数再推一次"),
                _autoOpt.exposure);
            _autoOpt.levels = EditorGUILayout.Toggle(
                new GUIContent("色阶", "按 0.2% / 99.8% 分位数定黑白位"), _autoOpt.levels);
            _autoOpt.highlightsShadows = EditorGUILayout.Toggle(
                new GUIContent("高光 / 阴影", "只在两端真的挤住了才动"), _autoOpt.highlightsShadows);
            _autoOpt.contrast = EditorGUILayout.Toggle(
                new GUIContent("对比度", "拉完色阶之后中间还挤，说明是平片，加反差"), _autoOpt.contrast);
            _autoOpt.whiteBalance = EditorGUILayout.Toggle(
                new GUIContent("白平衡", "把高光区的平均色中性化，打七折。日落烛光这类片子建议关掉"),
                _autoOpt.whiteBalance);
            if (EditorGUI.EndChangeCheck()) Repaint();

            using (new EditorGUI.DisabledScope(_lib.Current == null))
                if (GUILayout.Button("对当前这张")) _pendingAction = AutoToneCurrent;

            int n = _lib.Selected.Count > 1 ? _lib.Selected.Count : _lib.Visible.Count;
            using (new EditorGUI.DisabledScope(n == 0))
                if (GUILayout.Button($"对{(_lib.Selected.Count > 1 ? "选中的" : "当前筛选下的")} {n} 张"))
                    _pendingAction = AutoToneSelection;

            EditorGUILayout.HelpBox("每张按自己的分布各算一套，不是把当前这张的参数抄过去——那是上面的同步。",
                                    MessageType.None);
        }

        #endregion

        #region 导出

        [SerializeField] ExportPreset _export = new ExportPreset();
        Texture2D _watermark;
        string _watermarkLoaded;

        static readonly string[] CornerNames = { "左上", "右上", "左下", "右下" };
        static readonly string[] WmModeNames = { "图片", "文字" };
        static readonly string[] CollisionNames = { "自动加序号", "直接覆盖", "跳过" };

        void DrawExportBar()
        {
            EditorGUILayout.LabelField("导出", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _export.jpg = EditorGUILayout.Popup(_export.jpg ? 1 : 0,
                new[] { "PNG（无损）", "JPG" }, GUILayout.Width(100f)) == 1;
            if (_export.jpg) _export.jpgQuality = EditorGUILayout.IntSlider(_export.jpgQuality, 1, 100);
            EditorGUILayout.EndHorizontal();

            _export.maxLongEdge = EditorGUILayout.IntField(
                new GUIContent("长边上限", "0 表示不限制。发网上一般 2048 或 1600"), _export.maxLongEdge);
            if (_export.maxLongEdge > 0)
                _export.noUpscale = EditorGUILayout.Toggle(
                    new GUIContent("只缩不放", "原图比上限还小时不硬拉大——那只会糊"), _export.noUpscale);

            _export.nameTemplate = EditorGUILayout.TextField(
                new GUIContent("命名模板", "可用记号见下面"), _export.nameTemplate);
            _export.subfolder = EditorGUILayout.TextField(
                new GUIContent("子目录", "留空就直接写到选定目录"), _export.subfolder);
            _export.collision = EditorGUILayout.Popup(
                new GUIContent("重名时"), _export.collision, CollisionNames);

            DrawNamePreview();

            EditorGUILayout.Space(2f);
            _export.watermark = EditorGUILayout.Toggle("加水印", _export.watermark);
            if (_export.watermark) DrawWatermarkFields();

            EditorGUILayout.Space(4f);

            using (new EditorGUI.DisabledScope(_full == null))
                if (GUILayout.Button("导出当前这张…")) _pendingAction = ExportSingle;

            using (new EditorGUI.DisabledScope(_lib.Visible.Count == 0))
            {
                // 导出的是「筛选之后看得见的那些」，不是列表全部。
                // 挑完片直接导出选中的那批，这才是挑片的意义
                int n = _lib.Selected.Count > 1 ? _lib.Selected.Count : _lib.Visible.Count;
                string what = _lib.Selected.Count > 1 ? "选中的" : "当前筛选下的";
                if (GUILayout.Button($"批量导出{what} {n} 张…")) _pendingAction = ExportAll;
            }
        }

        void DrawNamePreview()
        {
            // 模板改错了要能立刻看出来，不能等导完一批才发现文件名全是乱的
            var ctx = new ExportContext
            {
                sourceName = _lib.Current != null ? _lib.Current.name : "IMG_0001",
                index = 1, total = Mathf.Max(1, _lib.Visible.Count),
                rating = _lib.Current?.rating ?? 0,
                time = DateTime.Now,
            };
            if (_full != null)
            {
                _settings.OutputSize(_full.width, _full.height, out int gw, out int gh);
                ExportNaming.ComputeSize(gw, gh, _export, out ctx.width, out ctx.height);
            }

            EditorGUILayout.LabelField("预览",
                ExportNaming.Expand(_export.nameTemplate, ctx) + _export.Extension +
                (ctx.width > 0 ? $"    {ctx.width}×{ctx.height}" : ""),
                EditorStyles.miniLabel);

            if (GUILayout.Button("可用记号…", EditorStyles.miniButton))
            {
                var menu = new GenericMenu();
                foreach (var t in ExportNaming.Tokens)
                {
                    string tok = t.token;
                    menu.AddItem(new GUIContent(tok + "  " + t.desc), false,
                                 () => { _export.nameTemplate += tok; Repaint(); });
                }
                menu.ShowAsContext();
            }
        }

        void DrawWatermarkFields()
        {
            EditorGUI.indentLevel++;

            _export.wmMode = GUILayout.Toolbar(_export.wmMode, WmModeNames);

            if (_export.wmMode == 0)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("图片", string.IsNullOrEmpty(_export.watermarkPath)
                    ? "（未选）" : Path.GetFileName(_export.watermarkPath));
                if (GUILayout.Button("选择…", EditorStyles.miniButton, GUILayout.Width(50f)))
                {
                    string p = EditorUtility.OpenFilePanel("选择水印图片（建议带透明通道的 PNG）", "", "png,jpg");
                    if (!string.IsNullOrEmpty(p)) { _export.watermarkPath = p; ReleaseWatermark(); }
                }
                EditorGUILayout.EndHorizontal();

                _export.wmScale = EditorGUILayout.Slider(
                    new GUIContent("大小", "相对输出画面的长边"), _export.wmScale, 0.02f, 0.6f);
            }
            else
            {
                _export.wmText = EditorGUILayout.TextField("文字", _export.wmText);

                _wmFont = (Font)EditorGUILayout.ObjectField(
                    new GUIContent("字体", "留空用系统自带的。要打中文得指定一个中文字体"),
                    _wmFont, typeof(Font), false);

                _export.wmFontScale = EditorGUILayout.Slider(
                    new GUIContent("字号", "相对输出画面的长边"), _export.wmFontScale, 0.008f, 0.2f);
                _export.wmColor = EditorGUILayout.ColorField("颜色", _export.wmColor);
                _export.wmOutline = EditorGUILayout.Slider(
                    new GUIContent("描边", "相对字号。亮底上的白字不描边基本看不见"),
                    _export.wmOutline, 0f, 0.3f);
            }

            _export.corner = EditorGUILayout.Popup(new GUIContent("位置"), _export.corner, CornerNames);
            _export.wmMargin = EditorGUILayout.Slider("边距", _export.wmMargin, 0f, 0.2f);
            _export.wmOpacity = EditorGUILayout.Slider("不透明度", _export.wmOpacity, 0.05f, 1f);

            if (_export.wmMode == 0 && string.IsNullOrEmpty(_export.watermarkPath))
                EditorGUILayout.HelpBox("还没选水印图片。建议用带透明通道的 PNG。", MessageType.None);
            else if (_export.wmMode == 1 && _wmFont != null && !_wmFont.dynamic)
                EditorGUILayout.HelpBox("「" + _wmFont.name + "」是静态图集字体，只有烘焙进去的那些字能出来，" +
                                        "字号也是缩放上去的、会糊。中文水印建议用 Dynamic 模式的字体。",
                                        MessageType.Warning);

            EditorGUI.indentLevel--;
        }

        Texture2D Watermark
        {
            get
            {
                if (!_export.watermark || string.IsNullOrEmpty(_export.watermarkPath)) return null;
                if (_watermark != null && _watermarkLoaded == _export.watermarkPath) return _watermark;

                ReleaseWatermark();
                try
                {
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
                    { hideFlags = HideFlags.HideAndDontSave };
                    if (t.LoadImage(File.ReadAllBytes(_export.watermarkPath)))
                    {
                        _watermark = t;
                        _watermarkLoaded = _export.watermarkPath;
                    }
                    else DestroyImmediate(t);
                }
                catch (Exception e) { Debug.LogWarning("[修图台] 水印读取失败：" + e.Message); }

                return _watermark;
            }
        }

        void ReleaseWatermark()
        {
            if (_watermark != null) DestroyImmediate(_watermark);
            _watermark = null;
            _watermarkLoaded = null;
        }

        void ExportSingle()
        {
            if (_full == null || _lib.Current == null) return;

            string path = EditorUtility.SaveFilePanel("导出修好的图片", "",
                _lib.Current.name + "_graded", _export.jpg ? "jpg" : "png");
            if (string.IsNullOrEmpty(path)) return;

            if (WriteProcessed(_full, path, _lib.Current, 1, 1))
            {
                Debug.Log($"[修图台] 已导出：{path}");
                if (!Application.isBatchMode) EditorUtility.RevealInFinder(path);
            }
        }

        void ExportAll()
        {
            string outDir = EditorUtility.SaveFolderPanel("导出到哪个文件夹", "", "graded");
            if (string.IsNullOrEmpty(outDir)) return;

            // 选中多张就导这些，否则导当前筛选下看得见的那些
            var batch = _lib.Selected.Count > 1
                ? new List<PhotoEntry>(_lib.Selected)
                : new List<PhotoEntry>(_lib.Visible);
            if (batch.Count == 0) return;

            if (!string.IsNullOrEmpty(_export.subfolder))
                outDir = Path.Combine(outDir, ExportNaming.Sanitize(_export.subfolder));
            Directory.CreateDirectory(outDir);

            // 当前这张的参数还没落进 store，先收一下，否则它会用上一次存的
            StashSettings();
            StashRepairs();

            // 这一批已经用掉的名字。只看磁盘不够：同一批里两张算出同名时文件还没落地，
            // 第二张会直接盖掉第一张
            var taken = new HashSet<string>();
            var now = DateTime.Now;
            int ok = 0, skipped = 0;

            var mySettings = _settings.Clone();   // 循环里要临时改 _settings，先留个底

            try
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    var entry = batch[i];
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "批量导出", entry.name, (float)i / batch.Count)) break;

                    // 每张用它自己的参数和修补
                    var rec = _store.Get(entry.path);
                    if (rec != null && rec.hasSettings && rec.settings != null) _settings.CopyFrom(rec.settings);
                    else _settings.CopyFrom(mySettings);

                    // 字符串要比内容不能比引用：两个内容相同的 string 未必是同一个对象。
                    // 比错的话当前这张会被重新从磁盘读一遍，蒙版和修补也就套不上去了
                    var tex = entry.path == _loadedPath && _full != null
                        ? _full : LoadTextureFromFile(entry.path);
                    if (tex == null) continue;

                    _settings.OutputSize(tex.width, tex.height, out int gw, out int gh);
                    ExportNaming.ComputeSize(gw, gh, _export, out int ew, out int eh);

                    string name = ExportNaming.Expand(_export.nameTemplate, new ExportContext
                    {
                        sourceName = entry.name, index = i + 1, total = batch.Count,
                        width = ew, height = eh, rating = entry.rating, time = now,
                    });

                    string outPath = ExportNaming.Resolve(outDir, name, _export.Extension,
                                                          _export.collision, taken, null);
                    if (outPath == null) { skipped++; }
                    else if (WriteProcessed(tex, outPath, entry, i + 1, batch.Count)) ok++;

                    if (!ReferenceEquals(tex, _full)) DestroyImmediate(tex);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _settings.CopyFrom(mySettings);
                _dirty = true;
            }

            string msg = $"导出完成 {ok} / {batch.Count} 张" + (skipped > 0 ? $"，跳过 {skipped} 张重名的" : "");
            Debug.Log("[修图台] " + msg);
            if (!Application.isBatchMode)
            {
                EditorUtility.DisplayDialog("修图台", msg + "\n\n" + outDir, "好");
                EditorUtility.RevealInFinder(outDir + "/");
            }
        }

        /// <summary>
        /// 按当前参数处理并写文件。分屏和旁路只是查看方式，导出时强制关掉。
        ///
        /// 顺序是：调色（几何变换后的尺寸）-> 缩放到导出尺寸 -> 贴水印 -> 回读 -> 编码。
        /// 水印必须最后贴，不然会跟着一起被缩放和调色。
        /// </summary>
        bool WriteProcessed(Texture2D tex, string path, PhotoEntry entry, int index, int total)
        {
            var r = Renderer;
            if (r == null || tex == null) return false;

            _settings.OutputSize(tex.width, tex.height, out int gw, out int gh);
            ExportNaming.ComputeSize(gw, gh, _export, out int ew, out int eh);

            var graded = RenderTexture.GetTemporary(gw, gh, 0,
                                                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            RenderTexture scaled = null;
            var readback = new Texture2D(ew, eh, TextureFormat.RGBA32, false, false)
            { hideFlags = HideFlags.HideAndDontSave };

            try
            {
                r.GrainSeed = 7f;

                // 批量导出别的图时不能套当前这张的蒙版和修补——它们是逐图的
                bool isCurrent = ReferenceEquals(tex, _full);
                var opts = new VideoGradeRenderer.Options();
                if (isCurrent) { opts.externalMask = CurrentMask; opts.depthMap = CurrentMask; }
                opts.lut = _lut;
                opts.lutAmount = _lutAmount;
                opts.brushes = _brushes;

                // 天空能逐图现算，所以批量导出时也照算，不像 AI 蒙版那样只认当前这张
                Texture2D skyTmp = null;
                if (isCurrent) opts.skyMask = EnsureSky();
                else { skyTmp = BuildSkyFor(tex); opts.skyMask = skyTmp; }

                r.Render(isCurrent ? GradeSource : tex, graded, _settings, opts);

                RenderTexture target = graded;
                if (ew != gw || eh != gh)
                {
                    scaled = RenderTexture.GetTemporary(ew, eh, 0,
                                                        RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                    Graphics.Blit(graded, scaled);
                    target = scaled;
                }

                StampWatermark(target);

                var prev = RenderTexture.active;
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0f, 0f, ew, eh), 0, 0, false);
                readback.Apply(false, false);
                RenderTexture.active = prev;

                if (skyTmp != null) DestroyImmediate(skyTmp);

                byte[] bytes = _export.jpg ? readback.EncodeToJPG(_export.jpgQuality) : readback.EncodeToPNG();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, bytes);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[修图台] 导出失败 {path}：{e.Message}");
                return false;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(graded);
                if (scaled != null) RenderTexture.ReleaseTemporary(scaled);
                DestroyImmediate(readback);
                // 渲染完把预览标脏，否则下一帧会沿用导出时设的参数
                _dirty = true;
            }
        }

        /// <summary>把水印贴到目标上。用 Graphics.DrawTexture 走内置的带 alpha 混合的材质，省一个 shader。</summary>
        void StampWatermark(RenderTexture target)
        {
            if (target == null) return;

            // 两种水印共用一套摆位：LoadPixelMatrix 之后原点在左上、y 向下
            if (_export.wmMode == 1) StampText(target);
            else StampImage(target);
        }

        void StampImage(RenderTexture target)
        {
            var wm = Watermark;
            if (wm == null) return;

            float longEdge = Mathf.Max(target.width, target.height);
            float w = longEdge * Mathf.Clamp(_export.wmScale, 0.01f, 1f);
            float h = w * wm.height / Mathf.Max(wm.width, 1);

            var r = ExportNaming.WatermarkRect(target.width, target.height, w, h,
                                               _export.corner, _export.wmMargin);

            var prev = RenderTexture.active;
            RenderTexture.active = target;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, target.width, target.height, 0f);
            Graphics.DrawTexture(r, wm, new Rect(0f, 0f, 1f, 1f),
                                 0, 0, 0, 0, new Color(1f, 1f, 1f, Mathf.Clamp01(_export.wmOpacity)));
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        void StampText(RenderTexture target)
        {
            if (string.IsNullOrWhiteSpace(_export.wmText)) return;

            var font = _wmFont != null ? _wmFont : TextStamp.DefaultFont;
            if (font == null) return;

            float longEdge = Mathf.Max(target.width, target.height);

            // 字号按输出长边算，这样一批图缩到不同尺寸时水印的视觉大小是一致的。
            // 上限拦一下：动态字体图集是有大小的，字号给到几千会把图集撑爆、字反而丢
            int px = Mathf.Clamp(
                Mathf.RoundToInt(longEdge * Mathf.Clamp(_export.wmFontScale, 0.004f, 0.5f)), 8, 512);

            var lay = TextStamp.Measure(font, _export.wmText, px);
            if (lay.Empty) return;

            float outline = px * Mathf.Clamp01(_export.wmOutline);

            // 描边是往外扩的，量外框时要把它算进去，否则贴着角的那半圈会被裁掉
            var r = ExportNaming.WatermarkRect(target.width, target.height,
                                               lay.size.x + outline * 2f, lay.size.y + outline * 2f,
                                               _export.corner, _export.wmMargin);

            var c = _export.wmColor;
            c.a *= Mathf.Clamp01(_export.wmOpacity);

            var prev = RenderTexture.active;
            RenderTexture.active = target;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, target.width, target.height, 0f);
            TextStamp.Draw(lay, font, new Vector2(r.x + outline, r.y + outline), c, outline);
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        #endregion

        #region 预设

        void PresetMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("从调色台复制参数"), false, () =>
            {
                var pp = FindObjectOfType<VideoPostProcessor>();
                if (pp == null) { Debug.LogWarning("[修图台] 场景里没有 VideoPostProcessor"); return; }
                AutoSnapshot("覆盖之前");
                Undo.RecordObject(this, "复制调色参数");
                _settings.CopyFrom(pp.settings);
                _dirty = true;
            });
            menu.AddItem(new GUIContent("载入 JSON…"), false, () =>
            {
                string path = EditorUtility.OpenFilePanel("载入调色预设", "Assets/StreamingAssets/Story", "json");
                if (string.IsNullOrEmpty(path)) return;
                var loaded = VideoGradeSettings.FromJson(File.ReadAllText(path, System.Text.Encoding.UTF8));
                if (loaded == null) return;
                AutoSnapshot("载入预设之前");
                Undo.RecordObject(this, "载入预设");
                _settings.CopyFrom(loaded);
                _dirty = true;
            });
            menu.AddItem(new GUIContent("保存 JSON…"), false, () =>
            {
                string path = EditorUtility.SaveFilePanel("保存调色预设", "Assets/StreamingAssets/Story", "photo_look", "json");
                if (string.IsNullOrEmpty(path)) return;
                File.WriteAllText(path, _settings.ToJson(), System.Text.Encoding.UTF8);
                AssetDatabase.Refresh();
                Debug.Log($"[修图台] 已保存：{path}");
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("复制 JSON 到剪贴板"), false,
                () => EditorGUIUtility.systemCopyBuffer = _settings.ToJson());
            menu.ShowAsContext();
        }

        #endregion
    }
}
