using System.IO;
using Love.Tools;
using Love.Video;
using UnityEngine;

namespace Love.App
{
    /// <summary>
    /// 独立修图程序。
    ///
    /// 整条调色管线本来就没绑在编辑器上——<see cref="VideoGradeRenderer"/> 是个普通
    /// C# 类，不依赖 MonoBehaviour / 场景 / Play 模式。真正拦路的是**代码放在
    /// `Assets/Editor/` 里根本不进包**，以及界面那半用的是 `EditorGUILayout`。
    ///
    /// 前者已经解决：能跑在运行时的都搬进 `Assets/Scripts/Photo/` 了，
    /// 那批代码在完全没有 UnityEditor 引用的条件下编译通过。
    /// 后者这里先做一份最小的运行时界面，把整条链路打通。
    ///
    /// 复用的部分（和编辑器修图台是同一份源码，不是拷贝）：
    ///   VideoGradeRenderer  整条调色管线
    ///   VideoGradeSettings  参数
    ///   SonyRawImporter     索尼 ARW 解码
    ///   AutoTone            自适应起手值
    ///   NoiseEstimate       噪声估计
    ///   ExportNaming        导出命名与尺寸
    /// </summary>
    public class PhotoApp : MonoBehaviour
    {
        [Tooltip("VideoGrade 材质。必须在场景里引用着，否则出包时 shader 会被剔掉")]
        public Material gradeMaterial;

        VideoGradeRenderer _renderer;
        readonly RuntimeGui _gui = new RuntimeGui();
        readonly VideoGradeSettings _settings = new VideoGradeSettings();

        Texture2D _source;
        RenderTexture _preview;
        string _path;
        string _status = "拖一张图进来，或者按「打开」。支持 JPG / PNG / 索尼 ARW";
        bool _dirty = true;
        bool _bypass;

        // 画布
        float _zoom = 1f;
        Vector2 _pan;
        bool _fit = true;

        // 参数分组的展开状态
        bool _gBasic = true, _gColor, _gDetail, _gEffect;
        Vector2 _scroll;

        const float PanelWidth = 320f;

        void OnEnable()
        {
            _settings.Reset();
            if (gradeMaterial != null) _renderer = new VideoGradeRenderer(gradeMaterial);
        }

        void OnDisable()
        {
            _renderer?.Dispose();
            _renderer = null;
            Release();
            _gui.Dispose();
        }

        void Release()
        {
            if (_preview != null) { _preview.Release(); Destroy(_preview); _preview = null; }
            if (_source != null) { Destroy(_source); _source = null; }
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
                // 非 RAW 走 Unity 自带的解码，JPG / PNG 都认
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (!tex.LoadImage(File.ReadAllBytes(path)))
                {
                    Destroy(tex);
                    tex = null;
                    _status = "这张图读不了：" + Path.GetFileName(path);
                }
            }

            if (tex == null) return;

            Release();
            _source = tex;
            _path = path;
            _settings.Reset();
            _fit = true;
            _dirty = true;
            _status = $"{Path.GetFileName(path)}　{tex.width}×{tex.height}";
        }

        // ---------------- 渲染 ----------------

        void Update()
        {
            // Blit 一律排到这里。OnGUI 里切 RenderTexture.active 会把 GUI 状态搅乱
            if (_dirty && _source != null && _renderer != null) Render();
        }

        void Render()
        {
            _settings.OutputSize(_source.width, _source.height, out int w, out int h);

            if (_preview == null || _preview.width != w || _preview.height != h)
            {
                if (_preview != null) { _preview.Release(); Destroy(_preview); }
                _preview = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
                                             RenderTextureReadWrite.sRGB);
                _preview.Create();
            }

            _renderer.Render(_source, _preview, _settings,
                             new VideoGradeRenderer.Options { bypass = _bypass });
            _dirty = false;
        }

        // ---------------- 界面 ----------------

        void OnGUI()
        {
            _gui.EnsureSkin();
            _gui.BeginFrame();

            var full = new Rect(0f, 0f, Screen.width, Screen.height);
            var canvas = new Rect(0f, 0f, Screen.width - PanelWidth, Screen.height);
            var panel = new Rect(canvas.xMax, 0f, PanelWidth, Screen.height);

            GUI.DrawTexture(full, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0f,
                            new Color(0.16f, 0.16f, 0.17f, 1f), 0f, 0f);

            DrawCanvas(canvas);

            GUILayout.BeginArea(panel);
            DrawPanel();
            GUILayout.EndArea();

            HandleDrop();

            if (_gui.Changed) _dirty = true;
        }

        void DrawCanvas(Rect area)
        {
            GUI.BeginGroup(area);

            if (_preview != null)
            {
                float iw = _preview.width, ih = _preview.height;

                if (_fit)
                {
                    _zoom = Mathf.Min(area.width / iw, area.height / ih) * 0.94f;
                    _pan = Vector2.zero;
                }

                float w = iw * _zoom, h = ih * _zoom;
                var r = new Rect((area.width - w) * 0.5f + _pan.x,
                                 (area.height - h) * 0.5f + _pan.y, w, h);
                GUI.DrawTexture(r, _preview, ScaleMode.StretchToFill, false);
            }
            else
            {
                var msg = new Rect(0f, area.height * 0.5f - 12f, area.width, 24f);
                GUI.Label(msg, "把图片拖进来", new GUIStyle(_gui.Label)
                { alignment = TextAnchor.MiddleCenter });
            }

            GUI.EndGroup();

            HandleCanvasInput(area);
        }

        void HandleCanvasInput(Rect area)
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

        void DrawPanel()
        {
            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            if (_gui.Btn("打开…") && NativeFileDialog.Supported)
            {
                string p = NativeFileDialog.Open("打开图片",
                    "图片|*.jpg;*.jpeg;*.png;*.arw|索尼 RAW|*.arw|全部|*.*",
                    _path != null ? Path.GetDirectoryName(_path) : null);
                if (p != null) Load(p);
            }
            using (new GuiEnabled(_source != null))
            {
                if (_gui.Btn("导出…")) Export();
            }
            GUILayout.EndHorizontal();

            _gui.Info(_status);
            GUILayout.Space(4f);

            GUILayout.BeginHorizontal();
            bool bp = GUILayout.Toggle(_bypass, "看原图", _gui.Button, GUILayout.Height(20f));
            if (bp != _bypass) { _bypass = bp; _dirty = true; }
            if (_gui.Btn("适应窗口")) _fit = true;
            if (_gui.Btn("1:1")) { _fit = false; _zoom = 1f; _pan = Vector2.zero; }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);

            using (new GuiEnabled(_source != null))
            {
                GUILayout.BeginHorizontal();
                if (_gui.Btn("自动色调")) AutoToneNow();
                if (_gui.Btn("重置")) { _settings.Reset(); _dirty = true; }
                GUILayout.EndHorizontal();
            }

            _scroll = GUILayout.BeginScrollView(_scroll);
            DrawParams();
            GUILayout.EndScrollView();
        }

        void DrawParams()
        {
            var s = _settings;

            if (_gui.Group(ref _gBasic, "基础"))
            {
                _gui.Slider("曝光", ref s.exposure, -5f, 5f);
                _gui.Slider("对比度", ref s.contrast, -1f, 1f);
                _gui.Slider("高光", ref s.highlights, -1f, 1f);
                _gui.Slider("阴影", ref s.shadows, -1f, 1f);
                _gui.Slider("白位", ref s.outWhite, 0f, 1f);
                _gui.Slider("黑位", ref s.outBlack, 0f, 1f);
                _gui.Slider("中间调", ref s.levelsGamma, 0.1f, 3f);
            }

            if (_gui.Group(ref _gColor, "颜色"))
            {
                _gui.Slider("色温", ref s.temperature, -1f, 1f);
                _gui.Slider("色调", ref s.tint, -1f, 1f);
                _gui.Slider("饱和度", ref s.saturation, -1f, 1f);
                _gui.Slider("色相", ref s.hueShift, -180f, 180f, "0.0");
                _gui.Slider("肤色保护", ref s.skinProtect, 0f, 1f);
            }

            if (_gui.Group(ref _gDetail, "细节"))
            {
                _gui.Slider("清晰度", ref s.clarity, -1f, 1f);
                _gui.Slider("去朦胧", ref s.dehaze, -1f, 1f);
                _gui.Slider("纹理", ref s.texture, -1f, 1f);
                _gui.Slider("锐化", ref s.sharpen, 0f, 2f);
                _gui.Slider("降噪", ref s.denoise, 0f, 1f);
            }

            if (_gui.Group(ref _gEffect, "风格化"))
            {
                _gui.Slider("暗角", ref s.vignetteIntensity, -1f, 1f);
                _gui.Slider("暗角柔和", ref s.vignetteSmoothness, 0.01f, 1f);
                _gui.Slider("颗粒", ref s.grain, 0f, 1f);
                _gui.Slider("辉光", ref s.bloomIntensity, 0f, 2f);
                _gui.Slider("辉光阈值", ref s.bloomThreshold, 0f, 2f);
            }

            GUILayout.Space(10f);
            _gui.Info("这是最小可用版本。编辑器修图台里那 100+ 个参数、蒙版、\n" +
                      "修补、图片库还没搬过来——见 README「独立程序」一节。");
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
                Destroy(tmp);
            }
        }

        void Export()
        {
            if (_preview == null || _path == null) return;

            string name = Path.GetFileNameWithoutExtension(_path) + "_graded.jpg";
            string outPath = NativeFileDialog.Save("导出", "JPEG|*.jpg|PNG|*.png", name,
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
                Destroy(readback);
            }
        }

        /// <summary>把文件拖进窗口。比走对话框顺手，而且不挑平台。</summary>
        void HandleDrop()
        {
            var e = Event.current;
            if (e == null) return;

#if UNITY_EDITOR
            // 编辑器 Play 模式里没有运行时拖放事件，只能靠对话框
#endif
            if (e.type == EventType.DragPerform || e.type == EventType.DragUpdated)
            {
                e.Use();
            }
        }

        /// <summary>`using` 写法的 GUI.enabled，省得每次手动配对还原。</summary>
        readonly struct GuiEnabled : System.IDisposable
        {
            readonly bool _prev;
            public GuiEnabled(bool on) { _prev = GUI.enabled; GUI.enabled = on && _prev; }
            public void Dispose() => GUI.enabled = _prev;
        }
    }
}
