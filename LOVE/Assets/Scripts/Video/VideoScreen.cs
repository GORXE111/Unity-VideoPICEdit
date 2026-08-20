using System;
using System.Collections;
using System.IO;
using Love.Audio;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace Love.Video
{
    /// <summary>
    /// 视频播放层。
    ///
    /// 关键设计：使用 RenderTexture 输出而不是 CameraNearPlane。
    /// 因为 RenderTexture 在视频播放结束后仍然保留最后一次渲染的画面，
    /// 配合“播完不 Stop 只 Pause”，就能天然实现「画面定格在视频最后一帧」。
    /// </summary>
    [DisallowMultipleComponent]
    public class VideoScreen : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("全屏显示视频的 RawImage")]
        public RawImage screen;
        [Tooltip("挂在 RawImage 上的 AspectRatioFitter，用来保持视频比例")]
        public AspectRatioFitter aspectFitter;

        [Header("路径")]
        [Tooltip("视频所在目录，相对于 StreamingAssets")]
        public string videoFolder = "Videos";

        [Header("播放设置")]
        [Tooltip("丢帧追赶。开启后卡顿时会跳帧保持音画同步，剧情视频建议开")]
        public bool skipOnDrop = true;
        [Tooltip("画面比例适配方式。FitInParent = 加黑边不裁切；EnvelopeParent = 铺满并裁切")]
        public AspectRatioFitter.AspectMode aspectMode = AspectRatioFitter.AspectMode.FitInParent;

        VideoPlayer _player;
        AudioSource _audio;
        RenderTexture _rt;
        Action _onEnd;
        bool _reachedEnd;
        bool _hasError;
        string _currentFile;

        /// <summary>当前播放进度（秒）。</summary>
        public double Time => _player != null ? _player.time : 0d;

        /// <summary>视频总长度（秒），未准备好时为 0。</summary>
        public double Length => (_player != null && _player.frameCount > 0 && _player.frameRate > 0f)
            ? _player.frameCount / _player.frameRate
            : 0d;

        /// <summary>视频是否已经播到结尾（此时画面定格在最后一帧）。</summary>
        public bool ReachedEnd => _reachedEnd;

        /// <summary>本次播放是否出错（文件缺失、解码失败等）。</summary>
        public bool HasError => _hasError;

        /// <summary>是否正在播放。</summary>
        public bool IsPlaying => _player != null && _player.isPlaying;

        /// <summary>是否已经准备好并开始出画。</summary>
        public bool IsPrepared { get; private set; }

        /// <summary>
        /// 视频原始输出的 RenderTexture（未经调色）。
        /// VideoPostProcessor 拿它当输入，处理完再自己覆盖 RawImage 的贴图。
        /// </summary>
        public RenderTexture SourceTexture => _rt;

        void Awake()
        {
            // 引用为空时从 UI 面板预制体上自动取，这样面板换成预制体实例后不用手动连线
            if (screen == null || aspectFitter == null)
            {
                var ui = Love.UI.GameplayUIRoot.Find();
                if (ui != null)
                {
                    if (screen == null) screen = ui.videoImage;
                    if (aspectFitter == null) aspectFitter = ui.videoAspectFitter;
                }
            }

            _player = gameObject.GetComponent<VideoPlayer>();
            if (_player == null) _player = gameObject.AddComponent<VideoPlayer>();

            _audio = gameObject.GetComponent<AudioSource>();
            if (_audio == null) _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0f;

            _player.playOnAwake = false;
            _player.isLooping = false;
            _player.waitForFirstFrame = true;
            _player.skipOnDrop = skipOnDrop;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.source = VideoSource.Url;
            _player.audioOutputMode = VideoAudioOutputMode.AudioSource;
            // 音轨路由必须在 Prepare 之前设置好，否则视频声音不会走我们的 AudioSource
            _player.controlledAudioTrackCount = 1;
            _player.EnableAudioTrack(0, true);
            _player.SetTargetAudioSource(0, _audio);

            _player.prepareCompleted += OnPrepareCompleted;
            _player.loopPointReached += OnLoopPointReached;
            _player.errorReceived += OnErrorReceived;

            if (aspectFitter != null) aspectFitter.aspectMode = aspectMode;
        }

        void OnEnable() => SubscribeVolume();

        // Awake / OnEnable 之间的执行顺序不保证，AudioManager 可能还没 Awake 完。
        // Start 一定在所有 Awake 之后，所以这里再订阅一次兜底（订阅前先退订，不会重复）。
        void Start() => SubscribeVolume();

        void SubscribeVolume()
        {
            if (AudioManager.Instance == null) return;
            AudioManager.Instance.OnVolumeChanged -= SyncVolume;
            AudioManager.Instance.OnVolumeChanged += SyncVolume;
            SyncVolume();
        }

        void OnDisable()
        {
            if (AudioManager.Instance != null)
                AudioManager.Instance.OnVolumeChanged -= SyncVolume;
        }

        void OnDestroy()
        {
            if (_player != null)
            {
                _player.prepareCompleted -= OnPrepareCompleted;
                _player.loopPointReached -= OnLoopPointReached;
                _player.errorReceived -= OnErrorReceived;
            }
            ReleaseRenderTexture();
        }

        /// <summary>
        /// 播放一段视频。
        /// </summary>
        /// <param name="fileName">StreamingAssets/{videoFolder} 下的文件名，例如 "s01.mp4"</param>
        /// <param name="onEnd">播放到结尾时的回调（此时画面已定格在最后一帧）</param>
        public void Play(string fileName, Action onEnd = null)
        {
            _onEnd = onEnd;
            _reachedEnd = false;
            _hasError = false;
            IsPrepared = false;
            _currentFile = fileName;

            if (string.IsNullOrEmpty(fileName))
            {
                Debug.LogError("[VideoScreen] 视频文件名为空");
                _hasError = true;
                _reachedEnd = true;
                _onEnd?.Invoke();
                return;
            }

            _player.Stop();
            _player.url = BuildUrl(fileName);
            _player.skipOnDrop = skipOnDrop;
            SyncVolume();
            _player.Prepare();
        }

        /// <summary>暂停（画面保持在当前帧）。</summary>
        public void Pause()
        {
            if (_player != null && _player.isPlaying) _player.Pause();
        }

        /// <summary>继续播放。</summary>
        public void Resume()
        {
            if (_player != null && IsPrepared && !_reachedEnd) _player.Play();
        }

        /// <summary>跳到指定秒数。</summary>
        public void Seek(double seconds)
        {
            if (_player != null && IsPrepared) _player.time = seconds;
        }

        /// <summary>
        /// 从指定秒数重新开始播（Loop 等待用）。
        /// 会把"已播完"状态清掉，所以下一轮播到结尾时 ReachedEnd 会重新变 true。
        /// </summary>
        public void LoopFrom(double seconds)
        {
            if (_player == null || !IsPrepared) return;
            _reachedEnd = false;
            _player.time = Mathf.Max(0f, (float)seconds);
            _player.Play();
        }

        /// <summary>跳到视频结尾（用于“快进跳过本段”）。</summary>
        public void SkipToEnd()
        {
            if (_player == null || !IsPrepared || _reachedEnd) return;
            // 直接跳到最后一帧，让 loopPointReached 正常触发
            _player.frame = (long)_player.frameCount - 1;
            _player.Play();
        }

        /// <summary>
        /// 视频文件是否真的存在。
        /// 安卓上 StreamingAssets 在 apk 压缩包里，没法用 File 检查，一律当作存在（走真实播放，失败了会走 errorReceived）。
        /// </summary>
        public bool VideoFileExists(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string url = BuildUrl(fileName);
            if (url.Contains("://")) return true;
            return File.Exists(url);
        }

        /// <summary>把画面清空（切到占位模式时用，避免残留上一段的定格帧）。</summary>
        public void ClearScreen()
        {
            if (_player != null && _player.isPlaying) _player.Stop();
            // RawImage 的 texture 为空时会渲染成一整块白，所以直接把组件关掉
            if (screen != null) { screen.texture = null; screen.enabled = false; }
        }

        /// <summary>构造视频完整 URL。StreamingAssets 在各平台的路径差异由 Unity 处理。</summary>
        public string BuildUrl(string fileName)
        {
            string folder = string.IsNullOrEmpty(videoFolder)
                ? Application.streamingAssetsPath
                : Path.Combine(Application.streamingAssetsPath, videoFolder);
            return Path.Combine(folder, fileName).Replace('\\', '/');
        }

        void OnPrepareCompleted(VideoPlayer vp)
        {
            int w = (int)vp.width;
            int h = (int)vp.height;
            if (w <= 0 || h <= 0) { w = 1920; h = 1080; }

            EnsureRenderTexture(w, h);
            vp.targetTexture = _rt;

            if (screen != null)
            {
                screen.texture = _rt;
                screen.color = Color.white;
                screen.enabled = true;
            }
            if (aspectFitter != null)
            {
                aspectFitter.aspectMode = aspectMode;
                aspectFitter.aspectRatio = (float)w / h;
            }

            IsPrepared = true;
            SyncVolume();
            vp.Play();
        }

        void OnLoopPointReached(VideoPlayer vp)
        {
            _reachedEnd = true;
            // 关键：不要 Stop()。Pause 会保留 RenderTexture 里的最后一帧，实现画面定格。
            vp.Pause();
            var cb = _onEnd;
            _onEnd = null;
            cb?.Invoke();
        }

        void OnErrorReceived(VideoPlayer vp, string message)
        {
            Debug.LogError($"[VideoScreen] 播放失败：{_currentFile}\n{message}\nURL: {vp.url}");
            _hasError = true;
            _reachedEnd = true;
            var cb = _onEnd;
            _onEnd = null;
            cb?.Invoke();
        }

        void EnsureRenderTexture(int w, int h)
        {
            if (_rt != null && _rt.width == w && _rt.height == h) return;

            ReleaseRenderTexture();
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                name = $"VideoRT_{w}x{h}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            _rt.Create();
        }

        void ReleaseRenderTexture()
        {
            if (_rt == null) return;
            if (_player != null && _player.targetTexture == _rt) _player.targetTexture = null;
            if (screen != null && ReferenceEquals(screen.texture, _rt)) screen.texture = null;
            _rt.Release();
            Destroy(_rt);
            _rt = null;
        }

        void SyncVolume()
        {
            if (_audio == null || _player == null) return;
            float v = AudioManager.Instance != null ? AudioManager.Instance.EffectiveVideoVolume : 1f;
            _audio.volume = v;
            if (_player.controlledAudioTrackCount > 0)
                _player.SetTargetAudioSource(0, _audio);
        }
    }
}
