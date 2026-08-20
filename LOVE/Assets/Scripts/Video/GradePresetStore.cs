using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Love.Video
{
    /// <summary>
    /// 调色预设的存放与读取。
    ///
    /// 预设放在 StreamingAssets/Story/Grades/ 下，一个 look 一个 json。
    /// 放这里而不是 Assets 深处，是因为剧情段要在运行时按名字加载它们，
    /// 而且出包之后还能直接改——和视频、剧情表一样的策略。
    /// </summary>
    public static class GradePresetStore
    {
        public const string FolderName = "Story/Grades";

        public static string FolderPath =>
            Path.Combine(Application.streamingAssetsPath, FolderName).Replace('\\', '/');

        public static string PathFor(string presetName) =>
            Path.Combine(FolderPath, presetName + ".json").Replace('\\', '/');

        /// <summary>列出所有预设名（不含扩展名）。目录不存在时返回空表。</summary>
        public static List<string> List()
        {
            var result = new List<string>();
            try
            {
                if (!Directory.Exists(FolderPath)) return result;
                foreach (var f in Directory.GetFiles(FolderPath, "*.json"))
                    result.Add(Path.GetFileNameWithoutExtension(f));
                result.Sort(System.StringComparer.OrdinalIgnoreCase);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[GradePreset] 列目录失败：{e.Message}");
            }
            return result;
        }

        public static bool Save(string presetName, VideoGradeSettings settings)
        {
            if (string.IsNullOrEmpty(presetName) || settings == null) return false;
            try
            {
                Directory.CreateDirectory(FolderPath);
                File.WriteAllText(PathFor(presetName), settings.ToJson(), System.Text.Encoding.UTF8);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[GradePreset] 保存失败 {presetName}：{e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 同步读取。安卓上 StreamingAssets 在压缩包里读不到，
        /// 那种情况要走 StoryDirector 里的 UnityWebRequest 路径。
        /// </summary>
        public static VideoGradeSettings Load(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return null;
            try
            {
                string path = PathFor(presetName);
                if (path.Contains("://") || !File.Exists(path)) return null;
                return VideoGradeSettings.FromJson(File.ReadAllText(path, System.Text.Encoding.UTF8));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[GradePreset] 读取失败 {presetName}：{e.Message}");
                return null;
            }
        }

        public static bool Delete(string presetName)
        {
            try
            {
                string p = PathFor(presetName);
                if (!File.Exists(p)) return false;
                File.Delete(p);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[GradePreset] 删除失败 {presetName}：{e.Message}");
                return false;
            }
        }
    }
}
