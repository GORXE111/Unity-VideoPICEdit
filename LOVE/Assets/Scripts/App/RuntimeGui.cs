using UnityEngine;

namespace Love.App
{
    /// <summary>
    /// 独立程序里的参数控件。
    ///
    /// **IMGUI 本身在出包之后是能用的**——`GUI` / `GUILayout` / `Event` / `GUIStyle`
    /// 都在 UnityEngine 里。不能用的只有 `EditorGUILayout` / `EditorStyles` / `EditorWindow`。
    /// 所以编辑器那 973 行参数界面的**结构**是能沿用的，要换的只是控件这一层。
    ///
    /// 这里按编辑器侧 <c>GradeSettingsGUI</c> 里那几个包装（Slider / Toggle / MinMax /
    /// Group）一一对应地实现，签名也对齐，将来抽公共接口时两边能直接对上。
    /// </summary>
    public class RuntimeGui
    {
        public float LabelWidth = 96f;
        public float RowHeight = 18f;

        /// <summary>这一帧有没有人动过控件。</summary>
        public bool Changed { get; private set; }

        public void BeginFrame() => Changed = false;

        // ---- 皮肤 ----
        GUIStyle _label, _mini, _header, _box, _btn;
        Texture2D _px;

        Texture2D Px(Color c)
        {
            if (_px == null)
            {
                _px = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                { hideFlags = HideFlags.HideAndDontSave };
            }
            _px.SetPixel(0, 0, c);
            _px.Apply(false, false);
            return _px;
        }

        public void EnsureSkin()
        {
            if (_label != null) return;

            _label = new GUIStyle(GUI.skin.label)
            { alignment = TextAnchor.MiddleLeft, wordWrap = false };
            _mini = new GUIStyle(_label) { fontSize = 10 };
            _header = new GUIStyle(_label) { fontStyle = FontStyle.Bold };
            _box = new GUIStyle(GUI.skin.box);
            _btn = new GUIStyle(GUI.skin.button) { fontSize = 11 };
        }

        public GUIStyle Label { get { EnsureSkin(); return _label; } }
        public GUIStyle Mini { get { EnsureSkin(); return _mini; } }
        public GUIStyle Header { get { EnsureSkin(); return _header; } }
        public GUIStyle Button { get { EnsureSkin(); return _btn; } }

        // ---------------- 控件 ----------------

        public void Section(string title)
        {
            GUILayout.Space(8f);
            GUILayout.Label(title, Header);
        }

        /// <summary>
        /// 一行滑条：左边标签、中间滑条、右边数值。
        ///
        /// 数值单独显示是必需的：滑条只能给个大概，而调色经常要「就要 +0.35」。
        /// </summary>
        public void Slider(string label, ref float value, float min, float max, string fmt = "0.00")
        {
            EnsureSkin();
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _label, GUILayout.Width(LabelWidth));

            float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.MinWidth(60f));

            // 数值框：改完按回车生效。每敲一个字符就生效的话，
            // 打「-0.5」在只输入「-」的那一刻会被解析成 0，滑条当场跳回去
            string text = GUILayout.TextField(value.ToString(fmt), _label, GUILayout.Width(52f));
            if (float.TryParse(text, out float typed) && !Mathf.Approximately(typed, value))
                v = Mathf.Clamp(typed, min, max);

            GUILayout.EndHorizontal();

            if (!Mathf.Approximately(v, value)) { value = v; Changed = true; }
        }

        public void Toggle(string label, ref bool value)
        {
            EnsureSkin();
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _label, GUILayout.Width(LabelWidth));
            bool v = GUILayout.Toggle(value, GUIContent.none);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (v != value) { value = v; Changed = true; }
        }

        public void MinMax(string label, ref float lo, ref float hi, float limitMin, float limitMax)
        {
            EnsureSkin();
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _label, GUILayout.Width(LabelWidth));
            float a = GUILayout.HorizontalSlider(lo, limitMin, limitMax, GUILayout.MinWidth(40f));
            float b = GUILayout.HorizontalSlider(hi, limitMin, limitMax, GUILayout.MinWidth(40f));
            GUILayout.EndHorizontal();

            // 两根滑条会互相越过去，交叉之后区间是负的、下游一律出错
            if (a > b) { if (!Mathf.Approximately(a, lo)) b = a; else a = b; }

            if (!Mathf.Approximately(a, lo) || !Mathf.Approximately(b, hi))
            {
                lo = a; hi = b; Changed = true;
            }
        }

        /// <summary>可折叠的一组。返回是不是展开着。</summary>
        public bool Group(ref bool open, string title)
        {
            EnsureSkin();
            if (GUILayout.Button((open ? "▼  " : "▶  ") + title, _header, GUILayout.Height(20f)))
                open = !open;
            return open;
        }

        public bool Btn(string label, float width = 0f)
        {
            EnsureSkin();
            return width > 0f
                ? GUILayout.Button(label, _btn, GUILayout.Width(width))
                : GUILayout.Button(label, _btn);
        }

        /// <summary>带标签的下拉。IMGUI 没有现成的弹出菜单，点开就地展开成一列。</summary>
        public int Popup2(string label, int value, string[] names)
        {
            if (names == null || names.Length == 0) return value;
            value = Mathf.Clamp(value, 0, names.Length - 1);

            int id = GUIUtility.GetControlID(FocusType.Passive);
            _open.TryGetValue(id, out bool open);

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, Label, GUILayout.Width(LabelWidth));
            if (GUILayout.Button(names[value] + "  ▾", Button)) { open = !open; _open[id] = open; }
            GUILayout.EndHorizontal();

            if (!open) return value;

            for (int i = 0; i < names.Length; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(LabelWidth + 6f);
                if (GUILayout.Button((i == value ? "● " : "   ") + names[i], Mini))
                {
                    _open[id] = false;
                    Changed = true;
                    GUILayout.EndHorizontal();
                    return i;
                }
                GUILayout.EndHorizontal();
            }
            return value;
        }

        readonly System.Collections.Generic.Dictionary<int, bool> _open =
            new System.Collections.Generic.Dictionary<int, bool>();

        /// <summary>返回新值的开关。有些地方拿 ref 不方便。</summary>
        public bool Toggle2(string label, bool value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, Label, GUILayout.Width(LabelWidth));
            bool v = GUILayout.Toggle(value, GUIContent.none);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (v != value) Changed = true;
            return v;
        }

        public void Info(string text)
        {
            EnsureSkin();
            GUILayout.Label(text, _mini);
        }

        public void Dispose()
        {
            if (_px != null) { Object.Destroy(_px); _px = null; }
        }
    }
}
