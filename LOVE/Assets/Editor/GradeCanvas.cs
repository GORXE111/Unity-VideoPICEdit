using UnityEditor;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// 调色预览画布：棋盘底、缩放平移、硬裁剪、描边。
    ///
    /// 只负责「把一张已经渲好的贴图摆在屏幕上」，不做任何渲染——
    /// Graphics.Blit 绝不能出现在 OnGUI 里，这条规矩在这个类里体现为
    /// 它压根拿不到 renderer。
    ///
    /// 交互按 Photoshop 的习惯：滚轮以鼠标为锚点缩放，空格切抓手，
    /// F 适应窗口，1 看原像素。
    /// </summary>
    public class GradeCanvas
    {
        public float Zoom = 1f;
        public Vector2 Pan;

        /// <summary>下一次绘制时先适应窗口。换图、换视频、改了裁剪都该置上。</summary>
        public bool FitPending = true;

        /// <summary>上一次绘制时图像在屏幕上占的矩形。叠加层（裁剪框、吸管）靠它换算坐标。</summary>
        public Rect ImageRect { get; private set; }

        bool _spaceDown;

        /// <summary>按住反斜杠临时看原图。调用方每帧读它决定要不要旁路。</summary>
        public bool HoldCompare { get; private set; }

        /// <summary>按住反斜杠的状态刚变过没有。变了就得重渲染。</summary>
        public bool ConsumeCompareChanged()
        {
            bool v = _compareChanged;
            _compareChanged = false;
            return v;
        }

        bool _compareChanged;

        // ---------------- 棋盘底 ----------------

        static Texture2D _checker;

        /// <summary>透明区域的棋盘底，和 PS 一样。</summary>
        public static Texture2D Checker
        {
            get
            {
                if (_checker != null) return _checker;
                const int N = 16;
                _checker = new Texture2D(N * 2, N * 2, TextureFormat.RGBA32, false, false)
                {
                    name = "GradeCanvasChecker",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Repeat,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                var a = new Color32(64, 64, 66, 255);
                var b = new Color32(80, 80, 83, 255);
                var px = new Color32[N * 2 * N * 2];
                for (int y = 0; y < N * 2; y++)
                    for (int x = 0; x < N * 2; x++)
                        px[y * N * 2 + x] = ((x / N) + (y / N)) % 2 == 0 ? a : b;
                _checker.SetPixels32(px);
                _checker.Apply(false, false);
                return _checker;
            }
        }

        // ---------------- 绘制 ----------------

        /// <summary>
        /// 画一帧。<paramref name="imgW"/> / <paramref name="imgH"/> 是逻辑尺寸，
        /// 可能和 preview 的实际分辨率不同（比如裁剪模式下要显示整幅）。
        /// </summary>
        public void Draw(Rect area, Texture preview, int imgW, int imgH)
        {
            EditorGUI.DrawRect(area, GradeSkin.Canvas);

            if (preview == null || imgW <= 0 || imgH <= 0)
            {
                ImageRect = Rect.zero;
                return;
            }

            if (FitPending) { Fit(area, imgW, imgH); FitPending = false; }

            float w = imgW * Zoom;
            float h = imgH * Zoom;
            var img = new Rect(area.center.x - w * 0.5f + Pan.x,
                               area.center.y - h * 0.5f + Pan.y, w, h);
            ImageRect = img;

            if (Event.current.type != EventType.Repaint) return;

            // 硬裁到画布区内，放大之后才不会画到时间轴和参数栏上
            GUI.BeginGroup(area);
            var local = new Rect(img.x - area.x, img.y - area.y, img.width, img.height);

            GUI.DrawTextureWithTexCoords(local, Checker,
                new Rect(0f, 0f, local.width / 32f, local.height / 32f));

            GUI.DrawTexture(local, preview, ScaleMode.StretchToFill, true);

            // 一圈细描边，缩小时更清楚边界在哪
            GradeSkin.Frame(new Rect(local.x - 1f, local.y - 1f, local.width + 2f, local.height + 2f),
                            GradeSkin.Edge);

            GUI.EndGroup();
        }

        public void Fit(Rect area, int imgW, int imgH)
        {
            if (imgW <= 0 || imgH <= 0) return;
            Zoom = Mathf.Min(area.width / imgW, area.height / imgH) * 0.95f;
            Pan = Vector2.zero;
        }

        /// <summary>以画布中心为锚点设定缩放比例。</summary>
        public void SetZoom(float zoom)
        {
            float old = Zoom;
            Zoom = Mathf.Clamp(zoom, 0.02f, 16f);
            Pan *= Zoom / old;   // 保持当前看的位置不跳
        }

        // ---------------- 输入 ----------------

        /// <summary>
        /// 处理缩放平移。<paramref name="blockPan"/> 用来让叠加层（裁剪框之类）
        /// 优先拿到拖拽事件，画布不去抢。
        /// 返回 true 表示需要重绘。
        /// </summary>
        public bool HandleInput(Rect area, bool blockPan = false)
        {
            var e = Event.current;
            if (e.type == EventType.Layout) return false;

            // 正在文本框里打字时，别把按键抢走。画布的 HandleInput 排在参数栏之前，
            // 不加这道判断的话，在搜索框里敲 "f" 会被当成"适应窗口"，字根本输不进去
            if (EditorGUIUtility.editingTextField) return false;

            if (e.type == EventType.KeyDown)
            {
                // 快捷键对齐 PS / Lightroom 的习惯
                if (e.keyCode == KeyCode.Space) { _spaceDown = true; return true; }
                if (e.keyCode == KeyCode.Backslash && !HoldCompare)
                {
                    HoldCompare = true; _compareChanged = true; e.Use(); return true;
                }
                if (e.keyCode == KeyCode.F) { FitPending = true; e.Use(); return true; }
                if (e.keyCode == KeyCode.Alpha1) { SetZoom(1f); e.Use(); return true; }
                return false;
            }

            if (e.type == EventType.KeyUp)
            {
                if (e.keyCode == KeyCode.Space) { _spaceDown = false; return true; }
                if (e.keyCode == KeyCode.Backslash && HoldCompare)
                {
                    HoldCompare = false; _compareChanged = true; e.Use(); return true;
                }
                return false;
            }

            if (_spaceDown) EditorGUIUtility.AddCursorRect(area, MouseCursor.Pan);
            if (!area.Contains(e.mousePosition) || blockPan) return false;

            if (e.type == EventType.ScrollWheel)
            {
                float old = Zoom;
                Zoom = Mathf.Clamp(Zoom * (1f - e.delta.y * 0.05f), 0.02f, 16f);
                // 以鼠标位置为锚点缩放，否则放大时目标会跑出视野
                Vector2 toMouse = e.mousePosition - (area.center + Pan);
                Pan -= toMouse * (Zoom / old - 1f);
                e.Use();
                return true;
            }

            if (e.type == EventType.MouseDrag && (e.button == 0 || e.button == 2))
            {
                Pan += e.delta;
                e.Use();
                return true;
            }

            return false;
        }

    }
}
