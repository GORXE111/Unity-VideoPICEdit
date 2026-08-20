using UnityEngine;
using UnityEngine.UI;

namespace Love.UI
{
    /// <summary>
    /// 玩法 UI 面板的根节点，挂在 GameplayUI 预制体上。
    ///
    /// 作用是把面板内部的关键部件集中暴露出来，这样 Systems 那边的 StoryDirector /
    /// VideoScreen / VideoPostProcessor 在自己的引用为空时能自动找到它们。
    /// 于是这个预制体拖到任何场景里都能直接用，不用手动一根根连线。
    /// </summary>
    [DisallowMultipleComponent]
    public class GameplayUIRoot : MonoBehaviour
    {
        [Header("画布")]
        public Canvas canvas;
        public CanvasScaler scaler;

        [Header("视频层")]
        [Tooltip("显示视频画面的 RawImage")]
        public RawImage videoImage;
        public AspectRatioFitter videoAspectFitter;

        [Header("功能层")]
        public PlaceholderView placeholderView;
        public ChoicePanel choicePanel;
        public TitleScreen titleScreen;
        public ScreenFader fader;

        static GameplayUIRoot _cached;

        /// <summary>找场景里的 UI 面板根节点。结果会缓存，不会每次都全场景扫。</summary>
        public static GameplayUIRoot Find()
        {
            if (_cached != null) return _cached;
            _cached = FindObjectOfType<GameplayUIRoot>(true);
            return _cached;
        }

        void Awake()
        {
            _cached = this;
            if (canvas == null) canvas = GetComponent<Canvas>();
            if (scaler == null) scaler = GetComponent<CanvasScaler>();
        }

        void OnDestroy()
        {
            if (_cached == this) _cached = null;
        }
    }
}
