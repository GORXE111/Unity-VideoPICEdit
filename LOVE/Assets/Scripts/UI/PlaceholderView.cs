using Love.Story;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Love.UI
{
    /// <summary>
    /// 视频占位画面。
    /// 视频文件还没导入时顶上来，按配置的秒数走完一段"假视频"，
    /// 好让选项、计时、BGM、黑场过渡这些能在没有素材的阶段就完整测通。
    /// 视频文件一旦放进 StreamingAssets/Videos，这个占位就自动不出现了。
    /// </summary>
    [DisallowMultipleComponent]
    public class PlaceholderView : MonoBehaviour
    {
        [Header("引用")]
        public Image background;
        public TextMeshProUGUI titleLabel;
        public TextMeshProUGUI infoLabel;
        public Image progressFill;

        [Header("配色")]
        [Tooltip("按剧情段序号轮换，相邻两段颜色不同，一眼能看出确实切段了")]
        public Color[] palette = new[]
        {
            new Color(0.16f, 0.18f, 0.26f),
            new Color(0.24f, 0.17f, 0.20f),
            new Color(0.15f, 0.23f, 0.22f),
            new Color(0.22f, 0.20f, 0.15f),
        };

        string _infoPrefix = string.Empty;
        int _lastShownSecond = -1;

        void Awake() => Hide();

        public void Show(StorySegment seg, float duration, int index)
        {
            gameObject.SetActive(true);

            if (background != null && palette != null && palette.Length > 0)
                background.color = palette[Mathf.Abs(index) % palette.Length];

            if (titleLabel != null)
                titleLabel.text = string.IsNullOrEmpty(seg.title) ? seg.id : seg.title;

            _infoPrefix = $"占位画面 · 视频未导入\n{seg.video}   共 {duration:0.#} 秒\n";
            _lastShownSecond = -1;
            SetProgress(0f, duration);
        }

        public void SetProgress(float t01, float remain)
        {
            if (progressFill != null) progressFill.fillAmount = Mathf.Clamp01(t01);

            // 只在秒数变化时刷文本，避免每帧重建字符串
            int sec = Mathf.CeilToInt(Mathf.Max(0f, remain));
            if (infoLabel != null && sec != _lastShownSecond)
            {
                _lastShownSecond = sec;
                infoLabel.text = _infoPrefix + $"剩余 {sec} 秒";
            }
        }

        public void Hide()
        {
            if (gameObject.activeSelf) gameObject.SetActive(false);
        }
    }
}
