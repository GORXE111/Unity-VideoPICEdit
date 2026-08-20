using System;
using System.Collections;
using System.IO;
using Love.Audio;
using Love.UI;
using Love.Video;
using UnityEngine;
using UnityEngine.Networking;

namespace Love.Story
{
    /// <summary>
    /// 剧情总导演。整个玩法流程都在这里：
    ///   读 JSON → 播视频 → 到点弹选项 → 视频播完定格最后一帧 → 玩家选择 → 黑场过渡 → 下一段
    /// </summary>
    [DisallowMultipleComponent]
    public class StoryDirector : MonoBehaviour
    {
        [Header("引用")]
        public VideoScreen videoScreen;
        public ChoicePanel choicePanel;
        public ScreenFader fader;
        [Tooltip("按剧情段切换调色用。留空则不做调色切换")]
        public VideoPostProcessor postProcessor;
        [Tooltip("视频还没导入时顶上来的占位画面，可以留空")]
        public PlaceholderView placeholderView;
        [Tooltip("标题界面。留空则启动直接开播")]
        public TitleScreen titleScreen;

        [Header("无视频时的占位播放")]
        [Tooltip("视频文件不存在时，用占位画面把流程跑完，方便没素材时先测选项和分支")]
        public bool usePlaceholderWhenVideoMissing = true;
        [Tooltip("剧情段没单独配 placeholderSeconds 时用这个时长")]
        public float defaultPlaceholderSeconds = 8f;

        [Header("剧情配置")]
        [Tooltip("剧情表路径，相对于 StreamingAssets")]
        public string storyFile = "Story/story.json";
        [Tooltip("勾上则进游戏自动走流程（先标题界面，再剧情）")]
        public bool autoStart = true;
        [Tooltip("先停在标题界面，点开始游戏才进剧情。填了 debugStartId 时会自动跳过标题")]
        public bool showTitleScreen = true;
        [Tooltip("选项一出现就把视频暂停在那一帧，选完才继续。\n" +
                 "关掉的话选项浮出后视频会接着播。剧情段开了 loopWhileWaiting 时此项不生效")]
        public bool pauseVideoWhenChoicesShow = true;
        [Tooltip("标题界面淡入淡出时长")]
        public float titleFadeDuration = 0.6f;
        [Tooltip("留空则用 JSON 里的 startId；填了则从这一段开始（方便调试单段，会跳过标题界面）")]
        public string debugStartId = string.Empty;

        [Header("事件")]
        public Action<StorySegment> onSegmentStart;
        public Action<StorySegment, int> onChoiceSelected;
        public Action onStoryFinished;

        public StoryDatabase Story { get; private set; }
        public StorySegment CurrentSegment { get; private set; }
        public bool IsRunning { get; private set; }

        Coroutine _flow;

        void Awake()
        {
            // 引用为空时从 UI 面板预制体上自动取
            var ui = GameplayUIRoot.Find();
            if (ui != null)
            {
                if (choicePanel == null) choicePanel = ui.choicePanel;
                if (fader == null) fader = ui.fader;
                if (placeholderView == null) placeholderView = ui.placeholderView;
                if (titleScreen == null) titleScreen = ui.titleScreen;
            }
            if (videoScreen == null) videoScreen = FindObjectOfType<VideoScreen>();
            if (postProcessor == null) postProcessor = FindObjectOfType<VideoPostProcessor>();
        }

        IEnumerator Start()
        {
            if (!autoStart) yield break;

            yield return LoadStory();
            if (Story == null) yield break;

            bool debugJump = !string.IsNullOrEmpty(debugStartId);
            string startId = debugJump ? debugStartId : Story.startId;

            // 调试指定了起始段就直接开播，不用每次都点一遍标题
            if (showTitleScreen && titleScreen != null && !debugJump)
                yield return ShowTitleAndWait();

            StartStory(startId);
        }

        /// <summary>显示标题界面，等玩家点「开始游戏」。</summary>
        IEnumerator ShowTitleAndWait()
        {
            if (fader != null) fader.SetAlpha(1f);      // 从全黑起

            bool started = false;
            titleScreen.Show(() => started = true);

            // 标题界面的 BGM。和第一段剧情配同一首的话，进剧情时不会重头开始播
            if (AudioManager.Instance != null && !string.IsNullOrEmpty(Story.titleBgm))
                AudioManager.Instance.PlayBgm(Story.titleBgm, titleFadeDuration);

            if (fader != null) yield return fader.FadeIn(titleFadeDuration);

            yield return new WaitUntil(() => started);

            if (fader != null) yield return fader.FadeOut(titleFadeDuration);
            titleScreen.Hide();
        }

        #region 加载

        /// <summary>从 StreamingAssets 读取剧情 JSON。安卓等平台上 StreamingAssets 在压缩包里，必须用 UnityWebRequest。</summary>
        public IEnumerator LoadStory()
        {
            string path = Path.Combine(Application.streamingAssetsPath, storyFile).Replace('\\', '/');
            string json = null;

            if (path.Contains("://") || path.Contains(":///"))
            {
                using (var req = UnityWebRequest.Get(path))
                {
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogError($"[StoryDirector] 读取剧情表失败：{path}\n{req.error}");
                        yield break;
                    }
                    json = req.downloadHandler.text;
                }
            }
            else
            {
                if (!File.Exists(path))
                {
                    Debug.LogError($"[StoryDirector] 剧情表不存在：{path}");
                    yield break;
                }
                json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            }

            Story = ParseStory(json);
        }

        /// <summary>解析剧情 JSON。也可以直接调用它来热更/测试。</summary>
        public static StoryDatabase ParseStory(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var db = JsonUtility.FromJson<StoryDatabase>(json);
                db?.Validate();
                return db;
            }
            catch (Exception e)
            {
                Debug.LogError($"[StoryDirector] 剧情 JSON 解析失败：{e.Message}");
                return null;
            }
        }

        #endregion

        #region 流程

        /// <summary>从指定剧情段开始播。</summary>
        public void StartStory(string startId)
        {
            if (Story == null)
            {
                Debug.LogError("[StoryDirector] 剧情表还没加载好");
                return;
            }
            Stop();
            _flow = StartCoroutine(FlowRoutine(startId));
        }

        /// <summary>中断当前流程。</summary>
        public void Stop()
        {
            if (_flow != null) { StopCoroutine(_flow); _flow = null; }
            IsRunning = false;
            if (choicePanel != null) choicePanel.HideImmediate();
        }

        IEnumerator FlowRoutine(string startId)
        {
            IsRunning = true;
            string nextId = startId;
            bool first = true;

            while (!string.IsNullOrEmpty(nextId))
            {
                var seg = Story.Find(nextId);
                if (seg == null)
                {
                    Debug.LogError($"[StoryDirector] 找不到剧情段：{nextId}");
                    break;
                }

                // ---- 黑场过渡（第一段直接从黑场淡入）----
                float trans = Story.transitionDuration;
                if (first)
                {
                    if (fader != null) fader.SetAlpha(1f);
                }
                else if (fader != null)
                {
                    yield return fader.FadeOut(trans);
                }

                CurrentSegment = seg;
                onSegmentStart?.Invoke(seg);

                // ---- BGM ----
                ApplyBgm(seg);

                // ---- 调色 ----
                ApplyGrade(seg);

                // ---- 起播视频（或占位画面）----
                if (choicePanel != null) choicePanel.HideImmediate();

                bool usePlaceholder = usePlaceholderWhenVideoMissing && !videoScreen.VideoFileExists(seg.video);
                float placeholderLen = seg.placeholderSeconds > 0f ? seg.placeholderSeconds : defaultPlaceholderSeconds;
                float clock = 0f;

                if (usePlaceholder)
                {
                    Debug.Log($"[StoryDirector] 段 {seg.id} 的视频还没导入（{seg.video}），用占位画面跑流程 {placeholderLen:0.#} 秒");
                    videoScreen.ClearScreen();
                    if (placeholderView != null)
                        placeholderView.Show(seg, placeholderLen, Story.segments.IndexOf(seg));
                }
                else
                {
                    if (placeholderView != null) placeholderView.Hide();
                    videoScreen.Play(seg.video);
                    // 等待视频真正出画（或者直接报错了）
                    yield return new WaitUntil(() => videoScreen.IsPrepared || videoScreen.HasError);
                }

                if (fader != null) yield return fader.FadeIn(trans);
                first = false;

                if (!usePlaceholder && videoScreen.HasError)
                {
                    // 视频存在但解不开时不卡死，按“无选项”处理走到下一段
                    Debug.LogError($"[StoryDirector] 段 {seg.id} 的视频播放失败，跳过：{seg.video}");
                    nextId = seg.next;
                    continue;
                }

                // ---- 主循环：等选项 / 等播完 ----
                int picked = -1;
                bool choiceShown = false;
                bool hasChoices = seg.HasChoices;

                bool videoEnded = false;
                bool pausedForChoice = false;

                // Loop 等待和"出选项就暂停"是互斥的两种表现，Loop 优先
                bool freezeOnChoice = pauseVideoWhenChoicesShow && !seg.loopWhileWaiting;

                while (true)
                {
                    // 占位模式下自己走一个假时钟，让 choiceShowTime 和 Loop 照样生效
                    double playHead;
                    if (usePlaceholder)
                    {
                        if (!pausedForChoice) clock += Time.deltaTime;
                        playHead = clock;
                        if (placeholderView != null)
                            placeholderView.SetProgress(clock / placeholderLen, placeholderLen - clock);
                        if (clock >= placeholderLen) videoEnded = true;
                    }
                    else
                    {
                        playHead = videoScreen.Time;
                        videoEnded = videoScreen.ReachedEnd;
                    }

                    if (!choiceShown && hasChoices)
                    {
                        bool timeReached = seg.choiceShowTime >= 0f && playHead >= seg.choiceShowTime;
                        if (timeReached || videoEnded)
                        {
                            choiceShown = true;

                            // 选项一出现就把画面定在这一帧。
                            // 视频是自然播完的话本来就停在最后一帧了，这里 Pause 是空操作；
                            // 真正起作用的是 choiceShowTime 配了秒数、视频还在播的情况。
                            if (freezeOnChoice)
                            {
                                pausedForChoice = true;
                                if (!usePlaceholder) videoScreen.Pause();
                            }

                            var captured = seg;
                            choicePanel.Show(captured.choices, i => picked = i);
                        }
                    }

                    if (picked >= 0) break;                      // 玩家选了
                    if (videoEnded && !hasChoices) break;         // 没选项，播完就走

                    // Loop 等待：还在等玩家选，就跳回 loopStart 继续播，而不是定格死画面
                    if (videoEnded && hasChoices && seg.loopWhileWaiting)
                    {
                        if (usePlaceholder)
                        {
                            clock = Mathf.Max(0f, seg.loopStart);
                        }
                        else
                        {
                            videoScreen.LoopFrom(seg.loopStart);
                        }
                        videoEnded = false;
                    }

                    yield return null;
                }

                // ---- 结算 ----
                if (hasChoices && picked >= 0)
                {
                    onChoiceSelected?.Invoke(seg, picked);

                    // Loop 等待的段落一律立刻切：视频在循环，"等播完"没有意义
                    if (seg.waitVideoEndAfterSelect && !videoEnded && !seg.loopWhileWaiting)
                    {
                        // 选项出现时暂停过的话，这里得先恢复播放，不然会一直等一个永远不来的结尾
                        if (pausedForChoice && !usePlaceholder) videoScreen.Resume();
                        pausedForChoice = false;

                        if (usePlaceholder)
                        {
                            // 占位模式下没人推进假时钟了，这里自己把剩下的时间走完
                            while (clock < placeholderLen)
                            {
                                clock += Time.deltaTime;
                                if (placeholderView != null)
                                    placeholderView.SetProgress(clock / placeholderLen, placeholderLen - clock);
                                yield return null;
                            }
                        }
                        else
                        {
                            yield return new WaitUntil(() => videoEnded);
                        }
                    }

                    int idx = Mathf.Clamp(picked, 0, seg.choices.Count - 1);
                    nextId = seg.choices[idx].next;
                }
                else
                {
                    nextId = seg.next;
                }
            }

            // 流程结束：留在最后一帧上，黑场收尾交给上层决定
            IsRunning = false;
            _flow = null;
            onStoryFinished?.Invoke();
        }

        Coroutine _gradeFade;

        /// <summary>
        /// 切换本段的调色预设。配了渐变时长就跑一个插值协程，
        /// 比如回忆段逐渐褪成褒色，靠的就是这个。
        /// </summary>
        void ApplyGrade(StorySegment seg)
        {
            if (postProcessor == null || string.IsNullOrEmpty(seg.grade)) return;

            var target = GradePresetStore.Load(seg.grade);
            if (target == null)
            {
                Debug.LogWarning($"[StoryDirector] 段 {seg.id} 找不到调色预设：{seg.grade}");
                return;
            }

            if (_gradeFade != null) { StopCoroutine(_gradeFade); _gradeFade = null; }

            if (seg.gradeFade <= 0.01f)
            {
                postProcessor.settings.CopyFrom(target);
                return;
            }
            _gradeFade = StartCoroutine(FadeGradeRoutine(target, seg.gradeFade));
        }

        IEnumerator FadeGradeRoutine(VideoGradeSettings target, float duration)
        {
            // 起点要快照一份：postProcessor.settings 在渐变过程中会被不断改写，
            // 直接拿它当起点的话每帧的插值基准都在动，结果是一条越来越慢的曲线
            var from = postProcessor.settings.Clone();

            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                VideoGradeSettings.Lerp(from, target, t / duration, postProcessor.settings);
                yield return null;
            }
            postProcessor.settings.CopyFrom(target);
            _gradeFade = null;
        }

        void ApplyBgm(StorySegment seg)
        {
            if (AudioManager.Instance == null) return;
            if (string.IsNullOrEmpty(seg.bgm)) return;   // 空 = 保持不变

            if (seg.bgm.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                seg.bgm.Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                AudioManager.Instance.StopBgm(seg.bgmFade);
                return;
            }
            AudioManager.Instance.PlayBgm(seg.bgm, seg.bgmFade);
        }

        #endregion
    }
}
