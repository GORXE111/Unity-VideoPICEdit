using System.Collections.Generic;
using UnityEngine;

namespace Love.App
{
    /// <summary>
    /// 运行时控件层的第二半：蒙版列表要的那批带尺寸的控件，加上曲线编辑器。
    ///
    /// 拆成 partial 是因为主文件已经管够了参数栏那批，
    /// 再把曲线编辑那两百行堆进去就没法读了。
    /// </summary>
    public partial class RuntimeGradeGui
    {
        /// <summary>普通标签的样式。注意 Label 在这个类里是接口方法，不是样式。</summary>
        GUIStyle _plain;
        GUIStyle Plain => _plain ?? (_plain = new GUIStyle(GUI.skin.label));

        public Rect Row(float height) =>
            GUILayoutUtility.GetRect(0f, height, GUILayout.ExpandWidth(true));

        public void PrefixLabel(string text)
        {
            GUILayout.Label(text, Plain, GUILayout.Width(LabelWidth));
        }

        public void MiniBoldLabelW(string text, float width) =>
            GUILayout.Label(text, Bold, GUILayout.Width(width));

        public void MiniLabelW(string text, float width) =>
            GUILayout.Label(text, Mini, GUILayout.Width(width));

        public bool MiniButton(string label, float width) =>
            width > 0f ? GUILayout.Button(label, Mini, GUILayout.Width(width))
                       : GUILayout.Button(label, Mini);

        public bool MiniButtonIn(Rect r, string label, string tooltip) =>
            GUI.Button(r, new GUIContent(label, tooltip), Mini);

        public bool MiniToggle(bool value, string label, float width) =>
            GUILayout.Toggle(value, label, Mini, GUILayout.Width(width));

        public bool MiniToggleIn(Rect r, bool value, string label, string tooltip) =>
            GUI.Toggle(r, value, new GUIContent(label, tooltip), Mini);

        public bool ToggleIn(Rect r, bool value) => GUI.Toggle(r, value, GUIContent.none);

        public string TextFieldIn(Rect r, string value) => GUI.TextField(r, value ?? "");

        public int PopupW(int value, string[] names, float width)
        {
            if (names == null || names.Length == 0) return value;
            value = Mathf.Clamp(value, 0, names.Length - 1);

            // 窄的地方（比如「加/减/交」）用点一下换下一个更省地方，
            // 展开成一列会把那一行撑爆
            if (GUILayout.Button(names[value], Mini, GUILayout.Width(width)))
            {
                GUI.changed = true;
                return (value + 1) % names.Length;
            }
            return value;
        }

        public float FloatFieldW(float value, float width)
        {
            string t = GUILayout.TextField(value.ToString("0.###"), Plain, GUILayout.Width(width));
            return float.TryParse(t, out float v) ? v : value;
        }

        public Vector2 Vector2Field(string label, Vector2 v)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, Plain, GUILayout.Width(LabelWidth));
            float x = FloatFieldW(v.x, 52f);
            float y = FloatFieldW(v.y, 52f);
            GUILayout.EndHorizontal();
            return new Vector2(x, y);
        }

        // ================= 弹出菜单 =================

        /// <summary>
        /// 运行时没有 GenericMenu，就地展开成一列按钮。
        ///
        /// **菜单是跨帧的**：这一帧只记下"要展开"，下一帧才画。
        /// 点中之后回调在调用方早已返回之后才触发，所以名单必须先抓一份定死，
        /// 不能在回调里再去遍历那会儿的状态。
        /// </summary>
        public void ContextMenu(string[] labels, System.Action<int> pick)
        {
            if (labels == null || labels.Length == 0 || pick == null) return;
            _menuLabels = labels;
            _menuPick = pick;
            _menuAt = Event.current != null ? Event.current.mousePosition : Vector2.zero;
        }

        string[] _menuLabels;
        System.Action<int> _menuPick;
        Vector2 _menuAt;

        /// <summary>菜单要画在所有东西之上，所以由上层每帧最后调一次。</summary>
        public void DrawPendingMenu()
        {
            if (_menuLabels == null) return;

            const float W = 210f, H = 22f;
            var r = new Rect(_menuAt.x, _menuAt.y, W, _menuLabels.Length * H + 6f);
            r.x = Mathf.Min(r.x, Screen.width - W - 4f);
            r.y = Mathf.Min(r.y, Screen.height - r.height - 4f);

            FillRect(r, new Color(0.13f, 0.13f, 0.15f, 0.98f));
            FillRect(new Rect(r.x, r.y, r.width, 1f), new Color(1f, 1f, 1f, 0.22f));

            for (int i = 0; i < _menuLabels.Length; i++)
            {
                // 标签里 "|" 后面那半是提示，菜单窄，只显示前半
                string t = _menuLabels[i] ?? "";
                int bar = t.IndexOf('|');
                string head = bar >= 0 ? t.Substring(0, bar) : t;
                string tip = bar >= 0 ? t.Substring(bar + 1) : null;

                var row = new Rect(r.x + 3f, r.y + 3f + i * H, r.width - 6f, H - 2f);
                if (GUI.Button(row, new GUIContent(head, tip), Mini))
                {
                    var cb = _menuPick;
                    int idx = i;
                    _menuLabels = null;
                    _menuPick = null;
                    cb(idx);
                    GUI.changed = true;
                    return;
                }
            }

            // 点到菜单外面就收起来
            var e = Event.current;
            if (e != null && e.type == EventType.MouseDown && !r.Contains(e.mousePosition))
            {
                _menuLabels = null;
                _menuPick = null;
                e.Use();
            }
        }

        // ================= 曲线编辑 =================

        public bool CanEditCurves => true;

        /// <summary>每条曲线各自记着选中了哪个点。</summary>
        readonly Dictionary<int, int> _curvePick = new Dictionary<int, int>();

        const float CurveH = 96f;
        const float KeyR = 4f;

        /// <summary>
        /// 曲线编辑器。
        ///
        /// 拖点改值，双击空白加点，双击点删点。切线一律自动平滑——
        /// 手调切线要再来一套手柄，而调色曲线上几乎没人用得着。
        ///
        /// **首尾两个点只能上下拖，不能左右拖。** 曲线的定义域是整个 0~1，
        /// 端点一旦挪进来，外面那段就没有定义、Evaluate 会平推出去，
        /// 表现是高光或者暗部整片糊死，而且很难看出是曲线的锅。
        /// </summary>
        public AnimationCurve Curve(string label, AnimationCurve curve, Color color, Rect range)
        {
            if (curve == null) curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

            GUILayout.BeginHorizontal();
            Ind();
            GUILayout.Label(label, Plain, GUILayout.Width(LabelWidth));
            GUILayout.EndHorizontal();

            var r = GUILayoutUtility.GetRect(0f, CurveH, GUILayout.ExpandWidth(true));
            r = new Rect(r.x + 4f, r.y, r.width - 8f, r.height);

            int id = GUIUtility.GetControlID(FocusType.Passive);
            _curvePick.TryGetValue(id, out int pick);

            curve = HandleCurveInput(id, r, curve, range, ref pick);
            _curvePick[id] = pick;

            if (Event.current.type == EventType.Repaint) DrawCurve(r, curve, color, range, pick);
            return curve;
        }

        Vector2 CurveToScreen(Rect r, Rect range, float t, float v)
        {
            float nx = (t - range.x) / Mathf.Max(range.width, 1e-4f);
            float ny = (v - range.y) / Mathf.Max(range.height, 1e-4f);
            return new Vector2(r.x + nx * r.width, r.yMax - ny * r.height);
        }

        Vector2 ScreenToCurve(Rect r, Rect range, Vector2 p)
        {
            float nx = Mathf.Clamp01((p.x - r.x) / Mathf.Max(r.width, 1f));
            float ny = Mathf.Clamp01((r.yMax - p.y) / Mathf.Max(r.height, 1f));
            return new Vector2(range.x + nx * range.width, range.y + ny * range.height);
        }

        AnimationCurve HandleCurveInput(int id, Rect r, AnimationCurve curve, Rect range, ref int pick)
        {
            var e = Event.current;
            if (e == null) return curve;

            bool inside = r.Contains(e.mousePosition);

            if (e.type == EventType.MouseDown && e.button == 0 && inside)
            {
                int hit = NearestKey(r, curve, range, e.mousePosition, KeyR * 3f);

                if (e.clickCount >= 2)
                {
                    if (hit >= 0 && curve.length > 2 && hit != 0 && hit != curve.length - 1)
                    {
                        // 首尾不能删：删了定义域就缺一块
                        curve.RemoveKey(hit);
                        pick = -1;
                    }
                    else if (hit < 0)
                    {
                        var c = ScreenToCurve(r, range, e.mousePosition);
                        curve.AddKey(c.x, c.y);
                        Smooth(curve);
                        pick = NearestKey(r, curve, range, e.mousePosition, float.MaxValue);
                    }
                    GUI.changed = true;
                    e.Use();
                    return curve;
                }

                pick = hit;
                if (hit >= 0) { GUIUtility.hotControl = id; e.Use(); }
            }
            else if (e.type == EventType.MouseDrag && GUIUtility.hotControl == id && pick >= 0)
            {
                var c = ScreenToCurve(r, range, e.mousePosition);
                var keys = curve.keys;

                // 首尾只能上下动，否则曲线两端会缺定义
                float t = (pick == 0 || pick == keys.Length - 1) ? keys[pick].time : c.x;

                // 中间的点不能越过邻居，越过之后 AnimationCurve 会自己重排，
                // 手上拖的那个点会突然变成另一个，手感像是"点飞了"
                if (pick > 0) t = Mathf.Max(t, keys[pick - 1].time + 1e-3f);
                if (pick < keys.Length - 1) t = Mathf.Min(t, keys[pick + 1].time - 1e-3f);

                keys[pick].time = t;
                keys[pick].value = c.y;
                curve.keys = keys;
                Smooth(curve);

                GUI.changed = true;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && GUIUtility.hotControl == id)
            {
                GUIUtility.hotControl = 0;
                e.Use();
            }

            return curve;
        }

        static void Smooth(AnimationCurve c)
        {
            for (int i = 0; i < c.length; i++) c.SmoothTangents(i, 0f);
        }

        int NearestKey(Rect r, AnimationCurve curve, Rect range, Vector2 mouse, float maxDist)
        {
            int best = -1;
            float bestD = maxDist;
            for (int i = 0; i < curve.length; i++)
            {
                var k = curve.keys[i];
                float d = Vector2.Distance(CurveToScreen(r, range, k.time, k.value), mouse);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        void DrawCurve(Rect r, AnimationCurve curve, Color color, Rect range, int pick)
        {
            FillRect(r, new Color(0f, 0f, 0f, 0.35f));

            // 四分格。调色曲线全靠"中间那点抬了多少"判断，没有格子就只能凭感觉
            var grid = new Color(1f, 1f, 1f, 0.09f);
            for (int i = 1; i < 4; i++)
            {
                float f = i / 4f;
                FillRect(new Rect(r.x + f * r.width, r.y, 1f, r.height), grid);
                FillRect(new Rect(r.x, r.y + f * r.height, r.width, 1f), grid);
            }

            // 恒等线，看得出这一段是抬了还是压了
            Line(new Vector2(r.x, r.yMax), new Vector2(r.xMax, r.y),
                 new Color(1f, 1f, 1f, 0.18f), 1f);

            const int Seg = 64;
            Vector2 prev = Vector2.zero;
            for (int i = 0; i <= Seg; i++)
            {
                float t = range.x + (i / (float)Seg) * range.width;
                var p = CurveToScreen(r, range, t, curve.Evaluate(t));
                p.y = Mathf.Clamp(p.y, r.y, r.yMax);
                if (i > 0) Line(prev, p, color, 2f);
                prev = p;
            }

            for (int i = 0; i < curve.length; i++)
            {
                var k = curve.keys[i];
                var p = CurveToScreen(r, range, k.time, k.value);
                float s = i == pick ? KeyR + 2f : KeyR;
                FillRect(new Rect(p.x - s, p.y - s, s * 2f, s * 2f),
                         i == pick ? Color.white : color);
            }

            GUI.Label(new Rect(r.x + 4f, r.yMax - 15f, r.width - 8f, 14f),
                      "拖点改值　双击空白加点　双击点删点", Mini);
        }
    }
}
