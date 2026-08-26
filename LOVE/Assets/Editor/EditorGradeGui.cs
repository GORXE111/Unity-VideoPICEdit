using System;
using Love.Tools;
using UnityEditor;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// <see cref="IGradeGui"/> 的编辑器实现。
    ///
    /// **这一层必须是薄壳。** 每个方法就是原来那句 EditorGUILayout 调用，
    /// 一行不多。抽接口的目的是换实现，不是趁机改行为——
    /// 这里但凡多做一点，两个窗口的手感就和以前不一样了，而那种差别很难查。
    /// </summary>
    public class EditorGradeGui : IGradeGui
    {
        readonly MaskListGUI _masks = new MaskListGUI();

        /// <summary>撤销记到哪个对象上。窗口每帧设一次。</summary>
        public UnityEngine.Object UndoTarget { get; set; }

        public IMaskSectionGui Masks => _masks;

        // ---------------- 改动检测 ----------------

        public void BeginChange() => EditorGUI.BeginChangeCheck();
        public bool EndChange() => EditorGUI.EndChangeCheck();

        // ---------------- 排版 ----------------

        public void Space(float px) => EditorGUILayout.Space(px);
        public void Label(string text) => EditorGUILayout.LabelField(text);
        public void Label(string label, string value) => EditorGUILayout.LabelField(label, value);

        public void MiniBoldLabel(string text) =>
            EditorGUILayout.LabelField(text, EditorStyles.miniBoldLabel);

        public void MiniLabel(string text) =>
            EditorGUILayout.LabelField(text, EditorStyles.miniLabel);

        public void HelpBox(string text, GuiMsg kind) =>
            EditorGUILayout.HelpBox(text, Convert(kind));

        static MessageType Convert(GuiMsg k)
        {
            switch (k)
            {
                case GuiMsg.Info: return MessageType.Info;
                case GuiMsg.Warning: return MessageType.Warning;
                case GuiMsg.Error: return MessageType.Error;
                default: return MessageType.None;
            }
        }

        public IDisposable Disabled(bool on) => new EditorGUI.DisabledScope(on);

        public void Indent(int delta) => EditorGUI.indentLevel += delta;

        public Rect Row() => EditorGUILayout.GetControlRect();

        public float LabelWidth => EditorGUIUtility.labelWidth;

        public void FillRect(Rect r, Color c) => EditorGUI.DrawRect(r, c);

        public bool Section(bool open, string title)
        {
            EditorGUILayout.Space(3f);

            var r = GUILayoutUtility.GetRect(GUIContent.none, GradeSkin.SectionHeader,
                                             GUILayout.ExpandWidth(true));

            // GetRect 在 Layout 事件返回的是占位矩形，那时候画色条位置是错的
            if (Event.current.type == EventType.Repaint)
            {
                // 展开的组左边给一道强调色，一眼看出当前摊开了哪几组
                GradeSkin.Line(r.x, r.y + 2f, 2f, r.height - 4f,
                               open ? GradeSkin.Accent : GradeSkin.Grip);
            }

            var inner = new Rect(r.x + 7f, r.y, r.width - 7f, r.height);
            return EditorGUI.Foldout(inner, open, title, true, GradeSkin.SectionHeader);
        }

        // ---------------- 控件 ----------------

        public float Slider(string label, float value, float min, float max) =>
            EditorGUILayout.Slider(label, value, min, max);

        public float SliderIn(Rect r, string label, float value, float min, float max) =>
            EditorGUI.Slider(r, label, value, min, max);

        public bool Toggle(string label, bool value) => EditorGUILayout.Toggle(label, value);

        public bool ToggleLeft(string label, bool value) =>
            EditorGUILayout.ToggleLeft(label, value);

        public int Popup(string label, int value, string[] names) =>
            EditorGUILayout.Popup(label, value, names);

        public string TextField(string value) => EditorGUILayout.TextField(value);

        public string SearchField(string value) =>
            EditorGUILayout.TextField(value, EditorStyles.toolbarSearchField);

        public void MinMaxSlider(ref float lo, ref float hi, float limitMin, float limitMax) =>
            EditorGUILayout.MinMaxSlider(ref lo, ref hi, limitMin, limitMax);

        public AnimationCurve Curve(string label, AnimationCurve curve, Color color, Rect range) =>
            EditorGUILayout.CurveField(label, curve, color, range);

        public bool CanEditCurves => true;

        // ---------------- 色轮 ----------------

        public float TrackBallHeight(float width) => ColorWheelGUI.TrackBallHeight(width);

        public bool TrackBall(Rect r, string label, ref float rr, ref float gg, ref float bb,
                              float range) =>
            ColorWheelGUI.TrackBall(r, label, ref rr, ref gg, ref bb, range);

        public void HueSwatch(Rect r, float hue, float strength, Action<float, float> write) =>
            ColorWheelGUI.HueSwatch(r, hue, strength, write);

        public void HueOnlySwatch(Rect r, float hue, Action<float> write) =>
            ColorWheelGUI.HueOnlySwatch(r, hue, write);

        // ---------------- 其它 ----------------

        public void PolyLine(float width, Color color, Vector3[] points)
        {
            Color prev = Handles.color;
            Handles.color = color;
            Handles.DrawAAPolyLine(width, points);
            Handles.color = prev;
        }

        public bool Confirm(string title, string message, string ok, string cancel) =>
            !Application.isBatchMode && EditorUtility.DisplayDialog(title, message, ok, cancel);

        public void AssetsChanged() => AssetDatabase.Refresh();

        public void RecordUndo(string action)
        {
            if (UndoTarget == null) return;
            Undo.RecordObject(UndoTarget, action);
            if (!EditorApplication.isPlaying) EditorUtility.SetDirty(UndoTarget);
        }
    }
}
