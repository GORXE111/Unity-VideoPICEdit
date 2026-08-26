using System;
using UnityEngine;

namespace Love.App
{
    /// <summary>看图的画布：缩放、平移、适应窗口。两个台共用。</summary>
    public class Canvas2D
    {
        float _zoom = 1f;
        Vector2 _pan;
        bool _fit = true;

        /// <summary>上一次画出来的图占哪块（屏幕坐标）。吸管、修补、色卡角点都要靠它换算。</summary>
        public Rect ImageRect { get; private set; }

        /// <summary>
        /// 拖动是不是被上层接管了（比如正在涂修补）。
        /// 接管时画布自己不再平移，否则涂一笔画面跟着跑。
        /// </summary>
        public bool DragTakenOver { get; set; }

        public void Fit() { _fit = true; _pan = Vector2.zero; }
        public void OneToOne() { _fit = false; _zoom = 1f; _pan = Vector2.zero; }

        /// <summary>屏幕坐标 -> 图片 uv（原点左下，和 shader 的约定一致）。</summary>
        public bool ScreenToUv(Vector2 mouse, out Vector2 uv)
        {
            uv = Vector2.zero;
            var r = ImageRect;
            if (r.width <= 0f || r.height <= 0f || !r.Contains(mouse)) return false;
            uv = new Vector2((mouse.x - r.x) / r.width, 1f - (mouse.y - r.y) / r.height);
            return true;
        }

        public void Draw(Rect area, Texture tex, RuntimeGui ui, string emptyHint)
        {
            GUI.BeginGroup(area);

            if (tex != null)
            {
                float iw = tex.width, ih = tex.height;
                if (_fit) _zoom = Mathf.Min(area.width / iw, area.height / ih) * 0.94f;

                float w = iw * _zoom, h = ih * _zoom;
                var inner = new Rect((area.width - w) * 0.5f + _pan.x,
                                     (area.height - h) * 0.5f + _pan.y, w, h);
                GUI.DrawTexture(inner, tex, ScaleMode.StretchToFill, false);

                // 记的是屏幕坐标，不是 group 内的——上层拿到的鼠标位置是屏幕坐标
                ImageRect = new Rect(inner.x + area.x, inner.y + area.y, inner.width, inner.height);
            }
            else
            {
                ImageRect = new Rect(0f, 0f, 0f, 0f);
                GUI.Label(new Rect(0f, area.height * 0.5f - 12f, area.width, 24f), emptyHint,
                          new GUIStyle(ui.Label) { alignment = TextAnchor.MiddleCenter });
            }

            GUI.EndGroup();
            HandleInput(area);
        }

        void HandleInput(Rect area)
        {
            var e = Event.current;
            if (e == null || !area.Contains(e.mousePosition)) return;

            if (e.type == EventType.ScrollWheel)
            {
                _fit = false;
                _zoom = Mathf.Clamp(_zoom * (1f - e.delta.y * 0.05f), 0.02f, 16f);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 && !DragTakenOver)
            {
                _fit = false;
                _pan += e.delta;
                e.Use();
            }
        }
    }

    /// <summary>`using` 写法的 GUI.enabled，省得每次手动配对还原。</summary>
    public readonly struct GuiEnabled : IDisposable
    {
        readonly bool _prev;
        public GuiEnabled(bool on) { _prev = GUI.enabled; GUI.enabled = on && _prev; }
        public void Dispose() => GUI.enabled = _prev;
    }
}
