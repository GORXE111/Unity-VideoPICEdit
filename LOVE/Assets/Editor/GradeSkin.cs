using UnityEditor;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// 调色系窗口的统一外观。
    ///
    /// 之前颜色是二十多处散落的 new Color(0.13f, 0.14f, 0.16f) 字面量，
    /// 结果就是调色台、视频台、修图台三个窗口细看颜色对不齐，
    /// 改一处配色要翻遍所有文件。这里收成一份。
    /// </summary>
    public static class GradeSkin
    {
        // ---------------- 底色 ----------------
        // 由暗到亮：画布最暗（衬托画面）、面板最亮（承载控件）

        public static readonly Color Canvas   = new Color(0.13f, 0.14f, 0.16f);
        public static readonly Color Trough   = new Color(0.11f, 0.11f, 0.13f);   // 凹槽：时间轴底、进度条底
        public static readonly Color Bar      = new Color(0.19f, 0.19f, 0.21f);   // 次级条：时间轴、状态栏
        public static readonly Color Panel    = new Color(0.22f, 0.22f, 0.24f);   // 参数栏
        public static readonly Color Splitter = new Color(0.13f, 0.14f, 0.16f);

        // ---------------- 前景 ----------------

        public static readonly Color Grip      = new Color(1f, 1f, 1f, 0.22f);    // 分隔条上那道握把
        public static readonly Color Edge      = new Color(0f, 0f, 0f, 0.55f);    // 图像描边
        public static readonly Color Dim       = new Color(0f, 0f, 0f, 0.55f);    // 裁剪框外的压暗
        public static readonly Color Guide     = new Color(1f, 1f, 1f, 0.28f);    // 三分线这类参考线
        public static readonly Color Outline   = new Color(1f, 1f, 1f, 0.90f);    // 裁剪框、控制点
        public static readonly Color HoverRow  = new Color(1f, 1f, 1f, 0.045f);   // 参数行悬停

        // ---------------- 强调色 ----------------
        // 只用两个：蓝表示"选中的范围"，黄表示"当前位置"。
        // 多了以后界面会变成圣诞树，看久了反而分不清主次

        public static readonly Color Accent    = new Color(0.36f, 0.56f, 0.78f);
        public static readonly Color AccentDim = new Color(0.30f, 0.45f, 0.62f, 0.55f);
        public static readonly Color Playhead  = new Color(1f, 0.85f, 0.30f);

        // ---------------- 尺寸 ----------------

        public const float ToolbarH  = 22f;
        public const float StatusH   = 20f;
        public const float SplitterW = 5f;

        /// <summary>参数栏收起后剩下的那条竖边宽度，点它能再展开。</summary>
        public const float CollapsedW = 22f;

        // ---------------- 样式 ----------------

        static GUIStyle _sectionHeader, _status, _statusDim, _placeholder;

        /// <summary>分组标题。比默认的 foldoutHeader 紧凑一点，配色更沉。</summary>
        public static GUIStyle SectionHeader =>
            _sectionHeader ??= new GUIStyle(EditorStyles.foldoutHeader)
            {
                fontSize = 11,
                fixedHeight = 20f,
                margin = new RectOffset(0, 0, 2, 2),
            };

        public static GUIStyle Status =>
            _status ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 6, 0, 0),
            };

        /// <summary>状态栏里的次要信息，比主信息暗一档。</summary>
        public static GUIStyle StatusDim
        {
            get
            {
                if (_statusDim != null) return _statusDim;
                _statusDim = new GUIStyle(Status);
                var c = _statusDim.normal.textColor;
                c.a = 0.55f;
                _statusDim.normal.textColor = c;
                return _statusDim;
            }
        }

        /// <summary>空画布上那句提示。</summary>
        public static GUIStyle Placeholder =>
            _placeholder ??= new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 13 };

        // ---------------- 画法 ----------------

        /// <summary>可拖拽分隔条：底色 + 中间一道握把，让人看出这里能拖。</summary>
        public static void DrawSplitter(Rect r, bool vertical)
        {
            EditorGUIUtility.AddCursorRect(r, vertical ? MouseCursor.ResizeHorizontal : MouseCursor.ResizeVertical);
            if (Event.current.type != EventType.Repaint) return;

            EditorGUI.DrawRect(r, Splitter);
            var grip = vertical
                ? new Rect(r.center.x - 0.5f, r.center.y - 14f, 1f, 28f)
                : new Rect(r.center.x - 14f, r.center.y - 0.5f, 28f, 1f);
            EditorGUI.DrawRect(grip, Grip);
        }

        /// <summary>一像素细线。IMGUI 没有画线的原语，只能用薄矩形。</summary>
        public static void Line(float x, float y, float w, float h, Color c) =>
            EditorGUI.DrawRect(new Rect(x, y, w, h), c);

        /// <summary>矩形描边，四条边分开画。</summary>
        public static void Frame(Rect r, Color c, float t = 1f)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - t, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, t, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - t, r.y, t, r.height), c);
        }
    }
}
