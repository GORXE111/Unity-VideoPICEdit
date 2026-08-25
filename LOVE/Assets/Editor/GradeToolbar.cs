using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// 会自己收纳的工具栏。
    ///
    /// 起因是一个实打实的 bug：修图台工具栏的固定宽度加起来 934px，而窗口最小宽度
    /// 才 900px——也就是说窗口一旦不够宽，右边的按钮就被裁掉，用户根本点不到。
    /// 用 GUILayout 的话这个问题是隐形的：它不会报错，只是默默把东西挤没。
    ///
    /// 这里改成先声明、后测量、再绘制：宽度不够时按优先级从低到高把按钮
    /// 撤进一个「⋯」菜单，功能一个都不丢。
    ///
    /// 用法（每帧重新声明一遍，和 IMGUI 的即时模式一致）：
    /// <code>
    /// _tb.Begin(rect);
    /// _tb.Button("打开…", 78f, OpenFile, priority: 100);
    /// _tb.Toggle(_bypass, "原图对比", 64f, v => { _bypass = v; _dirty = true; }, priority: 90);
    /// _tb.Flex();
    /// _tb.Label(() => title, 200f, priority: 10);
    /// _tb.End();
    /// </code>
    /// </summary>
    public class GradeToolbar
    {
        enum Kind { Button, Toggle, Popup, Slider, Label, Space, Flex }

        struct Item
        {
            public Kind kind;
            public string label, tooltip;
            public float width;
            public int priority;
            public bool on, disabled;

            public Action click;
            public Action<bool> toggle;

            public string[] options;
            public int index;
            public Action<int> pick;

            public float value, min, max;
            public Action<float> setValue;

            public Func<string> text;

            /// <summary>放不下时能不能退进「⋯」菜单。滑条和下拉退不进去，只能整个不画。</summary>
            public bool menuable;
        }

        readonly List<Item> _items = new List<Item>();
        Rect _rect;

        const float OverflowW = 26f;

        public void Begin(Rect r)
        {
            _rect = r;
            _items.Clear();
        }

        public void Button(string label, float width, Action onClick,
                           int priority = 50, bool disabled = false, string tooltip = null)
            => _items.Add(new Item
            {
                kind = Kind.Button, label = label, width = width, priority = priority,
                click = onClick, disabled = disabled, tooltip = tooltip, menuable = true,
            });

        public void Toggle(bool value, string label, float width, Action<bool> onChange,
                           int priority = 50, bool disabled = false, string tooltip = null)
            => _items.Add(new Item
            {
                kind = Kind.Toggle, label = label, width = width, priority = priority,
                on = value, toggle = onChange, disabled = disabled, tooltip = tooltip, menuable = true,
            });

        /// <summary><paramref name="label"/> 只在被撤进「⋯」菜单时用作子菜单名。</summary>
        public void Popup(int index, string[] options, float width, Action<int> onPick,
                          int priority = 50, bool disabled = false, string label = null)
            => _items.Add(new Item
            {
                kind = Kind.Popup, options = options, index = index, width = width,
                priority = priority, pick = onPick, disabled = disabled, menuable = true,
                label = label,
            });

        public void Slider(float value, float min, float max, float width, Action<float> onChange,
                           int priority = 30, bool disabled = false)
            => _items.Add(new Item
            {
                kind = Kind.Slider, value = value, min = min, max = max, width = width,
                priority = priority, setValue = onChange, disabled = disabled, menuable = false,
            });

        /// <summary>状态文字。用 Func 而不是 string，是为了让被撤下时不必白算一次字符串。</summary>
        public void Label(Func<string> text, float width, int priority = 10)
            => _items.Add(new Item
            {
                kind = Kind.Label, text = text, width = width, priority = priority, menuable = false,
            });

        public void Space(float w = 8f)
            => _items.Add(new Item { kind = Kind.Space, width = w, priority = int.MaxValue, menuable = false });

        public void Flex()
            => _items.Add(new Item { kind = Kind.Flex, width = 0f, priority = int.MaxValue, menuable = false });

        public void End()
        {
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(_rect, GradeSkin.Bar);

            // ---- 先量：撤掉哪些才放得下 ----
            var dropped = new List<int>();
            float fixedW = 0f;
            int flexCount = 0;

            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].kind == Kind.Flex) flexCount++;
                else fixedW += _items[i].width;
            }

            float avail = _rect.width - 4f;
            if (fixedW > avail)
            {
                // 留出「⋯」的位置。注意它自己也占宽，所以目标要再减一次
                float target = avail - OverflowW;

                // 按优先级从低到高撤。同优先级时后声明的先撤——
                // 工具栏习惯上越靠右越次要
                var order = new List<int>();
                for (int i = 0; i < _items.Count; i++)
                    if (_items[i].kind != Kind.Flex && _items[i].kind != Kind.Space) order.Add(i);
                order.Sort((a, b) =>
                {
                    int c = _items[a].priority.CompareTo(_items[b].priority);
                    return c != 0 ? c : b.CompareTo(a);
                });

                foreach (int i in order)
                {
                    if (fixedW <= target) break;
                    dropped.Add(i);
                    fixedW -= _items[i].width;
                }
            }

            // ---- 再画 ----
            float flexW = flexCount > 0 ? Mathf.Max(0f, (avail - fixedW - (dropped.Count > 0 ? OverflowW : 0f)) / flexCount) : 0f;
            float x = _rect.x + 2f;
            float y = _rect.y;
            float h = _rect.height;

            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];

                if (it.kind == Kind.Flex) { x += flexW; continue; }
                if (dropped.Contains(i)) continue;
                if (it.kind == Kind.Space) { x += it.width; continue; }

                var r = new Rect(x, y, it.width, h);
                x += it.width;

                using (new EditorGUI.DisabledScope(it.disabled))
                    DrawOne(it, r);
            }

            if (dropped.Count > 0) DrawOverflow(new Rect(_rect.xMax - OverflowW - 2f, y, OverflowW, h), dropped);
        }

        void DrawOne(Item it, Rect r)
        {
            switch (it.kind)
            {
                case Kind.Button:
                    if (GUI.Button(r, new GUIContent(it.label, it.tooltip), EditorStyles.toolbarButton))
                        it.click?.Invoke();
                    break;

                case Kind.Toggle:
                    bool v = GUI.Toggle(r, it.on, new GUIContent(it.label, it.tooltip), EditorStyles.toolbarButton);
                    if (v != it.on) it.toggle?.Invoke(v);
                    break;

                case Kind.Popup:
                    int pick = EditorGUI.Popup(r, it.index, it.options, EditorStyles.toolbarPopup);
                    if (pick != it.index) it.pick?.Invoke(pick);
                    break;

                case Kind.Slider:
                    // 竖直方向居中一点，否则滑条贴着工具栏顶边
                    var sr = new Rect(r.x, r.y + (r.height - 16f) * 0.5f, r.width, 16f);
                    float nv = GUI.HorizontalSlider(sr, it.value, it.min, it.max);
                    if (!Mathf.Approximately(nv, it.value)) it.setValue?.Invoke(nv);
                    break;

                case Kind.Label:
                    GUI.Label(r, it.text != null ? it.text() : "", EditorStyles.miniLabel);
                    break;
            }
        }

        void DrawOverflow(Rect r, List<int> dropped)
        {
            if (!GUI.Button(r, new GUIContent("⋯", "放不下的按钮都在这里"), EditorStyles.toolbarButton)) return;

            var menu = new GenericMenu();
            bool any = false;

            // 按原来的声明顺序进菜单，位置感和工具栏一致
            dropped.Sort();
            foreach (int i in dropped)
            {
                var it = _items[i];
                if (!it.menuable) continue;
                any = true;

                string label = string.IsNullOrEmpty(it.label) ? "(未命名)" : it.label;
                if (it.disabled) { menu.AddDisabledItem(new GUIContent(label)); continue; }

                if (it.kind == Kind.Button)
                {
                    var act = it.click;
                    menu.AddItem(new GUIContent(label), false, () => act?.Invoke());
                }
                else if (it.kind == Kind.Toggle)
                {
                    var tog = it.toggle;
                    bool on = it.on;
                    menu.AddItem(new GUIContent(label), on, () => tog?.Invoke(!on));
                }
                else if (it.kind == Kind.Popup && it.options != null)
                {
                    // 下拉框做成子菜单。不做的话窄窗口下这些选项就彻底够不着了——
                    // 比如视频台的解码器切换
                    string head = string.IsNullOrEmpty(it.label) ? "选项" : it.label;
                    var pk = it.pick;
                    for (int k = 0; k < it.options.Length; k++)
                    {
                        int captured = k;
                        menu.AddItem(new GUIContent(head + "/" + it.options[k]),
                                     k == it.index, () => pk?.Invoke(captured));
                    }
                }
            }

            if (!any) menu.AddDisabledItem(new GUIContent("窗口太窄，部分控件已隐藏"));
            menu.DropDown(r);
        }
    }
}
