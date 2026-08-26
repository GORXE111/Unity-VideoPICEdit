using System;
using System.Collections.Generic;
using Love.Tools;
using Love.Video;
using UnityEngine;

namespace Love.App
{
    /// <summary>
    /// <see cref="IGradeGui"/> 的独立程序实现，纯 IMGUI。
    ///
    /// 编辑器那份是 EditorGUILayout 的薄壳，这份得自己画。
    /// 大部分控件（滑条、开关、文本框）在 `GUILayout` 里本来就有对应的，
    /// 真正要自己造的只有下拉、色轮和分组标题。
    ///
    /// 曲线编辑没做：那是个完整的子窗口。这里只把曲线画出来，
    /// 并且靠 <see cref="CanEditCurves"/> 让上层把话说清楚，而不是给个动不了的控件。
    /// </summary>
    public partial class RuntimeGradeGui : IGradeGui
    {
        public float LabelWidth { get; set; } = 104f;

        readonly Stack<bool> _changed = new Stack<bool>();
        readonly Dictionary<int, bool> _popupOpen = new Dictionary<int, bool>();
        int _indent;
        Texture2D _white;

        readonly MaskListGUI _masks;
        public IMaskSectionGui Masks => _masks;

        public RuntimeGradeGui()
        {
            // 蒙版列表和编辑器那边是同一份源码，只是控件层换成了这一份
            _masks = new MaskListGUI(this);
        }

        Texture2D White
        {
            get
            {
                if (_white == null)
                {
                    _white = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                    { hideFlags = HideFlags.HideAndDontSave };
                    _white.SetPixel(0, 0, Color.white);
                    _white.Apply(false, false);
                }
                return _white;
            }
        }

        public void Dispose()
        {
            if (_white != null) { UnityEngine.Object.Destroy(_white); _white = null; }
        }

        // ---------------- 改动检测 ----------------

        // 和 EditorGUI.BeginChangeCheck 一样是可嵌套的：进来时把外层的
        // GUI.changed 存起来清零，出去时再或回去。不或回去的话，
        // 内层控件一动，外层就以为自己没变过
        public void BeginChange()
        {
            _changed.Push(GUI.changed);
            GUI.changed = false;
        }

        public bool EndChange()
        {
            bool mine = GUI.changed;
            GUI.changed = mine || (_changed.Count > 0 && _changed.Pop());
            return mine;
        }

        // ---------------- 排版 ----------------

        public void Space(float px) => GUILayout.Space(px);

        public void Label(string text)
        {
            GUILayout.BeginHorizontal();
            Ind();
            GUILayout.Label(text);
            GUILayout.EndHorizontal();
        }

        public void Label(string label, string value)
        {
            GUILayout.BeginHorizontal();
            Ind();
            GUILayout.Label(label, GUILayout.Width(LabelWidth));
            GUILayout.Label(value);
            GUILayout.EndHorizontal();
        }

        public void MiniBoldLabel(string text)
        {
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            Ind();
            GUILayout.Label(text, Bold);
            GUILayout.EndHorizontal();
        }

        public void MiniLabel(string text)
        {
            GUILayout.BeginHorizontal();
            Ind();
            GUILayout.Label(text, Mini);
            GUILayout.EndHorizontal();
        }

        public void HelpBox(string text, GuiMsg kind)
        {
            var c = kind == GuiMsg.Warning ? new Color(1f, 0.82f, 0.4f)
                  : kind == GuiMsg.Error ? new Color(1f, 0.5f, 0.45f)
                  : new Color(0.72f, 0.74f, 0.78f);

            var style = new GUIStyle(Mini) { wordWrap = true, normal = { textColor = c } };
            GUILayout.BeginHorizontal();
            Ind();
            GUILayout.Label(text, style);
            GUILayout.EndHorizontal();
        }

        public IDisposable Disabled(bool on) => new Scope(on);

        readonly struct Scope : IDisposable
        {
            readonly bool _prev;
            public Scope(bool off) { _prev = GUI.enabled; GUI.enabled = !off && _prev; }
            public void Dispose() => GUI.enabled = _prev;
        }

        public void Indent(int delta) => _indent = Mathf.Max(0, _indent + delta);

        void Ind() { if (_indent > 0) GUILayout.Space(_indent * 12f); }

        public Rect Row() => GUILayoutUtility.GetRect(0f, 18f, GUILayout.ExpandWidth(true));

        public void FillRect(Rect r, Color c)
        {
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, White);
            GUI.color = prev;
        }

        public bool Section(bool open, string title)
        {
            GUILayout.Space(3f);
            var r = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));

            // GetRect 在 Layout 事件返回的是占位矩形，那时候画色条位置是错的
            if (Event.current.type == EventType.Repaint)
                FillRect(new Rect(r.x, r.y + 2f, 2f, r.height - 4f),
                         open ? new Color(0.35f, 0.65f, 1f) : new Color(1f, 1f, 1f, 0.18f));

            var inner = new Rect(r.x + 7f, r.y, r.width - 7f, r.height);
            if (GUI.Button(inner, (open ? "▼  " : "▶  ") + title, Bold)) open = !open;
            return open;
        }

        // ---------------- 控件 ----------------

        public float Slider(string label, float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            Ind();
            GUILayout.Label(label, GUILayout.Width(LabelWidth));
            float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.MinWidth(50f));

            // 光有滑条不够：调色经常要「就要 +0.35」，滑条给不了这个精度
            string txt = GUILayout.TextField(v.ToString("0.###"), GUILayout.Width(48f));
            if (float.TryParse(txt, out float typed) && !Mathf.Approximately(typed, v))
            {
                v = Mathf.Clamp(typed, min, max);
                GUI.changed = true;
            }
            GUILayout.EndHorizontal();
            return v;
        }

        public float SliderIn(Rect r, string label, float value, float min, float max)
        {
            float lw = Mathf.Min(LabelWidth, r.width * 0.5f);
            GUI.Label(new Rect(r.x, r.y, lw, r.height), label);
            return GUI.HorizontalSlider(new Rect(r.x + lw + 4f, r.y + 3f,
                                                 Mathf.Max(20f, r.width - lw - 8f), r.height - 6f),
                                        value, min, max);
        }

        public bool Toggle(string label, bool value)
        {
            GUILayout.BeginHorizontal();
            Ind();
            GUILayout.Label(label, GUILayout.Width(LabelWidth));
            bool v = GUILayout.Toggle(value, GUIContent.none);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return v;
        }

        public bool ToggleLeft(string label, bool value)
        {
            GUILayout.BeginHorizontal();
            Ind();
            bool v = GUILayout.Toggle(value, " " + label);
            GUILayout.EndHorizontal();
            return v;
        }

        /// <summary>
        /// 下拉。运行时 IMGUI 没有现成的弹出菜单，所以点开之后就地展开成一列按钮。
        ///
        /// 用左右箭头逐个切也行，但选项多的时候要点很多次，而且看不见全部选项。
        /// </summary>
        public int Popup(string label, int value, string[] names)
        {
            if (names == null || names.Length == 0) return value;
            value = Mathf.Clamp(value, 0, names.Length - 1);

            int id = GUIUtility.GetControlID(FocusType.Passive);
            _popupOpen.TryGetValue(id, out bool open);

            GUILayout.BeginHorizontal();
            Ind();
            GUILayout.Label(label, GUILayout.Width(LabelWidth));
            if (GUILayout.Button(names[value] + "  ▾"))
            {
                open = !open;
                _popupOpen[id] = open;
            }
            GUILayout.EndHorizontal();

            if (!open) return value;

            int picked = value;
            for (int i = 0; i < names.Length; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(LabelWidth + 8f);
                if (GUILayout.Button((i == value ? "● " : "   ") + names[i], Mini))
                {
                    picked = i;
                    _popupOpen[id] = false;
                    GUI.changed = true;
                }
                GUILayout.EndHorizontal();
            }
            return picked;
        }

        public string TextField(string value) => GUILayout.TextField(value ?? "");

        public string SearchField(string value) => GUILayout.TextField(value ?? "");

        public void MinMaxSlider(ref float lo, ref float hi, float limitMin, float limitMax)
        {
            float a = GUILayout.HorizontalSlider(lo, limitMin, limitMax, GUILayout.MinWidth(30f));
            float b = GUILayout.HorizontalSlider(hi, limitMin, limitMax, GUILayout.MinWidth(30f));

            // 两根滑条会互相越过去，交叉之后区间是负的、下游一律出错
            if (a > b) { if (!Mathf.Approximately(a, lo)) b = a; else a = b; }
            lo = a; hi = b;
        }

        // ---------------- 色轮 ----------------

        public float TrackBallHeight(float width) => width;

        /// <summary>
        /// 简化的色轮：在圆盘里拖，角度是色相、半径是强度。
        ///
        /// 和编辑器那个不是同一份实现，但读写的是同一组 RGB 偏移，
        /// 所以两边调出来的结果是一致的。
        /// </summary>
        public bool TrackBall(Rect r, string label, ref float rr, ref float gg, ref float bb,
                              float range)
        {
            float size = Mathf.Min(r.width, r.height) - 14f;
            var disc = new Rect(r.center.x - size * 0.5f, r.y, size, size);

            if (Event.current.type == EventType.Repaint)
            {
                FillRect(disc, new Color(1f, 1f, 1f, 0.06f));
                GUI.Label(new Rect(r.x, disc.yMax, r.width, 14f), label, Mini);

                // 当前偏移画一个点，看得出偏到哪儿了
                var off = new Vector2(rr - (rr + gg + bb) / 3f, gg - (rr + gg + bb) / 3f);
                var p = disc.center + off / Mathf.Max(range, 1e-4f) * size * 0.5f;
                FillRect(new Rect(p.x - 2f, p.y - 2f, 4f, 4f), Color.white);
            }

            var e = Event.current;
            if (e.type != EventType.MouseDrag || !disc.Contains(e.mousePosition)) return false;

            Vector2 d = (e.mousePosition - disc.center) / (size * 0.5f);
            if (d.magnitude > 1f) d = d.normalized;

            float hue = Mathf.Repeat(Mathf.Atan2(-d.y, d.x) / (Mathf.PI * 2f), 1f);
            Color c = Color.HSVToRGB(hue, 1f, 1f);
            float amt = d.magnitude * range;

            // 三通道和为 0：只推色偏、不动亮度，和达芬奇的色轮一致
            float mean = (c.r + c.g + c.b) / 3f;
            rr = (c.r - mean) * amt;
            gg = (c.g - mean) * amt;
            bb = (c.b - mean) * amt;

            e.Use();
            GUI.changed = true;
            return true;
        }

        public void HueSwatch(Rect r, float hue, float strength, Action<float, float> write)
        {
            if (Event.current.type == EventType.Repaint)
                FillRect(r, Color.HSVToRGB(Mathf.Repeat(hue, 1f), 0.85f, 0.95f));

            var e = Event.current;
            if ((e.type == EventType.MouseDrag || e.type == EventType.MouseDown) && r.Contains(e.mousePosition))
            {
                write(Mathf.Clamp01((e.mousePosition.x - r.x) / Mathf.Max(r.width, 1f)), strength);
                e.Use();
                GUI.changed = true;
            }
        }

        public void HueOnlySwatch(Rect r, float hue, Action<float> write)
        {
            HueSwatch(r, hue, 1f, (h, _) => write(h));
        }

        // ---------------- 其它 ----------------

        public void PolyLine(float width, Color color, Vector3[] points)
        {
            if (points == null || points.Length < 2) return;
            if (Event.current.type != EventType.Repaint) return;

            for (int i = 1; i < points.Length; i++)
                Line(points[i - 1], points[i], color, width);
        }

        /// <summary>
        /// 画一段线。
        ///
        /// 没走 GL：那要一个材质，而内置的 Hidden/Internal-Colored 出包时可能被剔掉，
        /// 表现是线在编辑器里有、出包就没了。旋转一个 1px 高的贴图更笨但是稳。
        /// </summary>
        void Line(Vector2 a, Vector2 b, Color c, float w)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) return;

            var prevColor = GUI.color;
            var prevMatrix = GUI.matrix;

            GUI.color = c;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            GUI.DrawTexture(new Rect(a.x, a.y - w * 0.5f, len, w), White);

            GUI.matrix = prevMatrix;
            GUI.color = prevColor;
        }

        /// <summary>独立程序里没有编辑器那种模态对话框，直接当作确认。</summary>
        public bool Confirm(string title, string message, string ok, string cancel) => true;

        public void AssetsChanged() { }

        /// <summary>运行时没有撤销栈。参数改动由上层自己存快照。</summary>
        public void RecordUndo(string action) { }

        // ---------------- 样式 ----------------

        GUIStyle _bold, _mini;
        GUIStyle Bold => _bold ?? (_bold = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });
        GUIStyle Mini => _mini ?? (_mini = new GUIStyle(GUI.skin.label) { fontSize = 10, wordWrap = true });

    }
}
