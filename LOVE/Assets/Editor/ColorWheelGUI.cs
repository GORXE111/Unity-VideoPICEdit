using System;
using UnityEditor;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// 色轮控件。
    ///
    /// 两种形态：
    ///   TrackBall —— 达芬奇式的一级校色轮，拖动改的是 RGB 三个偏移量
    ///   HueSwatch —— 一个色块按钮，点开弹出转盘选色相和强度
    ///
    /// RGB 和轮盘位置之间用的是色相六边形的可逆投影（R 在 0°、G 在 120°、B 在 240°），
    /// 所以拖轮盘和拖 R/G/B 滑条改的是同一份数据，两边永远同步。
    /// </summary>
    public static class ColorWheelGUI
    {
        // 三个通道在色相环上的方向
        static readonly Vector2 AxisR = new Vector2(1f, 0f);
        static readonly Vector2 AxisG = new Vector2(-0.5f, 0.8660254f);
        static readonly Vector2 AxisB = new Vector2(-0.5f, -0.8660254f);

        #region 轮盘贴图

        static Texture2D _wheel;

        /// <summary>HSV 色轮：角度是色相，半径是饱和度。生成一次全局复用。</summary>
        public static Texture2D Wheel
        {
            get
            {
                if (_wheel != null) return _wheel;

                const int N = 256;
                _wheel = new Texture2D(N, N, TextureFormat.RGBA32, false, false)
                {
                    name = "ColorWheel",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };

                var px = new Color32[N * N];
                float half = N * 0.5f;
                for (int y = 0; y < N; y++)
                {
                    for (int x = 0; x < N; x++)
                    {
                        float dx = (x + 0.5f - half) / half;
                        float dy = (y + 0.5f - half) / half;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);

                        if (d > 1.02f) { px[y * N + x] = new Color32(0, 0, 0, 0); continue; }

                        float hue = Mathf.Repeat(Mathf.Atan2(dy, dx) / (Mathf.PI * 2f), 1f);
                        Color c = Color.HSVToRGB(hue, Mathf.Clamp01(d), 1f);
                        // 边缘按半径羽化一像素，否则圆周是锯齿
                        byte a = (byte)(Mathf.Clamp01((1f - d) * half) * 255f);
                        px[y * N + x] = new Color32(
                            (byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), a);
                    }
                }
                _wheel.SetPixels32(px);
                _wheel.Apply(false, false);
                return _wheel;
            }
        }

        #endregion

        #region RGB 与轮盘位置的互转

        /// <summary>RGB 偏移量投影到色相环的 2D 坐标。</summary>
        public static Vector2 RgbToWheel(float r, float g, float b) =>
            AxisR * r + AxisG * g + AxisB * b;

        /// <summary>
        /// 轮盘坐标反解出 RGB 偏移量，约束三通道之和为 0。
        /// 和为 0 意味着纯粹改色偏、不动亮度，亮度交给旁边的主控滑条。
        /// </summary>
        public static void WheelToRgb(Vector2 p, out float r, out float g, out float b)
        {
            r = p.x / 1.5f;
            float d = p.y / 0.8660254f;
            g = (-r + d) * 0.5f;
            b = (-r - d) * 0.5f;
        }

        #endregion

        #region 一级校色轮

        /// <summary>色轮控件占多高（标题 + 轮盘）。</summary>
        public static float TrackBallHeight(float width) => width + 15f;

        /// <summary>
        /// 达芬奇式色轮：中心为中性，往某个方向拖就往那个色相偏。
        /// range 是三个通道各自的取值上限，用来把轮盘的归一化坐标映射回参数值。
        /// 双击回中。返回 true 表示这一帧被改动了。
        /// </summary>
        public static bool TrackBall(Rect rect, string label, ref float r, ref float g, ref float b, float range)
        {
            var wheelRect = new Rect(rect.x, rect.y + 15f, rect.width, rect.width);
            float radius = wheelRect.width * 0.5f;
            Vector2 center = wheelRect.center;

            if (Event.current.type == EventType.Repaint)
            {
                var title = new Rect(rect.x, rect.y, rect.width, 14f);
                GUI.Label(title, label, EditorStyles.miniBoldLabel);

                GUI.DrawTexture(wheelRect, Wheel, ScaleMode.StretchToFill, true);

                // 中心十字，标出中性位置
                var cross = new Color(0f, 0f, 0f, 0.35f);
                EditorGUI.DrawRect(new Rect(center.x - 4f, center.y - 0.5f, 8f, 1f), cross);
                EditorGUI.DrawRect(new Rect(center.x - 0.5f, center.y - 4f, 1f, 8f), cross);

                Vector2 p = RgbToWheel(r / range, g / range, b / range);
                if (p.magnitude > 1f) p.Normalize();
                DrawHandle(new Vector2(center.x + p.x * radius, center.y - p.y * radius));
            }

            var e = Event.current;
            bool changed = false;
            int id = GUIUtility.GetControlID(FocusType.Passive, wheelRect);

            if (e.type == EventType.MouseDown && e.button == 0 && wheelRect.Contains(e.mousePosition))
            {
                if (e.clickCount == 2)
                {
                    // 双击回中，比手动把三根滑条归零快得多
                    r = g = b = 0f;
                    changed = true;
                }
                else
                {
                    GUIUtility.hotControl = id;
                    changed = ApplyDrag(e.mousePosition, center, radius, range, ref r, ref g, ref b);
                }
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && GUIUtility.hotControl == id)
            {
                changed = ApplyDrag(e.mousePosition, center, radius, range, ref r, ref g, ref b);
                e.Use();
            }
            else if (e.type == EventType.MouseUp && GUIUtility.hotControl == id)
            {
                GUIUtility.hotControl = 0;
                e.Use();
            }

            return changed;
        }

        static bool ApplyDrag(Vector2 mouse, Vector2 center, float radius,
                              float range, ref float r, ref float g, ref float b)
        {
            Vector2 p = new Vector2((mouse.x - center.x) / radius, (center.y - mouse.y) / radius);
            if (p.magnitude > 1f) p.Normalize();

            WheelToRgb(p, out float nr, out float ng, out float nb);
            r = nr * range;
            g = ng * range;
            b = nb * range;
            return true;
        }

        static void DrawHandle(Vector2 pos)
        {
            EditorGUI.DrawRect(new Rect(pos.x - 5f, pos.y - 5f, 10f, 10f), new Color(0f, 0f, 0f, 0.75f));
            EditorGUI.DrawRect(new Rect(pos.x - 3f, pos.y - 3f, 6f, 6f), Color.white);
        }

        #endregion

        #region 色相色块 + 弹出转盘

        /// <summary>一个显示当前颜色的色块按钮，点击弹出转盘选色。</summary>
        public static void HueSwatch(Rect rect, float hue, float strength, Action<float, float> write)
        {
            if (Event.current.type == EventType.Repaint)
            {
                Color c = Color.Lerp(Color.white, Color.HSVToRGB(Mathf.Repeat(hue, 1f), 1f, 1f), strength);
                EditorGUI.DrawRect(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f),
                                   new Color(0f, 0f, 0f, 0.5f));
                EditorGUI.DrawRect(rect, c);
            }

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);

            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                PopupWindow.Show(rect, new HuePopup(hue, strength, write));
                Event.current.Use();
            }
        }

        /// <summary>纯色相色块，没有强度概念（限定器的色相中心用）。</summary>
        public static void HueOnlySwatch(Rect rect, float hue, Action<float> write)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f),
                                   new Color(0f, 0f, 0f, 0.5f));
                EditorGUI.DrawRect(rect, Color.HSVToRGB(Mathf.Repeat(hue, 1f), 0.85f, 1f));
            }

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);

            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                PopupWindow.Show(rect, new HuePopup(hue, 1f, (h, s) => write?.Invoke(h), true));
                Event.current.Use();
            }
        }

        /// <summary>弹出的转盘：角度选色相，半径选强度。</summary>
        class HuePopup : PopupWindowContent
        {
            const float WheelSize = 168f;
            const float Pad = 10f;

            float _hue, _strength;
            readonly Action<float, float> _write;
            readonly bool _hueOnly;

            public HuePopup(float hue, float strength, Action<float, float> write, bool hueOnly = false)
            {
                _hue = hue;
                _strength = strength;
                _write = write;
                _hueOnly = hueOnly;
            }

            public override Vector2 GetWindowSize() =>
                new Vector2(WheelSize + Pad * 2f, WheelSize + Pad * 2f + (_hueOnly ? 24f : 46f));

            public override void OnGUI(Rect rect)
            {
                var wheelRect = new Rect(Pad, Pad, WheelSize, WheelSize);
                float radius = WheelSize * 0.5f;
                Vector2 center = wheelRect.center;

                if (Event.current.type == EventType.Repaint)
                {
                    GUI.DrawTexture(wheelRect, Wheel, ScaleMode.StretchToFill, true);

                    float ang = Mathf.Repeat(_hue, 1f) * Mathf.PI * 2f;
                    float rr = _hueOnly ? 1f : _strength;
                    DrawHandle(new Vector2(center.x + Mathf.Cos(ang) * radius * rr,
                                           center.y - Mathf.Sin(ang) * radius * rr));
                }

                var e = Event.current;
                if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) &&
                    wheelRect.Contains(e.mousePosition))
                {
                    Vector2 p = new Vector2(e.mousePosition.x - center.x, center.y - e.mousePosition.y) / radius;
                    _hue = Mathf.Repeat(Mathf.Atan2(p.y, p.x) / (Mathf.PI * 2f), 1f);
                    if (!_hueOnly) _strength = Mathf.Clamp01(p.magnitude);
                    _write?.Invoke(_hue, _strength);
                    e.Use();
                    editorWindow.Repaint();
                }

                float y = WheelSize + Pad + 6f;
                if (!_hueOnly)
                {
                    EditorGUI.BeginChangeCheck();
                    float st = EditorGUI.Slider(new Rect(Pad, y, WheelSize, 16f), _strength, 0f, 1f);
                    if (EditorGUI.EndChangeCheck()) { _strength = st; _write?.Invoke(_hue, _strength); }
                    y += 20f;
                }

                EditorGUI.LabelField(new Rect(Pad, y, WheelSize, 16f),
                    _hueOnly ? $"色相 {_hue:0.000}" : $"色相 {_hue:0.000}    强度 {_strength:0.00}",
                    EditorStyles.miniLabel);
            }
        }

        #endregion
    }
}
