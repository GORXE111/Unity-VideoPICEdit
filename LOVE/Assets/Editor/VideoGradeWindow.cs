using System.IO;
using Love.Video;
using UnityEditor;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// 调色台（编辑器窗口）。
    ///
    /// 相比运行时那套面板的好处：不遮画面、能输精确数值、支持 Ctrl+Z 撤销、可停靠。
    /// 代价是只能在编辑器里用——视频只有进 Play 才会解码出画，所以实时预览要在播放模式下看。
    ///
    /// 参数直接改的是场景里 VideoPostProcessor 上那个 settings 对象，
    /// 它每帧都会被读去设置 shader uniform，所以拖动即时生效。
    /// </summary>
    public class VideoGradeWindow : EditorWindow
    {
        [MenuItem("Tools/影视游戏/调色台", false, 5)]
        public static void Open()
        {
            var w = GetWindow<VideoGradeWindow>("调色台");
            w.minSize = new Vector2(340f, 400f);
            w.Show();
        }

        VideoPostProcessor _target;
        Vector2 _scroll;
        double _lastRepaint;

        bool _foldMonitor = true;

        /// <summary>参数界面和修图台共用一份实现。</summary>
        readonly GradeSettingsGUI _gui = new GradeSettingsGUI();

        const string DefaultPresetPath = "Assets/StreamingAssets/Story/grade.json";

        void OnEnable() => titleContent = new GUIContent("调色台");

        void Update()
        {
            // 播放中要让直方图动起来，但没必要每个编辑器 tick 都重绘
            if (!EditorApplication.isPlaying) return;
            if (EditorApplication.timeSinceStartup - _lastRepaint < 0.08) return;
            _lastRepaint = EditorApplication.timeSinceStartup;
            Repaint();
        }

        void OnGUI()
        {
            if (_target == null) _target = FindObjectOfType<VideoPostProcessor>();

            if (_target == null)
            {
                EditorGUILayout.HelpBox(
                    "当前场景里没有 VideoPostProcessor。\n打开 Assets/Scenes/Main.unity，或先跑一次「一键搭建全部」。",
                    MessageType.Info);
                if (GUILayout.Button("重新查找")) _target = FindObjectOfType<VideoPostProcessor>();
                return;
            }

            if (!EditorApplication.isPlaying)
                EditorGUILayout.HelpBox("当前不在播放模式。参数改动会存进场景，但要进 Play 才能看到画面效果。", MessageType.None);

            DrawToolbar();

            // 标签宽度跟着窗口走。用 Unity 的固定默认值时，
            // 窗口一窄「反向（选窗口外）」这种标签就被截成「反向（选窗…」
            float prevLabel = EditorGUIUtility.labelWidth;
            bool prevWide = EditorGUIUtility.wideMode;
            EditorGUIUtility.labelWidth = Mathf.Clamp(position.width * 0.42f, 84f, 220f);
            EditorGUIUtility.wideMode = true;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var s = _target.settings;

            // 参数界面和修图台共用同一份，避免 60 多个滑条维护两遍
            _gui.PreviewTexture = _target.Output;
            _gui.PanelWidth = position.width;
            _gui.Draw(s, _target);
            // 转盘弹窗的改动落在 OnGUI 之外，读一次让预览跟上
            if (_gui.ConsumeExternalChange()) Repaint();

            _foldMonitor = GradeSettingsGUI.Section(_foldMonitor, "监看");
            if (_foldMonitor) DrawMonitor();

            EditorGUILayout.Space(8f);
            EditorGUILayout.EndScrollView();

            EditorGUIUtility.labelWidth = prevLabel;
            EditorGUIUtility.wideMode = prevWide;
        }


        #region 工具条与监看

        /// <summary>
        /// 注册撤销。settings 是 MonoBehaviour 上的普通字段，
        /// RecordObject 能把整个组件状态存进撤销栈，Ctrl+Z 一步回退。
        /// </summary>
        void RecordUndo(string action = "调色")
        {
            if (_target == null) return;
            Undo.RecordObject(_target, action);
            if (!EditorApplication.isPlaying) EditorUtility.SetDirty(_target);
        }

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            bool bypass = GUILayout.Toggle(_target.bypass, "原图对比", EditorStyles.toolbarButton);
            if (bypass != _target.bypass) { RecordUndo("切换原图对比"); _target.bypass = bypass; }

            bool split = GUILayout.Toggle(_target.splitCompare, "分屏", EditorStyles.toolbarButton);
            if (split != _target.splitCompare) { RecordUndo("切换分屏"); _target.splitCompare = split; }

            bool pattern = GUILayout.Toggle(_target.showTestPattern, "测试卡", EditorStyles.toolbarButton);
            if (pattern != _target.showTestPattern) { RecordUndo("切换测试卡"); _target.showTestPattern = pattern; }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("重置", EditorStyles.toolbarButton))
            {
                RecordUndo("重置调色");
                _target.settings.Reset();
            }
            if (GUILayout.Button("胶片化", EditorStyles.toolbarButton))
            {
                RecordUndo("胶片化预设");
                _target.settings.ApplyFilmLook();
            }
            if (GUILayout.Button("载入", EditorStyles.toolbarButton)) LoadPresetMenu();
            if (GUILayout.Button("保存", EditorStyles.toolbarButton)) SavePresetMenu();

            EditorGUILayout.EndHorizontal();

            if (_target.splitCompare)
            {
                float pos = EditorGUILayout.Slider("分割线位置", _target.splitPosition, 0f, 1f);
                if (!Mathf.Approximately(pos, _target.splitPosition))
                {
                    RecordUndo("调整分割线");
                    _target.splitPosition = pos;
                }
            }
        }

        void DrawMonitor()
        {
            bool on = EditorGUILayout.Toggle("启用直方图", _target.histogramEnabled);
            if (on != _target.histogramEnabled) { RecordUndo("直方图开关"); _target.histogramEnabled = on; }

            int interval = EditorGUILayout.IntSlider("刷新间隔（帧）", _target.histogramInterval, 1, 20);
            if (interval != _target.histogramInterval) { RecordUndo("直方图间隔"); _target.histogramInterval = interval; }

            var rect = GUILayoutUtility.GetRect(10f, 140f, GUILayout.ExpandWidth(true));
            DrawHistogram(rect);

            EditorGUILayout.Space(6f);
            EditorGUI.BeginChangeCheck();
            var kind = (VideoPostProcessor.ScopeKind)EditorGUILayout.EnumPopup("示波器", _target.scopeKind);
            if (EditorGUI.EndChangeCheck()) { RecordUndo("切换示波器"); _target.scopeKind = kind; }

            if (_target.scopeKind != VideoPostProcessor.ScopeKind.关闭)
            {
                var scope = _target.ScopeTexture;
                var sr = GUILayoutUtility.GetRect(10f, 176f, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(sr, new Color(0.04f, 0.05f, 0.06f));
                if (scope != null) GUI.DrawTexture(sr, scope, ScaleMode.ScaleToFit);

                EditorGUILayout.LabelField(ScopeHint(_target.scopeKind), EditorStyles.miniLabel);
            }

            if (!EditorApplication.isPlaying)
                EditorGUILayout.LabelField("（直方图和示波器只在播放模式下更新）", EditorStyles.miniLabel);
        }

        static string ScopeHint(VideoPostProcessor.ScopeKind kind)
        {
            switch (kind)
            {
                case VideoPostProcessor.ScopeKind.波形图:
                    return "横轴＝画面横向位置，纵轴＝亮度。看曝光是否过顶或压死";
                case VideoPostProcessor.ScopeKind.分量波形:
                    return "R / G / B 三段并排。三段基线不齐就是有偏色";
                case VideoPostProcessor.ScopeKind.矢量示波器:
                    return "圆心＝无彩色，角度＝色相，半径＝饱和度。肤色应落在左上偏 11 点方向";
                default:
                    return string.Empty;
            }
        }

        void DrawHistogram(Rect r)
        {
            EditorGUI.DrawRect(r, new Color(0.07f, 0.08f, 0.10f));

            // 四等分参考线，方便看黑位/中间调/白位落在哪
            for (int q = 1; q <= 3; q++)
            {
                float x = r.x + r.width * q / 4f;
                EditorGUI.DrawRect(new Rect(x, r.y, 1f, r.height), new Color(1f, 1f, 1f, 0.10f));
            }

            if (_target == null) return;
            // 半透明叠加，重叠处自然变亮，效果接近加色混合
            DrawBins(r, _target.HistogramR, new Color(1f, 0.25f, 0.25f, 0.55f));
            DrawBins(r, _target.HistogramG, new Color(0.25f, 1f, 0.35f, 0.55f));
            DrawBins(r, _target.HistogramB, new Color(0.3f, 0.5f, 1f, 0.55f));
        }

        void DrawBins(Rect r, float[] bins, Color color)
        {
            if (bins == null || bins.Length == 0) return;
            float w = r.width / bins.Length;
            for (int i = 0; i < bins.Length; i++)
            {
                float h = Mathf.Clamp01(bins[i]) * (r.height - 2f);
                if (h <= 0.5f) continue;
                EditorGUI.DrawRect(new Rect(r.x + i * w, r.yMax - h, Mathf.Max(1f, w), h), color);
            }
        }

        #endregion

        #region 预设存读

        void SavePresetMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("保存到 grade.json（随包发布）"), false, () =>
            {
                WriteJson(DefaultPresetPath);
                AssetDatabase.Refresh();
            });
            menu.AddItem(new GUIContent("另存为…"), false, () =>
            {
                string path = EditorUtility.SaveFilePanel("保存调色预设",
                    Path.GetDirectoryName(DefaultPresetPath), "grade_preset", "json");
                if (!string.IsNullOrEmpty(path)) { WriteJson(path); AssetDatabase.Refresh(); }
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("复制 JSON 到剪贴板"), false, () =>
            {
                EditorGUIUtility.systemCopyBuffer = _target.settings.ToJson();
                Debug.Log("[调色台] 参数已复制到剪贴板");
            });
            menu.ShowAsContext();
        }

        void LoadPresetMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("载入 grade.json"), false, () => ReadJson(DefaultPresetPath));
            menu.AddItem(new GUIContent("从文件载入…"), false, () =>
            {
                string path = EditorUtility.OpenFilePanel("载入调色预设",
                    Path.GetDirectoryName(DefaultPresetPath), "json");
                if (!string.IsNullOrEmpty(path)) ReadJson(path);
            });
            menu.AddItem(new GUIContent("从剪贴板粘贴"), false, () =>
            {
                var loaded = VideoGradeSettings.FromJson(EditorGUIUtility.systemCopyBuffer);
                if (loaded == null) { Debug.LogError("[调色台] 剪贴板里不是合法的调色 JSON"); return; }
                RecordUndo("粘贴调色参数");
                _target.settings.CopyFrom(loaded);
            });
            menu.ShowAsContext();
        }

        void WriteJson(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, _target.settings.ToJson(), System.Text.Encoding.UTF8);
                Debug.Log($"[调色台] 已保存：{path}");
            }
            catch (System.Exception e) { Debug.LogError($"[调色台] 保存失败：{e.Message}"); }
        }

        void ReadJson(string path)
        {
            if (!File.Exists(path)) { Debug.LogError($"[调色台] 文件不存在：{path}"); return; }
            var loaded = VideoGradeSettings.FromJson(File.ReadAllText(path, System.Text.Encoding.UTF8));
            if (loaded == null) return;
            RecordUndo("载入调色参数");
            _target.settings.CopyFrom(loaded);
            Debug.Log($"[调色台] 已载入：{path}");
        }

        #endregion

    }
}
