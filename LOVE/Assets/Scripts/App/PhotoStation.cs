using System.IO;
using Love.Tools;
using Love.Video;
using UnityEngine;

namespace Love.App
{
    /// <summary>
    /// 修图台。开一张图、过管线、导出。
    ///
    /// 解码、调色、自动色调、导出命名全是复用编辑器那边的同一份源码，
    /// 这个类只负责摆界面和管生命周期。
    /// </summary>
    public class PhotoStation : IStation
    {
        readonly Material _mat;
        readonly RuntimeGui _ui;
        VideoGradeRenderer _renderer;

        readonly VideoGradeSettings _settings = new VideoGradeSettings();
        Texture2D _source;
        RenderTexture _preview;
        string _path;
        string _status = "打开一张 JPG / PNG / 索尼 ARW";
        bool _dirty = true;
        bool _bypass;

        readonly Canvas2D _canvas = new Canvas2D();

        public PhotoStation(Material mat, RuntimeGui ui)
        {
            _mat = mat;
            _ui = ui;
            _settings.Reset();
            if (mat != null) _renderer = new VideoGradeRenderer(mat);
        }

        public string Status => _status;
        public bool HasSource => _source != null;
        public Vector2Int SourceSize =>
            _source != null ? new Vector2Int(_source.width, _source.height) : Vector2Int.zero;
        public Texture Preview => _preview;
        public VideoGradeSettings Settings => _settings;
        public void MarkDirty() => _dirty = true;
        public void OnHide() { }

        public void Dispose()
        {
            _renderer?.Dispose();
            _renderer = null;
            Release();
        }

        void Release()
        {
            if (_preview != null) { _preview.Release(); Object.Destroy(_preview); _preview = null; }
            if (_source != null) { Object.Destroy(_source); _source = null; }
        }

        // ---------------- 载入 ----------------

        public void Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            Texture2D tex = null;
            if (SonyRawImporter.IsRaw(path))
            {
                var r = SonyRawImporter.Load(path, new SonyRawImporter.Options());
                if (r != null && r.texture != null) tex = r.texture;
                else _status = "ARW 解码失败：" + (r?.error ?? "未知原因");
            }
            else
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (!tex.LoadImage(File.ReadAllBytes(path)))
                {
                    Object.Destroy(tex);
                    tex = null;
                    _status = "这张图读不了：" + Path.GetFileName(path);
                }
            }

            if (tex == null) return;

            Release();
            _source = tex;
            _path = path;
            _settings.Reset();
            _canvas.Fit();
            _dirty = true;
            _status = $"{Path.GetFileName(path)}　{tex.width}×{tex.height}";
        }

        // ---------------- 渲染 ----------------

        public void Tick()
        {
            // Blit 一律排到这里。OnGUI 里切 RenderTexture.active 会把 GUI 状态搅乱
            if (_dirty && _source != null && _renderer != null) Render();
        }

        void Render()
        {
            _settings.OutputSize(_source.width, _source.height, out int w, out int h);

            if (_preview == null || _preview.width != w || _preview.height != h)
            {
                if (_preview != null) { _preview.Release(); Object.Destroy(_preview); }
                _preview = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
                                             RenderTextureReadWrite.sRGB);
                _preview.Create();
            }

            _renderer.Render(_source, _preview, _settings,
                             new VideoGradeRenderer.Options { bypass = _bypass });
            _dirty = false;
        }

        // ---------------- 界面 ----------------

        public void DrawCanvas(Rect area) => _canvas.Draw(area, _preview, _ui, "把图打开看看");

        public void DrawPanel(RuntimeGui ui)
        {
            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            if (ui.Btn("打开图片…"))
            {
                string p = NativeFileDialog.Open("打开图片",
                    "图片|*.jpg;*.jpeg;*.png;*.arw|索尼 RAW|*.arw|全部|*.*",
                    _path != null ? Path.GetDirectoryName(_path) : null);
                if (p != null) Load(p);
            }
            using (new GuiEnabled(_source != null))
                if (ui.Btn("导出…")) Export();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool bp = GUILayout.Toggle(_bypass, "看原图", ui.Button, GUILayout.Height(20f));
            if (bp != _bypass) { _bypass = bp; _dirty = true; }
            if (ui.Btn("适应")) _canvas.Fit();
            if (ui.Btn("1:1")) _canvas.OneToOne();
            GUILayout.EndHorizontal();

            using (new GuiEnabled(_source != null))
            {
                GUILayout.BeginHorizontal();
                if (ui.Btn("自动色调")) AutoToneNow();
                if (ui.Btn("重置参数")) { _settings.Reset(); _dirty = true; }
                GUILayout.EndHorizontal();
            }
        }

        void AutoToneNow()
        {
            if (_source == null) return;

            // GetPixels 在大图上是内存炸弹，先缩到小图再统计
            const int Small = 256;
            var rt = RenderTexture.GetTemporary(Small, Small, 0, RenderTextureFormat.ARGB32,
                                                RenderTextureReadWrite.sRGB);
            var tmp = new Texture2D(Small, Small, TextureFormat.RGBA32, false, true);
            try
            {
                Graphics.Blit(_source, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tmp.ReadPixels(new Rect(0f, 0f, Small, Small), 0, 0, false);
                tmp.Apply(false, false);
                RenderTexture.active = prev;

                AutoTone.Apply(tmp.GetPixels(), _settings);
                _dirty = true;
                _status = "已套用自动色调";
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(tmp);
            }
        }

        void Export()
        {
            if (_preview == null || _path == null) return;

            string name = Path.GetFileNameWithoutExtension(_path) + "_graded.jpg";
            string outPath = NativeFileDialog.Save("导出图片", "JPEG|*.jpg|PNG|*.png", name,
                                                   Path.GetDirectoryName(_path));
            if (outPath == null) return;

            if (_dirty) Render();

            var readback = new Texture2D(_preview.width, _preview.height, TextureFormat.RGBA32,
                                         false, false);
            try
            {
                var prev = RenderTexture.active;
                RenderTexture.active = _preview;
                readback.ReadPixels(new Rect(0f, 0f, _preview.width, _preview.height), 0, 0, false);
                readback.Apply(false, false);
                RenderTexture.active = prev;

                bool png = outPath.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase);
                File.WriteAllBytes(outPath, png ? readback.EncodeToPNG() : readback.EncodeToJPG(92));
                _status = "已导出 " + Path.GetFileName(outPath);
            }
            finally
            {
                Object.Destroy(readback);
            }
        }
    }

    /// <summary>`using` 写法的 GUI.enabled，省得每次手动配对还原。</summary>
    public readonly struct GuiEnabled : System.IDisposable
    {
        readonly bool _prev;
        public GuiEnabled(bool on) { _prev = GUI.enabled; GUI.enabled = on && _prev; }
        public void Dispose() => GUI.enabled = _prev;
    }

    /// <summary>看图的画布：缩放、平移、适应窗口。两个台共用。</summary>
    public class Canvas2D
    {
        float _zoom = 1f;
        Vector2 _pan;
        bool _fit = true;

        public void Fit() { _fit = true; _pan = Vector2.zero; }
        public void OneToOne() { _fit = false; _zoom = 1f; _pan = Vector2.zero; }

        public void Draw(Rect area, Texture tex, RuntimeGui ui, string emptyHint)
        {
            GUI.BeginGroup(area);

            if (tex != null)
            {
                float iw = tex.width, ih = tex.height;
                if (_fit) _zoom = Mathf.Min(area.width / iw, area.height / ih) * 0.94f;

                float w = iw * _zoom, h = ih * _zoom;
                GUI.DrawTexture(new Rect((area.width - w) * 0.5f + _pan.x,
                                         (area.height - h) * 0.5f + _pan.y, w, h),
                                tex, ScaleMode.StretchToFill, false);
            }
            else
            {
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
            else if (e.type == EventType.MouseDrag && e.button == 0)
            {
                _fit = false;
                _pan += e.delta;
                e.Use();
            }
        }
    }
}
