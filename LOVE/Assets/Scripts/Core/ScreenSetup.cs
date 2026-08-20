using UnityEngine;

namespace Love.Core
{
    /// <summary>
    /// 启动时的窗口设置。
    ///
    /// 默认窗口化，并且按桌面分辨率算一个合适的初始窗口大小——
    /// PlayerSettings 里那个固定的 1920x1080 在 1080p 桌面上会正好占满屏幕，
    /// 标题栏被顶出可视区，窗口拖都拖不动。
    ///
    /// 玩家改过的窗口大小和全屏状态会记住，下次启动沿用。
    /// </summary>
    [DisallowMultipleComponent]
    public class ScreenSetup : MonoBehaviour
    {
        const string PrefWidth  = "screen.width";
        const string PrefHeight = "screen.height";
        const string PrefFull   = "screen.fullscreen";

        [Header("启动状态")]
        [Tooltip("勾上则默认窗口模式启动；取消则全屏")]
        public bool startWindowed = true;

        [Tooltip("首次启动时，窗口占桌面的比例。0.8 表示留出两成给任务栏和标题栏")]
        [Range(0.4f, 1f)] public float initialScreenFraction = 0.8f;

        [Tooltip("窗口保持这个宽高比，和视频一致就不会有黑边")]
        public Vector2Int windowAspect = new Vector2Int(16, 9);

        [Header("交互")]
        public KeyCode toggleFullscreenKey = KeyCode.F11;

        [Tooltip("记住玩家改过的窗口大小和全屏状态")]
        public bool rememberWindowState = true;

        Vector2Int _lastWindowedSize;

        void Awake()
        {
            // 编辑器里改分辨率没意义，Game 视图有自己的一套
            if (Application.isEditor) return;

            ApplyInitialWindow();
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleFullscreenKey)) ToggleFullscreen();

            // 玩家自己拖窗口改了大小，记下来
            if (rememberWindowState && !Screen.fullScreen &&
                (Screen.width != _lastWindowedSize.x || Screen.height != _lastWindowedSize.y))
            {
                _lastWindowedSize = new Vector2Int(Screen.width, Screen.height);
                SaveState();
            }
        }

        void ApplyInitialWindow()
        {
            bool fullscreen = !startWindowed;
            Vector2Int size = ComputeDefaultWindowSize();

            if (rememberWindowState && PlayerPrefs.HasKey(PrefWidth))
            {
                int w = PlayerPrefs.GetInt(PrefWidth, size.x);
                int h = PlayerPrefs.GetInt(PrefHeight, size.y);
                // 换了显示器/改了分辨率之后，存的尺寸可能比桌面还大，那就退回算出来的默认值
                if (w > 0 && h > 0 && w <= Display.main.systemWidth && h <= Display.main.systemHeight)
                    size = new Vector2Int(w, h);

                fullscreen = PlayerPrefs.GetInt(PrefFull, fullscreen ? 1 : 0) == 1;
            }

            _lastWindowedSize = size;
            Screen.SetResolution(size.x, size.y,
                fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed);
        }

        /// <summary>算一个能放进桌面、且保持目标宽高比的最大窗口。</summary>
        public Vector2Int ComputeDefaultWindowSize()
        {
            int dw = Mathf.Max(640, Display.main.systemWidth);
            int dh = Mathf.Max(480, Display.main.systemHeight);

            float budgetW = dw * initialScreenFraction;
            float budgetH = dh * initialScreenFraction;

            float aspect = windowAspect.y > 0
                ? (float)windowAspect.x / windowAspect.y
                : 16f / 9f;

            int w, h;
            if (budgetW / budgetH > aspect)
            {
                // 桌面比目标比例更宽，高度是瓶颈
                h = Mathf.RoundToInt(budgetH);
                w = Mathf.RoundToInt(h * aspect);
            }
            else
            {
                w = Mathf.RoundToInt(budgetW);
                h = Mathf.RoundToInt(w / aspect);
            }

            return new Vector2Int(Mathf.Max(640, w), Mathf.Max(360, h));
        }

        public void ToggleFullscreen()
        {
            if (Screen.fullScreen)
            {
                var size = _lastWindowedSize.x > 0 ? _lastWindowedSize : ComputeDefaultWindowSize();
                Screen.SetResolution(size.x, size.y, FullScreenMode.Windowed);
            }
            else
            {
                _lastWindowedSize = new Vector2Int(Screen.width, Screen.height);
                Screen.SetResolution(Display.main.systemWidth, Display.main.systemHeight,
                                     FullScreenMode.FullScreenWindow);
            }
            SaveState();
        }

        void SaveState()
        {
            if (!rememberWindowState) return;
            PlayerPrefs.SetInt(PrefWidth, _lastWindowedSize.x);
            PlayerPrefs.SetInt(PrefHeight, _lastWindowedSize.y);
            PlayerPrefs.SetInt(PrefFull, Screen.fullScreen ? 1 : 0);
        }

        /// <summary>清掉记住的窗口状态，下次启动回到自动计算的默认值。</summary>
        [ContextMenu("清除窗口状态存档")]
        public void ClearSavedState()
        {
            PlayerPrefs.DeleteKey(PrefWidth);
            PlayerPrefs.DeleteKey(PrefHeight);
            PlayerPrefs.DeleteKey(PrefFull);
            Debug.Log("[ScreenSetup] 窗口状态存档已清除");
        }
    }
}
