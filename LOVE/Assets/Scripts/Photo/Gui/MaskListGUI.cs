using System;
using System.Collections.Generic;
using Love.Video;
using UnityEngine;

namespace Love.Tools
{
    /// <summary>
    /// 蒙版列表的界面。三个窗口共用。
    ///
    /// 结构照搬 Lightroom：一个蒙版组 = 若干部件（加 / 减 / 交）+ 一组自己的调整参数。
    /// 关键是「显示」那个开关——调选区时必须能看见边界，靠猜是调不出来的。
    /// </summary>
    public class MaskListGUI : IMaskSectionGui
    {
        /// <summary>控件从哪儿来。编辑器和独立程序各一份实现。</summary>
        readonly IGradeGui G;

        public MaskListGUI(IGradeGui gui)
        {
            G = gui ?? throw new System.ArgumentNullException(nameof(gui));
        }

        /// <summary>当前源图尺寸，几何部件的界面提示要用。</summary>
        public Vector2Int SourceSize { get; set; }

        /// <summary>有没有可用的 AI 主体蒙版。没有时「主体」部件会给出提示。</summary>
        public bool HasSubjectMask { get; set; }

        /// <summary>当前这张图有没有算出天空。没有时界面上要说清楚，别让人对着一个不起作用的部件调半天。</summary>
        public bool HasSky { get; set; }

        /// <summary>天空占画面的比例，给界面显示用。</summary>
        public float SkyCoverage { get; set; }

        /// <summary>有没有可用的深度图。</summary>
        public bool HasDepthMap { get; set; }

        /// <summary>请求新建一张笔刷，返回它的 id。窗口负责真正分配那张 RT。</summary>
        public Func<int> RequestBrush { get; set; }

        /// <summary>正在手绘的那个部件，窗口据此接管画布上的鼠标。null 表示没在画。</summary>
        public MaskPart PaintingPart { get; private set; }

        /// <summary>正在画布上拖形状的那个部件。只有几何类部件才可能是它。</summary>
        public MaskPart EditingPart { get; private set; }

        /// <summary>笔刷的大小 / 硬度 / 流量画在哪。这几个参数是窗口的，不属于蒙版数据。</summary>
        public Action DrawBrushOptions { get; set; }

        Action<string> _recordUndo;
        Action _markChanged;
        int _expanded = -1;      // 展开了哪一组的细节

        static readonly string[] OpNames = { "加", "减", "交" };

        static readonly (MaskShape shape, string label, string tip)[] Adders =
        {
            (MaskShape.Ellipse,        "径向",     "椭圆选区，带羽化。提亮人脸、做局部光斑"),
            (MaskShape.LinearGradient, "渐变",     "线性渐变。压天空、提前景全靠它"),
            (MaskShape.Rect,           "矩形",     "矩形选区，带羽化"),
            (MaskShape.Brush,          "画笔",     "手绘。任意形状，画多少选多少"),
            (MaskShape.ColorRange,     "颜色范围", "按色相和饱和度圈一块，比如只调天空的蓝"),
            (MaskShape.LuminanceRange, "亮度范围", "按亮度圈一块，比如只压高光"),
            (MaskShape.DepthRange,     "深度范围", "按远近圈一块，需要先生成深度图"),
            (MaskShape.Sky,            "天空",     "从画面顶边漫延出来，自动圈住天空。压蓝天、救白天空"),
            (MaskShape.Subject,        "AI 主体",  "分割模型抠出来的主体，需要先生成蒙版"),
        };

        public void Draw(VideoGradeSettings s, Action<string> recordUndo, Action markChanged)
        {
            _recordUndo = recordUndo;
            _markChanged = markChanged;

            if (s.maskGroups == null) s.maskGroups = new List<MaskGroup>();

            DrawAddBar(s);

            if (s.maskGroups.Count == 0)
            {
                G.HelpBox(
                    "还没有蒙版。上面选一种来源新建一组。\n" +
                    "一组里可以叠多个部件：比如「渐变」加上，再「减」掉主体，就是只压天空不压人。",
                    GuiMsg.None);
                return;
            }

            for (int i = 0; i < s.maskGroups.Count; i++)
            {
                if (DrawGroup(s, i)) { i--; }   // 被删掉了，下标退一格
            }
        }

        void DrawAddBar(VideoGradeSettings s)
        {
            GUILayout.BeginHorizontal();
            G.MiniBoldLabelW("新建蒙版", 60f);

            if (G.MiniButton("＋", 26f))
            {
                var usable = UsableAdders();
                var labels = new string[usable.Count];
                for (int i = 0; i < usable.Count; i++)
                    labels[i] = usable[i].label + "|" + usable[i].tip;

                G.ContextMenu(labels, i => AddGroup(s, usable[i].shape, usable[i].label));
            }

            GUILayout.FlexibleSpace();
            G.MiniLabelW($"{s.maskGroups.Count} 组", 40f);
            GUILayout.EndHorizontal();
        }

        void AddGroup(VideoGradeSettings s, MaskShape shape, string label)
        {
            _recordUndo?.Invoke("新建蒙版");
            var g = new MaskGroup { name = label };
            g.parts.Add(NewPart(shape, true));
            s.maskGroups.Add(g);
            _expanded = s.maskGroups.Count - 1;
            _markChanged?.Invoke();
        }

        /// <summary>
        /// 这个窗口支不支持某种来源。
        ///
        /// 视频台没有笔刷画布（逐帧手绘也跟不住运动），列出来只会得到一个用不了的部件——
        /// 与其加完再提示"删掉重来"，不如一开始就不给。
        /// </summary>
        bool Supported(MaskShape shape) => shape != MaskShape.Brush || RequestBrush != null;

        MaskPart NewPart(MaskShape shape, bool first)
        {
            var p = new MaskPart { shape = (int)shape, op = first ? (int)MaskOp.Add : (int)MaskOp.Add };

            if (shape == MaskShape.LinearGradient)
            {
                p.center = new Vector2(0.5f, 0.75f);
                p.size = new Vector2(0.5f, 0.25f);
            }
            if (shape == MaskShape.Brush && RequestBrush != null) p.brushId = RequestBrush();
            return p;
        }

        /// <summary>返回 true 表示这一组被删了。</summary>
        bool DrawGroup(VideoGradeSettings s, int index)
        {
            var g = s.maskGroups[index];
            bool expanded = _expanded == index;

            var head = G.Row(20f);
            if (Event.current.type == EventType.Repaint)
                G.FillRect(head, expanded ? new Color(1f, 1f, 1f, 0.06f) : new Color(1f, 1f, 1f, 0.025f));

            float x = head.x + 2f;

            bool on = G.ToggleIn(new Rect(x, head.y + 2f, 16f, 16f), g.enabled);
            if (on != g.enabled) { _recordUndo?.Invoke("启用蒙版"); g.enabled = on; _markChanged?.Invoke(); }
            x += 18f;

            // 名字可以直接改。一屏五六组时，"蒙版 3"这种名字等于没有
            G.BeginChange();
            string nm = G.TextFieldIn(new Rect(x, head.y + 1f, head.width - 150f, 18f), g.name);
            if (G.EndChange()) { _recordUndo?.Invoke("重命名蒙版"); g.name = nm; }
            x = head.xMax - 128f;

            bool ov = G.MiniToggleIn(new Rect(x, head.y + 1f, 42f, 18f), g.showOverlay,
                                     "显示", "把选区以红色叠加在画面上");
            if (ov != g.showOverlay)
            {
                _recordUndo?.Invoke("显示蒙版");
                // 一次只看一组，同时叠两片红的什么也看不出来
                foreach (var other in s.maskGroups) other.showOverlay = false;
                g.showOverlay = ov;
                _markChanged?.Invoke();
            }
            x += 44f;

            if (G.MiniButtonIn(new Rect(x, head.y + 1f, 42f, 18f),
                               expanded ? "收起" : "编辑", null))
                _expanded = expanded ? -1 : index;
            x += 44f;

            if (G.MiniButtonIn(new Rect(x, head.y + 1f, 22f, 18f), "×", null))
            {
                _recordUndo?.Invoke("删除蒙版");
                s.maskGroups.RemoveAt(index);
                if (_expanded >= s.maskGroups.Count) _expanded = -1;
                _markChanged?.Invoke();
                return true;
            }

            if (!expanded) return false;

            G.Indent(1);
            DrawParts(g);
            G.Space(3f);
            DrawGroupAdjust(g);
            G.Indent(-1);
            G.Space(4f);
            return false;
        }

        void DrawParts(MaskGroup g)
        {
            G.MiniBoldLabel("部件");

            for (int i = 0; i < g.parts.Count; i++)
            {
                var p = g.parts[i];

                GUILayout.BeginHorizontal();

                // 第一个部件恒按"加"处理，下拉框没有意义
                using (G.Disabled(i == 0))
                {
                    int op = G.PopupW(i == 0 ? 0 : p.op, OpNames, 46f);
                    if (i > 0 && op != p.op) { _recordUndo?.Invoke("蒙版合并方式"); p.op = op; _markChanged?.Invoke(); }
                }

                G.MiniLabelW(ShapeName(p.Shape), 70f);

                bool inv = G.MiniToggle(p.invert, "反相", 38f);
                if (inv != p.invert) { _recordUndo?.Invoke("蒙版反相"); p.invert = inv; _markChanged?.Invoke(); }

                if (p.Shape == MaskShape.Brush)
                {
                    bool painting = ReferenceEquals(PaintingPart, p);
                    bool want = G.MiniToggle(painting, "涂抹", 38f);
                    if (want != painting) { PaintingPart = want ? p : null; if (want) EditingPart = null; }
                }
                else if (p.IsGeometric)
                {
                    // 只靠滑条对位置基本等于盲调，得能在画面上直接拖
                    bool editing = ReferenceEquals(EditingPart, p);
                    bool want = G.MiniToggle(editing, "定位", 38f);
                    if (want != editing) { EditingPart = want ? p : null; if (want) PaintingPart = null; }
                }

                if (G.MiniButton("×", 22f))
                {
                    _recordUndo?.Invoke("删除部件");
                    if (ReferenceEquals(PaintingPart, p)) PaintingPart = null;
                    if (ReferenceEquals(EditingPart, p)) EditingPart = null;
                    g.parts.RemoveAt(i);
                    _markChanged?.Invoke();
                    GUILayout.EndHorizontal();
                    i--;
                    continue;
                }

                GUILayout.EndHorizontal();

                G.Indent(1);
                DrawPartBody(p);
                G.Indent(-1);
            }

            if (G.MiniButton("＋ 添加部件", 0f))
            {
                var usable = UsableAdders();
                var labels = new string[usable.Count];
                for (int i = 0; i < usable.Count; i++) labels[i] = usable[i].label;

                G.ContextMenu(labels, i =>
                {
                    _recordUndo?.Invoke("添加部件");
                    g.parts.Add(NewPart(usable[i].shape, false));
                    _markChanged?.Invoke();
                });
            }
        }

        void DrawPartBody(MaskPart p)
        {
            G.BeginChange();

            switch (p.Shape)
            {
                case MaskShape.Ellipse:
                case MaskShape.Rect:
                case MaskShape.LinearGradient:
                    p.center = G.Vector2Field("中心", p.center);
                    p.size = G.Vector2Field(
                        p.Shape == MaskShape.LinearGradient ? "方向与跨度" : "半径", p.size);
                    p.rotation = G.Slider("旋转", p.rotation, -180f, 180f);
                    p.feather = G.Slider("羽化", p.feather, 0.001f, 1f);
                    if (ReferenceEquals(EditingPart, p))
                        G.HelpBox("正在画面上调整：拖黄点移动，白点改大小，蓝点转角度。",
                                                GuiMsg.Info);
                    break;

                case MaskShape.ColorRange:
                    p.hueCenter = G.Slider("色相中心", p.hueCenter, 0f, 1f);
                    p.hueRange = G.Slider("色相范围", p.hueRange, 0f, 0.5f);
                    p.hueSoft = G.Slider("色相柔和", p.hueSoft, 0.001f, 0.3f);
                    MinMax("饱和度区间", ref p.satMin, ref p.satMax, 0f, 1f);
                    p.satSoft = G.Slider("饱和柔和", p.satSoft, 0.001f, 0.5f);
                    break;

                case MaskShape.LuminanceRange:
                    MinMax("亮度区间", ref p.lumMin, ref p.lumMax, 0f, 1f);
                    p.lumSoft = G.Slider("柔和", p.lumSoft, 0.001f, 0.5f);
                    break;

                case MaskShape.DepthRange:
                    MinMax("深度区间", ref p.depthMin, ref p.depthMax, 0f, 1f);
                    p.depthSoft = G.Slider("柔和", p.depthSoft, 0.001f, 0.5f);
                    if (!HasDepthMap)
                        G.HelpBox("还没有深度图，这个部件目前不选中任何东西。" +
                                                "去「AI 蒙版」里选 MiDaS 模型生成一张。", GuiMsg.Warning);
                    break;

                case MaskShape.Sky:
                    if (HasSky)
                        G.HelpBox($"天空占画面 {SkyCoverage * 100f:F0}%。" +
                                                "换图或者改了裁剪 / 旋转之后会自动重算。", GuiMsg.None);
                    else
                        G.HelpBox("这张图没找到天空，所以这个部件目前不选中任何东西。" +
                                                "检测是从画面顶边往下漫延的：天空不在顶边（比如从窗户往外拍）、" +
                                                "或者顶边全是树枝屋檐时找不到。那种情况用渐变或者画笔。",
                                                GuiMsg.Warning);
                    break;

                case MaskShape.Subject:
                    if (!HasSubjectMask)
                        G.HelpBox("还没有主体蒙版，这个部件目前不选中任何东西。" +
                                                "去「AI 蒙版」里用 IS-Net 生成一张。", GuiMsg.Warning);
                    break;

                case MaskShape.Brush:
                    if (p.brushId < 0)
                        G.HelpBox("这个笔刷没有分配到画布，删掉重新添加一次。", GuiMsg.Warning);
                    else if (ReferenceEquals(PaintingPart, p))
                    {
                        G.HelpBox("正在涂抹：在画面上拖拽即可，按住 Alt 擦除。", GuiMsg.Info);
                        DrawBrushOptions?.Invoke();
                    }
                    break;
            }

            p.opacity = G.Slider("不透明度", p.opacity, 0f, 1f);

            if (!G.EndChange()) return;
            _recordUndo?.Invoke("调整蒙版部件");
            _markChanged?.Invoke();
        }

        void DrawGroupAdjust(MaskGroup g)
        {
            G.MiniBoldLabel("这一组的调整");

            G.BeginChange();
            g.exposure = G.Slider("曝光", g.exposure, -2f, 2f);
            g.contrast = G.Slider("对比度", g.contrast, 0f, 2f);
            g.highlights = G.Slider("高光", g.highlights, -1f, 1f);
            g.shadows = G.Slider("阴影", g.shadows, -1f, 1f);
            g.saturation = G.Slider("饱和度", g.saturation, 0f, 2f);
            g.hueShift = G.Slider("色相", g.hueShift, -0.5f, 0.5f);
            g.tintHue = G.Slider("染色色相", g.tintHue, 0f, 1f);
            g.tintStrength = G.Slider("染色强度", g.tintStrength, 0f, 1f);
            if (!G.EndChange()) return;

            _recordUndo?.Invoke("调整蒙版参数");
            _markChanged?.Invoke();
        }

        void MinMax(string label, ref float lo, ref float hi, float limitMin, float limitMax)
        {
            GUILayout.BeginHorizontal();
            G.PrefixLabel(label);
            lo = G.FloatFieldW(lo, 46f);
            G.MinMaxSlider(ref lo, ref hi, limitMin, limitMax);
            hi = G.FloatFieldW(hi, 46f);
            GUILayout.EndHorizontal();
            // 上下限交叉的话区间隶属度恒为 0，界面上看起来就是"蒙版突然全没了"
            if (hi < lo + 0.005f) hi = lo + 0.005f;
        }

        /// <summary>这一刻能加的那些形状。菜单是跨帧的，所以名单要先抓一份定死。</summary>
        List<(MaskShape shape, string label, string tip)> UsableAdders()
        {
            var list = new List<(MaskShape, string, string)>();
            foreach (var a in Adders)
                if (Supported(a.shape)) list.Add((a.shape, a.label, a.tip));
            return list;
        }

        static string ShapeName(MaskShape s)
        {
            switch (s)
            {
                case MaskShape.Ellipse: return "径向";
                case MaskShape.Rect: return "矩形";
                case MaskShape.LinearGradient: return "渐变";
                case MaskShape.ColorRange: return "颜色范围";
                case MaskShape.LuminanceRange: return "亮度范围";
                case MaskShape.DepthRange: return "深度范围";
                case MaskShape.Subject: return "AI 主体";
                case MaskShape.Sky: return "天空";
                default: return "画笔";
            }
        }
    }
}
