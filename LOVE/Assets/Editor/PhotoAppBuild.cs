using System.IO;
using Love.App;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Love.EditorTools
{
    /// <summary>
    /// 独立修图程序的场景生成与打包。
    ///
    /// 和影视游戏那条线完全分开：不同的场景、不同的输出目录、不同的 exe 名。
    /// 场景一样是代码生成的（和 <see cref="MovieGameSetup"/> 同一套路数），
    /// 手改场景改完下次重建就没了。
    /// </summary>
    public static class PhotoAppBuild
    {
        const string ScenePath = "Assets/Scenes/PhotoApp.unity";
        const string MatPath = "Assets/GameAssets/Materials/VideoGrade.mat";
        const string OutDir = "../Build/PhotoApp";
        const string ExeName = "修图台.exe";

        [MenuItem("Tools/修图程序/生成场景", false, 200)]
        public static void SetupScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // NewScene 会卸载未使用资源，之前拿到的资产引用会变成 Unity 的"假 null"：
            // == null 为真，但赋值给序列化字段时 instanceID 仍能解析出正确的 GUID，
            // 于是磁盘上的 YAML 看着完全正常、只有部分代码路径失效。所以在这之后才去读
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null)
            {
                Debug.LogError("[PhotoApp] 找不到调色材质：" + MatPath +
                               "\n先跑一次「一键搭建全部」把它生成出来。");
                return;
            }

            var camGo = new GameObject("Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.16f, 0.17f, 1f);
            cam.orthographic = true;
            cam.cullingMask = 0;          // 什么都不画，界面全走 OnGUI

            var appGo = new GameObject("PhotoApp");
            var app = appGo.AddComponent<PhotoApp>();
            app.gradeMaterial = mat;

            EditorSceneManager.MarkSceneDirty(scene);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath) ?? "Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            EnsureShaderIncluded();
            Debug.Log("[PhotoApp] 场景已生成：" + ScenePath);
        }

        /// <summary>
        /// 把调色 shader 塞进「总是包含」。
        ///
        /// 场景里引用着材质通常就够了，但这条管线用的是 <c>shader_feature_local</c>
        /// 的多变体，而且渲染器是运行时 new 出来的、不挂在任何 Renderer 上。
        /// 漏了的表现是画面全黑或者全粉，而且只在出包之后才出现。
        /// </summary>
        static void EnsureShaderIncluded()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/GameAssets/Shaders/VideoGrade.shader");
            if (shader == null) return;

            var gs = AssetDatabase.LoadAssetAtPath<GraphicsSettings>(
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
            Debug.Log("[PhotoApp] VideoGrade.shader 已加进「总是包含的 Shader」");
        }

        [MenuItem("Tools/修图程序/打包 Windows", false, 201)]
        public static void BuildWindows()
        {
            if (!File.Exists(ScenePath))
            {
                Debug.Log("[PhotoApp] 场景还没有，先生成一份");
                SetupScene();
            }

            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", OutDir));
            Directory.CreateDirectory(dir);

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(dir, ExeName),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(opts);
            var sum = report.summary;

            Debug.Log($"[PhotoAppBuild] {sum.result}　{sum.totalSize / 1048576} MB　" +
                      $"{sum.totalTime.TotalSeconds:0} 秒　{opts.locationPathName}");

            // batchmode 里弹窗会把进程卡死在那儿等人点
            if (sum.result == BuildResult.Succeeded && !Application.isBatchMode)
                EditorUtility.RevealInFinder(opts.locationPathName);
        }

        /// <summary>一步到位，给命令行用。</summary>
        public static void SetupAndBuild()
        {
            SetupScene();
            BuildWindows();
        }
    }
}
