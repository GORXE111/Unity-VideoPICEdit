using System.IO;
using Love.App;
using Love.Core;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// 工具包（修图台 + 视频台）的场景生成与打包。
    ///
    /// 和影视游戏那条线完全分开：不同的场景、不同的输出目录、不同的 exe。
    /// 场景一样是代码生成的，手改改完下次重建就没了。
    /// </summary>
    public static class ToolAppBuild
    {
        const string ScenePath = "Assets/Scenes/ToolApp.unity";
        const string MatPath = "Assets/GameAssets/Materials/VideoGrade.mat";
        const string ShaderPath = "Assets/GameAssets/Shaders/VideoGrade.shader";
        const string OutDir = "../Build/工具包";
        const string ExeName = "调色工具台.exe";

        [MenuItem("Tools/工具包/生成场景", false, 200)]
        public static void SetupScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // NewScene 会卸载未使用资源，之前拿到的资产引用会变成 Unity 的"假 null"：
            // == null 为真，但赋值给序列化字段时 instanceID 仍能解析出正确的 GUID，
            // 于是磁盘上的 YAML 看着完全正常、只有部分代码路径失效。所以在这之后才去读
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null)
            {
                Debug.LogError("[工具包] 找不到调色材质：" + MatPath +
                               "\n先跑一次「一键搭建全部」把它生成出来。");
                return;
            }

            var camGo = new GameObject("Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.16f, 0.17f, 1f);
            cam.orthographic = true;
            cam.cullingMask = 0;          // 什么都不画，界面全走 OnGUI

            var appGo = new GameObject("ToolApp");
            var app = appGo.AddComponent<ToolApp>();
            app.gradeMaterial = mat;

            // 窗口化。工具不该全屏——两个台都要和资源管理器、播放器来回切
            var screen = appGo.AddComponent<ScreenSetup>();
            screen.startWindowed = true;
            screen.initialScreenFraction = 0.82f;
            screen.windowAspect = new Vector2Int(16, 10);
            screen.rememberWindowState = true;

            EditorSceneManager.MarkSceneDirty(scene);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath) ?? "Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            EnsureShaderIncluded();
            Debug.Log("[工具包] 场景已生成：" + ScenePath);
        }

        /// <summary>
        /// 把调色 shader 塞进「总是包含」。
        ///
        /// 场景里引用着材质通常就够了，但这条管线用的是 shader_feature_local 的多变体，
        /// 而且渲染器是运行时 new 出来的、不挂在任何 Renderer 上。
        /// 漏了的表现是画面全黑或者全粉，而且只在出包之后才出现。
        /// </summary>
        static void EnsureShaderIncluded()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null) return;

            var gs = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.GraphicsSettings>(
                "ProjectSettings/GraphicsSettings.asset");
            if (gs == null) return;

            var so = new SerializedObject(gs);
            var list = so.FindProperty("m_AlwaysIncludedShaders");
            if (list == null) return;

            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;

            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            Debug.Log("[工具包] VideoGrade.shader 已加进「总是包含的 Shader」");
        }

        [MenuItem("Tools/工具包/打包 Windows（修图台 + 视频台）", false, 201)]
        public static void BuildWindows()
        {
            if (!File.Exists(ScenePath))
            {
                Debug.Log("[工具包] 场景还没有，先生成一份");
                SetupScene();
                if (!File.Exists(ScenePath)) return;
            }

            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", OutDir));
            Directory.CreateDirectory(dir);

            // PlayerSettings 是工程全局的，影视游戏那条线也在用同一份。
            // 改完不还原的话，下次打游戏包会带着工具包的产品名和窗口设置出去
            var saved = SavePlayerSettings();

            try
            {
                ApplyToolSettings();

                var opts = new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = Path.Combine(dir, ExeName),
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None,
                };

                var report = BuildPipeline.BuildPlayer(opts);
                var sum = report.summary;

                Debug.Log($"[ToolAppBuild] {sum.result}　{sum.totalSize / 1048576} MB　" +
                          $"{sum.totalTime.TotalSeconds:0} 秒　{opts.locationPathName}");

                if (sum.result == BuildResult.Succeeded)
                {
                    WriteReadme(dir);
                    // batchmode 里弹窗 / 开文件夹会把进程卡死在那儿等人点
                    if (!Application.isBatchMode) EditorUtility.RevealInFinder(opts.locationPathName);
                }
            }
            finally
            {
                RestorePlayerSettings(saved);
            }
        }

        struct SavedSettings
        {
            public string product, company;
            public FullScreenMode fullscreen;
            public bool resizable, runInBackground, defaultIsNativeResolution;
            public int width, height;
            public bool allowFullscreenSwitch;
        }

        static SavedSettings SavePlayerSettings() => new SavedSettings
        {
            product = PlayerSettings.productName,
            company = PlayerSettings.companyName,
            fullscreen = PlayerSettings.fullScreenMode,
            resizable = PlayerSettings.resizableWindow,
            runInBackground = PlayerSettings.runInBackground,
            defaultIsNativeResolution = PlayerSettings.defaultIsNativeResolution,
            width = PlayerSettings.defaultScreenWidth,
            height = PlayerSettings.defaultScreenHeight,
            allowFullscreenSwitch = PlayerSettings.allowFullscreenSwitch,
        };

        static void RestorePlayerSettings(SavedSettings s)
        {
            PlayerSettings.productName = s.product;
            PlayerSettings.companyName = s.company;
            PlayerSettings.fullScreenMode = s.fullscreen;
            PlayerSettings.resizableWindow = s.resizable;
            PlayerSettings.runInBackground = s.runInBackground;
            PlayerSettings.defaultIsNativeResolution = s.defaultIsNativeResolution;
            PlayerSettings.defaultScreenWidth = s.width;
            PlayerSettings.defaultScreenHeight = s.height;
            PlayerSettings.allowFullscreenSwitch = s.allowFullscreenSwitch;
            AssetDatabase.SaveAssets();
        }

        static void ApplyToolSettings()
        {
            PlayerSettings.productName = "调色工具台";
            PlayerSettings.companyName = "linyang";

            // 窗口化，而且可以拖大小。工具全屏没法和别的软件对着用
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.defaultIsNativeResolution = false;
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 1000;
            PlayerSettings.allowFullscreenSwitch = true;

            // 解码和导出都在后台跑，窗口失焦就停摆的话导出会卡住
            PlayerSettings.runInBackground = true;
        }

        static void WriteReadme(string dir)
        {
            string text =
                "调色工具台\n" +
                "==========\n\n" +
                "两个台：修图台、视频台。上面那排页签切。\n\n" +
                "修图台\n" +
                "  打开 JPG / PNG / 索尼 ARW，调完导出 JPG 或 PNG。\n" +
                "  「自动色调」按直方图给一组起手曝光和色阶。\n\n" +
                "视频台\n" +
                "  导入 mp4 / mov / mkv，拖时间轴逐帧看，调完导出 H.264。\n" +
                "  **需要 ffmpeg**，没有的话预览和导出都不能用。\n" +
                "  装了但找不到，可以在面板里手动指定 ffmpeg.exe。\n" +
                "  预览是降分辨率解码的，导出走原始分辨率。\n\n" +
                "参数面板\n" +
                "  和编辑器里的修图台是同一份，97 个控件。\n" +
                "  曲线和蒙版目前只能看不能改——那两个是完整的子界面，还没搬过来。\n\n" +
                "画布\n" +
                "  滚轮缩放，左键拖动平移。\n\n" +
                "窗口\n" +
                "  默认窗口化，可以拖大小，F11 切全屏。窗口大小会记住。\n";

            File.WriteAllText(Path.Combine(dir, "使用说明.txt"), text,
                              new System.Text.UTF8Encoding(true));
        }

        /// <summary>一步到位，给命令行用。</summary>
        public static void SetupAndBuild()
        {
            SetupScene();
            BuildWindows();
        }
    }
}
