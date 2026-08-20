using System;
using System.Collections.Generic;
using Love.Video;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Love.UI
{
    /// <summary>
    /// 运行时调色面板（开发期自用）。
    ///
    /// 整套 UI 是运行时用代码搭出来的，场景里不需要任何预制体或接线——
    /// 挂上这个脚本就能用，正式版把 enabled 取消勾选或者删掉这个物体即可。
    ///
    /// 默认 F1 开关。分三个页签：一级校色 / 效果 / 监看。
    /// </summary>
    [DisallowMultipleComponent]
    public class VideoGradePanel : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("留空则自动在场景里找")]
        public VideoPostProcessor postProcessor;
        [Tooltip("显示测试卡时要临时藏掉的占位画面，可留空")]
        public PlaceholderView placeholderView;
        [Tooltip("用来判断当前是不是停在标题界面，可留空")]
        public TitleScreen titleScreen;

        [Header("设置")]
        public KeyCode toggleKey = KeyCode.F1;
        public bool openOnStart = false;
        [Tooltip("正式包（非开发版）里是否保留这个面板。\n" +
                 "默认关闭：调色在编辑器的「调色台」窗口里做，正式版不需要它，也不该让玩家按到")]
        public bool enableInReleaseBuild = false;

        // 配色
        static readonly Color PanelBg    = new Color(0.09f, 0.10f, 0.12f, 0.94f);
        static readonly Color HeaderCol  = new Color(0.55f, 0.78f, 1.00f, 1f);
        static readonly Color LabelCol   = new Color(0.85f, 0.87f, 0.90f, 1f);
        static readonly Color ValueCol   = new Color(0.62f, 0.66f, 0.72f, 1f);
        static readonly Color TrackCol   = new Color(1f, 1f, 1f, 0.13f);
        static readonly Color FillCol    = new Color(0.35f, 0.62f, 0.95f, 1f);
        static readonly Color HandleCol  = new Color(0.92f, 0.94f, 0.97f, 1f);
        static readonly Color BtnCol     = new Color(1f, 1f, 1f, 0.10f);
        static readonly Color TabOffCol  = new Color(1f, 1f, 1f, 0.06f);
        static readonly Color TabOnCol   = new Color(0.35f, 0.62f, 0.95f, 0.85f);

        const float PanelWidth  = 520f;
        const float LabelIndent = 6f;
        const float LabelWidth  = 140f;
        const float ValueWidth  = 72f;
        const float RowHeight   = 32f;
        const float TabBarHeight   = 40f;
        const float BarRowHeight   = 44f;
        const float BarRowGap      = 8f;

        GameObject _root;
        RectTransform _panel;
        ScrollRect _scroll;
        RectTransform _viewport;
        bool _open;
        bool _placeholderWasActive;

        readonly List<Action> _refreshers = new List<Action>();

        // 页签
        class Tab
        {
            public string name;
            public RectTransform content;
            public Image buttonImage;
        }
        readonly List<Tab> _tabs = new List<Tab>();
        int _activeTab;

        // 当前正在往里加控件的页签内容容器
        RectTransform _target;

        GameObject _splitHandle;

        void Awake()
        {
            if (postProcessor == null) postProcessor = FindObjectOfType<VideoPostProcessor>();
            if (placeholderView == null) placeholderView = FindObjectOfType<PlaceholderView>(true);
            if (titleScreen == null) titleScreen = FindObjectOfType<TitleScreen>(true);
        }

        void Start()
        {
            // 正式包里默认不装这套面板：调色已经搬到编辑器的「调色台」窗口，
            // 留着只会让玩家误按 F1 弹出一堆开发用滑条。
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
            if (!enableInReleaseBuild) { enabled = false; return; }
#endif
            Build();
            SetOpen(openOnStart);
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey)) SetOpen(!_open);
        }

        public void SetOpen(bool open)
        {
            _open = open;
            if (_root != null) _root.SetActive(open);
            if (postProcessor == null) return;

            if (open)
            {
                // 没有视频源就顶一张测试卡上来，否则面板打开了也看不到调色效果。
                // 但标题界面是不透明的全屏层，这时候放测试卡也会被挡住，所以跳过。
                bool titleShowing = titleScreen != null && titleScreen.IsShowing;
                bool noVideo = postProcessor.videoScreen == null ||
                               postProcessor.videoScreen.SourceTexture == null;
                if (noVideo && !titleShowing)
                {
                    postProcessor.showTestPattern = true;
                    if (placeholderView != null)
                    {
                        _placeholderWasActive = placeholderView.gameObject.activeSelf;
                        placeholderView.gameObject.SetActive(false);
                    }
                }
                RefreshAll();
            }
            else if (postProcessor.showTestPattern)
            {
                postProcessor.showTestPattern = false;
                if (placeholderView != null && _placeholderWasActive)
                    placeholderView.gameObject.SetActive(true);
            }

            UpdateSplitHandle();
        }

        void RefreshAll()
        {
            foreach (var r in _refreshers) r();
            UpdateSplitHandle();
        }

        void UpdateSplitHandle()
        {
            if (_splitHandle == null || postProcessor == null) return;
            _splitHandle.SetActive(_open && postProcessor.splitCompare && !postProcessor.bypass);
        }

        #region 搭 UI

        void Build()
        {
            if (postProcessor == null)
            {
                Debug.LogWarning("[VideoGradePanel] 场景里没有 VideoPostProcessor，调色面板不可用");
                enabled = false;
                return;
            }

            _root = new GameObject("GradePanelCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _root.transform.SetParent(transform, false);
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;   // 永远压在游戏 UI 之上

            // 固定像素尺寸，不跟游戏分辨率缩放。
            // 用 ScaleWithScreenSize 的话，Game 视图只要不是满 1080 高，整个面板连字一起等比缩小，
            // 中文笔画糊成一团，看着就像被裁掉了。开发工具要的是永远清晰可读。
            var scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            BuildSplitHandle();

            // 面板底板，贴右侧
            _panel = NewRect(_root.transform, "Panel");
            _panel.anchorMin = new Vector2(1f, 0f);
            _panel.anchorMax = new Vector2(1f, 1f);
            _panel.pivot = new Vector2(1f, 0.5f);
            _panel.sizeDelta = new Vector2(PanelWidth, 0f);
            _panel.anchoredPosition = Vector2.zero;
            _panel.gameObject.AddComponent<Image>().color = PanelBg;

            BuildTitle();
            float barTotal = BuildButtonBars();
            BuildViewport(barTotal);
            BuildTabBar();

            // 三个页签的内容
            BuildTab("一级校色", BuildPrimaryRows);
            BuildTab("效果",     BuildEffectRows);
            BuildTab("监看",     BuildMonitorRows);

            SelectTab(0);
        }

        void BuildTitle()
        {
            var title = NewText(_panel, "Title", "视频调色", 30f, HeaderCol, TextAlignmentOptions.Left);
            var titleRt = (RectTransform)title.transform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.sizeDelta = new Vector2(-32f, 40f);
            titleRt.anchoredPosition = new Vector2(0f, -10f);

            var hint = NewText(_panel, "Hint", $"{toggleKey} 开关", 18f, ValueCol, TextAlignmentOptions.Right);
            var hintRt = (RectTransform)hint.transform;
            hintRt.anchorMin = new Vector2(0f, 1f);
            hintRt.anchorMax = new Vector2(1f, 1f);
            hintRt.pivot = new Vector2(0.5f, 1f);
            hintRt.sizeDelta = new Vector2(-32f, 40f);
            hintRt.anchoredPosition = new Vector2(0f, -10f);
        }

        void BuildTabBar()
        {
            var bar = NewRect(_panel, "TabBar");
            bar.anchorMin = new Vector2(0f, 1f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(0.5f, 1f);
            bar.sizeDelta = new Vector2(-24f, TabBarHeight);
            bar.anchoredPosition = new Vector2(0f, -52f);

            var layout = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            _tabBar = bar;
        }
        RectTransform _tabBar;

        float BuildButtonBars()
        {
            float barTotal = BarRowHeight * 2f + BarRowGap;

            var barTop = MakeButtonRow(_panel, "ButtonBarTop", 10f + BarRowHeight + BarRowGap);
            AddToggleButton(barTop, "原图对比",
                            () => postProcessor.bypass,
                            v => { postProcessor.bypass = v; UpdateSplitHandle(); });
            AddToggleButton(barTop, "测试卡",
                            () => postProcessor.showTestPattern,
                            v =>
                            {
                                postProcessor.showTestPattern = v;
                                if (placeholderView != null) placeholderView.gameObject.SetActive(!v && _placeholderWasActive);
                            });

            var barBottom = MakeButtonRow(_panel, "ButtonBarBottom", 10f);
            AddButton(barBottom, "重置", () => { postProcessor.settings.Reset(); RefreshAll(); });
            AddButton(barBottom, "载入", () => { postProcessor.LoadPreset(); RefreshAll(); });
            AddButton(barBottom, "保存", () => postProcessor.SavePreset());

            return barTotal;
        }

        void BuildViewport(float barTotal)
        {
            _viewport = NewRect(_panel, "Viewport");
            _viewport.anchorMin = new Vector2(0f, 0f);
            _viewport.anchorMax = new Vector2(1f, 1f);
            _viewport.offsetMin = new Vector2(4f, barTotal + 18f);
            _viewport.offsetMax = new Vector2(-4f, -(52f + TabBarHeight + 8f));
            // RectMask2D 需要一个 Graphic 来接收滚轮事件
            _viewport.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            _viewport.gameObject.AddComponent<RectMask2D>();

            _scroll = _panel.gameObject.AddComponent<ScrollRect>();
            _scroll.viewport = _viewport;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 32f;
        }

        void BuildTab(string name, Action buildRows)
        {
            var content = NewRect(_viewport, "Content_" + name);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;

            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 2f;
            vlg.padding = new RectOffset(10, 10, 4, 12);
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _target = content;
            buildRows();

            var tab = new Tab { name = name, content = content };

            // 页签按钮
            var go = new GameObject("Tab_" + name, typeof(RectTransform));
            go.transform.SetParent(_tabBar, false);
            var img = go.AddComponent<Image>();
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            tab.buttonImage = img;

            var label = NewText(go.transform, "Label", name, 19f, LabelCol, TextAlignmentOptions.Center, autoSize: true);
            var rt = (RectTransform)label.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(4f, 0f);
            rt.offsetMax = new Vector2(-4f, 0f);

            int index = _tabs.Count;
            btn.onClick.AddListener(() => SelectTab(index));

            _tabs.Add(tab);
        }

        void SelectTab(int index)
        {
            _activeTab = Mathf.Clamp(index, 0, _tabs.Count - 1);
            for (int i = 0; i < _tabs.Count; i++)
            {
                bool on = i == _activeTab;
                _tabs[i].content.gameObject.SetActive(on);
                if (_tabs[i].buttonImage != null) _tabs[i].buttonImage.color = on ? TabOnCol : TabOffCol;
                if (on && _scroll != null)
                {
                    _scroll.content = _tabs[i].content;
                    _scroll.verticalNormalizedPosition = 1f;
                }
            }
        }

        #endregion

        #region 各页签的内容

        void BuildPrimaryRows()
        {
            var s = postProcessor.settings;

            AddHeader("曝光与白平衡");
            AddSlider("曝光",   -3f,   3f,   () => s.exposure,    v => s.exposure = v,    SignedFmt);
            AddSlider("色温",   -1f,   1f,   () => s.temperature, v => s.temperature = v, SignedFmt);
            AddSlider("色调",   -1f,   1f,   () => s.tint,        v => s.tint = v,        SignedFmt);
            AddEnumRow("色调映射", new[] { "无", "Reinhard", "Filmic", "ACES" },
                       () => s.tonemap, v => s.tonemap = v);

            AddHeader("色阶");
            AddSlider("输入黑",  0f,   0.5f, () => s.inBlack,     v => s.inBlack = v,     "0.000");
            AddSlider("输入白",  0.5f, 1f,   () => s.inWhite,     v => s.inWhite = v,     "0.000");
            AddSlider("中间调",  0.2f, 3f,   () => s.levelsGamma, v => s.levelsGamma = v);
            AddSlider("输出黑",  0f,   0.5f, () => s.outBlack,    v => s.outBlack = v,    "0.000");
            AddSlider("输出白",  0.5f, 1f,   () => s.outWhite,    v => s.outWhite = v,    "0.000");

            AddHeader("Lift（暗部）");
            AddSlider("主控",   -0.3f, 0.3f, () => s.lift,  v => s.lift = v,  SignedFmt);
            AddSlider("红",     -0.3f, 0.3f, () => s.liftR, v => s.liftR = v, SignedFmt);
            AddSlider("绿",     -0.3f, 0.3f, () => s.liftG, v => s.liftG = v, SignedFmt);
            AddSlider("蓝",     -0.3f, 0.3f, () => s.liftB, v => s.liftB = v, SignedFmt);

            AddHeader("Gamma（中间调）");
            AddSlider("主控",    0.2f, 3f,   () => s.gammaMaster, v => s.gammaMaster = v);
            AddSlider("红",      0.2f, 3f,   () => s.gammaR,      v => s.gammaR = v);
            AddSlider("绿",      0.2f, 3f,   () => s.gammaG,      v => s.gammaG = v);
            AddSlider("蓝",      0.2f, 3f,   () => s.gammaB,      v => s.gammaB = v);

            AddHeader("Gain（亮部）");
            AddSlider("主控",    0f,   2f,   () => s.gainMaster, v => s.gainMaster = v);
            AddSlider("红",      0f,   2f,   () => s.gainR,      v => s.gainR = v);
            AddSlider("绿",      0f,   2f,   () => s.gainG,      v => s.gainG = v);
            AddSlider("蓝",      0f,   2f,   () => s.gainB,      v => s.gainB = v);

            AddHeader("Offset（整体平移）");
            AddSlider("主控",   -0.2f, 0.2f, () => s.offset,  v => s.offset = v,  SignedFmt);
            AddSlider("红",     -0.2f, 0.2f, () => s.offsetR, v => s.offsetR = v, SignedFmt);
            AddSlider("绿",     -0.2f, 0.2f, () => s.offsetG, v => s.offsetG = v, SignedFmt);
            AddSlider("蓝",     -0.2f, 0.2f, () => s.offsetB, v => s.offsetB = v, SignedFmt);

            AddHeader("反差与色彩");
            AddSlider("对比度",  0f,   2f,   () => s.contrast,    v => s.contrast = v);
            AddSlider("高光",   -1f,   1f,   () => s.highlights,  v => s.highlights = v,  SignedFmt);
            AddSlider("阴影",   -1f,   1f,   () => s.shadows,     v => s.shadows = v,     SignedFmt);
            AddSlider("饱和度",  0f,   2f,   () => s.saturation,  v => s.saturation = v);
            AddSlider("肤色保护", 0f,   1f,   () => s.skinProtect, v => s.skinProtect = v);
            AddSlider("色相",   -0.5f, 0.5f, () => s.hueShift,    v => s.hueShift = v,    SignedFmt);

            AddHeader("色调分离");
            AddSlider("阴影色相", 0f,    1f,   () => s.shadowHue,          v => s.shadowHue = v);
            AddSlider("阴影强度", 0f,    1f,   () => s.shadowStrength,     v => s.shadowStrength = v);
            AddSlider("高光色相", 0f,    1f,   () => s.highlightHue,       v => s.highlightHue = v);
            AddSlider("高光强度", 0f,    1f,   () => s.highlightStrength,  v => s.highlightStrength = v);
            AddSlider("分界平衡", -0.5f, 0.5f, () => s.splitBalance,       v => s.splitBalance = v, SignedFmt);
        }

        void BuildEffectRows()
        {
            var s = postProcessor.settings;

            AddHeader("辉光与模糊");
            AddSlider("辉光阈值", 0f, 2f, () => s.bloomThreshold, v => s.bloomThreshold = v);
            AddSlider("辉光强度", 0f, 3f, () => s.bloomIntensity, v => s.bloomIntensity = v);
            AddSlider("辉光扩散", 0f, 1f, () => s.bloomScatter,   v => s.bloomScatter = v);
            AddSlider("整体模糊", 0f, 1f, () => s.blur,           v => s.blur = v);

            AddHeader("细节");
            AddSlider("锐化",   0f, 2f,   () => s.sharpen, v => s.sharpen = v);
            AddSlider("抖动",   0f, 1f,   () => s.dither,  v => s.dither = v);

            AddHeader("风格化");
            AddSlider("暗角强度", 0f, 1f,   () => s.vignetteIntensity,  v => s.vignetteIntensity = v);
            AddSlider("暗角柔和", 0f, 1f,   () => s.vignetteSmoothness, v => s.vignetteSmoothness = v);
            AddSlider("颗粒",    0f, 0.3f, () => s.grain,              v => s.grain = v, "0.000");
            AddSlider("色差",    0f, 2f,   () => s.chromatic,          v => s.chromatic = v);
        }

        void BuildMonitorRows()
        {
            AddHeader("直方图");
            AddHistogram();
            AddEnumRow("显示通道", new[] { "红", "绿", "蓝", "亮度", "RGB" },
                       () => _histogramView != null ? _histogramView.channel : 4,
                       v => { if (_histogramView != null) { _histogramView.channel = v; _histogramView.Repaint(); } });
            AddSlider("刷新间隔", 1f, 20f,
                      () => postProcessor.histogramInterval,
                      v => postProcessor.histogramInterval = Mathf.RoundToInt(v), "0 帧");

            AddHeader("分屏对比");
            AddToggleRow("开启分屏", () => postProcessor.splitCompare,
                         v => { postProcessor.splitCompare = v; UpdateSplitHandle(); });
            AddSlider("分割线位置", 0f, 1f,
                      () => postProcessor.splitPosition,
                      v => postProcessor.splitPosition = v);
            AddNote("开启后左半原片、右半调色，直接拖画面上那条白线也行");
        }

        HistogramView _histogramView;

        void AddHistogram()
        {
            var row = NewRect(_target, "Histogram");
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 150f;
            le.minHeight = 150f;

            var imgGo = new GameObject("Image", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            imgGo.transform.SetParent(row, false);
            var imgRt = (RectTransform)imgGo.transform;
            imgRt.anchorMin = Vector2.zero;
            imgRt.anchorMax = Vector2.one;
            imgRt.offsetMin = new Vector2(6f, 6f);
            imgRt.offsetMax = new Vector2(-6f, -6f);
            var raw = imgGo.GetComponent<RawImage>();
            raw.raycastTarget = false;

            _histogramView = imgGo.AddComponent<HistogramView>();
            _histogramView.channel = 4;
            _histogramView.Initialize(postProcessor, raw);
        }

        void BuildSplitHandle()
        {
            // 覆盖全屏的容器，只用来给手柄提供坐标系
            var overlay = NewRect(_root.transform, "SplitOverlay");
            overlay.anchorMin = Vector2.zero;
            overlay.anchorMax = Vector2.one;
            overlay.offsetMin = Vector2.zero;
            overlay.offsetMax = Vector2.zero;
            overlay.pivot = new Vector2(0.5f, 0.5f);

            var handle = NewRect(overlay, "SplitHandle");
            handle.anchorMin = new Vector2(0.5f, 0f);
            handle.anchorMax = new Vector2(0.5f, 1f);
            handle.pivot = new Vector2(0.5f, 0.5f);
            handle.sizeDelta = new Vector2(24f, 0f);
            handle.anchoredPosition = Vector2.zero;

            // 抓取区域要有一定宽度才好拖，但几乎全透明，不挡画面
            var grab = handle.gameObject.AddComponent<Image>();
            grab.color = new Color(1f, 1f, 1f, 0.02f);

            var drag = handle.gameObject.AddComponent<SplitDividerHandle>();
            drag.postProcessor = postProcessor;
            var uiRoot = GameplayUIRoot.Find();
            drag.videoRect = uiRoot != null && uiRoot.videoImage != null
                ? (RectTransform)uiRoot.videoImage.transform
                : null;

            _splitHandle = handle.gameObject;
            _splitHandle.SetActive(false);
        }

        #endregion

        #region 控件工厂

        const string SignedFmt = "+0.00;-0.00; 0.00";

        void AddHeader(string text)
        {
            var row = NewRect(_target, "Header_" + text);
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 36f;
            le.minHeight = 36f;

            var label = NewText(row, "Label", text, 21f, HeaderCol, TextAlignmentOptions.Left);
            var rt = (RectTransform)label.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(LabelIndent, 0f);
            rt.offsetMax = new Vector2(-8f, -2f);

            var line = NewRect(row, "Line");
            line.anchorMin = new Vector2(0f, 0f);
            line.anchorMax = new Vector2(1f, 0f);
            line.pivot = new Vector2(0.5f, 0f);
            line.sizeDelta = new Vector2(-8f, 1f);
            line.anchoredPosition = new Vector2(0f, 2f);
            line.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.10f);
        }

        void AddNote(string text)
        {
            var row = NewRect(_target, "Note");
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 44f;
            le.minHeight = 44f;

            var label = NewText(row, "Label", text, 16f, ValueCol, TextAlignmentOptions.TopLeft);
            label.enableWordWrapping = true;
            var rt = (RectTransform)label.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(LabelIndent, 2f);
            rt.offsetMax = new Vector2(-8f, -2f);
        }

        RectTransform MakeRow(string name)
        {
            var row = NewRect(_target, name);
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = RowHeight;
            le.minHeight = RowHeight;
            return row;
        }

        TextMeshProUGUI MakeRowLabel(RectTransform row, string name)
        {
            var label = NewText(row, "Label", name, 19f, LabelCol, TextAlignmentOptions.Left);
            var rt = (RectTransform)label.transform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(LabelWidth, 0f);
            rt.anchoredPosition = new Vector2(LabelIndent, 0f);
            return label;
        }

        void AddSlider(string name, float min, float max, Func<float> get, Action<float> set, string fmt = "0.00")
        {
            var row = MakeRow("Row_" + name);
            MakeRowLabel(row, name);

            var value = NewText(row, "Value", "", 18f, ValueCol, TextAlignmentOptions.Right);
            var valueRt = (RectTransform)value.transform;
            valueRt.anchorMin = new Vector2(1f, 0f);
            valueRt.anchorMax = new Vector2(1f, 1f);
            valueRt.pivot = new Vector2(1f, 0.5f);
            valueRt.sizeDelta = new Vector2(ValueWidth, 0f);
            valueRt.anchoredPosition = new Vector2(-8f, 0f);

            var slider = MakeSlider(row, min, max);
            var sliderRt = (RectTransform)slider.transform;
            sliderRt.anchorMin = Vector2.zero;
            sliderRt.anchorMax = Vector2.one;
            sliderRt.offsetMin = new Vector2(LabelIndent + LabelWidth + 12f, 5f);
            sliderRt.offsetMax = new Vector2(-(ValueWidth + 14f), -5f);

            bool syncing = false;
            slider.onValueChanged.AddListener(v =>
            {
                if (syncing) return;
                set(v);
                value.text = v.ToString(fmt);
            });

            // 重置/载入之后用它把滑条拨回去，同时避免回调再写一次数据
            _refreshers.Add(() =>
            {
                syncing = true;
                float v = get();
                slider.value = v;
                value.text = v.ToString(fmt);
                syncing = false;
            });

            float init = get();
            slider.value = init;
            value.text = init.ToString(fmt);
        }

        /// <summary>一行「标签 + 可循环切换的按钮」，用来选枚举。</summary>
        void AddEnumRow(string name, string[] options, Func<int> get, Action<int> set)
        {
            var row = MakeRow("Enum_" + name);
            MakeRowLabel(row, name);

            var go = new GameObject("Value", typeof(RectTransform));
            go.transform.SetParent(row, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(LabelIndent + LabelWidth + 12f, 3f);
            rt.offsetMax = new Vector2(-8f, -3f);

            var img = go.AddComponent<Image>();
            img.color = BtnCol;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;

            var label = NewText(go.transform, "Label", "", 18f, LabelCol, TextAlignmentOptions.Center, autoSize: true);
            var lrt = (RectTransform)label.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(6f, 0f);
            lrt.offsetMax = new Vector2(-6f, 0f);

            Action paint = () =>
            {
                int i = Mathf.Clamp(get(), 0, options.Length - 1);
                label.text = options[i];
            };
            btn.onClick.AddListener(() =>
            {
                set((get() + 1) % options.Length);
                paint();
            });
            _refreshers.Add(paint);
            paint();
        }

        /// <summary>一行「标签 + 开关按钮」。</summary>
        void AddToggleRow(string name, Func<bool> get, Action<bool> set)
        {
            var row = MakeRow("Toggle_" + name);
            MakeRowLabel(row, name);

            var go = new GameObject("Value", typeof(RectTransform));
            go.transform.SetParent(row, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(LabelIndent + LabelWidth + 12f, 3f);
            rt.offsetMax = new Vector2(-8f, -3f);

            var img = go.AddComponent<Image>();
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;

            var label = NewText(go.transform, "Label", "", 18f, LabelCol, TextAlignmentOptions.Center, autoSize: true);
            var lrt = (RectTransform)label.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(6f, 0f);
            lrt.offsetMax = new Vector2(-6f, 0f);

            Action paint = () =>
            {
                bool on = get();
                img.color = on ? FillCol : BtnCol;
                label.text = on ? "开" : "关";
            };
            btn.onClick.AddListener(() => { set(!get()); paint(); });
            _refreshers.Add(paint);
            paint();
        }

        Slider MakeSlider(Transform parent, float min, float max)
        {
            var go = new GameObject("Slider", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var slider = go.AddComponent<Slider>();

            var bg = NewRect(go.transform, "Background");
            bg.anchorMin = new Vector2(0f, 0.5f);
            bg.anchorMax = new Vector2(1f, 0.5f);
            bg.pivot = new Vector2(0.5f, 0.5f);
            bg.sizeDelta = new Vector2(0f, 5f);
            bg.anchoredPosition = Vector2.zero;
            bg.gameObject.AddComponent<Image>().color = TrackCol;

            var fillArea = NewRect(go.transform, "Fill Area");
            fillArea.anchorMin = new Vector2(0f, 0.5f);
            fillArea.anchorMax = new Vector2(1f, 0.5f);
            fillArea.pivot = new Vector2(0.5f, 0.5f);
            fillArea.sizeDelta = new Vector2(-16f, 5f);
            fillArea.anchoredPosition = Vector2.zero;

            var fill = NewRect(fillArea, "Fill");
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = new Vector2(0f, 1f);
            fill.pivot = new Vector2(0f, 0.5f);
            fill.sizeDelta = new Vector2(16f, 0f);
            fill.gameObject.AddComponent<Image>().color = FillCol;

            var handleArea = NewRect(go.transform, "Handle Slide Area");
            handleArea.anchorMin = Vector2.zero;
            handleArea.anchorMax = Vector2.one;
            handleArea.offsetMin = new Vector2(8f, 0f);
            handleArea.offsetMax = new Vector2(-8f, 0f);

            var handle = NewRect(handleArea, "Handle");
            handle.anchorMin = new Vector2(0f, 0f);
            handle.anchorMax = new Vector2(0f, 1f);
            handle.pivot = new Vector2(0.5f, 0.5f);
            handle.sizeDelta = new Vector2(16f, -6f);
            var handleImg = handle.gameObject.AddComponent<Image>();
            handleImg.color = HandleCol;

            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImg;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = false;
            slider.transition = Selectable.Transition.None;

            return slider;
        }

        /// <summary>建一排等宽横排按钮的容器，bottomOffset 是距面板底部的距离。</summary>
        RectTransform MakeButtonRow(Transform parent, string name, float bottomOffset)
        {
            var bar = NewRect(parent, name);
            bar.anchorMin = new Vector2(0f, 0f);
            bar.anchorMax = new Vector2(1f, 0f);
            bar.pivot = new Vector2(0.5f, 0f);
            bar.sizeDelta = new Vector2(-24f, BarRowHeight);
            bar.anchoredPosition = new Vector2(0f, bottomOffset);

            var layout = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            return bar;
        }

        void AddButton(Transform parent, string text, Action onClick)
        {
            var go = new GameObject("Btn_" + text, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = BtnCol;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick());

            var label = NewText(go.transform, "Label", text, 19f, LabelCol, TextAlignmentOptions.Center, autoSize: true);
            var rt = (RectTransform)label.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(6f, 0f);
            rt.offsetMax = new Vector2(-6f, 0f);
        }

        void AddToggleButton(Transform parent, string text, Func<bool> get, Action<bool> set)
        {
            var go = new GameObject("Toggle_" + text, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;

            var label = NewText(go.transform, "Label", text, 19f, LabelCol, TextAlignmentOptions.Center, autoSize: true);
            var rt = (RectTransform)label.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(6f, 0f);
            rt.offsetMax = new Vector2(-6f, 0f);

            Action paint = () => img.color = get() ? FillCol : BtnCol;
            btn.onClick.AddListener(() => { set(!get()); paint(); });
            _refreshers.Add(paint);
            paint();
        }

        static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>字体不指定，TMP 会自动用默认字体资产——一键搭建时已经把它设成中文字体了。</summary>
        static TextMeshProUGUI NewText(Transform parent, string name, string text, float size,
                                       Color color, TextAlignmentOptions alignment, bool autoSize = false)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = alignment;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;

            // 只有长度不固定的文字（按钮标签）才开自适应。
            // 参数名这种固定短词一律用统一字号——各行字号不一样反而显得没对齐。
            if (autoSize)
            {
                tmp.enableAutoSizing = true;
                tmp.fontSizeMax = size;
                tmp.fontSizeMin = Mathf.Max(11f, size * 0.7f);
            }
            return tmp;
        }

        #endregion
    }
}
