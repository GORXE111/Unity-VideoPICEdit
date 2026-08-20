using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Love.EditorTools
{
    /// <summary>
    /// 常驻的 ffmpeg 解码进程，顺序吐裸 RGBA 帧。
    ///
    /// 为什么不是「要哪一帧就起一个进程抓一帧」：实测在 1080p 上，
    /// 每帧起进程要 145~180ms，而常驻进程顺序读只要 7.5ms——差二十倍。
    /// 前者连 7fps 都跑不到，后者能跑到 134fps。
    ///
    /// 于是定位策略变成：能顺着读就顺着读，往前跳一点就读掉扔掉，
    /// 跳得远或者往回跳才重开进程。
    /// </summary>
    public class VideoFrameStream : IDisposable
    {
        /// <summary>
        /// 往前跳多少帧之内值得「读掉扔掉」而不是重开进程。
        /// 顺序读一帧约 7.5ms，重开一次约 150ms，所以二十帧上下是分界。
        /// </summary>
        const int SkipLimit = 24;

        readonly string _src;
        readonly double _fps;
        readonly int _w, _h;

        Process _proc;
        Stream _stdout;
        long _next = -1;          // 下一次 ReadInto 会拿到的帧号，-1 表示还没开
        bool _ended;

        public int Width => _w;
        public int Height => _h;
        public long FrameSize => (long)_w * _h * 4;

        public VideoFrameStream(string src, double fps, int width, int height)
        {
            _src = src;
            _fps = Math.Max(fps, 1.0);
            _w = Mathf.Max(2, width);
            _h = Mathf.Max(2, height);
        }

        /// <summary>
        /// 取指定帧。返回 false 表示到片尾或解码出错。
        /// <paramref name="buf"/> 长度必须不小于 <see cref="FrameSize"/>。
        /// </summary>
        public bool TryGet(long frame, byte[] buf)
        {
            if (frame < 0) frame = 0;
            if (buf == null || buf.LongLength < FrameSize) return false;

            // 往回跳、跳太远、还没开过、或者上一次已经读到尾了，都得重开
            bool needReopen = _proc == null || _ended || _next < 0
                              || frame < _next || frame > _next + SkipLimit;

            if (needReopen && !Reopen(frame)) return false;

            // 顺着读掉中间那几帧。这比重开进程便宜得多
            while (_next < frame)
            {
                if (!ReadOne(buf)) return false;
            }

            return ReadOne(buf);
        }

        bool Reopen(long frame)
        {
            Close();

            // -ss 放在 -i 前面走快速定位：先跳到最近的关键帧再解到目标时刻。
            // 现代 ffmpeg 这条路是帧精确的，而且比放在后面快一个数量级。
            // 取帧中心的时刻而不是边界，免得浮点误差把定位甩到隔壁帧
            double t = (frame + 0.5) / _fps;

            try
            {
                _proc = FfmpegTool.StartStream(_src, frame > 0 ? t : 0.0, _w, _h);
                _stdout = _proc.StandardOutput.BaseStream;
                // stderr 没人读的话，管道写满时 ffmpeg 会卡死在那儿不退出
                _proc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) Debug.LogWarning("[视频台/解码] " + e.Data);
                };
                _proc.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                Debug.LogError("[视频台] 启动解码进程失败：" + e.Message);
                Close();
                return false;
            }

            _next = frame;
            _ended = false;
            return true;
        }

        bool ReadOne(byte[] buf)
        {
            if (_stdout == null) { _ended = true; return false; }

            int need = (int)FrameSize, got = 0;
            while (got < need)
            {
                int n;
                // 管道一次不一定给满一帧，要循环读到够
                try { n = _stdout.Read(buf, got, need - got); }
                catch { _ended = true; return false; }
                if (n <= 0) { _ended = true; return false; }   // EOF
                got += n;
            }

            _next++;
            return true;
        }

        void Close()
        {
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
            _stdout = null;
            _next = -1;
        }

        public void Dispose() => Close();
    }
}
