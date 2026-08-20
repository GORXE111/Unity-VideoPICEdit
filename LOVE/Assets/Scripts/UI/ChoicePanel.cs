using System;
using System.Collections;
using System.Collections.Generic;
using Love.Story;
using UnityEngine;
using UnityEngine.UI;

namespace Love.UI
{
    /// <summary>
    /// 选项面板。负责在视频上方弹出 1~2 个横向排列的选项按钮。
    /// 布局与参考图一致：底部居中、左右并排、单个选项时自动居中。
    /// </summary>
    [DisallowMultipleComponent]
    public class ChoicePanel : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("按钮的父节点，挂了 HorizontalLayoutGroup + ContentSizeFitter")]
        public RectTransform container;
        [Tooltip("选项按钮预制体")]
        public ChoiceButtonView buttonPrefab;
        [Tooltip("整个面板的 CanvasGroup，用于淡入淡出")]
        public CanvasGroup canvasGroup;

        [Header("参数")]
        [Tooltip("最多同时显示几个选项")]
        public int maxChoices = 2;
        [Tooltip("出现动画时长（秒）")]
        public float fadeInDuration = 0.3f;
        [Tooltip("消失动画时长（秒）")]
        public float fadeOutDuration = 0.2f;
        [Tooltip("出现时从多低的位置升上来（像素）")]
        public float riseOffset = 30f;
        [Tooltip("两个选项之间错开出现的间隔（秒）")]
        public float stagger = 0.06f;

        readonly List<ChoiceButtonView> _pool = new List<ChoiceButtonView>();
        Action<int> _onSelected;
        Coroutine _anim;
        Vector2 _containerBasePos;
        bool _basePosCached;

        /// <summary>当前是否正在显示选项。</summary>
        public bool IsShowing { get; private set; }

        void Awake()
        {
            if (canvasGroup == null) canvasGroup = GetComponent<CanvasGroup>();
            CacheBasePos();
            HideImmediate();
        }

        void CacheBasePos()
        {
            if (_basePosCached || container == null) return;
            _containerBasePos = container.anchoredPosition;
            _basePosCached = true;
        }

        /// <summary>
        /// 弹出选项。
        /// </summary>
        /// <param name="choices">选项数据，超过 maxChoices 的部分会被忽略</param>
        /// <param name="onSelected">玩家选中后回调，参数是选项下标</param>
        public void Show(IList<StoryChoice> choices, Action<int> onSelected)
        {
            if (choices == null || choices.Count == 0) return;
            CacheBasePos();

            int count = Mathf.Min(choices.Count, maxChoices);
            if (choices.Count > maxChoices)
                Debug.LogWarning($"[ChoicePanel] 配置了 {choices.Count} 个选项，当前只支持 {maxChoices} 个，多余的被忽略");

            _onSelected = onSelected;
            EnsurePool(count);

            for (int i = 0; i < _pool.Count; i++)
            {
                var view = _pool[i];
                bool used = i < count;
                view.gameObject.SetActive(used);
                if (!used) continue;

                int index = i;   // 闭包捕获
                view.Setup(choices[i].text, () => HandleSelected(index));
                if (view.canvasGroup != null) view.canvasGroup.alpha = 0f;
            }

            gameObject.SetActive(true);
            IsShowing = true;

            if (_anim != null) StopCoroutine(_anim);
            _anim = StartCoroutine(PlayShowAnim(count));
        }

        /// <summary>收起选项（带淡出动画）。</summary>
        public void Hide()
        {
            if (!IsShowing) return;
            IsShowing = false;
            foreach (var v in _pool) v.SetInteractable(false);
            if (_anim != null) StopCoroutine(_anim);
            _anim = StartCoroutine(PlayHideAnim());
        }

        /// <summary>立刻收起，不播动画。</summary>
        public void HideImmediate()
        {
            IsShowing = false;
            _onSelected = null;
            if (_anim != null) { StopCoroutine(_anim); _anim = null; }
            if (canvasGroup != null)
            {
                canvasGroup.alpha = 0f;
                canvasGroup.blocksRaycasts = false;
                canvasGroup.interactable = false;
            }
            foreach (var v in _pool) v.gameObject.SetActive(false);
            if (container != null && _basePosCached) container.anchoredPosition = _containerBasePos;
            gameObject.SetActive(false);
        }

        void HandleSelected(int index)
        {
            if (!IsShowing) return;
            var cb = _onSelected;
            _onSelected = null;
            Hide();
            cb?.Invoke(index);
        }

        void EnsurePool(int count)
        {
            if (buttonPrefab == null)
            {
                Debug.LogError("[ChoicePanel] 没有配置 buttonPrefab");
                return;
            }
            while (_pool.Count < Mathf.Max(count, maxChoices))
            {
                var view = Instantiate(buttonPrefab, container);
                view.name = $"Choice_{_pool.Count}";
                if (view.canvasGroup == null) view.canvasGroup = view.GetComponent<CanvasGroup>();
                if (view.canvasGroup == null) view.canvasGroup = view.gameObject.AddComponent<CanvasGroup>();
                view.gameObject.SetActive(false);
                _pool.Add(view);
            }
        }

        IEnumerator PlayShowAnim(int count)
        {
            if (canvasGroup != null)
            {
                canvasGroup.alpha = 0f;
                canvasGroup.blocksRaycasts = true;
                canvasGroup.interactable = true;
            }
            if (container != null) container.anchoredPosition = _containerBasePos + Vector2.down * riseOffset;

            float t = 0f;
            float total = fadeInDuration + stagger * Mathf.Max(0, count - 1);
            while (t < total)
            {
                t += Time.unscaledDeltaTime;

                float k = Mathf.Clamp01(t / Mathf.Max(0.0001f, fadeInDuration));
                float ease = 1f - (1f - k) * (1f - k);   // EaseOutQuad
                if (canvasGroup != null) canvasGroup.alpha = ease;
                if (container != null)
                    container.anchoredPosition = _containerBasePos + Vector2.down * (riseOffset * (1f - ease));

                for (int i = 0; i < count; i++)
                {
                    float bt = Mathf.Clamp01((t - stagger * i) / Mathf.Max(0.0001f, fadeInDuration));
                    float be = 1f - (1f - bt) * (1f - bt);
                    var cg = _pool[i].canvasGroup;
                    if (cg != null) cg.alpha = be;
                }
                yield return null;
            }

            if (canvasGroup != null) canvasGroup.alpha = 1f;
            if (container != null) container.anchoredPosition = _containerBasePos;
            for (int i = 0; i < count; i++)
                if (_pool[i].canvasGroup != null) _pool[i].canvasGroup.alpha = 1f;

            _anim = null;
        }

        IEnumerator PlayHideAnim()
        {
            if (canvasGroup != null) { canvasGroup.blocksRaycasts = false; canvasGroup.interactable = false; }

            float start = canvasGroup != null ? canvasGroup.alpha : 1f;
            float t = 0f;
            while (t < fadeOutDuration)
            {
                t += Time.unscaledDeltaTime;
                if (canvasGroup != null)
                    canvasGroup.alpha = Mathf.Lerp(start, 0f, Mathf.Clamp01(t / fadeOutDuration));
                yield return null;
            }
            _anim = null;
            HideImmediate();
        }
    }
}
