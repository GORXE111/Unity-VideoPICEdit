using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>
    /// 检测到 Sentis 包就自动打开 LOVE_SENTIS 宏，删掉包就自动关掉。
    ///
    /// 这样深度估计那些实验代码可以整块包在条件编译里：
    /// 没装包的机器上打开工程不会报一堆找不到 Unity.Sentis 的错，
    /// 装了包也不用手动去 Player Settings 里加宏。
    /// </summary>
    [InitializeOnLoad]
    static class SentisDefineSetup
    {
        const string Define = "LOVE_SENTIS";

        static SentisDefineSetup()
        {
            // 延后一帧：构造函数阶段包管理器可能还没就绪
            EditorApplication.delayCall += Sync;
        }

        [MenuItem("Tools/影视游戏/刷新 Sentis 宏", false, 41)]
        public static void ForceSync() => Sync();

        static void Sync()
        {
            bool installed = IsSentisInstalled();

            var target = NamedBuildTarget.FromBuildTargetGroup(
                BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));

            PlayerSettings.GetScriptingDefineSymbols(target, out string[] current);
            var set = new List<string>(current);
            bool has = set.Contains(Define);

            if (installed == has) return;   // 已经是对的，别白白触发一次重编译

            if (installed) set.Add(Define);
            else set.Remove(Define);

            PlayerSettings.SetScriptingDefineSymbols(target, set.ToArray());
            Debug.Log($"[Sentis] {(installed ? "检测到 Sentis，已打开" : "未检测到 Sentis，已关闭")} {Define} 宏");
        }

        /// <summary>
        /// 用包管理器判断，不要用 AppDomain.GetAssemblies()。
        ///
        /// .NET 的程序集是按需加载的：只要还没有任何代码碰过 Unity.Sentis 里的类型，
        /// 它就不在当前应用域里，用 GetAssemblies 去找必然找不到——
        /// 于是包明明装好了，宏却永远打不开。
        /// </summary>
        static bool IsSentisInstalled()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(
                "Packages/com.unity.sentis/package.json");
            if (info != null) return true;

            // 兜底：直接看包缓存目录
            return System.IO.Directory.Exists("Library/PackageCache") &&
                   System.IO.Directory.GetDirectories("Library/PackageCache", "com.unity.sentis*").Length > 0;
        }
    }
}
