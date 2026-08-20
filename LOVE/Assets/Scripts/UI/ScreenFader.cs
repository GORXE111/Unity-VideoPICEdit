using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Love.UI
{
    /// <summary>
    /// 全屏黑场遮罩。用于剧情段之间的过渡，避免切换视频时闪一下上一段的定格画面。
    /// </summary>
    [DisallowMultipleComponent]
    public class ScreenFader : MonoBehaviour
    {
        [Tooltip("覆盖全屏的纯色 Image")]
        public Image overlay;
        [Tooltip("过渡颜色，默认黑")]
        public Color fadeColor = Color.black;

        Coroutine _routine;

        /// <summary>当前遮罩不透明度（1 = 全黑）。</summary>
        public float Alpha => overlay != null ? overlay.color.a : 0f;

        void Awake()
        {
            if (overlay == null) overlay = GetComponent<Image>();
            if (overlay != null)
            {
                overlay.raycastTarget = false;
                SetAlpha(overlay.color.a);
            }
        }

        public void SetAlpha(float a)
        {
            if (overlay == null) return;
            var c = fadeColor;
            c.a = Mathf.Clamp01(a);
            overlay.color = c;
            overlay.enabled = c.a > 0.001f;
        }

        /// <summary>淡到全黑。</summary>
        public Coroutine FadeOut(float duration) => Run(1f, duration);

        /// <summary>从全黑淡出到透明。</summary>
        public Coroutine FadeIn(float duration) => Run(0f, duration);

        Coroutine Run(float target, float duration)
        {
            if (_routine != null) StopCoroutine(_routine);
            _routine = StartCoroutine(FadeRoutine(target, duration));
            return _routine;
        }

        IEnumerator FadeRoutine(float target, float duration)
        {
            if (overlay == null) yield break;

            float start = overlay.color.a;
            if (duration <= 0.001f)
            {
                SetAlpha(target);
                _routine = null;
                yield break;
            }

            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                SetAlpha(Mathf.Lerp(start, target, Mathf.Clamp01(t / duration)));
                yield return null;
            }
            SetAlpha(target);
            _routine = null;
        }
    }
}
