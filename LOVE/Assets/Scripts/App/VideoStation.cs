using System;
using System.IO;
using Love.Tools;
using Love.Video;
using UnityEngine;

namespace Love.App
{
    /// <summary>
    /// 视频台。导入视频、逐帧查看、调色、导出。
    ///
    /// 预览走常驻 ffmpeg 解码流（<see cref="VideoFrameStream"/>），
    /// 不是「要哪一帧就起一个进程抓一帧」——实测 1080p 上每帧起进程要 145~180ms，
    /// 常驻进程顺序读只要 6.4ms，差二十倍，前者连 7fps 都跑不到。
    /// </summary>
    public class VideoStation : IStation
    {
        readonly Material _mat;
        readonly RuntimeGui _ui;
        VideoGradeRenderer _renderer;

        readonly VideoGradeSettings _settings = new VideoGradeSettings();
        readonly Canvas2D _canvas = new Canvas2D();

        string _path;
        int _srcW, _srcH;
        double _fps = 25.0;
        double _duration;
        long _frameCount;

        VideoFrameStream _stream;
        byte[] _buf;
        Texture2D _frameTex;
        RenderTexture _preview;

        long _current = -1;
        bool _dirty;
        bool _bypass;
        string _status = "导入一段视频（mp4 / mov / mkv…）";

        // 预览的解码分辨率。全分辨率解码在 4K 上跟不上，而调色看的是整体关系不是像素
        int _previewH = 720;

        // 播放
        bool _playing;
        double _nextFrameTime;

        // 拖时间轴时只登记「想去哪一帧」，在 Tick 里一拍最多兑现一次
        long _wantFrame = -1;
        double _lastSeek;

        // 导出
        bool _exporting;
        long _exportFrame;
        long _exportEnd;
        System.Diagnostics.Process _encoder;
        Stream _encoderIn;
        VideoFrameStream _exportStream;
        byte[] _exportBuf;
        Texture2D _exportRead;
        Texture2D _exportOut;      // 回读用，复用一张：几千帧就是几千次分配
        byte[] _exportOutBytes;
        int _exportW, _exportH;
        string _exportPath;

        public VideoStation(Material mat, RuntimeGui ui)
        {
            _mat = mat;
            _ui = ui;
            _settings.Reset();
            if (mat != null) _renderer = new VideoGradeRenderer(mat);
        }

        public string Status => _status;
        public bool HasSource => _frameTex != null;
        public Vector2Int SourceSize => new Vector2Int(_srcW, _srcH);
        public Texture Preview => _preview;
        public VideoGradeSettings Settings => _settings;
        public void MarkDirty() => _dirty = true;

        public void OnHide() => _playing = false;

        public void Dispose()
        {
            StopExport(false);
            _stream?.Dispose();
            _stream = null;
            _renderer?.Dispose();
            _renderer = null;
            ReleaseTextures();
        }

        void ReleaseTextures()
        {
            if (_preview != null) { _preview.Release(); UnityEngine.Object.Destroy(_preview); _preview = null; }
            if (_frameTex != null) { UnityEngine.Object.Destroy(_frameTex); _frameTex = null; }
        }

        // ---------------- 载入 ----------------

        public void Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            if (!FfmpegTool.Available)
            {
                _status = "没找到 ffmpeg。视频台的预览和导出都要它。";
                return;
            }

            if (!FfmpegTool.Probe(path, out int w, out int h, out double fps, out double dur))
            {
                _status = "读不出这段视频的信息：" + Path.GetFileName(path);
                return;
            }

            _stream?.Dispose();
            _stream = null;
            ReleaseTextures();

            _path = path;
            _srcW = w; _srcH = h;
            _fps = fps > 0.01 ? fps : 25.0;
            _duration = dur;
            _frameCount = Math.Max(1, (long)Math.Round(dur * _fps));

            OpenStream();
            _current = -1;
            _wantFrame = 0;
            _settings.Reset();
            _canvas.Fit();
            _status = $"{Path.GetFileName(path)}　{w}×{h}　{_fps:0.##}fps　{dur:0.0}s　{_frameCount} 帧";
        }

        void OpenStream()
        {
            int ph = Mathf.Clamp(_previewH, 180, Mathf.Max(180, _srcH));
            int pw = Mathf.Max(2, Mathf.RoundToInt(_srcW * (ph / (float)_srcH)));
            pw -= pw & 1;   // ffmpeg 的 scale 要偶数

            _stream?.Dispose();
            _stream = new VideoFrameStream(_path, _fps, pw, ph);
            _buf = new byte[(long)pw * ph * 4];

            if (_frameTex != null) UnityEngine.Object.Destroy(_frameTex);
            _frameTex = new Texture2D(pw, ph, TextureFormat.RGBA32, false, false);
        }

        // ---------------- 每帧 ----------------

        public void Tick()
        {
            if (_exporting) { ExportStep(); return; }

            if (_stream == null) return;

            // 播放推进。不「补齐」落后的帧——按累积时间一次步进多帧，
            // 会变成「越慢越要多解码」的正反馈，一旦跟不上就再也追不回来
            if (_playing && Time.realtimeSinceStartupAsDouble >= _nextFrameTime)
            {
                _nextFrameTime = Time.realtimeSinceStartupAsDouble + 1.0 / _fps;
                long next = _current + 1;
                if (next >= _frameCount) { _playing = false; next = _frameCount - 1; }
                _wantFrame = next;
            }

            // 一拍最多兑现一个定位请求，而且只兑现最后那个。
            // 拖时间轴一次会甩出几十个 MouseDrag，每个都真去定位的话请求会越堆越多
            if (_wantFrame >= 0 && Time.realtimeSinceStartupAsDouble - _lastSeek > 0.016)
            {
                long f = Mathf.Clamp((int)_wantFrame, 0, (int)Math.Max(0, _frameCount - 1));
                _wantFrame = -1;
                _lastSeek = Time.realtimeSinceStartupAsDouble;

                if (f != _current && _stream.TryGet(f, _buf))
                {
                    _current = f;
                    _frameTex.LoadRawTextureData(_buf);
                    _frameTex.Apply(false, false);
                    _dirty = true;
                }
            }

            if (_dirty && _frameTex != null && _renderer != null) Render();
        }

        void Render()
        {
            _settings.OutputSize(_frameTex.width, _frameTex.height, out int w, out int h);

            if (_preview == null || _preview.width != w || _preview.height != h)
            {
                if (_preview != null) { _preview.Release(); UnityEngine.Object.Destroy(_preview); }
                _preview = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
                                             RenderTextureReadWrite.sRGB);
                _preview.Create();
            }

            _renderer.Render(_frameTex, _preview, _settings,
                             new VideoGradeRenderer.Options { bypass = _bypass });
            _dirty = false;
        }

        // ---------------- 界面 ----------------

        public void DrawCanvas(Rect area)
        {
            const float BarH = 34f;
            var view = new Rect(area.x, area.y, area.width, Mathf.Max(40f, area.height - BarH));
            _canvas.Draw(view, _preview, _ui, "导入一段视频");

            if (_stream == null) return;
            DrawTimeline(new Rect(area.x + 8f, area.yMax - BarH + 6f, area.width - 16f, BarH - 12f));
        }

        void DrawTimeline(Rect r)
        {
            float t = _frameCount > 1 ? _current / (float)(_frameCount - 1) : 0f;
            float nt = GUI.HorizontalSlider(r, t, 0f, 1f);

            if (!Mathf.Approximately(nt, t))
            {
                // 这里只登记，不去解码。真去定位放在 Tick 里，一拍最多一次
                _wantFrame = (long)Mathf.Round(nt * Mathf.Max(1, _frameCount - 1));
                _playing = false;
            }

            double sec = _current / Math.Max(_fps, 0.001);
            GUI.Label(new Rect(r.x, r.yMax - 2f, r.width, 16f),
                      $"{sec:0.00}s / {_duration:0.00}s　第 {Math.Max(_current, 0)} / {_frameCount - 1} 帧",
                      _ui.Mini);
        }

        public void DrawPanel(RuntimeGui ui)
        {
            GUILayout.Space(6f);

            if (!FfmpegTool.Available)
            {
                ui.Info("没找到 ffmpeg。视频台的预览和导出都靠它，装一个再来。");
                if (ui.Btn("手动指定 ffmpeg.exe…"))
                {
                    string p = NativeFileDialog.Open("找到 ffmpeg.exe", "ffmpeg|ffmpeg.exe|全部|*.*");
                    if (p != null) { FfmpegTool.OverridePath = p; FfmpegTool.Rescan(); }
                }
                return;
            }

            GUILayout.BeginHorizontal();
            if (ui.Btn("导入视频…"))
            {
                string p = NativeFileDialog.Open("导入视频",
                    "视频|*.mp4;*.mov;*.mkv;*.avi;*.webm|全部|*.*",
                    _path != null ? Path.GetDirectoryName(_path) : null);
                if (p != null) Load(p);
            }
            using (new GuiEnabled(_stream != null && !_exporting))
                if (ui.Btn("导出…")) BeginExport();
            GUILayout.EndHorizontal();

            if (_exporting)
            {
                var r = GUILayoutUtility.GetRect(0f, 18f, GUILayout.ExpandWidth(true));
                float p = _exportEnd > 0 ? _exportFrame / (float)_exportEnd : 0f;
                ui.Info($"导出中 {_exportFrame}/{_exportEnd}　{p * 100f:0}%");
                if (ui.Btn("取消导出")) StopExport(false);
                return;
            }

            using (new GuiEnabled(_stream != null))
            {
                GUILayout.BeginHorizontal();
                if (ui.Btn(_playing ? "暂停" : "播放"))
                {
                    _playing = !_playing;
                    _nextFrameTime = Time.realtimeSinceStartupAsDouble;
                }
                if (ui.Btn("◀", 32f)) { _wantFrame = Math.Max(0, _current - 1); _playing = false; }
                if (ui.Btn("▶", 32f)) { _wantFrame = _current + 1; _playing = false; }
                if (ui.Btn("回到开头")) { _wantFrame = 0; _playing = false; }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                bool bp = GUILayout.Toggle(_bypass, "看原片", ui.Button, GUILayout.Height(20f));
                if (bp != _bypass) { _bypass = bp; _dirty = true; }
                if (ui.Btn("适应")) _canvas.Fit();
                if (ui.Btn("1:1")) _canvas.OneToOne();
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("预览高度", ui.Label, GUILayout.Width(60f));
                int ph = Mathf.RoundToInt(GUILayout.HorizontalSlider(_previewH, 240f, 1440f));
                ph = (ph / 60) * 60;
                GUILayout.Label(ph + "p", ui.Mini, GUILayout.Width(44f));
                GUILayout.EndHorizontal();
                if (ph != _previewH && _stream != null)
                {
                    _previewH = ph;
                    OpenStream();
                    _current = -1;
                    _wantFrame = 0;
                }

                if (ui.Btn("重置参数")) { _settings.Reset(); _dirty = true; }
            }

            ui.Info("预览是降分辨率解码的，导出走原始分辨率。\n" +
                    "调色看的是整体关系，不需要在预览里就跑满像素。");
        }

        // ---------------- 导出 ----------------

        void BeginExport()
        {
            if (_path == null) return;

            string name = Path.GetFileNameWithoutExtension(_path) + "_graded.mp4";
            string outPath = NativeFileDialog.Save("导出视频", "MP4|*.mp4", name,
                                                   Path.GetDirectoryName(_path));
            if (outPath == null) return;

            try
            {
                // 导出走原始分辨率，另开一条流，不动预览那条
                _exportStream = new VideoFrameStream(_path, _fps, _srcW, _srcH);
                _exportBuf = new byte[(long)_srcW * _srcH * 4];
                _exportRead = new Texture2D(_srcW, _srcH, TextureFormat.RGBA32, false, false);

                _settings.OutputSize(_srcW, _srcH, out int ow, out int oh);
                // H.264 要偶数尺寸，裁剪之后很容易变成奇数
                ow -= ow & 1; oh -= oh & 1;
                _exportW = ow; _exportH = oh;

                // 导出中途改参数会改变输出尺寸，而编码器的尺寸是开头就定死的。
                // 所以整轮导出用开头这一组，不跟着参数走
                _exportOut = new Texture2D(ow, oh, TextureFormat.RGBA32, false, false);
                _exportOutBytes = new byte[(long)ow * oh * 4];

                _encoder = FfmpegTool.StartEncoder(outPath, ow, oh, _fps, 18, _path, 0, _duration);
                _encoderIn = _encoder.StandardInput.BaseStream;

                // 子进程的 stderr 必须有人读，否则管道写满时 ffmpeg 会卡死在那儿不退出
                _encoder.ErrorDataReceived += (_, __) => { };
                _encoder.BeginErrorReadLine();

                _exportPath = outPath;
                _exportFrame = 0;
                _exportEnd = _frameCount;
                _exporting = true;
                _playing = false;
                _status = "开始导出 " + Path.GetFileName(outPath);
            }
            catch (Exception e)
            {
                _status = "导出起不来：" + e.Message;
                StopExport(false);
            }
        }

        /// <summary>
        /// 一帧一步。
        ///
        /// 一口气导完的话窗口整个假死，几分钟里连进度都看不到。
        /// 分开走的代价是每帧多一次 Update 调度，相对解码和编码可以忽略。
        /// </summary>
        void ExportStep()
        {
            if (_exportStream == null || _encoderIn == null || _renderer == null)
            { StopExport(false); return; }

            try
            {
                if (_exportFrame >= _exportEnd) { StopExport(true); return; }

                if (!_exportStream.TryGet(_exportFrame, _exportBuf)) { StopExport(true); return; }

                _exportRead.LoadRawTextureData(_exportBuf);
                _exportRead.Apply(false, false);

                var rt = RenderTexture.GetTemporary(_exportW, _exportH, 0,
                                                    RenderTextureFormat.ARGB32,
                                                    RenderTextureReadWrite.sRGB);
                try
                {
                    _renderer.Render(_exportRead, rt, _settings, new VideoGradeRenderer.Options());

                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    _exportOut.ReadPixels(new Rect(0f, 0f, _exportW, _exportH), 0, 0, false);
                    _exportOut.Apply(false, false);
                    RenderTexture.active = prev;

                    _exportOut.GetRawTextureData<byte>().CopyTo(_exportOutBytes);
                    _encoderIn.Write(_exportOutBytes, 0, _exportOutBytes.Length);
                }
                finally
                {
                    RenderTexture.ReleaseTemporary(rt);
                }

                _exportFrame++;
                _status = $"导出中 {_exportFrame}/{_exportEnd}";
            }
            catch (Exception e)
            {
                _status = "导出中断：" + e.Message;
                StopExport(false);
            }
        }

        void StopExport(bool finished)
        {
            if (_encoderIn != null)
            {
                try { _encoderIn.Flush(); _encoderIn.Close(); } catch { }
                _encoderIn = null;
            }
            if (_encoder != null)
            {
                try { if (finished) _encoder.WaitForExit(15000); else _encoder.Kill(); } catch { }
                _encoder.Dispose();
                _encoder = null;
            }

            _exportStream?.Dispose();
            _exportStream = null;
            _exportBuf = null;
            if (_exportRead != null) { UnityEngine.Object.Destroy(_exportRead); _exportRead = null; }
            if (_exportOut != null) { UnityEngine.Object.Destroy(_exportOut); _exportOut = null; }
            _exportOutBytes = null;

            if (_exporting)
                _status = finished ? "已导出 " + Path.GetFileName(_exportPath ?? "") : "导出已取消";
            _exporting = false;
        }
    }
}
