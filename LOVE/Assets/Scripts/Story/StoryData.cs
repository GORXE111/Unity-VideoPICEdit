using System;
using System.Collections.Generic;
using UnityEngine;

namespace Love.Story
{
    /// <summary>一个选项。</summary>
    [Serializable]
    public class StoryChoice
    {
        /// <summary>按钮上显示的文字。</summary>
        public string text = string.Empty;

        /// <summary>选中后要跳转到的剧情段 id。留空表示流程结束。</summary>
        public string next = string.Empty;
    }

    /// <summary>一段剧情 = 一段视频（+ 可选的结尾选项）。</summary>
    [Serializable]
    public class StorySegment
    {
        /// <summary>唯一标识，选项的 next 就是填这个。</summary>
        public string id = string.Empty;

        /// <summary>给人看的备注名，只用于日志和占位画面，不影响流程。</summary>
        public string title = string.Empty;

        /// <summary>StreamingAssets/Videos 下的视频文件名，例如 "s01.mp4"。</summary>
        public string video = string.Empty;

        /// <summary>
        /// 视频文件还没导入时，占位画面要停留几秒。
        /// 用来在没有视频的阶段先把选项和流程跑通。0 或负数 = 用 StoryDirector 上的默认值。
        /// </summary>
        public float placeholderSeconds = 0f;

        /// <summary>
        /// 本段要播的 BGM 名字（Resources/Audio/BGM 下的文件名，不含扩展名）。
        /// 留空 = 保持上一段的 BGM 不变；填 "none" 或 "stop" = 淡出停止 BGM。
        /// </summary>
        public string bgm = string.Empty;

        /// <summary>BGM 过渡时长（秒）。负数表示用 AudioManager 的默认值。</summary>
        public float bgmFade = -1f;

        /// <summary>
        /// 选项出现的时间点（从本段视频开头算起的秒数）。
        /// 负数（默认 -1）= 视频播完后再出选项。
        /// </summary>
        public float choiceShowTime = -1f;

        /// <summary>
        /// 玩家选完之后是否要等本段视频播完再切下一段。
        /// false（默认）= 立刻切；true = 等视频自然播完。
        /// 注意：loopWhileWaiting 打开时这个字段无效，选完一律立刻切。
        /// </summary>
        public bool waitVideoEndAfterSelect = false;

        /// <summary>
        /// 等玩家选择期间，视频是否循环播放。
        /// false（默认）= 播完定格在最后一帧等着。
        /// true = 播到结尾就跳回 loopStart 继续播，循环到玩家选完为止。
        /// 角色在待机小动作里等玩家决定，比定格一张死画面自然得多。
        /// </summary>
        public bool loopWhileWaiting = false;

        /// <summary>循环回跳的时间点（秒）。0 = 从头循环整段；填正数 = 只循环片尾那一小段待机。</summary>
        public float loopStart = 0f;

        /// <summary>
        /// 本段用哪套调色预设（StreamingAssets/Story/Grades 下的文件名，不含扩展名）。
        /// 留空 = 沿用上一段，不做任何切换。
        /// </summary>
        public string grade = string.Empty;

        /// <summary>调色切换的渐变时长（秒）。0 = 瞬切。</summary>
        public float gradeFade = 0f;

        /// <summary>没有选项时，本段播完自动跳转到的 id。留空表示流程结束。</summary>
        public string next = string.Empty;

        /// <summary>选项列表，最多 2 个（超出部分会被忽略并给出警告）。</summary>
        public List<StoryChoice> choices = new List<StoryChoice>();

        public bool HasChoices => choices != null && choices.Count > 0;
    }

    /// <summary>整个剧情表。对应 StreamingAssets/Story/story.json。</summary>
    [Serializable]
    public class StoryDatabase
    {
        /// <summary>起始剧情段 id。</summary>
        public string startId = string.Empty;

        /// <summary>
        /// 标题界面的 BGM（Resources/Audio/BGM 下的文件名）。
        /// 和第一段剧情填同一首的话，点开始游戏时不会重头开始播，直接连着走下去。
        /// </summary>
        public string titleBgm = string.Empty;

        /// <summary>段与段之间黑场过渡时长（秒）。</summary>
        public float transitionDuration = 0.35f;

        public List<StorySegment> segments = new List<StorySegment>();

        Dictionary<string, StorySegment> _index;

        /// <summary>按 id 查找剧情段，找不到返回 null。</summary>
        public StorySegment Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_index == null)
            {
                _index = new Dictionary<string, StorySegment>(segments != null ? segments.Count : 0);
                if (segments != null)
                {
                    foreach (var s in segments)
                    {
                        if (s == null || string.IsNullOrEmpty(s.id)) continue;
                        if (_index.ContainsKey(s.id))
                        {
                            Debug.LogWarning($"[Story] 剧情段 id 重复：{s.id}，后一个被忽略");
                            continue;
                        }
                        _index[s.id] = s;
                    }
                }
            }
            return _index.TryGetValue(id, out var seg) ? seg : null;
        }

        /// <summary>加载并校验后调用，把明显的配置错误提前报出来。</summary>
        public void Validate()
        {
            if (segments == null || segments.Count == 0)
            {
                Debug.LogError("[Story] 剧情表里一段都没有");
                return;
            }
            if (string.IsNullOrEmpty(startId))
            {
                startId = segments[0].id;
                Debug.LogWarning($"[Story] 没有配置 startId，自动使用第一段：{startId}");
            }
            if (Find(startId) == null)
                Debug.LogError($"[Story] startId 指向的剧情段不存在：{startId}");

            foreach (var s in segments)
            {
                if (s == null) continue;
                if (string.IsNullOrEmpty(s.video))
                    Debug.LogWarning($"[Story] 段 {s.id} 没有配置 video");
                if (!string.IsNullOrEmpty(s.next) && Find(s.next) == null)
                    Debug.LogError($"[Story] 段 {s.id} 的 next 指向不存在的段：{s.next}");
                if (s.choices == null) continue;
                for (int i = 0; i < s.choices.Count; i++)
                {
                    var c = s.choices[i];
                    if (c == null) continue;
                    if (!string.IsNullOrEmpty(c.next) && Find(c.next) == null)
                        Debug.LogError($"[Story] 段 {s.id} 的第 {i + 1} 个选项指向不存在的段：{c.next}");
                }
            }
        }
    }
}
