using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// ffmpeg 的定位与命令行拼装。
    ///
    /// 视频台的导出走「ffmpeg 解码 → Unity 调色 → ffmpeg 编码」，两头都是管道，
    /// 中间不落任何临时文件。这样做的另一个好处是导出完全不依赖 VideoPlayer——
    /// 编辑器模式下逐帧步进能不能稳定触发事件是个未知数，而导出这一步必须确定。
    /// </summary>
    public static class FfmpegTool
    {
        const string PrefPath = "Love.ffmpegPath";

        static string _cached;
        static bool _searched;

        /// <summary>手动指定的路径。留空表示自动探测。</summary>
        public static string OverridePath
        {
            get => EditorPrefs.GetString(PrefPath, "");
            set { EditorPrefs.SetString(PrefPath, value ?? ""); _searched = false; _cached = null; }
        }

        public static string Path
        {
            get
            {
                if (_searched) return _cached;
                _searched = true;
                _cached = Locate();
                return _cached;
            }
        }

        public static bool Available => !string.IsNullOrEmpty(Path);

        public static void Rescan() { _searched = false; _cached = null; }

        static string Locate()
        {
            string over = OverridePath;
            if (!string.IsNullOrEmpty(over) && File.Exists(over)) return over;

            string exe = Application.platform == RuntimePlatform.WindowsEditor ? "ffmpeg.exe" : "ffmpeg";

            // 1) PATH 里逐个目录找。比起 Process.Start 试跑一次，这样不会在没装时
            //    弹出一个「找不到程序」的系统对话框
            string env = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in env.Split(System.IO.Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string p;
                try { p = System.IO.Path.Combine(dir.Trim(), exe); }
                catch { continue; }
                if (File.Exists(p)) return p;
            }

            // 2) WinGet 装的 ffmpeg 不进 PATH，得去它的包目录里翻
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string pkgs = System.IO.Path.Combine(local, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(pkgs))
                {
                    var hits = Directory.GetFiles(pkgs, exe, SearchOption.AllDirectories);
                    if (hits.Length > 0) return hits[0];
                }
            }
            catch { /* 权限或路径问题，当作没找到 */ }

            return null;
        }

        /// <summary>
        /// 解码进程：把源视频的指定区间吐成裸 RGBA 帧到 stdout。
        ///
        /// 加 vflip 是因为 ffmpeg 的裸帧第一行是画面顶部，而 Unity 的贴图数据
        /// 第一行是底部。不翻的话画面在管线里是倒着的——暗角这类中心对称的效果
        /// 看不出来，但裁剪、渐变窗口、镜头畸变会全部上下颠倒。
        /// </summary>
        public static Process StartDecoder(string src, double startSeconds, double durationSeconds)
        {
            string args =
                "-hide_banner -loglevel error " +
                (startSeconds > 0.0005 ? $"-ss {startSeconds.ToString(Inv)} " : "") +
                $"-i \"{src}\" " +
                (durationSeconds > 0 ? $"-t {durationSeconds.ToString(Inv)} " : "") +
                "-vf vflip -f rawvideo -pix_fmt rgba -";

            return Start(args, redirectOut: true, redirectIn: false);
        }

        /// <summary>
        /// 编码进程：从 stdin 收裸 RGBA 帧，写成 H.264 的 mp4。
        /// <paramref name="audioFrom"/> 非空时把那个文件的音轨原样复制过来。
        /// </summary>
        public static Process StartEncoder(string outPath, int w, int h, double fps, int crf,
                                           string audioFrom, double audioStart, double audioDuration)
        {
            bool withAudio = !string.IsNullOrEmpty(audioFrom);

            string args =
                "-y -hide_banner -loglevel error " +
                $"-f rawvideo -pix_fmt rgba -video_size {w}x{h} -framerate {fps.ToString(Inv)} -i - ";

            if (withAudio)
            {
                if (audioStart > 0.0005) args += $"-ss {audioStart.ToString(Inv)} ";
                if (audioDuration > 0) args += $"-t {audioDuration.ToString(Inv)} ";
                args += $"-i \"{audioFrom}\" -map 0:v:0 -map 1:a:0? ";
            }
            else args += "-map 0:v:0 ";

            // 回读拿到的数据仍是自下而上的，再翻一次转回正
            args += $"-vf vflip -c:v libx264 -preset medium -crf {crf} -pix_fmt yuv420p ";

            // 音轨直接复制，不重编码。源是 AAC 的话 mp4 能直接装下
            if (withAudio) args += "-c:a copy -shortest ";

            args += $"\"{outPath}\"";

            return Start(args, redirectOut: false, redirectIn: true);
        }

        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        static Process Start(string args, bool redirectOut, bool redirectIn)
        {
            var psi = new ProcessStartInfo(Path, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = redirectOut,
                RedirectStandardInput = redirectIn,
                RedirectStandardError = true,
            };
            var p = new Process { StartInfo = psi };
            p.Start();
            return p;
        }

        /// <summary>
        /// 抓单独一帧，返回裸 RGBA 字节（已经翻成 Unity 的自下而上）。
        ///
        /// -ss 放在 -i 前面走的是快速定位：先跳到最近的关键帧再解到目标时刻。
        /// 现代 ffmpeg 这条路是帧精确的，而且比放在后面快一个数量级。
        /// </summary>
        public static bool GrabFrame(string src, double seconds, int w, int h, byte[] into)
        {
            if (!Available || into == null || into.Length < (long)w * h * 4) return false;

            Process p = null;
            try
            {
                string args = "-hide_banner -loglevel error " +
                              (seconds > 0.0005 ? $"-ss {seconds.ToString(Inv)} " : "") +
                              $"-i \"{src}\" -frames:v 1 -vf vflip -f rawvideo -pix_fmt rgba -";
                p = Start(args, redirectOut: true, redirectIn: false);

                var stream = p.StandardOutput.BaseStream;
                int need = w * h * 4, got = 0;
                while (got < need)
                {
                    int n = stream.Read(into, got, need - got);
                    if (n <= 0) break;      // EOF：多半是时间点越界了
                    got += n;
                }

                // stderr 不读干净的话进程可能卡在那里不退出
                p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return got == need;
            }
            catch { return false; }
            finally { try { p?.Dispose(); } catch { } }
        }

        /// <summary>
        /// 探一下源视频的宽高、帧率和总帧数。用 ffprobe，没有就退回 ffmpeg 同目录找。
        /// 取不到时返回 false，调用方该退回 VideoPlayer 报的那套数字。
        /// </summary>
        public static bool Probe(string src, out int width, out int height, out double fps, out double duration)
        {
            width = height = 0; fps = 0; duration = 0;
            if (!Available) return false;

            string dir = System.IO.Path.GetDirectoryName(Path) ?? "";
            string probe = System.IO.Path.Combine(dir,
                Application.platform == RuntimePlatform.WindowsEditor ? "ffprobe.exe" : "ffprobe");
            if (!File.Exists(probe)) return false;

            try
            {
                var psi = new ProcessStartInfo(probe,
                    "-v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate " +
                    $"-show_entries format=duration -of default=noprint_wrappers=1 \"{src}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                };
                using (var p = Process.Start(psi))
                {
                    string all = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);

                    foreach (var line in all.Split('\n'))
                    {
                        var kv = line.Trim().Split('=');
                        if (kv.Length != 2) continue;
                        switch (kv[0])
                        {
                            case "width": int.TryParse(kv[1], out width); break;
                            case "height": int.TryParse(kv[1], out height); break;
                            case "duration": double.TryParse(kv[1], System.Globalization.NumberStyles.Float, Inv, out duration); break;
                            case "avg_frame_rate":
                                // 形如 "30000/1001"，直接除
                                var f = kv[1].Split('/');
                                if (f.Length == 2 &&
                                    double.TryParse(f[0], System.Globalization.NumberStyles.Float, Inv, out double n) &&
                                    double.TryParse(f[1], System.Globalization.NumberStyles.Float, Inv, out double dd) &&
                                    dd > 0) fps = n / dd;
                                break;
                        }
                    }
                }
            }
            catch { return false; }

            return width > 0 && height > 0 && fps > 0.01;
        }
    }
}
