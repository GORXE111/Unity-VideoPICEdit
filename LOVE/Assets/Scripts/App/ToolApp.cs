using Love.Tools;
using UnityEngine;

namespace Love.App
{
    /// <summary>
    /// 工具包的外壳：上面一排页签，下面是当前那个台。
    ///
    /// 修图台和视频台共用一套东西——调色管线、参数界面、控件层，
    /// 所以这些放在外壳上，两个台各自只管自己的源和时间轴。
    /// </summary>
    [DisallowMultipleComponent]
    public class ToolApp : MonoBehaviour
    {
        [Tooltip("VideoGrade 材质。必须在场景里引用着，否则出包时 shader 会被剔掉")]
        public Material gradeMaterial;

        /// <summary>参数栏宽度。窗口窄的时候按比例收一点，别把画布挤没。</summary>
        public float PanelWidth => Mathf.Clamp(Screen.width * 0.26f, 260f, 380f);

        readonly RuntimeGradeGui _backend = new RuntimeGradeGui();
        readonly RuntimeGui _ui = new RuntimeGui();
        GradeSettingsGUI _params;

        PhotoStation _photo;
        VideoStation _video;
        int _tab;

        static readonly string[] TabNames = { "修图台", "视频台" };

        void Awake()
        {
            // 工具跑起来之后大部分时间在后台解码 / 推理，
            // 不勾这个的话窗口一失焦整个进程就停摆
            Application.runInBackground = true;
        }

        void OnEnable()
        {
            _params = new GradeSettingsGUI(_backend);
            _photo = new PhotoStation(gradeMaterial, _ui);
            _video = new VideoStation(gradeMaterial, _ui);
        }

        void OnDisable()
        {
            _photo?.Dispose();
            _video?.Dispose();
            _ui.Dispose();
            _backend.Dispose();
        }

        IStation Active => _tab == 0 ? (IStation)_photo : _video;

        void Update() => Active?.Tick();

        void OnGUI()
        {
            _ui.EnsureSkin();

            float panelW = PanelWidth;
            var full = new Rect(0f, 0f, Screen.width, Screen.height);
            GUI.DrawTexture(full, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0f,
                            new Color(0.16f, 0.16f, 0.17f, 1f), 0f, 0f);

            const float TabH = 26f;
            int tab = GUI.Toolbar(new Rect(6f, 4f, 200f, TabH - 4f), _tab, TabNames);
            if (tab != _tab)
            {
                // 切走的时候把播放停掉，不然它在后台一直解码
                Active?.OnHide();
                _tab = tab;
            }

            GUI.Label(new Rect(214f, 6f, Screen.width - 220f, 18f),
                      Active?.Status ?? "", _ui.Mini);

            var canvas = new Rect(0f, TabH, Screen.width - panelW, Screen.height - TabH);
            var panel = new Rect(canvas.xMax, TabH, panelW, Screen.height - TabH);

            Active?.DrawCanvas(canvas);

            GUILayout.BeginArea(panel);
            _ui.BeginFrame();
            Active?.DrawPanel(_ui);

            // 参数界面就是编辑器修图台那一份，一行没抄
            if (Active != null && Active.HasSource)
            {
                _params.PanelWidth = panelW - 16f;
                _params.SourceSize = Active.SourceSize;
                _params.PreviewTexture = Active.Preview;

                _backend.LabelWidth = Mathf.Clamp(panelW * 0.34f, 78f, 120f);

                _backend.BeginChange();
                _params.Draw(Active.Settings);
                if (_backend.EndChange() || _params.ConsumeExternalChange()) Active.MarkDirty();
            }
            GUILayout.EndArea();
        }
    }

    /// <summary>两个台共用的形状。外壳只认这个，不认具体是哪个台。</summary>
    public interface IStation
    {
        string Status { get; }
        bool HasSource { get; }
        Vector2Int SourceSize { get; }
        Texture Preview { get; }
        Love.Video.VideoGradeSettings Settings { get; }

        void MarkDirty();
        void Tick();
        void DrawCanvas(Rect area);
        void DrawPanel(RuntimeGui ui);

        /// <summary>切到别的页签了。播放这类后台活动要停掉。</summary>
        void OnHide();

        void Dispose();
    }
}
