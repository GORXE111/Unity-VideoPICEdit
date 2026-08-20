using System;
using System.Collections.Generic;
using Love.Video;
using UnityEditor;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// 调色参数的界面绘制，调色台和修图台共用一份。
    /// 参数有 60 多个，分两处维护迟早会不一致。
    ///
    /// 每个控件都用 ref 直接指向 settings 上的字段，加参数就是加一行。
    /// </summary>
    public class GradeSettingsGUI
    {
        static readonly Rect CurveRange = new Rect(0f, 0f, 1f, 1f);

        // 折叠状态存 EditorPrefs：程序集一重载普通字段就重置，
        // 每次改完代码所有分组都弹回默认状态很烦
        bool _foldPrimary   { get => GetFold("primary", true);    set => SetFold("primary", value); }
        bool _foldLevels    { get => GetFold("levels", false);    set => SetFold("levels", value); }
        bool _foldWheels    { get => GetFold("wheels", false);    set => SetFold("wheels", value); }
        bool _foldGrade     { get => GetFold("grade", true);      set => SetFold("grade", value); }
        bool _foldCurve     { get => GetFold("curve", false);     set => SetFold("curve", value); }
        bool _foldSecondary { get => GetFold("secondary", false); set => SetFold("secondary", value); }
        bool _foldEffect    { get => GetFold("effect", true);     set => SetFold("effect", value); }
        bool _foldMask      { get => GetFold("aimask", false);    set => SetFold("aimask", value); }
        bool _foldLog       { get => GetFold("log", false);       set => SetFold("log", value); }
        bool _foldQuality   { get => GetFold("quality", false);   set => SetFold("quality", value); }
        bool _foldSix       { get => GetFold("sixcurve", false);  set => SetFold("sixcurve", value); }
        bool _foldLibrary   { get => GetFold("library", false);   set => SetFold("library", value); }
        bool _foldHsl       { get => GetFold("hsl", false);       set => SetFold("hsl", value); }
        bool _foldCrop      { get => GetFold("crop", false);      set => SetFold("crop", value); }

        string _newPresetName = "";
        List<string> _presetCache;
        double _presetCacheTime;

        static bool GetFold(string key, bool def) => EditorPrefs.GetBool("GradeGUI.fold." + key, def);
        static void SetFold(string key, bool value) => EditorPrefs.SetBool("GradeGUI.fold." + key, value);

        UnityEngine.Object _undoTarget;

        /// <summary>Power Window 的画中画预览用的贴图，传 null 就不显示预览。</summary>
        public Texture PreviewTexture { get; set; }

        /// <summary>
        /// 参数栏的实际可用宽度。必须由调用方传进来——
        /// EditorGUIUtility.currentViewWidth 在 BeginArea 里返回的是整个窗口的宽度，
        /// 拿它算色轮尺寸会在窄面板里算出一排放不下的大轮子。
        /// </summary>
        public float PanelWidth { get; set; } = 360f;

        /// <summary>
        /// 当前源图的像素尺寸。裁剪的比例预设要靠它换算成归一化的框——
        /// 不知道原图多宽多高，就没法把「16:9」变成一组 cropW / cropH。
        /// 调用方不设的话，比例按钮只剩「自由」能用。
        /// </summary>
        public Vector2Int SourceSize { get; set; }

        bool _externalChange;

        /// <summary>
        /// 弹窗类控件（转盘选色）是跨帧的，改动发生在调用方的 OnGUI 之外，
        /// BeginChangeCheck 捕捉不到。调用方每帧读一次这个来决定要不要重渲染。
        /// </summary>
        public bool ConsumeExternalChange()
        {
            bool v = _externalChange;
            _externalChange = false;
            return v;
        }

        public void Draw(VideoGradeSettings s, UnityEngine.Object undoTarget)
        {
            _undoTarget = undoTarget;

            _foldLibrary = Section(_foldLibrary, "预设库");
            if (_foldLibrary) DrawPresetLibrary(s);

            _foldCrop = Section(_foldCrop, "裁剪与旋转");
            if (_foldCrop) DrawCrop(s);

            _foldLog = Section(_foldLog, "素材解码与校色基准");
            if (_foldLog)
            {
                EditorGUI.BeginChangeCheck();
                var lm = (LogMode)EditorGUILayout.EnumPopup("LOG 编码", (LogMode)s.logMode);
                if (EditorGUI.EndChangeCheck()) { RecordUndo("LOG 编码"); s.logMode = (int)lm; }
                if (s.logMode != 0)
                    EditorGUILayout.HelpBox("已解码回线性。LOG 素材必须先解码再调色，否则拉对比度会又灰又脏。",
                                            MessageType.None);

                Toggle("启用色卡校色矩阵", ref s.colorMatrixEnabled);
                if (s.colorMatrixEnabled && (s.colorMatrix == null || s.colorMatrix.Length < 12))
                    EditorGUILayout.HelpBox("还没有解出矩阵，去修图台的「色卡校色」里拍一张 24 色卡解算。",
                                            MessageType.Warning);
            }

            _foldQuality = Section(_foldQuality, "画质提升");
            if (_foldQuality)
            {
                Slider("降噪", ref s.denoise, 0f, 1f);
                Slider("通透度", ref s.clarity, -1f, 1f);
                Slider("  通透半径", ref s.clarityRadius, 2f, 16f);
                Slider("纹理", ref s.texture, -1f, 1f);
                Slider("去朦胧", ref s.dehaze, -1f, 1f);
                EditorGUILayout.Space(2f);
                Slider("锐化", ref s.sharpen, 0f, 2f);
                Slider("  只锐对焦区", ref s.sharpenFocusOnly, 0f, 1f);
                EditorGUILayout.HelpBox("纹理负值可磨皮；「只锐对焦区」靠局部对比判断，避免把背景噪点锐出来。\n" +
                                        "去朦胧走大气散射模型，负值反过来是加雾。它不带饱和度补偿，通常要配合饱和度一起调。",
                                        MessageType.None);
            }

            _foldPrimary = Section(_foldPrimary, "曝光与白平衡");
            if (_foldPrimary)
            {
                Slider("曝光", ref s.exposure, -3f, 3f);
                Slider("色温", ref s.temperature, -1f, 1f);
                Slider("色调", ref s.tint, -1f, 1f);
                EnumField("色调映射", ref s.tonemap);
            }

            _foldLevels = Section(_foldLevels, "色阶");
            if (_foldLevels)
            {
                Slider("输入黑点", ref s.inBlack, 0f, 0.5f);
                Slider("输入白点", ref s.inWhite, 0.5f, 1f);
                Slider("中间调", ref s.levelsGamma, 0.2f, 3f);
                Slider("输出黑点", ref s.outBlack, 0f, 0.5f);
                Slider("输出白点", ref s.outWhite, 0.5f, 1f);
            }

            _foldWheels = Section(_foldWheels, "色轮  Lift / Gamma / Gain / Offset");
            if (_foldWheels) DrawColorWheels(s);

            _foldGrade = Section(_foldGrade, "反差、色彩与色调分离");
            if (_foldGrade)
            {
                Slider("对比度", ref s.contrast, 0f, 2f);
                Slider("高光", ref s.highlights, -1f, 1f);
                Slider("阴影", ref s.shadows, -1f, 1f);
                Slider("饱和度", ref s.saturation, 0f, 2f);
                Slider("肤色保护", ref s.skinProtect, 0f, 1f);
                Slider("色相", ref s.hueShift, -0.5f, 0.5f);

                EditorGUILayout.Space(4f);
                TintRow("阴影染色", () => s.shadowHue, () => s.shadowStrength,
                        (h, st) => { s.shadowHue = h; s.shadowStrength = st; });
                TintRow("高光染色", () => s.highlightHue, () => s.highlightStrength,
                        (h, st) => { s.highlightHue = h; s.highlightStrength = st; });
                Slider("分界平衡", ref s.splitBalance, -0.5f, 0.5f);
            }

            _foldCurve = Section(_foldCurve, "曲线");
            if (_foldCurve) DrawCurves(s);

            _foldHsl = Section(_foldHsl, "HSL 八色带混合器");
            if (_foldHsl) DrawHslMixer(s);

            _foldSix = Section(_foldSix, "六条曲线");
            if (_foldSix)
            {
                Toggle("启用六条曲线", ref s.sixCurveEnabled);
                using (new EditorGUI.DisabledScope(!s.sixCurveEnabled))
                {
                    EditorGUILayout.HelpBox("横轴是输入、纵轴是增减量，中线 0.5 是不变。", MessageType.None);
                    SixCurveField("色相 vs 色相", ref s.hueVsHue);
                    SixCurveField("色相 vs 饱和", ref s.hueVsSat);
                    SixCurveField("色相 vs 亮度", ref s.hueVsLum);
                    SixCurveField("亮度 vs 饱和", ref s.lumVsSat);
                    SixCurveField("饱和 vs 饱和", ref s.satVsSat);
                    SixCurveField("饱和 vs 亮度", ref s.satVsLum);
                    if (GUILayout.Button("全部拉平"))
                    {
                        RecordUndo("重置六条曲线");
                        s.hueVsHue = VideoGradeSettings.Flat(); s.hueVsSat = VideoGradeSettings.Flat();
                        s.hueVsLum = VideoGradeSettings.Flat(); s.lumVsSat = VideoGradeSettings.Flat();
                        s.satVsSat = VideoGradeSettings.Flat(); s.satVsLum = VideoGradeSettings.Flat();
                    }
                }
            }

            _foldSecondary = Section(_foldSecondary, "二级校色（Power Window / HSL 限定器）");
            if (_foldSecondary) DrawSecondary(s);

            _foldMask = Section(_foldMask, "AI 蒙版用法");
            if (_foldMask)
            {
                EditorGUILayout.HelpBox("先在上面生成蒙版，这里的参数才有效果。", MessageType.None);
                Slider("背景虚化", ref s.backgroundBlur, 0f, 1f);
                Toggle("反选（作用于背景）", ref s.maskInvert);
                Slider("边缘收缩", ref s.maskLow, 0f, 0.6f);
                Slider("边缘扩张", ref s.maskHigh, 0.4f, 1f);
                Toggle("二级校色叠加蒙版", ref s.secondaryUseMask);
            }

            _foldEffect = Section(_foldEffect, "效果");
            if (_foldEffect)
            {
                Slider("辉光阈值", ref s.bloomThreshold, 0f, 2f);
                Slider("辉光强度", ref s.bloomIntensity, 0f, 3f);
                Slider("辉光扩散", ref s.bloomScatter, 0f, 1f);
                Slider("整体模糊", ref s.blur, 0f, 1f);
                EditorGUILayout.Space(4f);
                Slider("抖动", ref s.dither, 0f, 1f);
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("镜头", EditorStyles.miniBoldLabel);
                Slider("  畸变", ref s.distortK1, -0.5f, 0.5f);
                Slider("  畸变高阶", ref s.distortK2, -0.3f, 0.3f);
                Slider("  缩放补偿", ref s.distortScale, 0.8f, 1.3f);
                EditorGUILayout.Space(2f);
                Slider("暗角强度", ref s.vignetteIntensity, 0f, 1f);
                Slider("暗角柔和", ref s.vignetteSmoothness, 0f, 1f);
                Slider("颗粒", ref s.grain, 0f, 0.3f);
                Slider("色差", ref s.chromatic, 0f, 2f);

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("斑马纹（监看用，导出不受影响）", EditorStyles.miniBoldLabel);
                Slider("  过曝阈值", ref s.zebraHigh, 0f, 1f);
                Slider("  欠曝阈值", ref s.zebraLow, 0f, 0.2f);
            }
        }

        #region 预设库

        /// <summary>
        /// 预设库。存的是 StreamingAssets/Story/Grades 下的 json，
        /// 和剧情段的 grade 字段用的是同一批文件——在这里存好，剧情里直接按名字引用。
        /// </summary>
        void DrawPresetLibrary(VideoGradeSettings s)
        {
            // 目录列举有磁盘开销，缓存一秒，避免每帧都扫
            if (_presetCache == null || EditorApplication.timeSinceStartup - _presetCacheTime > 1.0)
            {
                _presetCache = GradePresetStore.List();
                _presetCacheTime = EditorApplication.timeSinceStartup;
            }

            EditorGUILayout.BeginHorizontal();
            _newPresetName = EditorGUILayout.TextField(_newPresetName);
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_newPresetName)))
            {
                if (GUILayout.Button("存为新预设", GUILayout.Width(88f)))
                {
                    if (GradePresetStore.Save(_newPresetName.Trim(), s))
                    {
                        AssetDatabase.Refresh();
                        _presetCache = null;
                        _newPresetName = "";
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            if (_presetCache.Count == 0)
            {
                EditorGUILayout.HelpBox("还没有预设。调好之后在上面起个名字存一个。", MessageType.None);
                return;
            }

            foreach (var name in _presetCache)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(name);

                if (GUILayout.Button("载入", GUILayout.Width(46f)))
                {
                    var loaded = GradePresetStore.Load(name);
                    if (loaded != null) { RecordUndo("载入预设 " + name); s.CopyFrom(loaded); _externalChange = true; }
                }
                if (GUILayout.Button("覆盖", GUILayout.Width(46f)))
                {
                    if (EditorUtility.DisplayDialog("预设库", $"用当前参数覆盖「{name}」？", "覆盖", "取消"))
                    {
                        GradePresetStore.Save(name, s);
                        AssetDatabase.Refresh();
                    }
                }
                if (GUILayout.Button("删", GUILayout.Width(30f)))
                {
                    if (EditorUtility.DisplayDialog("预设库", $"删除预设「{name}」？", "删除", "取消"))
                    {
                        GradePresetStore.Delete(name);
                        AssetDatabase.Refresh();
                        _presetCache = null;
                        GUIUtility.ExitGUI();   // 列表已变，本帧不能再继续遍历
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.HelpBox("这些预设名可以直接填进 story.json 的 grade 字段，按剧情段自动切换。",
                                    MessageType.None);
        }

        #endregion

        #region 色轮

        bool _showWheelNumbers { get => GetFold("wheelNums", false); set => SetFold("wheelNums", value); }

        /// <summary>
        /// 四个一级校色轮。每列是「色轮 + 主控滑条」，
        /// 色轮改的是 RGB 色偏（三通道和为 0，不动亮度），主控改亮度——和达芬奇一致。
        /// 面板宽度不够时自动从一行四个折成两行两个。
        /// </summary>
        void DrawColorWheels(VideoGradeSettings s)
        {
            float avail = Mathf.Max(140f, PanelWidth - 24f);
            int perRow = avail >= 430f ? 4 : 2;
            float gap = 8f;
            float cell = Mathf.Clamp((avail - gap * (perRow + 1)) / perRow, 62f, 128f);

            // 撤销必须在改动之前登记。事后再 RecordObject 存进去的是新状态，Ctrl+Z 就回不去了
            if (Event.current.type == EventType.MouseDown) RecordUndo("色轮");

            DrawWheelRow(s, 0, perRow, cell, gap);
            if (perRow < 4) DrawWheelRow(s, 2, perRow, cell, gap);

            EditorGUILayout.Space(2f);
            _showWheelNumbers = EditorGUILayout.ToggleLeft("显示数值（微调用）", _showWheelNumbers);
            if (!_showWheelNumbers) return;

            EditorGUI.indentLevel++;
            Slider("Lift 红", ref s.liftR, -0.3f, 0.3f);
            Slider("Lift 绿", ref s.liftG, -0.3f, 0.3f);
            Slider("Lift 蓝", ref s.liftB, -0.3f, 0.3f);
            Slider("Gamma 红", ref s.gammaR, 0.2f, 3f);
            Slider("Gamma 绿", ref s.gammaG, 0.2f, 3f);
            Slider("Gamma 蓝", ref s.gammaB, 0.2f, 3f);
            Slider("Gain 红", ref s.gainR, 0f, 2f);
            Slider("Gain 绿", ref s.gainG, 0f, 2f);
            Slider("Gain 蓝", ref s.gainB, 0f, 2f);
            Slider("Offset 红", ref s.offsetR, -0.2f, 0.2f);
            Slider("Offset 绿", ref s.offsetG, -0.2f, 0.2f);
            Slider("Offset 蓝", ref s.offsetB, -0.2f, 0.2f);
            EditorGUI.indentLevel--;
        }

        void DrawWheelRow(VideoGradeSettings s, int startIndex, int count, float cell, float gap)
        {
            float rowH = ColorWheelGUI.TrackBallHeight(cell) + 22f;
            var row = GUILayoutUtility.GetRect(0f, rowH, GUILayout.ExpandWidth(true));

            for (int i = 0; i < count; i++)
            {
                int idx = startIndex + i;
                if (idx > 3) break;

                var cellRect = new Rect(row.x + gap + i * (cell + gap), row.y, cell, rowH);
                DrawOneWheel(s, idx, cellRect);
            }
        }

        void DrawOneWheel(VideoGradeSettings s, int index, Rect cellRect)
        {
            var wheelRect = new Rect(cellRect.x, cellRect.y, cellRect.width,
                                     ColorWheelGUI.TrackBallHeight(cellRect.width));
            var masterRect = new Rect(cellRect.x, wheelRect.yMax + 3f, cellRect.width, 16f);

            bool changed = false;
            switch (index)
            {
                case 0:
                    changed = ColorWheelGUI.TrackBall(wheelRect, "Lift 暗部",
                        ref s.liftR, ref s.liftG, ref s.liftB, 0.3f);
                    changed |= MiniSlider(masterRect, ref s.lift, -0.3f, 0.3f);
                    break;
                case 1:
                    // Gamma 三通道中性值是 1，色轮要的是以 0 为中心的偏移，进出各转一次
                    changed = TrackBallAroundOne(wheelRect, "Gamma 中间调",
                        ref s.gammaR, ref s.gammaG, ref s.gammaB, 1f);
                    changed |= MiniSlider(masterRect, ref s.gammaMaster, 0.2f, 3f);
                    break;
                case 2:
                    changed = TrackBallAroundOne(wheelRect, "Gain 亮部",
                        ref s.gainR, ref s.gainG, ref s.gainB, 1f);
                    changed |= MiniSlider(masterRect, ref s.gainMaster, 0f, 2f);
                    break;
                default:
                    changed = ColorWheelGUI.TrackBall(wheelRect, "Offset 平移",
                        ref s.offsetR, ref s.offsetG, ref s.offsetB, 0.2f);
                    changed |= MiniSlider(masterRect, ref s.offset, -0.2f, 0.2f);
                    break;
            }

            if (changed) _externalChange = true;
        }

        /// <summary>Gamma / Gain 的中性值是 1 不是 0，先减 1 交给色轮，出来再加回去。</summary>
        bool TrackBallAroundOne(Rect rect, string label, ref float r, ref float g, ref float b, float range)
        {
            float dr = r - 1f, dg = g - 1f, db = b - 1f;
            if (!ColorWheelGUI.TrackBall(rect, label, ref dr, ref dg, ref db, range)) return false;
            r = dr + 1f; g = dg + 1f; b = db + 1f;
            return true;
        }

        bool MiniSlider(Rect rect, ref float value, float min, float max)
        {
            EditorGUI.BeginChangeCheck();
            float v = GUI.HorizontalSlider(rect, value, min, max);
            if (!EditorGUI.EndChangeCheck()) return false;
            value = v;
            return true;
        }

        #endregion

        #region 曲线

        void DrawCurves(VideoGradeSettings s)
        {
            Toggle("启用曲线", ref s.curveEnabled);
            using (new EditorGUI.DisabledScope(!s.curveEnabled))
            {
                CurveField("主曲线", ref s.curveMaster, Color.white);
                CurveField("红", ref s.curveR, new Color(1f, 0.35f, 0.35f));
                CurveField("绿", ref s.curveG, new Color(0.35f, 1f, 0.45f));
                CurveField("蓝", ref s.curveB, new Color(0.45f, 0.6f, 1f));

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("全部拉直")) { RecordUndo("重置曲线"); ResetCurves(s); }
                if (GUILayout.Button("经典 S 曲线")) { RecordUndo("S 曲线"); s.curveMaster = SCurve(); }
                EditorGUILayout.EndHorizontal();
            }
        }

        /// <summary>六条曲线的字段。恒等是 y=0.5 的水平线，不是 y=x。</summary>
        void SixCurveField(string label, ref AnimationCurve curve)
        {
            if (curve == null) curve = VideoGradeSettings.Flat();
            EditorGUI.BeginChangeCheck();
            var c = EditorGUILayout.CurveField(label, curve, new Color(0.9f, 0.8f, 0.4f), CurveRange);
            if (!EditorGUI.EndChangeCheck()) return;
            RecordUndo("编辑六条曲线");
            curve = c;
        }

        void CurveField(string label, ref AnimationCurve curve, Color color)
        {
            if (curve == null) curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            EditorGUI.BeginChangeCheck();
            var c = EditorGUILayout.CurveField(label, curve, color, CurveRange);
            if (!EditorGUI.EndChangeCheck()) return;
            RecordUndo("编辑曲线");
            curve = c;
        }

        static void ResetCurves(VideoGradeSettings s)
        {
            s.curveMaster = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            s.curveR = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            s.curveG = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            s.curveB = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        }

        /// <summary>暗部压一点、亮部提一点的经典反差曲线。</summary>
        static AnimationCurve SCurve() => new AnimationCurve(
            new Keyframe(0f, 0f, 0.6f, 0.6f),
            new Keyframe(0.25f, 0.19f, 0.9f, 0.9f),
            new Keyframe(0.75f, 0.81f, 1.2f, 1.2f),
            new Keyframe(1f, 1f, 1.4f, 1.4f));

        #endregion

        #region 二级校色

        void DrawSecondary(VideoGradeSettings s)
        {
            Toggle("启用二级校色", ref s.secondaryEnabled);
            if (!s.secondaryEnabled)
            {
                EditorGUILayout.HelpBox("开启后，下面的调整只作用于 Power Window 和 HSL 限定器圈出的区域。", MessageType.None);
                return;
            }

            Toggle("显示遮罩（灰度）", ref s.showMask);
            if (s.showMask)
                EditorGUILayout.HelpBox("画面现在显示的是遮罩本身：白色是选中区域。调完记得关掉。", MessageType.Warning);

            EditorGUILayout.LabelField("Power Window", EditorStyles.miniBoldLabel);
            EditorGUI.BeginChangeCheck();
            int shape = EditorGUILayout.Popup("形状", s.windowShape, new[] { "不限（整幅）", "椭圆", "矩形", "线性渐变" });
            if (EditorGUI.EndChangeCheck()) { RecordUndo("窗口形状"); s.windowShape = shape; }

            if (s.windowShape > 0)
            {
                if (PreviewTexture != null) DrawWindowPreview(s);
                Slider("  中心 X", ref s.windowCenter.x, 0f, 1f);
                Slider("  中心 Y", ref s.windowCenter.y, 0f, 1f);
                bool grad = s.windowShape == 3;
                if (!grad) Slider("  半宽", ref s.windowSize.x, 0.02f, 1.5f);
                Slider(grad ? "  渐变跨度" : "  半高", ref s.windowSize.y, 0.02f, 1.5f);
                Slider(grad ? "  渐变方向" : "  旋转", ref s.windowRotation, -180f, 180f);
                Slider("  羽化", ref s.windowFeather, 0.001f, 1f);
                Toggle("  反向（选窗口外）", ref s.windowInvert);
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("HSL 限定器", EditorStyles.miniBoldLabel);
            Toggle("  启用", ref s.qualifierEnabled);
            using (new EditorGUI.DisabledScope(!s.qualifierEnabled))
            {
                var hueRow = GUILayoutUtility.GetRect(0f, 20f, GUILayout.ExpandWidth(true));
                EditorGUI.LabelField(new Rect(hueRow.x, hueRow.y + 2f, EditorGUIUtility.labelWidth - 4f, 16f),
                                     "  色相中心");

                var hueSwatch = new Rect(hueRow.x + EditorGUIUtility.labelWidth, hueRow.y + 2f, 32f, 16f);
                ColorWheelGUI.HueOnlySwatch(hueSwatch, s.qualHueCenter, nh =>
                {
                    RecordUndo("限定器色相");
                    s.qualHueCenter = nh;
                    _externalChange = true;
                });

                var hueSlider = new Rect(hueSwatch.xMax + 6f, hueRow.y + 2f,
                                         Mathf.Max(40f, hueRow.xMax - hueSwatch.xMax - 8f), 16f);
                EditorGUI.BeginChangeCheck();
                float sh = GUI.HorizontalSlider(hueSlider, s.qualHueCenter, 0f, 1f);
                if (EditorGUI.EndChangeCheck()) { RecordUndo("限定器色相"); s.qualHueCenter = sh; }

                Slider("  色相范围", ref s.qualHueRange, 0f, 0.5f);
                Slider("  色相柔和", ref s.qualHueSoft, 0.001f, 0.3f);
                MinMax("  饱和度", ref s.qualSatMin, ref s.qualSatMax, 0f, 1f);
                Slider("  饱和柔和", ref s.qualSatSoft, 0.001f, 0.3f);
                MinMax("  亮度", ref s.qualLumMin, ref s.qualLumMax, 0f, 1f);
                Slider("  亮度柔和", ref s.qualLumSoft, 0.001f, 0.3f);

                if (GUILayout.Button("对准肤色"))
                {
                    RecordUndo("限定器预设：肤色");
                    s.qualHueCenter = 0.06f; s.qualHueRange = 0.05f; s.qualHueSoft = 0.035f;
                    s.qualSatMin = 0.12f; s.qualSatMax = 0.75f; s.qualSatSoft = 0.08f;
                    s.qualLumMin = 0.15f; s.qualLumMax = 1f; s.qualLumSoft = 0.1f;
                }
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("遮罩内的调整", EditorStyles.miniBoldLabel);
            Slider("  曝光", ref s.secExposure, -2f, 2f);
            Slider("  对比度", ref s.secContrast, 0f, 2f);
            Slider("  饱和度", ref s.secSaturation, 0f, 2f);
            Slider("  色相", ref s.secHueShift, -0.5f, 0.5f);
            TintRow("  染色", () => s.secTintHue, () => s.secTintStrength,
                    (h, st) => { s.secTintHue = h; s.secTintStrength = st; });
        }

        /// <summary>
        /// 画中画预览。在里面拖动直接挪 Power Window 的中心，滚轮缩放。
        /// Game 视图里用不了 Handles，所以把交互放到窗口内部来做。
        /// </summary>
        void DrawWindowPreview(VideoGradeSettings s)
        {
            var tex = PreviewTexture;
            float aspect = tex.height > 0 ? (float)tex.width / tex.height : 16f / 9f;

            float w = Mathf.Min(Mathf.Max(140f, PanelWidth - 24f), 520f);
            var rect = GUILayoutUtility.GetRect(w, w / aspect, GUILayout.ExpandWidth(true));

            HandlePreviewInput(rect, s);

            // Layout 事件里 GetRect 返回的是占位矩形，那时画会画错位置
            if (Event.current.type != EventType.Repaint)
            {
                EditorGUILayout.LabelField("在预览里拖动移动窗口，滚轮缩放", EditorStyles.miniLabel);
                return;
            }

            GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit);

            Color prev = Handles.color;
            Handles.color = new Color(1f, 0.85f, 0.2f, 0.9f);

            Vector2 c = new Vector2(rect.x + s.windowCenter.x * rect.width,
                                    rect.yMax - s.windowCenter.y * rect.height);
            float rx = s.windowSize.x / aspect * rect.width;
            float ry = s.windowSize.y * rect.height;
            float rot = -s.windowRotation * Mathf.Deg2Rad;

            const int Seg = 48;
            var pts = new Vector3[Seg + 1];
            for (int i = 0; i <= Seg; i++)
            {
                float t = i / (float)Seg * Mathf.PI * 2f;
                float ox, oy;
                if (s.windowShape > 1.5f)
                {
                    // 矩形：把角度映射到边框上
                    float ct = Mathf.Cos(t), st = Mathf.Sin(t);
                    float m = Mathf.Max(Mathf.Abs(ct), Mathf.Abs(st));
                    ox = ct / m * rx; oy = st / m * ry;
                }
                else { ox = Mathf.Cos(t) * rx; oy = Mathf.Sin(t) * ry; }

                pts[i] = new Vector3(
                    c.x + ox * Mathf.Cos(rot) - oy * Mathf.Sin(rot),
                    c.y + ox * Mathf.Sin(rot) + oy * Mathf.Cos(rot), 0f);
            }
            Handles.DrawAAPolyLine(2f, pts);
            Handles.color = prev;

            EditorGUILayout.LabelField("在预览里拖动移动窗口，滚轮缩放", EditorStyles.miniLabel);
        }

        void HandlePreviewInput(Rect rect, VideoGradeSettings s)
        {
            var e = Event.current;
            if (e.type == EventType.Layout || !rect.Contains(e.mousePosition)) return;

            if (e.type == EventType.MouseDrag && e.button == 0)
            {
                RecordUndo("移动 Power Window");
                s.windowCenter = new Vector2(
                    Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width),
                    Mathf.Clamp01((rect.yMax - e.mousePosition.y) / rect.height));
                e.Use();
            }
            else if (e.type == EventType.ScrollWheel)
            {
                RecordUndo("缩放 Power Window");
                float k = 1f - e.delta.y * 0.03f;
                s.windowSize = new Vector2(
                    Mathf.Clamp(s.windowSize.x * k, 0.02f, 1.5f),
                    Mathf.Clamp(s.windowSize.y * k, 0.02f, 1.5f));
                e.Use();
            }
        }

        #endregion

        #region 控件

        #region 裁剪与旋转

        // 常用构图比例。ratio 是宽/高，0 表示自由不锁
        static readonly (string name, float ratio)[] AspectPresets =
        {
            ("自由", 0f), ("1:1", 1f), ("4:3", 4f / 3f), ("3:2", 3f / 2f),
            ("16:9", 16f / 9f), ("9:16", 9f / 16f), ("2.39:1", 2.39f),
        };

        void DrawCrop(VideoGradeSettings s)
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("逆时针 90°")) { RecordUndo("旋转"); s.rotate90 = (s.rotate90 + 3) % 4; }
            if (GUILayout.Button("顺时针 90°")) { RecordUndo("旋转"); s.rotate90 = (s.rotate90 + 1) % 4; }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            Toggle("水平翻转", ref s.flipH);
            Toggle("垂直翻转", ref s.flipV);
            EditorGUILayout.EndHorizontal();

            Slider("拉直", ref s.straighten, -45f, 45f);

            EditorGUILayout.Space(4f);
            Toggle("启用裁剪", ref s.cropEnabled);

            using (new EditorGUI.DisabledScope(!s.cropEnabled))
            {
                EditorGUILayout.LabelField("构图比例", EditorStyles.miniBoldLabel);

                // 七个按钮一行放不下，按面板宽度折行
                int perRow = Mathf.Max(3, Mathf.FloorToInt(PanelWidth / 66f));
                for (int i = 0; i < AspectPresets.Length; i += perRow)
                {
                    EditorGUILayout.BeginHorizontal();
                    for (int j = i; j < Mathf.Min(i + perRow, AspectPresets.Length); j++)
                    {
                        var preset = AspectPresets[j];
                        if (!GUILayout.Button(preset.name, EditorStyles.miniButton)) continue;
                        RecordUndo("裁剪比例 " + preset.name);
                        if (preset.ratio <= 0f) s.ResetCrop();
                        else ApplyAspect(s, preset.ratio);
                        s.cropEnabled = true;
                    }
                    EditorGUILayout.EndHorizontal();
                }

                Slider("左边界", ref s.cropX, 0f, 0.95f);
                Slider("下边界", ref s.cropY, 0f, 0.95f);
                Slider("宽度", ref s.cropW, 0.05f, 1f);
                Slider("高度", ref s.cropH, 0.05f, 1f);

                // 出界的框渲染端会夹回去，但界面上不说一声，用户只会觉得滑条失灵
                if (s.cropX + s.cropW > 1.0005f || s.cropY + s.cropH > 1.0005f)
                    EditorGUILayout.HelpBox("裁剪框超出画面，渲染时会自动往回夹。", MessageType.Warning);

                if (SourceSize.x > 0 && SourceSize.y > 0)
                {
                    s.OutputSize(SourceSize.x, SourceSize.y, out int ow, out int oh);
                    EditorGUILayout.LabelField("输出尺寸", ow + " × " + oh);
                }

                if (GUILayout.Button("重置裁剪")) { RecordUndo("重置裁剪"); s.ResetCrop(); }
            }
        }

        /// <summary>套一个长宽比：取画面里最大的、居中的、满足该比例的框。</summary>
        void ApplyAspect(VideoGradeSettings s, float ratio)
        {
            if (SourceSize.x <= 0 || SourceSize.y <= 0) return;

            // 裁剪框定义在旋转之后的画面里，所以这里也要按旋转后的长宽算
            float bw = (s.rotate90 & 1) == 0 ? SourceSize.x : SourceSize.y;
            float bh = (s.rotate90 & 1) == 0 ? SourceSize.y : SourceSize.x;
            float imgRatio = bw / Mathf.Max(bh, 1f);

            float cw, ch;
            if (ratio >= imgRatio) { cw = 1f; ch = imgRatio / ratio; }   // 目标更宽，宽度顶满
            else                   { ch = 1f; cw = ratio / imgRatio; }   // 目标更高，高度顶满

            s.cropW = cw; s.cropH = ch;
            s.cropX = (1f - cw) * 0.5f;
            s.cropY = (1f - ch) * 0.5f;
        }

        #endregion

        #region HSL 八色带混合器

        int _hslTab;

        void DrawHslMixer(VideoGradeSettings s)
        {
            Toggle("启用 HSL 混合器", ref s.hslEnabled);

            using (new EditorGUI.DisabledScope(!s.hslEnabled))
            {
                _hslTab = GUILayout.Toolbar(_hslTab, new[] { "色相", "饱和度", "明亮度" });

                // 这三个字段是后加的，早先存下来的 grade.json 里根本没有，读进来可能是 null
                EnsureBands(ref s.hslHue); EnsureBands(ref s.hslSat); EnsureBands(ref s.hslLum);

                var arr = _hslTab == 0 ? s.hslHue : _hslTab == 1 ? s.hslSat : s.hslLum;
                string what = _hslTab == 0 ? "色相" : _hslTab == 1 ? "饱和度" : "明亮度";

                for (int i = 0; i < VideoGradeSettings.HslBandCount; i++)
                    BandSlider(VideoGradeSettings.HslNames[i], VideoGradeSettings.HslCenters[i], arr, i, what);

                if (GUILayout.Button("这一页归零"))
                {
                    RecordUndo("HSL 归零");
                    for (int i = 0; i < arr.Length; i++) arr[i] = 0f;
                }

                EditorGUILayout.HelpBox("接近中性灰的像素不受影响——那里的色相本来就是噪声，" +
                                        "动它只会让灰墙和白衬衫染上颜色。", MessageType.None);
            }
        }

        static void EnsureBands(ref float[] a)
        {
            if (a == null || a.Length != VideoGradeSettings.HslBandCount)
                a = new float[VideoGradeSettings.HslBandCount];
        }

        /// <summary>一行 = 色带颜色块 + 滑条。有色块就不用记「第三根是黄色」。</summary>
        void BandSlider(string name, float hue, float[] arr, int i, string what)
        {
            var row = EditorGUILayout.GetControlRect();

            // GetControlRect 在 Layout 事件返回的是占位矩形，那时候画色块位置是错的
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(row.x, row.y + 2f, 12f, row.height - 4f),
                                   Color.HSVToRGB(hue, 0.8f, 0.95f));

            var rest = new Rect(row.x + 16f, row.y, row.width - 16f, row.height);
            EditorGUI.BeginChangeCheck();
            float v = EditorGUI.Slider(rest, name, arr[i], -1f, 1f);
            if (!EditorGUI.EndChangeCheck()) return;
            RecordUndo("HSL " + what + "：" + name);
            arr[i] = v;
        }

        #endregion

        public static bool Section(bool state, string title)
        {
            EditorGUILayout.Space(2f);
            return EditorGUILayout.Foldout(state, title, true, EditorStyles.foldoutHeader);
        }

        void Slider(string label, ref float value, float min, float max)
        {
            EditorGUI.BeginChangeCheck();
            float v = EditorGUILayout.Slider(label, value, min, max);
            if (!EditorGUI.EndChangeCheck()) return;
            RecordUndo("调色：" + label.Trim());
            value = v;
        }

        void Toggle(string label, ref bool value)
        {
            EditorGUI.BeginChangeCheck();
            bool v = EditorGUILayout.Toggle(label, value);
            if (!EditorGUI.EndChangeCheck()) return;
            RecordUndo("调色：" + label.Trim());
            value = v;
        }

        void EnumField(string label, ref int value)
        {
            EditorGUI.BeginChangeCheck();
            var mode = (TonemapMode)EditorGUILayout.EnumPopup(label, (TonemapMode)value);
            if (!EditorGUI.EndChangeCheck()) return;
            RecordUndo("调色：" + label);
            value = (int)mode;
        }

        /// <summary>一行双头滑条，用来定区间。</summary>
        void MinMax(string label, ref float lo, ref float hi, float limitMin, float limitMax)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel($"{label} {lo:0.00}–{hi:0.00}");
            float a = lo, b = hi;
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.MinMaxSlider(ref a, ref b, limitMin, limitMax);
            if (EditorGUI.EndChangeCheck())
            {
                RecordUndo("调色：" + label.Trim());
                lo = a; hi = b;
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>染色行：色相滑条右边带一个实时颜色预览块，光看数字不知道调成什么颜色了。</summary>
        /// <summary>
        /// 染色行：一个色块 + 强度滑条。点色块弹出转盘选色，
        /// 比拖一根 0~1 的色相滑条直观得多——那根滑条上根本看不出 0.58 是什么颜色。
        /// </summary>
        /// <summary>
        /// 染色行：一个色块 + 强度滑条。点色块弹出转盘选色。
        ///
        /// 不能用 ref 传参：弹窗是跨帧存在的，回调触发时本方法早就返回了，
        /// ref 参数指向的栈位置已经失效。所以改成直接把值写回 settings 对象。
        /// </summary>
        void TintRow(string label, Func<float> getHue, Func<float> getStrength, Action<float, float> write)
        {
            float hue = getHue(), strength = getStrength();
            var row = GUILayoutUtility.GetRect(0f, 20f, GUILayout.ExpandWidth(true));

            var labelRect = new Rect(row.x, row.y + 2f, EditorGUIUtility.labelWidth - 4f, 16f);
            EditorGUI.LabelField(labelRect, label);

            var swatchRect = new Rect(row.x + EditorGUIUtility.labelWidth, row.y + 2f, 32f, 16f);
            ColorWheelGUI.HueSwatch(swatchRect, hue, strength, (nh, ns) =>
            {
                RecordUndo("调色：" + label.Trim());
                write(nh, ns);
                _externalChange = true;
            });

            var sliderRect = new Rect(swatchRect.xMax + 6f, row.y + 2f,
                                      Mathf.Max(40f, row.xMax - swatchRect.xMax - 8f), 16f);
            EditorGUI.BeginChangeCheck();
            float ns2 = GUI.HorizontalSlider(sliderRect, strength, 0f, 1f);
            if (EditorGUI.EndChangeCheck())
            {
                RecordUndo("调色：" + label.Trim());
                write(hue, ns2);
            }
        }

        void RecordUndo(string action)
        {
            if (_undoTarget == null) return;
            Undo.RecordObject(_undoTarget, action);
            if (!EditorApplication.isPlaying) EditorUtility.SetDirty(_undoTarget);
        }

        #endregion
    }
}
