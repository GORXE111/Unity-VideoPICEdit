using System;
using Love.Video;
using UnityEditor;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// 在画布上直接拖蒙版的形状。视频台和修图台共用。
    ///
    /// 没有它的话，径向和渐变只能靠拖滑条对位置——那基本等于盲调。
    ///
    /// 坐标换算是这里唯一有点绕的地方，必须和 shader 里 PartShapeMask 的正向变换互为逆：
    ///     shader:  d = uv - center;  d' = (d.x * aspect, d.y);  rp = R(d')
    /// 画布上图像矩形的宽高比就是 aspect，所以化简之后，
    /// 局部坐标到屏幕的缩放在两个方向上都是 img.height——各向同性，画起来反而简单。
    /// </summary>
    public class MaskOverlay
    {
        enum Handle { None = -1, Center = 0, SizeX = 1, SizeY = 2, Rotate = 3 }

        Handle _drag = Handle.None;
        Vector2 _centerAtDown;
        Vector2 _sizeAtDown;
        float _rotAtDown;

        const float HandleR = 5f;      // 手柄半边长
        const float GrabR = 10f;       // 命中判定半径，比手柄大一圈才好点中

        public bool Dragging => _drag != Handle.None;

        // ---------------- 坐标 ----------------

        static Vector2 CenterScreen(Rect img, MaskPart p) =>
            new Vector2(img.x + p.center.x * img.width,
                        img.y + (1f - p.center.y) * img.height);

        /// <summary>蒙版局部坐标 -> 屏幕。</summary>
        static Vector2 ToScreen(Rect img, MaskPart p, Vector2 local)
        {
            float rad = p.rotation * Mathf.Deg2Rad;
            float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
            // R 的逆
            var d = new Vector2(local.x * c + local.y * s, -local.x * s + local.y * c);
            return CenterScreen(img, p) + new Vector2(d.x, -d.y) * img.height;
        }

        /// <summary>屏幕 -> 蒙版局部坐标。</summary>
        static Vector2 ToLocal(Rect img, MaskPart p, Vector2 screen)
        {
            var so = (screen - CenterScreen(img, p)) / Mathf.Max(img.height, 1f);
            var d = new Vector2(so.x, -so.y);
            float rad = p.rotation * Mathf.Deg2Rad;
            float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
            return new Vector2(d.x * c - d.y * s, d.x * s + d.y * c);
        }

        // ---------------- 绘制 ----------------

        public void Draw(Rect canvas, Rect img, MaskPart p)
        {
            if (p == null || !p.IsGeometric || Event.current.type != EventType.Repaint) return;

            GUI.BeginGroup(canvas);
            var off = new Vector2(canvas.x, canvas.y);
            Color prev = Handles.color;

            Handles.color = new Color(1f, 1f, 1f, 0.85f);

            if (p.Shape == MaskShape.LinearGradient) DrawGradient(img, p, off);
            else DrawShape(img, p, off);

            // 手柄
            Dot(ToScreen(img, p, Vector2.zero) - off, GradeSkin.Playhead);
            if (p.Shape != MaskShape.LinearGradient)
            {
                Dot(ToScreen(img, p, new Vector2(p.size.x, 0f)) - off, Color.white);
                Dot(ToScreen(img, p, new Vector2(0f, p.size.y)) - off, Color.white);
                Dot(ToScreen(img, p, RotateHandleLocal(p)) - off, GradeSkin.Accent);
            }
            else
            {
                Dot(ToScreen(img, p, new Vector2(0f, p.size.y)) - off, GradeSkin.Accent);
            }

            Handles.color = prev;
            GUI.EndGroup();
        }

        static Vector2 RotateHandleLocal(MaskPart p) => new Vector2(p.size.x + 0.07f, 0f);

        void DrawShape(Rect img, MaskPart p, Vector2 off)
        {
            if (p.Shape == MaskShape.Rect)
            {
                var c = new[]
                {
                    new Vector2(-p.size.x, -p.size.y), new Vector2(p.size.x, -p.size.y),
                    new Vector2(p.size.x, p.size.y), new Vector2(-p.size.x, p.size.y),
                };
                var pts = new Vector3[5];
                for (int i = 0; i < 4; i++)
                {
                    var s = ToScreen(img, p, c[i]) - off;
                    pts[i] = new Vector3(s.x, s.y, 0f);
                }
                pts[4] = pts[0];
                Handles.DrawAAPolyLine(1.6f, pts);
                return;
            }

            const int N = 56;
            var e = new Vector3[N + 1];
            for (int i = 0; i <= N; i++)
            {
                float t = i / (float)N * Mathf.PI * 2f;
                var s = ToScreen(img, p, new Vector2(Mathf.Cos(t) * p.size.x, Mathf.Sin(t) * p.size.y)) - off;
                e[i] = new Vector3(s.x, s.y, 0f);
            }
            Handles.DrawAAPolyLine(1.6f, e);
        }

        void DrawGradient(Rect img, MaskPart p, Vector2 off)
        {
            // 三条线：中线（半选）和两条羽化边界。和 shader 里
            // t = rp.y / size.y; m = 1 - smoothstep(-feather, feather, t) 对应
            float fe = Mathf.Clamp(p.feather, 0.001f, 1f) * p.size.y;
            float half = 3f;    // 横向拉长到画面之外，看起来才像一条无限长的分界线

            void Line(float y, float width, Color col)
            {
                Handles.color = col;
                var a = ToScreen(img, p, new Vector2(-half, y)) - off;
                var b = ToScreen(img, p, new Vector2(half, y)) - off;
                Handles.DrawAAPolyLine(width, new Vector3(a.x, a.y, 0f), new Vector3(b.x, b.y, 0f));
            }

            Line(-fe, 1.2f, new Color(1f, 1f, 1f, 0.45f));
            Line(0f, 1.8f, new Color(1f, 1f, 1f, 0.9f));
            Line(fe, 1.2f, new Color(1f, 1f, 1f, 0.45f));

            // 从中心指向"不选"那一侧的短轴，指明方向
            Handles.color = new Color(1f, 1f, 1f, 0.6f);
            var c0 = ToScreen(img, p, Vector2.zero) - off;
            var c1 = ToScreen(img, p, new Vector2(0f, p.size.y)) - off;
            Handles.DrawAAPolyLine(1.4f, new Vector3(c0.x, c0.y, 0f), new Vector3(c1.x, c1.y, 0f));
        }

        static void Dot(Vector2 p, Color c)
        {
            EditorGUI.DrawRect(new Rect(p.x - HandleR, p.y - HandleR, HandleR * 2f, HandleR * 2f), c);
            EditorGUI.DrawRect(new Rect(p.x - HandleR + 1f, p.y - HandleR + 1f,
                                        HandleR * 2f - 2f, HandleR * 2f - 2f),
                               new Color(0f, 0f, 0f, 0.55f));
            EditorGUI.DrawRect(new Rect(p.x - 2f, p.y - 2f, 4f, 4f), c);
        }

        // ---------------- 交互 ----------------

        /// <summary>返回 true 表示事件被吃掉了，画布别再拿去平移。</summary>
        public bool HandleInput(Rect img, MaskPart p, Action<string> recordUndo, Action changed)
        {
            if (p == null || !p.IsGeometric) { _drag = Handle.None; return false; }

            var e = Event.current;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                var hit = HitTest(img, p, e.mousePosition);
                if (hit == Handle.None) return false;

                // Undo 要在改之前记，事后记存进撤销栈的是新状态
                recordUndo?.Invoke("调整蒙版形状");
                _drag = hit;
                _centerAtDown = p.center;
                _sizeAtDown = p.size;
                _rotAtDown = p.rotation;
                e.Use();
                return true;
            }

            if (e.type == EventType.MouseDrag && _drag != Handle.None)
            {
                Apply(img, p, e.mousePosition);
                changed?.Invoke();
                e.Use();
                return true;
            }

            if (e.type == EventType.MouseUp && _drag != Handle.None)
            {
                _drag = Handle.None;
                e.Use();
                return true;
            }

            return _drag != Handle.None;
        }

        Handle HitTest(Rect img, MaskPart p, Vector2 mouse)
        {
            bool grad = p.Shape == MaskShape.LinearGradient;

            if (Vector2.Distance(ToScreen(img, p, Vector2.zero), mouse) <= GrabR) return Handle.Center;
            if (Vector2.Distance(ToScreen(img, p, new Vector2(0f, p.size.y)), mouse) <= GrabR) return Handle.SizeY;
            if (grad) return Handle.None;

            if (Vector2.Distance(ToScreen(img, p, new Vector2(p.size.x, 0f)), mouse) <= GrabR) return Handle.SizeX;
            if (Vector2.Distance(ToScreen(img, p, RotateHandleLocal(p)), mouse) <= GrabR) return Handle.Rotate;
            return Handle.None;
        }

        void Apply(Rect img, MaskPart p, Vector2 mouse)
        {
            if (_drag == Handle.Center)
            {
                p.center = new Vector2(Mathf.Clamp01((mouse.x - img.x) / Mathf.Max(img.width, 1f)),
                                       Mathf.Clamp01(1f - (mouse.y - img.y) / Mathf.Max(img.height, 1f)));
                return;
            }

            // 拖手柄时要按"按下那一刻"的旋转来反解，否则旋转边改边算会自己转起来
            var snapshot = new MaskPart { center = p.center, rotation = _rotAtDown };
            var local = ToLocal(img, snapshot, mouse);

            switch (_drag)
            {
                case Handle.SizeX:
                    p.size = new Vector2(Mathf.Clamp(Mathf.Abs(local.x), 0.005f, 4f), p.size.y);
                    break;

                case Handle.SizeY:
                    if (p.Shape == MaskShape.LinearGradient)
                    {
                        // 渐变的端点手柄同时定方向和跨度，和 Lightroom 一致
                        float len = Mathf.Clamp(local.magnitude, 0.01f, 4f);
                        p.size = new Vector2(p.size.x, len);
                        // local 是在 _rotAtDown 那个坐标系里量的，所以角度要叠加回去
                        p.rotation = _rotAtDown + Mathf.Atan2(local.x, local.y) * Mathf.Rad2Deg;
                    }
                    else p.size = new Vector2(p.size.x, Mathf.Clamp(Mathf.Abs(local.y), 0.005f, 4f));
                    break;

                case Handle.Rotate:
                    p.rotation = _rotAtDown + Mathf.Atan2(-local.y, local.x) * Mathf.Rad2Deg;
                    break;
            }
        }
    }
}
