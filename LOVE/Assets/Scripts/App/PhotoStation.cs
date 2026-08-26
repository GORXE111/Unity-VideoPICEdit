using System.Collections.Generic;
using System.IO;
using Love.Tools;
using Love.Video;
using UnityEngine;

namespace Love.App
{
    /// <summary>
    /// 修图台。
    ///
    /// 图片库、修补、快照、导出、LUT、色卡、吸管——全部复用编辑器那边的同一份逻辑，
    /// 这个类只负责摆界面和管生命周期。
    /// </summary>
    public partial class PhotoStation : IStation
    {
        /// <summary>画布上正在用哪个工具。</summary>
        public enum Tool { None, Repair, Clone, WbPick, Chart }

        readonly Material _mat;
        readonly RuntimeGui _ui;
        VideoGradeRenderer _renderer;

        readonly VideoGradeSettings _settings = new VideoGradeSettings();
        readonly PhotoLibrary _lib = new PhotoLibrary();
        readonly PhotoEditStore _store = new PhotoEditStore();
        readonly ImageRepair _repair = new ImageRepair();
        readonly Canvas2D _canvas = new Canvas2D();

        /// <summary>蒙版那一节。外壳把控件层的那份交给这里，好往里塞"有没有天空/主体"。</summary>
        IMaskSectionGui _guiMasks = new NullMaskSection();

        public IMaskSectionGui MaskSection
        {
            get => _guiMasks;
            set => _guiMasks = value ?? new NullMaskSection();
        }

        Texture2D _full;
        RenderTexture _preview;

        /// <summary>
        /// 大图现在载的是哪一条。
        ///
        /// **必须和 <c>_lib.Current</c> 分开。** 多选时 Current 已经先动了，
        /// 如果载入函数还拿 Current 判断"要不要换图"，就永远相等、图片反而不换。
        /// </summary>
        string _loadedPath;

        string _status = "打开图片，或者一次选一批";
        bool _dirty = true;
        bool _bypass;
        Tool _tool = Tool.None;

        // 待生成缩略图的队列。Blit + ReadPixels 不能在 OnGUI 里做，排到 Tick
        readonly Queue<string> _pending = new Queue<string>();

        const int ThumbMax = 192;

        public PhotoStation(Material mat, RuntimeGui ui)
        {
            _mat = mat;
            _ui = ui;
            _settings.Reset();
            _store.Load();
            if (mat != null) _renderer = new VideoGradeRenderer(mat);
        }

        public string Status => _status;
        public bool HasSource => _full != null;
        public Vector2Int SourceSize =>
            _full != null ? new Vector2Int(_full.width, _full.height) : Vector2Int.zero;
        public Texture Preview => _preview;
        public VideoGradeSettings Settings => _settings;
        public void MarkDirty() => _dirty = true;
        public void OnHide() => Stash();

        public void Dispose()
        {
            Stash();
            _store.Save(force: true);

            _renderer?.Dispose();
            _renderer = null;
            _repair.Dispose();
            ReleaseAi();
            ReleaseSky();
            ReleaseFull();
            ReleaseLut();
            foreach (var e in _lib.All) if (e.thumb != null) Object.Destroy(e.thumb);
            _lib.Clear();
        }

        void ReleaseFull()
        {
            if (_preview != null) { _preview.Release(); Object.Destroy(_preview); _preview = null; }
            if (_full != null) { Object.Destroy(_full); _full = null; }
        }

        /// <summary>当前这张的参数、修补、评级先收好再干别的。</summary>
        void Stash()
        {
            if (_loadedPath == null) return;
            _store.PutSettings(_loadedPath, _settings);
            _store.PutRepairs(_loadedPath, _repair.Spots);

            var cur = _lib.Current;
            if (cur != null && cur.path == _loadedPath) _store.PutMeta(cur.path, cur.rating, cur.flag);
        }

        // ---------------- 导入 ----------------

        public void ImportMany(IEnumerable<string> paths)
        {
            int added = 0;
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                if (!IsImage(p) || _lib.Contains(p)) continue;
                _pending.Enqueue(p);
                added++;
            }
            _status = added > 0 ? $"排队 {added} 张…" : "没有新图片";
        }

        /// <summary>把某张图所在的整个文件夹一起收进来。</summary>
        public void ImportFolderOf(string anyFile)
        {
            string dir = Path.GetDirectoryName(anyFile);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            ImportMany(Directory.GetFiles(dir));
        }

        static bool IsImage(string p)
        {
            string e = Path.GetExtension(p).ToLowerInvariant();
            return e == ".jpg" || e == ".jpeg" || e == ".png" || e == ".arw";
        }

        /// <summary>一拍处理一张。几百张一次做完的话窗口会僵住好几秒。</summary>
        void ProcessPending()
        {
            if (_pending.Count == 0) return;
            string p = _pending.Dequeue();
            if (_lib.Contains(p)) return;

            var thumb = MakeThumb(p);
            if (thumb == null) return;

            var entry = _lib.Add(p, Path.GetFileName(p), thumb);
            entry.modified = SafeStamp(p);

            // 之前调过这张的话，评级和旗标跟着回来
            var rec = _store.Get(p);
            if (rec != null) { entry.rating = rec.rating; entry.flag = rec.flag; }

            _lib.Rebuild();
            if (_lib.Current == null) { _lib.SelectOnly(entry); LoadEntry(entry); }

            _status = _pending.Count > 0
                ? $"排队中，还剩 {_pending.Count} 张"
                : $"共 {_lib.Count} 张";
        }

        static long SafeStamp(string p)
        {
            try { return File.GetLastWriteTimeUtc(p).Ticks; } catch { return 0; }
        }

        Texture2D MakeThumb(string path)
        {
            Texture2D src = LoadTexture(path, preferPreview: true);
            if (src == null) return null;

            int w = src.width, h = src.height;
            float k = ThumbMax / (float)Mathf.Max(w, h);
            int tw = Mathf.Max(1, Mathf.RoundToInt(w * k));
            int th = Mathf.Max(1, Mathf.RoundToInt(h * k));

            var rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32,
                                                RenderTextureReadWrite.sRGB);
            var thumb = new Texture2D(tw, th, TextureFormat.RGBA32, false, false);
            try
            {
                Graphics.Blit(src, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                thumb.ReadPixels(new Rect(0f, 0f, tw, th), 0, 0, false);
                thumb.Apply(false, false);
                RenderTexture.active = prev;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(src);
            }
            return thumb;
        }

        /// <summary>
        /// 读一张图。<paramref name="preferPreview"/> 时 RAW 只解嵌入的预览图——
        /// 做缩略图不值得整解一遍 6100 万像素。
        /// </summary>
        Texture2D LoadTexture(string path, bool preferPreview)
        {
            try
            {
                if (SonyRawImporter.IsRaw(path))
                {
                    if (preferPreview)
                    {
                        var pv = SonyRawImporter.LoadPreviewOnly(path);
                        if (pv != null) return pv;
                    }
                    var r = SonyRawImporter.Load(path, new SonyRawImporter.Options());
                    if (r != null && r.texture != null) return r.texture;
                    _status = "ARW 解码失败：" + (r?.error ?? "未知原因");
                    return null;
                }

                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (t.LoadImage(File.ReadAllBytes(path))) return t;
                Object.Destroy(t);
            }
            catch (System.Exception e)
            {
                _status = "读不了 " + Path.GetFileName(path) + "：" + e.Message;
            }
            return null;
        }

        // ---------------- 选中 / 载入 ----------------

        void Select(PhotoEntry e, bool additive, bool range)
        {
            if (e == null) return;
            if (range) _lib.SelectRange(e);
            else if (additive) _lib.Toggle(e);
            else _lib.SelectOnly(e);

            _lib.SetCurrent(e);
            if (e.path != _loadedPath) LoadEntry(e);
        }

        void LoadEntry(PhotoEntry e)
        {
            if (e == null) return;

            Stash();

            var tex = LoadTexture(e.path, preferPreview: false);
            if (tex == null) return;

            ReleaseFull();
            _full = tex;
            _loadedPath = e.path;

            var rec = _store.Get(e.path);
            if (rec != null && rec.hasSettings && rec.settings != null) _settings.CopyFrom(rec.settings);
            else _settings.Reset();

            _repair.Spots.Clear();
            if (rec != null && rec.repairs != null) _repair.Spots.AddRange(rec.repairs);
            _repair.InvalidateProbe();
            _repairDirty = true;

            _canvas.Fit();
            _dirty = true;
            _snapChanged = false;
            ReleaseSky();
            _status = $"{e.name}　{tex.width}×{tex.height}　（{_lib.IndexOfVisible(e) + 1}/{_lib.Visible.Count}）";
        }

        // ---------------- 每帧 ----------------

        bool _repairDirty;

        public void Tick()
        {
            ProcessPending();
            StepDenoise();

            // 限流落盘。拖滑条时不会每帧写文件，但崩了最多丢八秒
            if (_store.Dirty) { Stash(); _store.Save(); }

            if (_repairDirty)
            {
                _repairDirty = false;
                _repair.Rebuild(DenoisedOrFull);   // 里面有 Blit，只能在这里做
                _dirty = true;
            }

            if (_pendingAction != null)
            {
                var a = _pendingAction;
                _pendingAction = null;
                a();
            }

            // Blit 一律排到这里。OnGUI 里切 RenderTexture.active 会把 GUI 状态搅乱
            if (_dirty && _full != null && _renderer != null) Render();
        }

        System.Action _pendingAction;

        /// <summary>
        /// 调色的源。
        ///
        /// 顺序是**降噪 → 修补 → 调色**：修补是拿周围像素补窟窿，
        /// 在带噪的图上找取样源，补上去的那块也是带噪的。
        /// </summary>
        Texture GradeSource => _repair.Result != null ? (Texture)_repair.Result : DenoisedOrFull;

        void Render()
        {
            _settings.OutputSize(_full.width, _full.height, out int w, out int h);

            if (_preview == null || _preview.width != w || _preview.height != h)
            {
                if (_preview != null) { _preview.Release(); Object.Destroy(_preview); }
                _preview = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
                                             RenderTextureReadWrite.sRGB);
                _preview.Create();
            }

            _renderer.Render(GradeSource, _preview, _settings, new VideoGradeRenderer.Options
            {
                bypass = _bypass,
                lut = _lut,
                lutAmount = _lutAmount,
                externalMask = CurrentMask,
                depthMap = CurrentMask,   // 选了 MiDaS 时生成的就是深度图，共用一张
                skyMask = EnsureSky(),
            });
            _dirty = false;
        }

        // ---------------- 画布 ----------------

        public void DrawCanvas(Rect area)
        {
            float strip = _lib.Count > 0 ? 108f : 0f;
            var view = new Rect(area.x, area.y, area.width, Mathf.Max(60f, area.height - strip));

            // 用工具的时候画布不该跟着平移
            _canvas.DragTakenOver = _tool != Tool.None;
            _canvas.Draw(view, _preview, _ui, "打开图片，或者一次选一批");

            if (_tool != Tool.None) HandleTool(view);
            if (_tool == Tool.Chart) DrawChartOverlay();

            if (strip > 0f)
                DrawFilmstrip(new Rect(area.x, area.yMax - strip, area.width, strip));
        }

        void HandleTool(Rect view)
        {
            var e = Event.current;
            if (e == null || !view.Contains(e.mousePosition)) return;

            bool click = e.type == EventType.MouseDown && e.button == 0;
            bool drag = e.type == EventType.MouseDrag && e.button == 0;
            if (!click && !drag) return;
            if (!_canvas.ScreenToUv(e.mousePosition, out Vector2 uv)) return;

            switch (_tool)
            {
                case Tool.Repair:
                case Tool.Clone:
                    if (click || drag) AddRepair(uv, _tool == Tool.Clone);
                    e.Use();
                    break;

                case Tool.WbPick:
                    if (click) { PickWhiteBalance(uv); _tool = Tool.None; e.Use(); }
                    break;

                case Tool.Chart:
                    if (click || drag) { DragChartCorner(uv, click); e.Use(); }
                    break;
            }
        }

        // ---------------- 胶片条 ----------------

        Vector2 _stripScroll;

        void DrawFilmstrip(Rect area)
        {
            _ui.EnsureSkin();
            GUI.DrawTexture(area, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0f,
                            new Color(0.11f, 0.11f, 0.12f, 1f), 0f, 0f);

            var visible = _lib.Visible;
            const float CellW = 96f, CellH = 84f, Gap = 4f;

            var inner = new Rect(area.x + 4f, area.y + 4f, area.width - 8f, area.height - 8f);
            var content = new Rect(0f, 0f, visible.Count * (CellW + Gap), inner.height - 18f);

            _stripScroll = GUI.BeginScrollView(inner, _stripScroll, content, true, false);

            for (int i = 0; i < visible.Count; i++)
            {
                var entry = visible[i];
                var cell = new Rect(i * (CellW + Gap), 0f, CellW, CellH);

                bool isCurrent = entry == _lib.Current;
                bool isSelected = _lib.Selected.Contains(entry);

                if (isCurrent || isSelected)
                    GUI.DrawTexture(cell, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0f,
                                    isCurrent ? new Color(0.35f, 0.65f, 1f, 0.9f)
                                              : new Color(1f, 1f, 1f, 0.25f), 0f, 0f);

                var img = new Rect(cell.x + 3f, cell.y + 3f, cell.width - 6f, cell.height - 20f);
                if (entry.thumb != null) GUI.DrawTexture(img, entry.thumb, ScaleMode.ScaleToFit);

                // 星级和旗标就画在缩略图下沿，一眼扫得到
                var meta = new Rect(cell.x + 3f, cell.yMax - 16f, cell.width - 6f, 14f);
                string stars = entry.rating > 0 ? new string('★', entry.rating) : "";
                string flag = entry.flag > 0 ? " ✓" : entry.flag < 0 ? " ✕" : "";
                GUI.Label(meta, stars + flag, _ui.Mini);

                if (GUI.Button(cell, GUIContent.none, GUIStyle.none))
                    Select(entry, Event.current.control || Event.current.command,
                           Event.current.shift);
            }

            GUI.EndScrollView();

            GUI.Label(new Rect(area.x + 6f, area.yMax - 16f, area.width - 12f, 14f),
                      $"{visible.Count} / {_lib.Count} 张　选中 {_lib.Selected.Count}　" +
                      "点选，Ctrl 加选，Shift 连选，1~5 打星，0 清星",
                      _ui.Mini);

            HandleStripKeys();
        }

        void HandleStripKeys()
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;

            var cur = _lib.Current;
            if (cur == null) return;

            if (e.keyCode >= KeyCode.Alpha0 && e.keyCode <= KeyCode.Alpha5)
            {
                int r = e.keyCode - KeyCode.Alpha0;
                foreach (var s in _lib.Selected) s.rating = r;
                cur.rating = r;
                _store.PutMeta(cur.path, cur.rating, cur.flag);
                _lib.Rebuild();
                e.Use();
            }
            else if (e.keyCode == KeyCode.LeftArrow) { Select(_lib.Step(-1), false, false); e.Use(); }
            else if (e.keyCode == KeyCode.RightArrow) { Select(_lib.Step(1), false, false); e.Use(); }
        }
    }
}
