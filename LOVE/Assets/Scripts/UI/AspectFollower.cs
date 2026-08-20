using UnityEngine;
using UnityEngine.UI;

namespace Love.UI
{
    /// <summary>
    /// 让本节点的显示区域和视频画面完全重合。
    ///
    /// 窗口比例和视频不一致时，视频是带黑边的（AspectRatioFitter 的 FitInParent）。
    /// 选项按钮如果按整个屏幕定位，就会掉进黑边里，看着像飘在画面外。
    /// 这个组件把视频那个 Fitter 的比例照抄过来，本节点就永远和画面区域对齐。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AspectRatioFitter))]
    public class AspectFollower : MonoBehaviour
    {
        [Tooltip("要跟随的目标，通常是视频 RawImage 上那个 AspectRatioFitter")]
        public AspectRatioFitter source;

        AspectRatioFitter _self;

        void Awake()
        {
            _self = GetComponent<AspectRatioFitter>();
            if (source == null)
            {
                var ui = GameplayUIRoot.Find();
                if (ui != null) source = ui.videoAspectFitter;
            }
        }

        void LateUpdate()
        {
            if (_self == null || source == null) return;
            if (_self.aspectMode != source.aspectMode) _self.aspectMode = source.aspectMode;
            if (!Mathf.Approximately(_self.aspectRatio, source.aspectRatio))
                _self.aspectRatio = source.aspectRatio;
        }
    }
}
