using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Love.UI
{
    /// <summary>
    /// 标题界面。启动后先停在这里，点「开始游戏」才进入剧情。
    /// 按钮复用 ChoiceButton 预制体，所以悬停放大和点击音效是白送的，以后改按钮样式这里也跟着变。
    /// </summary>
    [DisallowMultipleComponent]
    public class TitleScreen : MonoBehaviour
    {
        [Header("引用")]
        public CanvasGroup canvasGroup;
        [Tooltip("底图。想换成美术图就把 sprite 拖进去")]
        public Image background;
        [Tooltip("可选，游戏 Logo 图。留空则只显示文字标题")]
        public Image logo;
        public TextMeshProUGUI titleLabel;
        public TextMeshProUGUI subtitleLabel;
        public ChoiceButtonView startButton;
        public ChoiceButtonView quitButton;

        [Header("文案")]
        public string gameTitle = "LOVE";
        public string subtitle = "";
        public string startText = "开始游戏";
        public string quitText = "退出游戏";

        [Header("动画")]
        public float fadeDuration = 0.4f;

        Action _onStart;
        Coroutine _anim;

        public bool IsShowing { get; private set; }

        void Awake()
        {
            if (canvasGroup == null) canvasGroup = GetComponent<CanvasGroup>();
            HideImmediate();
        }

        /// <summary>显示标题界面。玩家点「开始游戏」时回调 onStart。</summary>
        public void Show(Action onStart)
        {
            _onStart = onStart;
            gameObject.SetActive(true);
            IsShowing = true;

            if (titleLabel != null) titleLabel.text = gameTitle;
            if (subtitleLabel != null)
            {
                subtitleLabel.text = subtitle;
                subtitleLabel.gameObject.SetActive(!string.IsNullOrEmpty(subtitle));
            }
            if (logo != null) logo.gameObject.SetActive(logo.sprite != null);

            if (startButton != null) startButton.Setup(startText, HandleStart);
            if (quitButton != null) quitButton.Setup(quitText, HandleQuit);

            if (canvasGroup != null)
            {
                canvasGroup.blocksRaycasts = true;
                canvasGroup.interactable = true;
            }

            if (_anim != null) StopCoroutine(_anim);
            _anim = StartCoroutine(Fade(0f, 1f, fadeDuration, false));
        }

        public void Hide()
        {
            if (!IsShowing) return;
            IsShowing = false;
            if (canvasGroup != null)
            {
                canvasGroup.blocksRaycasts = false;
                canvasGroup.interactable = false;
            }
            if (_anim != null) StopCoroutine(_anim);
            _anim = StartCoroutine(Fade(canvasGroup != null ? canvasGroup.alpha : 1f, 0f, fadeDuration, true));
        }

        public void HideImmediate()
        {
            IsShowing = false;
            _onStart = null;
            if (_anim != null) { StopCoroutine(_anim); _anim = null; }
            if (canvasGroup != null)
            {
                canvasGroup.alpha = 0f;
                canvasGroup.blocksRaycasts = false;
                canvasGroup.interactable = false;
            }
            gameObject.SetActive(false);
        }

        void HandleStart()
        {
            if (!IsShowing) return;
            var cb = _onStart;
            _onStart = null;
            cb?.Invoke();
        }

        void HandleQuit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        IEnumerator Fade(float from, float to, float duration, bool deactivateAtEnd)
        {
            if (canvasGroup == null)
            {
                if (deactivateAtEnd) gameObject.SetActive(false);
                _anim = null;
                yield break;
            }

            canvasGroup.alpha = from;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                canvasGroup.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(t / duration));
                yield return null;
            }
            canvasGroup.alpha = to;

            _anim = null;
            if (deactivateAtEnd) gameObject.SetActive(false);
        }
    }
}
