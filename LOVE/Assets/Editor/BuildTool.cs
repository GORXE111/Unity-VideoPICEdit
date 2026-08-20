using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// Windows 打包工具。
    /// 打之前会先做一轮资源体检，把「打完才发现视频没进去」这类问题挡在前面。
    /// </summary>
    public static class BuildTool
    {
        const string ScenePath   = "Assets/Scenes/Main.unity";
        const string ExeName     = "LOVE.exe";
        const string StoryFile   = "Assets/StreamingAssets/Story/story.json";
        const string VideoFolder = "Assets/StreamingAssets/Videos";

        /// <summary>命令行无人值守打包时不能弹任何窗，否则会一直卡在那等人点。</summary>
        static bool Batch => Application.isBatchMode;

        /// <summary>输出到工程目录的同级 Build 文件夹，不会污染 Assets。</summary>
        static string BuildRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Build")).Replace('\\', '/');

        [MenuItem("Tools/影视游戏/打包 Windows 版本", false, 100)]
        public static void BuildWindows() => Build(false);

        [MenuItem("Tools/影视游戏/打包 Windows 版本（开发版·带日志）", false, 101)]
        public static void BuildWindowsDev() => Build(true);

        /// <summary>
        /// 重建场景 + 打包，一步到位。
        /// 命令行专用：两次 Unity 调用连着跑的话，第二个实例会在第一个还没释放工程锁时启动，
        /// 直接崩在 HandleProjectAlreadyOpenInAnotherInstance。合成一个进程就没这问题。
        /// </summary>
        [MenuItem("Tools/影视游戏/重建场景并打包 Windows", false, 103)]
        public static void SetupAndBuildWindows()
        {
            MovieGameSetup.SetupAll();
            Build(false);
        }

        [MenuItem("Tools/影视游戏/打开打包输出目录", false, 102)]
        public static void OpenBuildFolder()
        {
            Directory.CreateDirectory(BuildRoot);
            EditorUtility.RevealInFinder(BuildRoot + "/");
        }

        static void Build(bool development)
        {
            if (!PreflightCheck()) return;

            string outDir = $"{BuildRoot}/Windows{(development ? "-Dev" : "")}";
            string exePath = $"{outDir}/{ExeName}";

            // 每次重打前清干净，避免上一次的残留文件混进来
            if (Directory.Exists(outDir))
            {
                if (!Batch && !EditorUtility.DisplayDialog("打包",
                        $"输出目录已存在，要清空重打吗？\n\n{outDir}", "清空重打", "取消"))
                    return;
                try { Directory.Delete(outDir, true); }
                catch (Exception e) { Fail($"清空目录失败：{e.Message}"); return; }
            }
            Directory.CreateDirectory(outDir);

            var options = new BuildPlayerOptions
            {
                scenes = EnabledScenes(),
                locationPathName = exePath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = development
                    ? (BuildOptions.Development | BuildOptions.AllowDebugging)
                    : BuildOptions.None,
            };

            Debug.Log($"[Build] 开始打包 -> {exePath}");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                Fail($"打包失败：{summary.result}，{summary.totalErrors} 个错误");
                return;
            }

            string verdict = PostBuildCheck(outDir);

            double mb = summary.totalSize / 1024.0 / 1024.0;
            string msg =
                $"打包完成\n\n" +
                $"输出：{exePath}\n" +
                $"体积：{mb:0.0} MB\n" +
                $"耗时：{summary.totalTime.TotalSeconds:0} 秒\n\n" +
                verdict;

            Debug.Log("[BuildResult] " + msg.Replace("\n", " | "));

            if (Batch) return;
            if (EditorUtility.DisplayDialog("打包", msg, "打开目录", "关闭"))
                EditorUtility.RevealInFinder(exePath);
        }

        /// <summary>报错。命令行模式下顺便让 Unity 以非零退出码结束，外面才知道打包没成。</summary>
        static void Fail(string message)
        {
            Debug.LogError("[BuildResult] 失败：" + message);
            if (Batch) EditorApplication.Exit(1);
            else EditorUtility.DisplayDialog("打包", message, "好");
        }

        static string[] EnabledScenes()
        {
            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0) scenes = new[] { ScenePath };
            return scenes;
        }

        /// <summary>打包前体检：这些问题打完包才发现的话，排查起来很费时间。</summary>
        static bool PreflightCheck()
        {
            var problems = new List<string>();
            var warnings = new List<string>();

            // 场景
            if (!File.Exists(ScenePath))
                problems.Add($"主场景不存在：{ScenePath}\n先跑一次「一键搭建全部」");
            else if (!EditorBuildSettings.scenes.Any(s => s.enabled && s.path == ScenePath))
                warnings.Add($"{ScenePath} 不在 Build Settings 的启用列表里");

            // 剧情表
            if (!File.Exists(StoryFile))
                problems.Add($"剧情表不存在：{StoryFile}");

            // 视频：把 story.json 里引用到的文件挨个核对
            if (File.Exists(StoryFile))
            {
                var missing = MissingVideos();
                if (missing.Count > 0)
                    problems.Add("story.json 里引用了不存在的视频：\n  " + string.Join("\n  ", missing));
            }

            // 字体
            var fontGuids = AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { "Assets/GameAssets/Fonts" });
            if (fontGuids.Length == 0)
                problems.Add("没有 TMP 中文字体资产，打出来会全是豆腐块\n跑一次「单步：重建字体资产」");

            // 调色材质
            if (AssetDatabase.LoadAssetAtPath<Material>("Assets/GameAssets/Materials/VideoGrade.mat") == null)
                warnings.Add("调色材质缺失，后处理在打包版本里不会生效");

            // 调色参数
            if (!File.Exists("Assets/StreamingAssets/Story/grade.json"))
                warnings.Add("没有 grade.json，打包版本会用默认（不调色）的参数\n在游戏里按 F1 调好后点「保存」");

            if (problems.Count > 0)
            {
                Fail("打包前检查没过：\n• " + string.Join("\n• ", problems));
                return false;
            }

            if (warnings.Count > 0)
            {
                string msg = "有几个提醒：\n• " + string.Join("\n• ", warnings);
                Debug.LogWarning("[BuildResult] " + msg);
                // 命令行模式下警告不拦，只记录
                if (!Batch && !EditorUtility.DisplayDialog("打包", msg + "\n\n要继续打包吗？", "继续", "取消"))
                    return false;
            }

            return true;
        }

        /// <summary>核对 story.json 里引用的视频文件是不是都在。</summary>
        static List<string> MissingVideos()
        {
            var missing = new List<string>();
            try
            {
                string json = File.ReadAllText(StoryFile, System.Text.Encoding.UTF8);
                var db = Love.Story.StoryDirector.ParseStory(json);
                if (db?.segments == null) return missing;

                foreach (var seg in db.segments)
                {
                    if (seg == null || string.IsNullOrEmpty(seg.video)) continue;
                    string path = Path.Combine(VideoFolder, seg.video);
                    if (!File.Exists(path)) missing.Add($"{seg.id} -> {seg.video}");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Build] 校验视频时读剧情表失败：{e.Message}");
            }
            return missing;
        }

        /// <summary>打完之后确认 StreamingAssets 真的被带过去了。</summary>
        static string PostBuildCheck(string outDir)
        {
            string dataDir = $"{outDir}/{Path.GetFileNameWithoutExtension(ExeName)}_Data";
            string sa = $"{dataDir}/StreamingAssets";

            if (!Directory.Exists(sa))
                return "⚠ 输出里找不到 StreamingAssets，视频和剧情表没被带过去";

            var videos = Directory.Exists($"{sa}/Videos")
                ? Directory.GetFiles($"{sa}/Videos", "*.mp4")
                : Array.Empty<string>();

            double videoMb = videos.Sum(f => new FileInfo(f).Length) / 1024.0 / 1024.0;

            var lines = new List<string>
            {
                $"视频 {videos.Length} 个，共 {videoMb:0.0} MB",
                File.Exists($"{sa}/Story/story.json") ? "剧情表 ✓" : "⚠ 剧情表缺失",
                File.Exists($"{sa}/Story/grade.json") ? "调色参数 ✓" : "调色参数 —（未保存）",
            };
            return string.Join("\n", lines);
        }
    }
}
