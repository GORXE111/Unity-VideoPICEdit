using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Love.UI
{
    /// <summary>
    /// 单个选项按钮。挂在 ChoiceButton 预制体根节点上。
    /// </summary>
    [DisallowMultipleComponent]
    public class ChoiceButtonView : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [Header("引用")]
        public Button button;
        public Image background;
        public TextMeshProUGUI label;
        public CanvasGroup canvasGroup;

        [Header("悬停效果")]
        public Color normalColor = Color.white;
        public Color hoverColor = new Color(1f, 1f, 1f, 1f);
        [Tooltip("鼠标悬停时的放大倍率")]
        public float hoverScale = 1.03f;
        public float hoverLerpSpeed = 12f;

        [Header("点击音效（可选）")]
        public AudioClip clickSfx;
        public AudioClip hoverSfx;

        Action _onClick;
        bool _hovered;
        Vector3 _baseScale = Vector3.one;
        bool _interactable = true;

        void Reset()
        {
            button = GetComponent<Button>();
            background = GetComponent<Image>();
            canvasGroup = GetComponent<CanvasGroup>();
            label = GetComponentInChildren<TextMeshProUGUI>();
        }

        void Awake()
        {
            if (button == null) button = GetComponent<Button>();
            if (background == null) background = GetComponent<Image>();
            if (canvasGroup == null) canvasGroup = GetComponent<CanvasGroup>();
            if (label == null) label = GetComponentInChildren<TextMeshProUGUI>(true);

            if (button != null)
            {
                button.onClick.RemoveListener(HandleClick);
                button.onClick.AddListener(HandleClick);
            }
        }

        void OnEnable()
        {
            _hovered = false;
            transform.localScale = _baseScale;
            if (background != null) background.color = normalColor;
        }

        void Update()
        {
            float target = (_hovered && _interactable) ? hoverScale : 1f;
            transform.localScale = Vector3.Lerp(transform.localScale, _baseScale * target,
                                                Time.unscaledDeltaTime * hoverLerpSpeed);
            if (background != null)
                background.color = Color.Lerp(background.color,
                                              (_hovered && _interactable) ? hoverColor : normalColor,
                                              Time.unscaledDeltaTime * hoverLerpSpeed);
        }

        /// <summary>设置文字和点击回调。</summary>
        public void Setup(string text, Action onClick)
        {
            _onClick = onClick;
            if (label != null) label.text = text ?? string.Empty;
            SetInteractable(true);
        }

        /// <summary>选完之后把所有按钮禁掉，避免连点选中两个。</summary>
        public void SetInteractable(bool value)
        {
            _interactable = value;
            if (button != null) button.interactable = value;
            if (canvasGroup != null) canvasGroup.blocksRaycasts = value;
        }

        void HandleClick()
        {
            if (!_interactable) return;
            if (clickSfx != null && Love.Audio.AudioManager.Instance != null)
                Love.Audio.AudioManager.Instance.PlaySfx(clickSfx);
            var cb = _onClick;
            _onClick = null;
            cb?.Invoke();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!_interactable) return;
            _hovered = true;
            if (hoverSfx != null && Love.Audio.AudioManager.Instance != null)
                Love.Audio.AudioManager.Instance.PlaySfx(hoverSfx);
        }

        public void OnPointerExit(PointerEventData eventData) => _hovered = false;
    }
}
