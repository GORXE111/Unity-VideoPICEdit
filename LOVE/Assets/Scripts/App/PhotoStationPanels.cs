using System;
using System.Collections.Generic;
using System.IO;
using Love.Tools;
using Love.Video;
using UnityEngine;

namespace Love.App
{
    /// <summary>
    /// 修图台的右侧面板。拆成 partial 是因为主文件已经管够了图片库和画布，
    /// 再把七个小节堆进去就没法读了。
    /// </summary>
    public partial class PhotoStation
    {
        // ---- 面板折叠状态 ----
        bool _pFile = true, _pLib, _pRepair, _pSnap, _pExport, _pLut, _pChart;

        // ---- 导出 ----
        readonly ExportPreset _export = new ExportPreset();
        string _exportDir;
        Font _wmFont;

        // ---- LUT ----
        Texture3D _lut;
        float _lutAmount = 1f;
        string _lutName = "";

        // ---- 修补 ----
        float _brushRadius = 0.03f;
        float _brushFeather = 0.35f;

        // ---- 快照 ----
        bool _snapChanged;
        string _newSnapName = "";

        // ---- 色卡 ----
        Vector2[] _chartCorners = DefaultChartCorners();
        int _chartDrag = -1;
        string _chartStatus = "";

        static Vector2[] DefaultChartCorners() => new[]
        {
            new Vector2(0.3f, 0.65f), new Vector2(0.7f, 0.65f),
            new Vector2(0.7f, 0.35f), new Vector2(0.3f, 0.35f),
        };

        void ReleaseLut()
        {
            if (_lut != null) { UnityEngine.Object.Destroy(_lut); _lut = null; }
            _lutName = "";
        }

        public void DrawPanel(RuntimeGui ui)
        {
            GUILayout.Space(4f);

            if (ui.Group(ref _pFile, "文件")) DrawFile(ui);
            if (ui.Group(ref _pLib, "图片库")) DrawLibrary(ui);

            using (new GuiEnabled(_full != null))
            {
                if (ui.Group(ref _pRepair, "污点修复 / 仿制图章")) DrawRepair(ui);
                if (ui.Group(ref _pSnap, "快照")) DrawSnapshots(ui);
                if (ui.Group(ref _pLut, "LUT (.cube)")) DrawLut(ui);
                if (ui.Group(ref _pChart, "色卡校色")) DrawChart(ui);
                if (ui.Group(ref _pExport, "导出")) DrawExport(ui);
            }

            GUILayout.Space(6f);
        }

        // ---------------- 文件 ----------------

        void DrawFile(RuntimeGui ui)
        {
            GUILayout.BeginHorizontal();
            if (ui.Btn("打开图片…"))
            {
                var ps = NativeFileDialog.OpenMany("打开图片（可多选）",
                    "图片|*.jpg;*.jpeg;*.png;*.arw|索尼 RAW|*.arw|全部|*.*", LastDir());
                if (ps.Length > 0) ImportMany(ps);
            }
            if (ui.Btn("整个文件夹…"))
            {
                string p = NativeFileDialog.Open("选这个文件夹里的任意一张",
                    "图片|*.jpg;*.jpeg;*.png;*.arw|全部|*.*", LastDir());
                if (p != null) ImportFolderOf(p);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool bp = GUILayout.Toggle(_bypass, "看原图", ui.Button, GUILayout.Height(20f));
            if (bp != _bypass) { _bypass = bp; _dirty = true; }
            if (ui.Btn("适应")) _canvas.Fit();
            if (ui.Btn("1:1")) _canvas.OneToOne();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (ui.Btn("自动色调")) _pendingAction = AutoToneNow;
            if (ui.Btn("重置参数"))
            {
                AutoSnapshot("重置之前");
                _settings.Reset();
                _dirty = true;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool wb = _tool == Tool.WbPick;
            bool nwb = GUILayout.Toggle(wb, "白平衡吸管", ui.Button, GUILayout.Height(20f));
            if (nwb != wb) _tool = nwb ? Tool.WbPick : Tool.None;
            GUILayout.EndHorizontal();

            if (_tool == Tool.WbPick)
                ui.Info("在画面上点一处本该是中性灰的地方。");
        }

        string LastDir() =>
            _loadedPath != null ? Path.GetDirectoryName(_loadedPath) : null;

        void AutoToneNow()
        {
            if (_full == null) return;
            AutoSnapshot("自动色调之前");

            // GetPixels 在大图上是内存炸弹，先缩到小图再统计
            const int Small = 256;
            var rt = RenderTexture.GetTemporary(Small, Small, 0, RenderTextureFormat.ARGB32,
                                                RenderTextureReadWrite.sRGB);
            var tmp = new Texture2D(Small, Small, TextureFormat.RGBA32, false, true);
            try
            {
                Graphics.Blit(GradeSource, rt);
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
                UnityEngine.Object.Destroy(tmp);
            }
        }

        // ---------------- 图片库 ----------------

        static readonly string[] SortNames = { "文件名", "日期", "星级" };
        // 顺序必须和 PhotoFilter 一一对上：All / Picked / NotRejected / Rated1..5
        static readonly string[] FilterNames =
            { "全部", "只看留用", "不看排除", "1★+", "2★+", "3★+", "4★+", "5★" };

        void DrawLibrary(RuntimeGui ui)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("排序", ui.Label, GUILayout.Width(36f));
            int sort = GUILayout.Toolbar((int)_lib.Sort, SortNames);
            if (sort != (int)_lib.Sort) _lib.Sort = (PhotoSort)sort;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool desc = GUILayout.Toggle(_lib.Descending, "倒序", ui.Button, GUILayout.Height(20f));
            if (desc != _lib.Descending) _lib.Descending = desc;
            if (ui.Btn("全选")) _lib.SelectAllVisible();
            GUILayout.EndHorizontal();

            int filter = ui.Popup2("筛选", (int)_lib.Filter, FilterNames);
            if (filter != (int)_lib.Filter) _lib.Filter = (PhotoFilter)filter;

            var cur = _lib.Current;
            if (cur != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("星级", ui.Label, GUILayout.Width(36f));
                for (int i = 0; i <= 5; i++)
                {
                    if (!GUILayout.Button(i == 0 ? "·" : i.ToString(), ui.Button)) continue;
                    foreach (var s in _lib.Selected) s.rating = i;
                    cur.rating = i;
                    _store.PutMeta(cur.path, cur.rating, cur.flag);
                    _lib.Rebuild();
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("旗标", ui.Label, GUILayout.Width(36f));
                if (ui.Btn("留用")) SetFlag(1);
                if (ui.Btn("未标")) SetFlag(0);
                if (ui.Btn("排除")) SetFlag(-1);
                GUILayout.EndHorizontal();

                if (ui.Btn("把当前参数套给选中的全部"))
                    _pendingAction = ApplyToSelected;
            }

            if (ui.Btn("移除选中（不删文件）")) _pendingAction = RemoveSelected;
        }

        void SetFlag(int f)
        {
            var cur = _lib.Current;
            if (cur == null) return;
            foreach (var s in _lib.Selected) s.flag = f;
            cur.flag = f;
            _store.PutMeta(cur.path, cur.rating, cur.flag);
            _lib.Rebuild();
        }

        void ApplyToSelected()
        {
            int n = 0;
            foreach (var s in _lib.Selected)
            {
                if (s.path == _loadedPath) continue;
                _store.PutSettings(s.path, _settings);
                n++;
            }
            _status = $"参数已套给 {n} 张";
        }

        void RemoveSelected()
        {
            var doomed = new List<PhotoEntry>(_lib.Selected);
            foreach (var e in doomed)
            {
                if (e.path == _loadedPath) { ReleaseFull(); _loadedPath = null; }
                if (e.thumb != null) UnityEngine.Object.Destroy(e.thumb);
                _lib.Remove(e);
            }
            _lib.Rebuild();
            if (_lib.Current != null && _lib.Current.path != _loadedPath) LoadEntry(_lib.Current);
            _status = $"已移除 {doomed.Count} 张（文件没动）";
        }

        // ---------------- 修补 ----------------

        void DrawRepair(RuntimeGui ui)
        {
            GUILayout.BeginHorizontal();
            bool heal = _tool == Tool.Repair;
            bool nh = GUILayout.Toggle(heal, "污点修复", ui.Button, GUILayout.Height(20f));
            bool clone = _tool == Tool.Clone;
            bool nc = GUILayout.Toggle(clone, "仿制图章", ui.Button, GUILayout.Height(20f));
            GUILayout.EndHorizontal();

            if (nh != heal) _tool = nh ? Tool.Repair : Tool.None;
            else if (nc != clone) _tool = nc ? Tool.Clone : Tool.None;

            ui.Slider("笔尖大小", ref _brushRadius, 0.004f, 0.12f, "0.000");
            ui.Slider("羽化", ref _brushFeather, 0f, 1f);

            GUILayout.BeginHorizontal();
            using (new GuiEnabled(_repair.HasSpots))
            {
                if (ui.Btn("撤销上一处"))
                {
                    _repair.Spots.RemoveAt(_repair.Spots.Count - 1);
                    _repairDirty = true;
                    _store.MarkDirty();
                }
                if (ui.Btn("全部清除"))
                {
                    _repair.Spots.Clear();
                    _repairDirty = true;
                    _store.MarkDirty();
                }
            }
            GUILayout.EndHorizontal();

            ui.Info($"已修 {_repair.Spots.Count} 处。在画面上点或者拖着涂。\n" +
                    "修补跑在调色前面——在带污点的图上调色是白调。");
        }

        void AddRepair(Vector2 uv, bool clone)
        {
            if (_full == null) return;
            _repair.Add(GradeSourceRaw, uv, _brushRadius, _brushFeather, clone, null);
            _repairDirty = true;
            _store.MarkDirty();
        }

        /// <summary>找取样源要在原图上找，不能在已经修过的图上找，否则会越修越糊。</summary>
        Texture GradeSourceRaw => _full;

        // ---------------- 快照 ----------------

        void AutoSnapshot(string label)
        {
            if (_loadedPath == null || !_snapChanged) return;
            var rec = _store.GetOrCreate(_loadedPath);
            Snapshots.Add(rec.snapshots, _settings, label, true, DateTime.Now);
            _store.MarkDirty();
            _snapChanged = false;
        }

        void DrawSnapshots(RuntimeGui ui)
        {
            var rec = _loadedPath != null ? _store.Get(_loadedPath) : null;
            var list = rec?.snapshots;

            GUILayout.BeginHorizontal();
            _newSnapName = GUILayout.TextField(_newSnapName);
            if (ui.Btn("存一份", 56f))
            {
                var r = _store.GetOrCreate(_loadedPath);
                Snapshots.Add(r.snapshots, _settings,
                    string.IsNullOrWhiteSpace(_newSnapName)
                        ? "快照 " + DateTime.Now.ToString("HH:mm:ss") : _newSnapName.Trim(),
                    false, DateTime.Now);
                _store.MarkDirty();
                _snapChanged = false;
                _newSnapName = "";
            }
            GUILayout.EndHorizontal();

            if (list == null || list.Count == 0)
            {
                ui.Info("还没有快照。重置、自动色调这些覆盖性操作之前会自动存一份。");
                return;
            }

            // 新的在上面：刚存的那份最可能马上要用
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var snap = list[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label((snap.auto ? "· " : "● ") + snap.name, ui.Mini);
                GUILayout.FlexibleSpace();
                if (ui.Btn("恢复", 44f))
                {
                    AutoSnapshot("恢复之前");
                    if (snap.settings != null) _settings.CopyFrom(snap.settings);
                    _dirty = true;
                    _snapChanged = false;
                }
                if (ui.Btn("×", 24f)) { list.RemoveAt(i); _store.MarkDirty(); }
                GUILayout.EndHorizontal();
            }

            ui.Info($"{list.Count} / {Snapshots.MaxPerPhoto} 份　满了先挤最老的自动快照");
        }

        // ---------------- LUT ----------------

        void DrawLut(RuntimeGui ui)
        {
            GUILayout.BeginHorizontal();
            if (ui.Btn("导入 .cube…"))
            {
                string p = NativeFileDialog.Open("导入 LUT", "Cube LUT|*.cube|全部|*.*", LastDir());
                if (p != null) LoadLut(p);
            }
            using (new GuiEnabled(_lut != null))
                if (ui.Btn("卸载")) { ReleaseLut(); _dirty = true; }
            GUILayout.EndHorizontal();

            if (_lut != null)
            {
                ui.Info("已载入 " + _lutName);
                ui.Slider("强度", ref _lutAmount, 0f, 1f);
                if (ui.Changed) _dirty = true;
            }

            if (ui.Btn("把当前参数烘成 .cube…")) _pendingAction = BakeLut;
        }

        void LoadLut(string path)
        {
            ReleaseLut();
            _lut = CubeLutIO.Load(path, out string err);
            if (_lut == null) { _status = "LUT 读不了：" + err; return; }
            _lutName = Path.GetFileName(path);
            _lutAmount = 1f;
            _dirty = true;
            _status = "已载入 LUT " + _lutName;
        }

        /// <summary>
        /// 把当前这套参数烘成一张 .cube：拿恒等色带过一遍管线，出来的就是查找表。
        ///
        /// 烘的时候要**关掉几何和风格化**——LUT 只描述颜色映射，
        /// 带上裁剪和暗角的话，套到别的软件里会得到一张歪掉的图。
        /// </summary>
        void BakeLut()
        {
            if (_renderer == null) return;

            string outPath = NativeFileDialog.Save("烘成 .cube", "Cube LUT|*.cube",
                                                   "look.cube", LastDir());
            if (outPath == null) return;

            int size = CubeLutIO.DefaultBakeSize;
            var strip = CubeLutIO.BuildIdentityStrip(size);
            var rt = RenderTexture.GetTemporary(strip.width, strip.height, 0,
                                                RenderTextureFormat.ARGB32,
                                                RenderTextureReadWrite.sRGB);
            var readback = new Texture2D(strip.width, strip.height, TextureFormat.RGBA32, false, true);
            try
            {
                var bake = _settings.Clone();
                bake.ResetCrop();
                bake.cropEnabled = false;
                bake.rotate90 = 0; bake.flipH = false; bake.flipV = false; bake.straighten = 0f;
                bake.vignetteIntensity = 0f; bake.grain = 0f; bake.bloomIntensity = 0f;
                bake.blur = 0f; bake.chromatic = 0f; bake.distortK1 = 0f; bake.distortK2 = 0f;
                bake.clarity = 0f; bake.texture = 0f; bake.sharpen = 0f; bake.denoise = 0f;
                if (bake.maskGroups != null) bake.maskGroups.Clear();

                _renderer.Render(strip, rt, bake, new VideoGradeRenderer.Options());

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0f, 0f, strip.width, strip.height), 0, 0, false);
                readback.Apply(false, false);
                RenderTexture.active = prev;

                _status = CubeLutIO.WriteCube(outPath, readback, size,
                                              Path.GetFileNameWithoutExtension(outPath), out string err)
                    ? "已烘出 " + Path.GetFileName(outPath)
                    : "烘失败：" + err;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(readback);
                UnityEngine.Object.Destroy(strip);
            }
        }

        // ---------------- 色卡校色 ----------------

        void DrawChart(RuntimeGui ui)
        {
            bool on = _tool == Tool.Chart;
            bool now = GUILayout.Toggle(on, "标定四角", ui.Button, GUILayout.Height(20f));
            if (now != on) _tool = now ? Tool.Chart : Tool.None;

            if (_tool == Tool.Chart)
                ui.Info("把四个角拖到色卡的四角上：左上、右上、右下、左下。");

            GUILayout.BeginHorizontal();
            if (ui.Btn("解算矩阵")) _pendingAction = SolveChart;
            if (ui.Btn("重置角点")) _chartCorners = DefaultChartCorners();
            GUILayout.EndHorizontal();

            bool has = _settings.colorMatrix != null && _settings.colorMatrix.Length >= 12;
            using (new GuiEnabled(has))
                if (ui.Btn("清除矩阵"))
                {
                    _settings.colorMatrix = null;
                    _settings.colorMatrixEnabled = false;
                    _dirty = true;
                }

            if (!string.IsNullOrEmpty(_chartStatus)) ui.Info(_chartStatus);
        }

        void DrawChartOverlay()
        {
            var r = _canvas.ImageRect;
            if (r.width <= 0f || Event.current.type != EventType.Repaint) return;

            for (int i = 0; i < 4; i++)
            {
                var p = UvToScreen(_chartCorners[i]);
                GUI.DrawTexture(new Rect(p.x - 5f, p.y - 5f, 10f, 10f), Texture2D.whiteTexture,
                                ScaleMode.StretchToFill, false, 0f,
                                new Color(1f, 0.85f, 0.2f, 0.95f), 0f, 0f);
            }
        }

        Vector2 UvToScreen(Vector2 uv)
        {
            var r = _canvas.ImageRect;
            return new Vector2(r.x + uv.x * r.width, r.y + (1f - uv.y) * r.height);
        }

        void DragChartCorner(Vector2 uv, bool startDrag)
        {
            if (startDrag)
            {
                float best = float.MaxValue;
                _chartDrag = -1;
                for (int i = 0; i < 4; i++)
                {
                    float d = (uv - _chartCorners[i]).sqrMagnitude;
                    if (d < best) { best = d; _chartDrag = i; }
                }
            }
            if (_chartDrag >= 0)
                _chartCorners[_chartDrag] = new Vector2(Mathf.Clamp01(uv.x), Mathf.Clamp01(uv.y));
        }

        void SolveChart()
        {
            if (_full == null) return;

            var measured = ColorCheckerSolver.SamplePatches(_full, _chartCorners);
            if (measured == null) { _chartStatus = "取样失败，检查四角是不是圈住了色卡"; return; }

            var m = ColorCheckerSolver.Solve(measured, out float residual);
            if (m == null) { _chartStatus = "解不出矩阵"; return; }

            AutoSnapshot("色卡校色之前");
            _settings.colorMatrix = m;
            _settings.colorMatrixEnabled = true;
            _dirty = true;
            _chartStatus = $"已解出矩阵，残差 {residual:0.0000}";
        }

        // ---------------- 白平衡吸管 ----------------

        void PickWhiteBalance(Vector2 uv)
        {
            if (_full == null) return;

            int x = Mathf.Clamp(Mathf.RoundToInt(uv.x * (_full.width - 1)), 0, _full.width - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(uv.y * (_full.height - 1)), 0, _full.height - 1);

            Color c;
            try { c = _full.GetPixel(x, y); }
            catch { _status = "这张图读不了像素（可能是压缩格式）"; return; }

            AutoSnapshot("吸白平衡之前");
            WhiteBalancePicker.Solve(new Vector3(c.linear.r, c.linear.g, c.linear.b),
                                     out float temp, out float tint);
            _settings.temperature = temp;
            _settings.tint = tint;
            _dirty = true;
            _status = $"白平衡：色温 {temp:0.000}　色调 {tint:0.000}";
        }

        // ---------------- 导出 ----------------

        static readonly string[] CollisionNames = { "加序号", "覆盖", "跳过" };
        static readonly string[] WmModeNames = { "图片", "文字" };
        Texture2D _watermark;
        string _watermarkLoaded;

        void DrawExport(RuntimeGui ui)
        {
            _export.jpg = GUILayout.Toolbar(_export.jpg ? 0 : 1, new[] { "JPEG", "PNG" }) == 0;
            if (_export.jpg)
            {
                float q = _export.jpgQuality;
                ui.Slider("画质", ref q, 40f, 100f, "0");
                _export.jpgQuality = Mathf.RoundToInt(q);
            }

            float longEdge = _export.maxLongEdge;
            ui.Slider("长边上限", ref longEdge, 0f, 8000f, "0");
            _export.maxLongEdge = Mathf.RoundToInt(longEdge / 50f) * 50;
            if (_export.maxLongEdge == 0) ui.Info("长边 0 = 不限制");
            _export.noUpscale = ui.Toggle2("只缩不放", _export.noUpscale);

            GUILayout.BeginHorizontal();
            GUILayout.Label("命名", ui.Label, GUILayout.Width(48f));
            _export.nameTemplate = GUILayout.TextField(_export.nameTemplate ?? "");
            GUILayout.EndHorizontal();
            ui.Info("{name} {index} {index2} {index3} {total} {date} {time} {rating} {w} {h}");

            _export.collision = ui.Popup2("重名时", _export.collision, CollisionNames);

            // ---- 水印 ----
            _export.watermark = ui.Toggle2("加水印", _export.watermark);
            if (_export.watermark)
            {
                _export.wmMode = GUILayout.Toolbar(_export.wmMode, WmModeNames);
                if (_export.wmMode == 0)
                {
                    if (ui.Btn(string.IsNullOrEmpty(_export.watermarkPath) ? "选水印图片…"
                                                                          : Path.GetFileName(_export.watermarkPath)))
                    {
                        string p = NativeFileDialog.Open("水印图片（建议带透明通道的 PNG）",
                                                         "图片|*.png;*.jpg|全部|*.*", LastDir());
                        if (p != null) { _export.watermarkPath = p; ReleaseWatermark(); }
                    }
                    ui.Slider("大小", ref _export.wmScale, 0.02f, 0.6f);
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("文字", ui.Label, GUILayout.Width(48f));
                    _export.wmText = GUILayout.TextField(_export.wmText ?? "");
                    GUILayout.EndHorizontal();
                    ui.Slider("字号", ref _export.wmFontScale, 0.008f, 0.2f, "0.000");
                    ui.Slider("描边", ref _export.wmOutline, 0f, 0.3f);
                }

                _export.corner = ui.Popup2("位置", _export.corner,
                                           new[] { "左上", "右上", "左下", "右下" });
                ui.Slider("边距", ref _export.wmMargin, 0f, 0.2f);
                ui.Slider("不透明度", ref _export.wmOpacity, 0.05f, 1f);
            }

            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (ui.Btn("导出当前")) _pendingAction = () => ExportBatch(false);
            using (new GuiEnabled(_lib.Selected.Count > 0))
                if (ui.Btn($"导出选中 {_lib.Selected.Count} 张")) _pendingAction = () => ExportBatch(true);
            GUILayout.EndHorizontal();

            if (_full != null)
            {
                var ctx = MakeContext(_loadedPath, 1, 1);
                ui.Info("文件名预览： " + ExportNaming.Expand(_export.nameTemplate, ctx) +
                        _export.Extension);
            }
        }

        void ReleaseWatermark()
        {
            if (_watermark != null) { UnityEngine.Object.Destroy(_watermark); _watermark = null; }
            _watermarkLoaded = null;
        }

        Texture2D Watermark
        {
            get
            {
                if (!_export.watermark || _export.wmMode != 0 ||
                    string.IsNullOrEmpty(_export.watermarkPath)) return null;
                if (_watermark != null && _watermarkLoaded == _export.watermarkPath) return _watermark;

                ReleaseWatermark();
                try
                {
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                    if (t.LoadImage(File.ReadAllBytes(_export.watermarkPath)))
                    {
                        _watermark = t;
                        _watermarkLoaded = _export.watermarkPath;
                    }
                    else UnityEngine.Object.Destroy(t);
                }
                catch { }
                return _watermark;
            }
        }

        ExportContext MakeContext(string path, int index, int total)
        {
            int w = 0, h = 0;
            if (path == _loadedPath && _full != null)
                _settings.OutputSize(_full.width, _full.height, out w, out h);

            var entry = _lib.Current;
            return new ExportContext
            {
                sourceName = Path.GetFileNameWithoutExtension(path ?? ""),
                index = index,
                total = total,
                width = w,
                height = h,
                rating = entry != null ? entry.rating : 0,
                time = DateTime.Now,
            };
        }

        void ExportBatch(bool selected)
        {
            if (_renderer == null) return;

            var targets = new List<string>();
            if (selected) { foreach (var e in _lib.Selected) targets.Add(e.path); }
            else if (_loadedPath != null) targets.Add(_loadedPath);
            if (targets.Count == 0) return;

            targets.Sort(StringComparer.OrdinalIgnoreCase);

            string probe = NativeFileDialog.Save("选导出目录（文件名由模板决定）",
                                                 "任意|*.*", "在这里导出.txt",
                                                 _exportDir ?? LastDir());
            if (probe == null) return;
            _exportDir = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(_exportDir)) return;

            string dir = string.IsNullOrEmpty(_export.subfolder)
                ? _exportDir : Path.Combine(_exportDir, _export.subfolder);
            Directory.CreateDirectory(dir);

            // 光看磁盘不够：同一批里两张算出同名时文件还没落地，第二张会盖掉第一张
            var taken = new HashSet<string>();
            int ok = 0, skipped = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                string src = targets[i];
                bool isCurrent = src == _loadedPath;

                Texture2D tex = isCurrent ? _full : LoadTexture(src, preferPreview: false);
                if (tex == null) { skipped++; continue; }

                // 批量导出别的图时要用它自己的参数，不是当前这张的
                var st = _settings;
                if (!isCurrent)
                {
                    var rec = _store.Get(src);
                    st = rec != null && rec.hasSettings && rec.settings != null ? rec.settings : _settings;
                }

                var ctx = MakeContext(src, i + 1, targets.Count);
                st.OutputSize(tex.width, tex.height, out int gw, out int gh);
                ExportNaming.ComputeSize(gw, gh, _export, out int ew, out int eh);
                ctx.width = ew; ctx.height = eh;

                string baseName = ExportNaming.Expand(_export.nameTemplate, ctx);
                string outPath = ExportNaming.Resolve(dir, baseName, _export.Extension,
                                                      _export.collision, taken, null);
                if (outPath == null) { skipped++; if (!isCurrent) UnityEngine.Object.Destroy(tex); continue; }

                if (ExportOne(isCurrent ? GradeSource : tex, st, ew, eh, outPath)) ok++;
                else skipped++;

                if (!isCurrent) UnityEngine.Object.Destroy(tex);
            }

            _status = $"导出完成：{ok} 张" + (skipped > 0 ? $"，跳过 {skipped}" : "");
        }

        bool ExportOne(Texture src, VideoGradeSettings st, int ew, int eh, string outPath)
        {
            st.OutputSize(src.width, src.height, out int gw, out int gh);

            var graded = RenderTexture.GetTemporary(gw, gh, 0, RenderTextureFormat.ARGB32,
                                                    RenderTextureReadWrite.sRGB);
            RenderTexture scaled = null;
            var readback = new Texture2D(ew, eh, TextureFormat.RGBA32, false, false);

            try
            {
                _renderer.Render(src, graded, st, new VideoGradeRenderer.Options
                {
                    lut = _lut,
                    lutAmount = _lutAmount,
                });

                RenderTexture target = graded;
                if (ew != gw || eh != gh)
                {
                    scaled = RenderTexture.GetTemporary(ew, eh, 0, RenderTextureFormat.ARGB32,
                                                        RenderTextureReadWrite.sRGB);
                    Graphics.Blit(graded, scaled);
                    target = scaled;
                }

                // 水印最后贴，在缩放之后——不然它会跟着一起被缩放和调色
                if (_export.watermark) StampWatermark(target);

                var prev = RenderTexture.active;
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0f, 0f, ew, eh), 0, 0, false);
                readback.Apply(false, false);
                RenderTexture.active = prev;

                File.WriteAllBytes(outPath, _export.jpg
                    ? readback.EncodeToJPG(_export.jpgQuality)
                    : readback.EncodeToPNG());
                return true;
            }
            catch (Exception e)
            {
                _status = "导出失败：" + e.Message;
                return false;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(graded);
                if (scaled != null) RenderTexture.ReleaseTemporary(scaled);
                UnityEngine.Object.Destroy(readback);
            }
        }

        void StampWatermark(RenderTexture target)
        {
            if (_export.wmMode == 1) StampText(target);
            else StampImage(target);
        }

        void StampImage(RenderTexture target)
        {
            var wm = Watermark;
            if (wm == null) return;

            float longEdge = Mathf.Max(target.width, target.height);
            float w = longEdge * Mathf.Clamp(_export.wmScale, 0.01f, 1f);
            float h = w * wm.height / Mathf.Max(wm.width, 1);

            var r = ExportNaming.WatermarkRect(target.width, target.height, w, h,
                                               _export.corner, _export.wmMargin);

            var prev = RenderTexture.active;
            RenderTexture.active = target;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, target.width, target.height, 0f);
            Graphics.DrawTexture(r, wm, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0,
                                 new Color(1f, 1f, 1f, Mathf.Clamp01(_export.wmOpacity)));
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        void StampText(RenderTexture target)
        {
            if (string.IsNullOrWhiteSpace(_export.wmText)) return;

            var font = _wmFont != null ? _wmFont : TextStamp.DefaultFont;
            if (font == null) return;

            float longEdge = Mathf.Max(target.width, target.height);
            int px = Mathf.Clamp(
                Mathf.RoundToInt(longEdge * Mathf.Clamp(_export.wmFontScale, 0.004f, 0.5f)), 8, 512);

            var lay = TextStamp.Measure(font, _export.wmText, px);
            if (lay.Empty) return;

            float outline = px * Mathf.Clamp01(_export.wmOutline);

            // 描边是往外扩的，量外框时要算进去，否则贴着角的那半圈会被裁掉
            var r = ExportNaming.WatermarkRect(target.width, target.height,
                                               lay.size.x + outline * 2f, lay.size.y + outline * 2f,
                                               _export.corner, _export.wmMargin);

            var c = _export.wmColor;
            c.a *= Mathf.Clamp01(_export.wmOpacity);

            var prev = RenderTexture.active;
            RenderTexture.active = target;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, target.width, target.height, 0f);
            TextStamp.Draw(lay, font, new Vector2(r.x + outline, r.y + outline), c, outline);
            GL.PopMatrix();
            RenderTexture.active = prev;
        }
    }
}
