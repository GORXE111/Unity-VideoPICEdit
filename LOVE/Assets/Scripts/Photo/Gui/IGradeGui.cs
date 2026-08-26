using System;
using Love.Video;
using UnityEngine;

namespace Love.Tools
{
    public enum GuiMsg { None, Info, Warning, Error }

    /// <summary>
    /// 参数界面的控件层。
    ///
    /// <see cref="GradeSettingsGUI"/> 那 973 行、100 多个控件，本身没有一处依赖编辑器——
    /// 依赖全在这一层。换一个实现，同一份参数界面就能跑在独立程序里。
    ///
    /// **IMGUI 本身出包之后是能用的**：`GUI` / `GUILayout` / `GUILayoutUtility` /
    /// `Event` / `GUIStyle` 都在 UnityEngine 里。所以布局、按钮、拖拽这些
    /// 根本不用进这个接口，只有 `EditorGUILayout` / `EditorGUI` / `EditorStyles`
    /// 这些真正编辑器专有的才需要。
    ///
    /// 接口方法的签名刻意贴着 EditorGUILayout 的形状（传值、返回新值），
    /// 这样编辑器那份实现就是一层薄壳，看得出来它没偷偷改语义。
    /// </summary>
    public interface IGradeGui
    {
        // ---------------- 改动检测 ----------------

        void BeginChange();

        /// <summary>上一次 <see cref="BeginChange"/> 之后有没有人动过控件。</summary>
        bool EndChange();

        // ---------------- 排版 ----------------

        void Space(float px);
        void Label(string text);
        void Label(string label, string value);
        void MiniBoldLabel(string text);
        void MiniLabel(string text);
        void HelpBox(string text, GuiMsg kind);

        /// <summary>灰掉一段。用 <c>using</c> 包住，省得手动配对还原。</summary>
        IDisposable Disabled(bool on);

        void Indent(int delta);

        /// <summary>要一行的矩形。</summary>
        Rect Row();

        /// <summary>标签栏有多宽。色块、色相条这些要按它对齐。</summary>
        float LabelWidth { get; }

        void FillRect(Rect r, Color c);

        /// <summary>分组标题。返回要不要画它的内容。</summary>
        bool Section(bool open, string title);

        // ---------------- 控件 ----------------

        float Slider(string label, float value, float min, float max);
        float SliderIn(Rect r, string label, float value, float min, float max);
        bool Toggle(string label, bool value);
        bool ToggleLeft(string label, bool value);
        int Popup(string label, int value, string[] names);
        string TextField(string value);
        string SearchField(string value);
        void MinMaxSlider(ref float lo, ref float hi, float limitMin, float limitMax);

        /// <summary>
        /// 曲线编辑。
        ///
        /// 这是整套控件里唯一没法在运行时随手做出来的——曲线编辑器是个完整的
        /// 子窗口。独立程序那份实现只把曲线画出来、不给编辑，并且明说。
        /// </summary>
        AnimationCurve Curve(string label, AnimationCurve curve, Color color, Rect range);

        /// <summary>这个实现支不支持编辑曲线。不支持时界面上要讲清楚。</summary>
        bool CanEditCurves { get; }

        // ---------------- 色轮 ----------------

        float TrackBallHeight(float width);
        bool TrackBall(Rect r, string label, ref float rr, ref float gg, ref float bb, float range);

        /// <summary>色块，点开弹转盘选色。回调是跨帧的，所以不能用 ref 传值。</summary>
        void HueSwatch(Rect r, float hue, float strength, Action<float, float> write);
        void HueOnlySwatch(Rect r, float hue, Action<float> write);

        // ---------------- 其它 ----------------

        /// <summary>画一圈折线，用来在预览上勾出 Power Window。</summary>
        void PolyLine(float width, Color color, Vector3[] points);

        /// <summary>确认对话框。运行时没有的话直接返回 true（当作确认）。</summary>
        bool Confirm(string title, string message, string ok, string cancel);

        /// <summary>磁盘上的资产变了，编辑器要刷新一下；独立程序里什么也不用做。</summary>
        void AssetsChanged();

        /// <summary>登记一次撤销。必须在改动**之前**调——事后记录存的是新状态，Ctrl+Z 回不去。</summary>
        void RecordUndo(string action);

        /// <summary>蒙版那一节。它自成体系，单独一个接口。</summary>
        IMaskSectionGui Masks { get; }
    }

    /// <summary>
    /// 蒙版列表那一节。
    ///
    /// 窗口要往里塞「有没有主体蒙版 / 深度图 / 天空」，也要读「正在涂哪个笔刷」，
    /// 所以这些得摆在接口上，不能藏在实现里。
    /// </summary>
    public interface IMaskSectionGui
    {
        Vector2Int SourceSize { get; set; }
        bool HasSubjectMask { get; set; }
        bool HasSky { get; set; }
        float SkyCoverage { get; set; }
        bool HasDepthMap { get; set; }

        /// <summary>要一张新笔刷贴图，返回它的 id。</summary>
        Func<int> RequestBrush { get; set; }

        /// <summary>正在涂的那个部件，没有就是 null。</summary>
        MaskPart PaintingPart { get; }

        /// <summary>选中的那个部件。</summary>
        MaskPart EditingPart { get; }

        /// <summary>笔刷参数由窗口画，因为笔刷贴图和画笔状态都在窗口手上。</summary>
        Action DrawBrushOptions { get; set; }

        void Draw(VideoGradeSettings s, Action<string> recordUndo, Action markChanged);
    }
}
