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
            w.minSize = new Vector2(900f, 560f);
            w.Show();
        }

        const string MaterialPath = "Assets/GameAssets/Materials/VideoGrade.mat";
        const float ToolbarH = 22f;
        const float ThumbSize = 72f;
        const int ThumbPixels = 128;
        const float SplitterW = 5f;

        // 布局尺寸可拖拽调整，存 EditorPrefs 跨会话保留
        const string PrefRightW = "PhotoGrade.rightPanelW";
        const string PrefFilmH  = "PhotoGrade.filmH";
        const string PrefFilmOn = "PhotoGrade.filmVisible";

        float _rightPanelW = 360f;
        float _filmH = 96f;
        bool _filmVisible = true;

        enum Splitter { None, Right, Film }
        Splitter _dragging = Splitter.None;

        /// <summary>胶片条里的一项。只常驻缩略图，原图按需加载，否则几十张 24MP 照片能吃掉几个 G。</summary>
        class Entry
        {
            public string path;
            public string name;
            public Texture2D thumb;
        }

        [SerializeField] VideoGradeSettings _settings = new VideoGradeSettings();
        [SerializeField] bool _splitCompare;
        [SerializeField] float _splitPosition = 0.5f;
        [SerializeField] bool _bypass;
        [SerializeField] int _jpgQuality = 95;
        [SerializeField] bool _exportJpg;

        readonly List<Entry> _entries = new List<Entry>();
        readonly List<string> _pendingImports = new List<string>();
        int _selected = -1;
        int _pendingSelect = -1;
        System.Action _pendingAction;
        Texture2D _full;            // 当前选中图的原图，只留一张

        VideoGradeRenderer _renderer;
        Material _materialCopy;
        RenderTexture _preview;
        bool _dirty = true;

        float _zoom = 1f;
        Vector2 _pan;
        bool _fitPending = true;
        bool _spaceDown;      // 空格临时切换成抓手，和 PS 一致
        bool _holdCompare;    // 按住反斜杠看原图

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

        Vector2 _paramScroll, _filmScroll;
        readonly GradeSettingsGUI _gui = new GradeSettingsGUI();

        void OnEnable()
        {
            titleContent = new GUIContent("修图台");
            wantsMouseMove = true;

            _rightPanelW = EditorPrefs.GetFloat(PrefRightW, 360f);
            _filmH = EditorPrefs.GetFloat(PrefFilmH, 96f);
            _filmVisible = EditorPrefs.GetBool(PrefFilmOn, true);
        }

        void SaveLayout()
        {
            EditorPrefs.SetFloat(PrefRightW, _rightPanelW);
            EditorPrefs.SetFloat(PrefFilmH, _filmH);
            EditorPrefs.SetBool(PrefFilmOn, _filmVisible);
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
            if (_pendingAction != null)
            {
                var action = _pendingAction;
                _pendingAction = null;
                action();
                needRepaint = true;
            }

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
            ReleasePreview();
            ClearEntries();
            if (_lut != null) { DestroyImmediate(_lut); _lut = null; }
#if LOVE_SENTIS
            ReleaseMask();
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

            // 拖拽范围要留够，否则能把某一边拖到看不见
            _rightPanelW = Mathf.Clamp(_rightPanelW, 260f, Mathf.Max(300f, position.width - 320f));
            _filmH = Mathf.Clamp(_filmH, 64f, Mathf.Max(80f, position.height * 0.5f));

            float filmH = _filmVisible ? _filmH : 0f;

            var toolbar  = new Rect(0f, 0f, position.width, ToolbarH);
            var right    = new Rect(position.width - _rightPanelW, ToolbarH, _rightPanelW, position.height - ToolbarH);
            var vSplit   = new Rect(right.x - SplitterW, ToolbarH, SplitterW, position.height - ToolbarH);
            var left     = new Rect(0f, ToolbarH, vSplit.x, position.height - ToolbarH);
            var film     = new Rect(left.x, left.yMax - filmH, left.width, filmH);
            var hSplit   = new Rect(left.x, film.y - SplitterW, left.width, SplitterW);
            var canvas   = new Rect(left.x, left.y, left.width,
                                    left.height - filmH - (_filmVisible ? SplitterW : 0f));

            // 分隔条的输入要先处理，否则会被下面的面板抢走
            HandleSplitters(vSplit, hSplit);

            DrawToolbar(toolbar);
            DrawCanvas(canvas);
            if (_filmVisible) { DrawSplitter(hSplit, false); DrawFilmstrip(film); }
            DrawSplitter(vSplit, true);
            DrawParamPanel(right);
        }

        #region 可拖拽分隔条

        void HandleSplitters(Rect vSplit, Rect hSplit)
        {
            var e = Event.current;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (vSplit.Contains(e.mousePosition)) { _dragging = Splitter.Right; e.Use(); }
                else if (_filmVisible && hSplit.Contains(e.mousePosition)) { _dragging = Splitter.Film; e.Use(); }
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

        void DrawSplitter(Rect r, bool vertical)
        {
            EditorGUIUtility.AddCursorRect(r, vertical ? MouseCursor.ResizeHorizontal : MouseCursor.ResizeVertical);
            if (Event.current.type != EventType.Repaint) return;

            EditorGUI.DrawRect(r, new Color(0.13f, 0.14f, 0.16f));
            // 中间一道浅色，让人看出这里能拖
            var grip = vertical
                ? new Rect(r.center.x - 0.5f, r.center.y - 14f, 1f, 28f)
                : new Rect(r.center.x - 14f, r.center.y - 0.5f, 28f, 1f);
            EditorGUI.DrawRect(grip, new Color(1f, 1f, 1f, 0.22f));
        }

        #endregion

        void DrawToolbar(Rect r)
        {
            GUILayout.BeginArea(r, EditorStyles.toolbar);
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("打开图片…", EditorStyles.toolbarButton, GUILayout.Width(78f))) OpenFiles();
            if (GUILayout.Button("打开文件夹…", EditorStyles.toolbarButton, GUILayout.Width(88f))) OpenFolder();
            if (GUILayout.Button("清空列表", EditorStyles.toolbarButton, GUILayout.Width(64f))) ClearEntries();

            GUILayout.Space(12f);

            bool bypass = GUILayout.Toggle(_bypass, "原图对比", EditorStyles.toolbarButton, GUILayout.Width(64f));
            if (bypass != _bypass) { _bypass = bypass; _dirty = true; }

            bool split = GUILayout.Toggle(_splitCompare, "分屏", EditorStyles.toolbarButton, GUILayout.Width(44f));
            if (split != _splitCompare) { _splitCompare = split; _dirty = true; }

            if (_splitCompare)
            {
                float pos = GUILayout.HorizontalSlider(_splitPosition, 0f, 1f, GUILayout.Width(90f));
                if (!Mathf.Approximately(pos, _splitPosition)) { _splitPosition = pos; _dirty = true; }
            }

            GUILayout.Space(12f);

            if (GUILayout.Button("适应", EditorStyles.toolbarButton, GUILayout.Width(40f))) _fitPending = true;

            // 缩放下拉：当前值排在最前，选完立刻生效
            int pick = EditorGUILayout.Popup(0,
                new[] { $"{_zoom * 100f:0}%", "适应", "25%", "50%", "100%", "200%", "400%" },
                EditorStyles.toolbarPopup, GUILayout.Width(66f));
            if (pick == 1) _fitPending = true;
            else if (pick > 1)
            {
                float[] presets = { 0f, 0f, 0.25f, 0.5f, 1f, 2f, 4f };
                SetZoom(presets[pick], default);
            }

            bool film = GUILayout.Toggle(_filmVisible, "胶片条", EditorStyles.toolbarButton, GUILayout.Width(52f));
            if (film != _filmVisible) { _filmVisible = film; SaveLayout(); }

            GUILayout.FlexibleSpace();

            // 图片信息放这里而不是盖在画面上——画布要保持干净，只有被处理过的图
            if (_full != null)
                GUILayout.Label($"{_full.name}    {_full.width}×{_full.height}", EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("胶片化", EditorStyles.toolbarButton, GUILayout.Width(52f)))
            {
                Undo.RecordObject(this, "胶片化预设");
                _settings.ApplyFilmLook();
                _dirty = true;
            }
            if (GUILayout.Button("预设", EditorStyles.toolbarButton, GUILayout.Width(44f))) PresetMenu();
            if (GUILayout.Button("重置参数", EditorStyles.toolbarButton, GUILayout.Width(64f)))
            {
                Undo.RecordObject(this, "重置参数");
                _settings.Reset();
                _dirty = true;
            }

            EditorGUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void DrawParamPanel(Rect r)
        {
            EditorGUI.DrawRect(r, new Color(0.22f, 0.22f, 0.24f));
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
            DrawChartBar();
            EditorGUILayout.Space(8f);
            DrawLutBar();
            EditorGUILayout.Space(8f);
            DrawExportBar();
            EditorGUILayout.Space(6f);

            EditorGUI.BeginChangeCheck();
            _gui.PreviewTexture = _preview;
            _gui.PanelWidth = r.width - 8f;
            _gui.Draw(_settings, this);
            // 只有参数真的动了才重渲染。缩放平移不该触发全分辨率重算。
            // 转盘弹窗是跨帧的，改动落在 OnGUI 之外，BeginChangeCheck 捕捉不到，所以要单独问一次
            bool changed = EditorGUI.EndChangeCheck();
            if (changed | _gui.ConsumeExternalChange()) _dirty = true;

            EditorGUILayout.EndScrollView();

            EditorGUIUtility.labelWidth = prevLabel;
            EditorGUIUtility.wideMode = prevWide;
            GUILayout.EndArea();
        }

        #endregion

        #region 预览：缩放与平移

        /// <summary>棋盘底纹，和 PS 一样用来表示透明区域。</summary>
        static Texture2D _checker;
        static Texture2D Checker
        {
            get
            {
                if (_checker != null) return _checker;
                const int N = 16;
                _checker = new Texture2D(N * 2, N * 2, TextureFormat.RGBA32, false, false)
                { name = "Checker", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat,
                  hideFlags = HideFlags.HideAndDontSave };
                var a = new Color32(64, 64, 66, 255);
                var b = new Color32(80, 80, 83, 255);
                var px = new Color32[N * 2 * N * 2];
                for (int y = 0; y < N * 2; y++)
                    for (int x = 0; x < N * 2; x++)
                        px[y * N * 2 + x] = ((x / N) + (y / N)) % 2 == 0 ? a : b;
                _checker.SetPixels32(px);
                _checker.Apply(false, false);
                return _checker;
            }
        }

        void DrawCanvas(Rect r)
        {
            EditorGUI.DrawRect(r, new Color(0.13f, 0.14f, 0.16f));

            if (_full == null)
            {
                var hint = new Rect(r.x, r.center.y - 20f, r.width, 40f);
                EditorGUI.LabelField(hint,
                    "把图片拖进来，或用左上角「打开图片 / 打开文件夹」",
                    new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 13 });
                HandleDragAndDrop(r);
                return;
            }

            HandlePreviewInput(r);

            if (_fitPending) { FitToView(r); _fitPending = false; }

            float w = _full.width * _zoom;
            float h = _full.height * _zoom;
            var img = new Rect(r.center.x - w * 0.5f + _pan.x, r.center.y - h * 0.5f + _pan.y, w, h);

            if (Event.current.type == EventType.Repaint && _preview != null)
            {
                // 硬裁到画布区内，放大之后才不会画到胶片条和参数栏上。
                // 这里只画已经渲好的贴图，不做任何渲染。
                GUI.BeginGroup(r);
                var local = new Rect(img.x - r.x, img.y - r.y, img.width, img.height);

                // 棋盘底：图片有透明区域时才看得见，和 PS 一致
                GUI.DrawTextureWithTexCoords(local, Checker,
                    new Rect(0f, 0f, local.width / 32f, local.height / 32f));

                GUI.DrawTexture(local, _preview, ScaleMode.StretchToFill, true);

                // 一圈细描边，把画布和图像分开，缩小时更清楚边界在哪
                var edge = new Color(0f, 0f, 0f, 0.55f);
                EditorGUI.DrawRect(new Rect(local.x - 1f, local.y - 1f, local.width + 2f, 1f), edge);
                EditorGUI.DrawRect(new Rect(local.x - 1f, local.yMax, local.width + 2f, 1f), edge);
                EditorGUI.DrawRect(new Rect(local.x - 1f, local.y, 1f, local.height), edge);
                EditorGUI.DrawRect(new Rect(local.xMax, local.y, 1f, local.height), edge);

                GUI.EndGroup();
            }

            if (_chartMode) DrawChartOverlay(r, img);
            HandleDragAndDrop(r);
        }

        void FitToView(Rect r)
        {
            if (_full == null) return;
            _zoom = Mathf.Min(r.width / _full.width, r.height / _full.height) * 0.95f;
            _pan = Vector2.zero;
        }

        void HandlePreviewInput(Rect r)
        {
            var e = Event.current;
            if (e.type == EventType.Layout) return;

            if (e.type == EventType.KeyDown)
            {
                // 快捷键对齐 PS / Lightroom 的习惯
                if (e.keyCode == KeyCode.Space) { _spaceDown = true; Repaint(); }
                else if (e.keyCode == KeyCode.Backslash && !_holdCompare)
                {
                    // 按住看原图，松开回到成片
                    _holdCompare = true; _bypass = true; _dirty = true; e.Use(); Repaint();
                }
                else if (e.keyCode == KeyCode.F ||
                         (e.control && e.keyCode == KeyCode.Alpha0)) { _fitPending = true; e.Use(); Repaint(); }
                else if (e.keyCode == KeyCode.Alpha1 ||
                         (e.control && e.keyCode == KeyCode.Alpha1)) { SetZoom(1f, r); e.Use(); Repaint(); }
                return;
            }

            if (e.type == EventType.KeyUp)
            {
                if (e.keyCode == KeyCode.Space) { _spaceDown = false; Repaint(); }
                else if (e.keyCode == KeyCode.Backslash && _holdCompare)
                {
                    _holdCompare = false; _bypass = false; _dirty = true; e.Use(); Repaint();
                }
                return;
            }

            if (_spaceDown) EditorGUIUtility.AddCursorRect(r, MouseCursor.Pan);
            if (!r.Contains(e.mousePosition)) return;
            // 标定模式下正在拖角点，别让画布平移抢走事件
            if (_chartMode && _chartDragIndex >= 0) return;

            if (e.type == EventType.ScrollWheel)
            {
                float old = _zoom;
                _zoom = Mathf.Clamp(_zoom * (1f - e.delta.y * 0.05f), 0.02f, 16f);
                // 以鼠标位置为锚点缩放，否则放大时目标会跑出视野
                Vector2 toMouse = e.mousePosition - (r.center + _pan);
                _pan -= toMouse * (_zoom / old - 1f);
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseDrag && (e.button == 0 || e.button == 2))
            {
                _pan += e.delta;
                e.Use();
                Repaint();
            }
        }

        /// <summary>以画布中心为锚点设定缩放比例。</summary>
        void SetZoom(float zoom, Rect canvas)
        {
            float old = _zoom;
            _zoom = Mathf.Clamp(zoom, 0.02f, 16f);
            _pan *= _zoom / old;   // 保持当前看的位置不跳
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
        void RenderPreview()
        {
            if (_full == null) return;

            bool sizeChanged = _preview == null || _preview.width != _full.width || _preview.height != _full.height;

            if (sizeChanged)
            {
                ReleasePreview();
                _preview = new RenderTexture(_full.width, _full.height, 0,
                                             RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                { name = "PhotoGradePreview", hideFlags = HideFlags.HideAndDontSave };
                _preview.Create();
            }

            var r = Renderer;
            if (r == null) return;

            r.GrainSeed = 7f;   // 图片用固定种子，否则每次重绘噪点都在跳，导出也和预览对不上
            r.Render(_full, _preview, _settings, new VideoGradeRenderer.Options
            {
                bypass = _bypass,
                splitCompare = _splitCompare,
                splitPosition = _splitPosition,
                externalMask = CurrentMask,
                lut = _lut,
                lutAmount = _lutAmount,
            });
            _dirty = false;
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

        void DrawFilmstrip(Rect r)
        {
            EditorGUI.DrawRect(r, new Color(0.10f, 0.11f, 0.13f));

            if (_entries.Count == 0)
            {
                EditorGUI.LabelField(r, "图片列表为空", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var header = new Rect(r.x + 6f, r.y + 2f, r.width - 12f, 14f);
            EditorGUI.LabelField(header,
                $"{_selected + 1} / {_entries.Count}    ← → 切换", EditorStyles.miniLabel);

            HandleFilmKeys();

            var view = new Rect(r.x, r.y + 18f, r.width, r.height - 20f);
            float rowW = _entries.Count * (ThumbSize + 8f) + 8f;
            _filmScroll = GUI.BeginScrollView(view, _filmScroll,
                new Rect(0f, 0f, rowW, view.height - 16f), false, false);

            for (int i = 0; i < _entries.Count; i++)
            {
                var cell = new Rect(8f + i * (ThumbSize + 8f), 2f, ThumbSize, ThumbSize);

                if (i == _selected)
                    EditorGUI.DrawRect(new Rect(cell.x - 3f, cell.y - 3f, cell.width + 6f, cell.height + 6f),
                                       new Color(0.35f, 0.62f, 0.95f));

                EditorGUI.DrawRect(cell, new Color(0.06f, 0.07f, 0.08f));
                var e = _entries[i];
                if (e.thumb != null && Event.current.type == EventType.Repaint)
                    GUI.DrawTexture(cell, e.thumb, ScaleMode.ScaleToFit);

                if (GUI.Button(cell, GUIContent.none, GUIStyle.none)) Select(i);
            }

            GUI.EndScrollView();
        }

        void HandleFilmKeys()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown) return;
            if (e.keyCode == KeyCode.LeftArrow) { Select(_selected - 1); e.Use(); }
            else if (e.keyCode == KeyCode.RightArrow) { Select(_selected + 1); e.Use(); }
        }

        void Select(int index)
        {
            if (_entries.Count == 0) return;
            index = Mathf.Clamp(index, 0, _entries.Count - 1);
            if (index == _selected && _full != null) return;

            _selected = index;

            if (_full != null) { DestroyImmediate(_full); _full = null; }
            _full = LoadTextureFromFile(_entries[index].path);

            _fitPending = true;
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
            foreach (var pattern in new[] { "*.png", "*.jpg", "*.jpeg" })
                files.AddRange(Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly));
            files.Sort();
            foreach (var f in files) AddFile(f);
        }

        /// <summary>只排队，真正的载入放到 Update 里做——生成缩略图要 Blit，不能在 GUI 里跑。</summary>
        void AddFile(string path, bool selectIt = true)
        {
            if (_entries.Exists(x => x.path == path)) return;
            if (_pendingImports.Contains(path)) return;
            _pendingImports.Add(path);
            if (selectIt) _pendingSelect = -2;   // -2 = 导入完选中第一张新加的
        }

        void ProcessPendingImports()
        {
            int firstNew = _entries.Count;

            try
            {
                for (int i = 0; i < _pendingImports.Count; i++)
                {
                    string path = _pendingImports[i];
                    if (_pendingImports.Count > 3 && EditorUtility.DisplayCancelableProgressBar(
                            "载入图片", Path.GetFileName(path), (float)i / _pendingImports.Count)) break;

                    var full = LoadTextureFromFile(path);
                    if (full == null) { Debug.LogError($"[修图台] 读不了这个文件：{path}"); continue; }

                    _entries.Add(new Entry
                    {
                        path = path,
                        name = Path.GetFileNameWithoutExtension(path),
                        thumb = MakeThumbnail(full, ThumbPixels),
                    });
                    // 缩略图做完就把原图丢掉，选中时再按需加载
                    DestroyImmediate(full);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _pendingImports.Clear();
            }

            if (_entries.Count > firstNew && (_selected < 0 || _pendingSelect == -2))
                Select(firstNew);
            _pendingSelect = -1;
        }

        void ClearEntries()
        {
            foreach (var e in _entries) if (e.thumb != null) DestroyImmediate(e.thumb);
            _entries.Clear();
            _selected = -1;
            if (_full != null) { DestroyImmediate(_full); _full = null; }
            ReleasePreview();
            Repaint();
        }

        static bool IsImage(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg";
        }

        static Texture2D LoadTextureFromFile(string path)
        {
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
                EditorGUI.DrawRect(box, i == 0 ? new Color(1f, 0.85f, 0.2f) : Color.white);
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

        #region 导出

        void DrawExportBar()
        {
            EditorGUILayout.LabelField("导出", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _exportJpg = EditorGUILayout.Popup(_exportJpg ? 1 : 0,
                new[] { "PNG（无损）", "JPG" }, GUILayout.Width(100f)) == 1;
            if (_exportJpg) _jpgQuality = EditorGUILayout.IntSlider(_jpgQuality, 1, 100);
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(_full == null))
            {
                // 排队到 Update 执行，不在 GUI 里做渲染和回读
                if (GUILayout.Button("导出当前这张…")) _pendingAction = ExportSingle;
            }
            using (new EditorGUI.DisabledScope(_entries.Count == 0))
            {
                if (GUILayout.Button($"批量导出列表里全部（{_entries.Count} 张）…")) _pendingAction = ExportAll;
            }
        }

        void ExportSingle()
        {
            string ext = _exportJpg ? "jpg" : "png";
            string suggested = (_selected >= 0 ? _entries[_selected].name : "photo") + "_graded";
            string path = EditorUtility.SaveFilePanel("导出修好的图片", "", suggested, ext);
            if (string.IsNullOrEmpty(path)) return;

            if (WriteProcessed(_full, path))
            {
                Debug.Log($"[修图台] 已导出：{path}");
                EditorUtility.RevealInFinder(path);
            }
        }

        void ExportAll()
        {
            string outDir = EditorUtility.SaveFolderPanel("导出到哪个文件夹", "", "graded");
            if (string.IsNullOrEmpty(outDir)) return;

            Directory.CreateDirectory(outDir);
            string ext = _exportJpg ? ".jpg" : ".png";
            int ok = 0;

            try
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    var entry = _entries[i];
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "批量导出", entry.name, (float)i / _entries.Count)) break;

                    // 源目录和输出目录相同会覆盖原图，这里靠改名避开
                    string outPath = Path.Combine(outDir, entry.name + "_graded" + ext);

                    var tex = LoadTextureFromFile(entry.path);
                    if (tex == null) continue;
                    if (WriteProcessed(tex, outPath)) ok++;
                    DestroyImmediate(tex);
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            Debug.Log($"[修图台] 批量导出完成：{ok}/{_entries.Count}");
            EditorUtility.DisplayDialog("修图台", $"导出完成 {ok} / {_entries.Count} 张\n\n{outDir}", "好");
            EditorUtility.RevealInFinder(outDir + "/");
        }

        /// <summary>按当前参数处理并写文件。分屏和旁路只是查看方式，导出时强制关掉。</summary>
        bool WriteProcessed(Texture2D tex, string path)
        {
            var r = Renderer;
            if (r == null || tex == null) return false;

            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0,
                                                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var readback = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, false)
            { hideFlags = HideFlags.HideAndDontSave };

            try
            {
                r.GrainSeed = 7f;
                // 批量导出时每张图的蒙版不同，只有当前这张能用已生成的
                var opts = new VideoGradeRenderer.Options();
                if (ReferenceEquals(tex, _full)) opts.externalMask = CurrentMask;
                opts.lut = _lut;
                opts.lutAmount = _lutAmount;
                r.Render(tex, rt, _settings, opts);

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0f, 0f, tex.width, tex.height), 0, 0, false);
                readback.Apply(false, false);
                RenderTexture.active = prev;

                byte[] bytes = _exportJpg ? readback.EncodeToJPG(_jpgQuality) : readback.EncodeToPNG();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, bytes);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[修图台] 导出失败 {path}：{e.Message}");
                return false;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
                DestroyImmediate(readback);
                // 渲染完把预览标脏，否则下一帧会沿用导出时设的参数
                _dirty = true;
            }
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
