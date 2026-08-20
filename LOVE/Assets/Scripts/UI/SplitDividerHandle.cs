using Love.Video;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Love.UI
{
    /// <summary>
    /// 分屏对比的分割线手柄。拖它就能左右移动分界。
    ///
    /// 位置换算走的是视频 RawImage 的矩形而不是屏幕宽度：
    /// 视频比例和屏幕不一致时画面是带黑边的，按屏幕算会和实际画面对不上。
    /// </summary>
    [DisallowMultipleComponent]
    public class SplitDividerHandle : MonoBehaviour, IPointerDownHandler, IDragHandler
    {
        public VideoPostProcessor postProcessor;
        [Tooltip("显示视频的 RawImage 的 RectTransform")]
        public RectTransform videoRect;

        RectTransform _rt;
        RectTransform _parentRect;

        void Awake()
        {
            _rt = (RectTransform)transform;
            _parentRect = transform.parent as RectTransform;
        }

        public void OnPointerDown(PointerEventData e) => ApplyFromPointer(e);
        public void OnDrag(PointerEventData e) => ApplyFromPointer(e);

        void ApplyFromPointer(PointerEventData e)
        {
            if (postProcessor == null || videoRect == null) return;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    videoRect, e.position, e.pressEventCamera, out var local)) return;

            var r = videoRect.rect;
            postProcessor.splitPosition = Mathf.Clamp01((local.x - r.xMin) / Mathf.Max(1e-4f, r.width));
        }

        void LateUpdate()
        {
            if (postProcessor == null || videoRect == null || _rt == null || _parentRect == null) return;

            // 把手柄贴到分割线当前所在的屏幕位置
            var r = videoRect.rect;
            float localX = r.xMin + r.width * Mathf.Clamp01(postProcessor.splitPosition);
            Vector3 world = videoRect.TransformPoint(new Vector3(localX, 0f, 0f));
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, world);

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _parentRect, screen, null, out var localInParent))
                _rt.anchoredPosition = new Vector2(localInParent.x, 0f);
        }
    }
}
