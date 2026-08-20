using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Love.Audio
{
    /// <summary>
    /// 全局音频管理器。
    /// 负责 BGM 的播放 / 淡入 / 淡出 / 交叉过渡，以及 总音量、BGM 音量、视频音量、音效音量 的统一管理与存档。
    /// 视频本身的声音由 VideoPlayer 输出到自己的 AudioSource，音量由这里的 VideoVolume 控制（见 VideoScreen）。
    /// </summary>
    [DisallowMultipleComponent]
    public class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        const string PrefMaster = "audio.master";
        const string PrefBgm    = "audio.bgm";
        const string PrefVideo  = "audio.video";
        const string PrefSfx    = "audio.sfx";

        [Header("BGM 按名字播放时的查找目录")]
        [Tooltip("填 Resources 下的相对路径。例如填 Audio/BGM，则 JSON 里写 bgm:\"theater\" 会加载 Assets/Resources/Audio/BGM/theater")]
        public string bgmResourceFolder = "Audio/BGM";

        [Header("默认淡入淡出时长（秒）")]
        public float defaultFadeDuration = 1.0f;

        [Header("音量配置（0~1）")]
        [Tooltip("总音量，乘在所有轨道上")]
        [Range(0f, 1f)] public float masterVolume = 1f;
        [Tooltip("BGM 音量。和下面的视频音量互相独立，这里调的是背景音乐在整体里占多大比重")]
        [Range(0f, 1f)] public float bgmVolume = 0.6f;
        [Tooltip("视频自带声音的音量（对白、环境声）。和 BGM 完全分开")]
        [Range(0f, 1f)] public float videoVolume = 1f;
        [Tooltip("UI 音效音量")]
        [Range(0f, 1f)] public float sfxVolume = 1f;

        [Tooltip("勾上则玩家在设置里调过的音量存进 PlayerPrefs，下次启动沿用。\n" +
                 "开发期建议关掉，否则上面这四个值改了不生效——会被存档覆盖掉。")]
        public bool rememberPlayerSettings = false;

        [Header("跨场景保留")]
        public bool dontDestroyOnLoad = true;

        // 双轨 BGM，用于交叉过渡
        readonly AudioSource[] _bgm = new AudioSource[2];
        readonly float[] _bgmFade = new float[2];   // 每条轨自身的淡入淡出系数 0~1
        int _cur;                                   // 当前生效的轨
        AudioSource _sfx;
        Coroutine _fadeRoutine;

        /// <summary>任何音量变化时触发。VideoScreen 订阅它来同步视频音量。</summary>
        public event Action OnVolumeChanged;

        #region 音量属性

        // 这些属性是给运行时代码（设置界面的滑条等）用的：改完会立刻应用并按需存档。
        // 直接改上面那几个 public 字段的话只在 Inspector 里有效（靠 OnValidate 同步）。

        /// <summary>总音量 0~1</summary>
        public float MasterVolume
        {
            get => masterVolume;
            set { masterVolume = Mathf.Clamp01(value); Save(PrefMaster, masterVolume); ApplyVolumes(); }
        }

        /// <summary>BGM 音量 0~1。和 VideoVolume 相互独立。</summary>
        public float BgmVolume
        {
            get => bgmVolume;
            set { bgmVolume = Mathf.Clamp01(value); Save(PrefBgm, bgmVolume); ApplyVolumes(); }
        }

        /// <summary>视频音量 0~1（视频自带的对白/环境声）。和 BgmVolume 相互独立。</summary>
        public float VideoVolume
        {
            get => videoVolume;
            set { videoVolume = Mathf.Clamp01(value); Save(PrefVideo, videoVolume); ApplyVolumes(); }
        }

        /// <summary>音效音量 0~1</summary>
        public float SfxVolume
        {
            get => sfxVolume;
            set { sfxVolume = Mathf.Clamp01(value); Save(PrefSfx, sfxVolume); ApplyVolumes(); }
        }

        /// <summary>BGM 相对视频声音的比例。1 = 一样响，0.5 = BGM 只有视频声音的一半。</summary>
        public float BgmToVideoRatio
        {
            get => videoVolume > 0.0001f ? bgmVolume / videoVolume : 0f;
            set => BgmVolume = Mathf.Clamp01(videoVolume * Mathf.Max(0f, value));
        }

        /// <summary>视频 AudioSource 应该使用的最终音量（总音量 × 视频音量）。</summary>
        public float EffectiveVideoVolume => masterVolume * videoVolume;

        void Save(string key, float value)
        {
            if (rememberPlayerSettings) PlayerPrefs.SetFloat(key, value);
        }

        /// <summary>当前正在播放的 BGM 名字，没有则为空串。</summary>
        public string CurrentBgmName { get; private set; } = string.Empty;

        #endregion

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            if (dontDestroyOnLoad) DontDestroyOnLoad(gameObject);

            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject("BGM_" + i);
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = true;
                src.spatialBlend = 0f;   // 2D
                src.volume = 0f;
                _bgm[i] = src;
                _bgmFade[i] = 0f;
            }

            var sfxGo = new GameObject("SFX");
            sfxGo.transform.SetParent(transform, false);
            _sfx = sfxGo.AddComponent<AudioSource>();
            _sfx.playOnAwake = false;
            _sfx.spatialBlend = 0f;

            // 只有开了存档才去读 PlayerPrefs，否则一律用 Inspector 上配的值。
            // 不这样分的话，Inspector 里改了音量会被上一次的存档悄悄覆盖，改了跟没改一样。
            if (rememberPlayerSettings)
            {
                masterVolume = PlayerPrefs.GetFloat(PrefMaster, masterVolume);
                bgmVolume    = PlayerPrefs.GetFloat(PrefBgm,    bgmVolume);
                videoVolume  = PlayerPrefs.GetFloat(PrefVideo,  videoVolume);
                sfxVolume    = PlayerPrefs.GetFloat(PrefSfx,    sfxVolume);
            }
            ApplyVolumes();
        }

        void OnValidate()
        {
            masterVolume = Mathf.Clamp01(masterVolume);
            bgmVolume    = Mathf.Clamp01(bgmVolume);
            videoVolume  = Mathf.Clamp01(videoVolume);
            sfxVolume    = Mathf.Clamp01(sfxVolume);

            // 运行中在 Inspector 里拖滑条，立刻能听到变化
            if (Application.isPlaying && _bgm[0] != null) ApplyVolumes();
        }

        /// <summary>清掉音量存档，让 Inspector 上的配置重新生效。</summary>
        [ContextMenu("清除音量存档")]
        public void ClearSavedVolumes()
        {
            PlayerPrefs.DeleteKey(PrefMaster);
            PlayerPrefs.DeleteKey(PrefBgm);
            PlayerPrefs.DeleteKey(PrefVideo);
            PlayerPrefs.DeleteKey(PrefSfx);
            Debug.Log("[AudioManager] 音量存档已清除");
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        #region BGM

        /// <summary>
        /// 按名字播放 BGM（从 bgmResourceFolder 指定的 Resources 目录加载）。
        /// 如果和当前 BGM 同名则不做任何事，保证跨剧情段连续播放。
        /// </summary>
        /// <param name="clipName">Resources 下的文件名（不含扩展名）</param>
        /// <param name="fadeDuration">过渡时长，负数表示使用 defaultFadeDuration</param>
        public void PlayBgm(string clipName, float fadeDuration = -1f)
        {
            if (string.IsNullOrEmpty(clipName)) return;
            if (CurrentBgmName == clipName && _bgm[_cur].isPlaying) return;

            string path = string.IsNullOrEmpty(bgmResourceFolder)
                ? clipName
                : bgmResourceFolder.TrimEnd('/') + "/" + clipName;

            var clip = Resources.Load<AudioClip>(path);
            if (clip == null)
            {
                Debug.LogWarning($"[AudioManager] 找不到 BGM：Resources/{path}");
                return;
            }
            PlayBgm(clip, fadeDuration);
            CurrentBgmName = clipName;
        }

        /// <summary>直接用 AudioClip 播放 BGM，交叉过渡到新曲子。</summary>
        public void PlayBgm(AudioClip clip, float fadeDuration = -1f)
        {
            if (clip == null) return;
            float dur = fadeDuration < 0f ? defaultFadeDuration : fadeDuration;

            int next = 1 - _cur;
            var from = _bgm[_cur];
            var to = _bgm[next];

            to.clip = clip;
            to.time = 0f;
            to.loop = true;
            _bgmFade[next] = 0f;
            ApplyVolumes();
            to.Play();

            _cur = next;
            CurrentBgmName = clip.name;

            StartFade(new[] { next, 1 - next }, new[] { 1f, 0f }, dur, () =>
            {
                if (from != null && from.isPlaying && _bgmFade[1 - next] <= 0.001f)
                {
                    from.Stop();
                    from.clip = null;
                }
            });
        }

        /// <summary>停止 BGM（带淡出）。</summary>
        public void StopBgm(float fadeDuration = -1f)
        {
            float dur = fadeDuration < 0f ? defaultFadeDuration : fadeDuration;
            CurrentBgmName = string.Empty;
            StartFade(new[] { 0, 1 }, new[] { 0f, 0f }, dur, () =>
            {
                for (int i = 0; i < 2; i++)
                {
                    _bgm[i].Stop();
                    _bgm[i].clip = null;
                }
            });
        }

        /// <summary>把当前 BGM 淡到指定音量比例（做“视频高潮压低 BGM”这类效果用）。</summary>
        public void DuckBgm(float targetFactor, float fadeDuration = 0.3f)
        {
            StartFade(new[] { _cur }, new[] { Mathf.Clamp01(targetFactor) }, fadeDuration, null);
        }

        public void PauseBgm()
        {
            for (int i = 0; i < 2; i++) if (_bgm[i].isPlaying) _bgm[i].Pause();
        }

        public void ResumeBgm()
        {
            for (int i = 0; i < 2; i++) if (_bgm[i].clip != null) _bgm[i].UnPause();
        }

        #endregion

        #region SFX（一次性音效，供 UI 点击等使用）

        public void PlaySfx(AudioClip clip, float volumeScale = 1f)
        {
            if (clip == null || _sfx == null) return;
            _sfx.PlayOneShot(clip, Mathf.Clamp01(volumeScale) * masterVolume * sfxVolume);
        }

        #endregion

        #region 内部

        void ApplyVolumes()
        {
            for (int i = 0; i < 2; i++)
                if (_bgm[i] != null) _bgm[i].volume = masterVolume * bgmVolume * _bgmFade[i];

            OnVolumeChanged?.Invoke();
        }

        void StartFade(int[] tracks, float[] targets, float duration, Action onComplete)
        {
            if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
            _fadeRoutine = StartCoroutine(FadeRoutine(tracks, targets, duration, onComplete));
        }

        IEnumerator FadeRoutine(int[] tracks, float[] targets, float duration, Action onComplete)
        {
            var start = new float[tracks.Length];
            for (int i = 0; i < tracks.Length; i++) start[i] = _bgmFade[tracks[i]];

            if (duration <= 0.001f)
            {
                for (int i = 0; i < tracks.Length; i++) _bgmFade[tracks[i]] = targets[i];
                ApplyVolumes();
            }
            else
            {
                float t = 0f;
                while (t < duration)
                {
                    // 用 unscaledDeltaTime，避免 Time.timeScale 被改动时音频过渡卡住
                    t += Time.unscaledDeltaTime;
                    float k = Mathf.Clamp01(t / duration);
                    for (int i = 0; i < tracks.Length; i++)
                        _bgmFade[tracks[i]] = Mathf.Lerp(start[i], targets[i], k);
                    ApplyVolumes();
                    yield return null;
                }
                for (int i = 0; i < tracks.Length; i++) _bgmFade[tracks[i]] = targets[i];
                ApplyVolumes();
            }

            _fadeRoutine = null;
            onComplete?.Invoke();
        }

        #endregion
    }
}
