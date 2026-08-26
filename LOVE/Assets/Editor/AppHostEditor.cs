using Love.Tools;
using UnityEditor;

namespace Love.EditorTools
{
    /// <summary>
    /// 编辑器里把 <see cref="AppHost"/> 的几个委托换成编辑器版。
    ///
    /// ffmpeg 路径这种要跨工程记住，得用 EditorPrefs（按机器存）而不是
    /// PlayerPrefs（按工程存）。
    /// </summary>
    [InitializeOnLoad]
    static class AppHostEditor
    {
        static AppHostEditor()
        {
            AppHost.GetPref = EditorPrefs.GetString;
            AppHost.SetPref = (k, v) => EditorPrefs.SetString(k, v);
            AppHost.TimeSinceStartup = () => EditorApplication.timeSinceStartup;

            // 编辑器里写工程根的 UserSettings/，那个目录已经在 .gitignore 里
            AppHost.DataRoot = () =>
                System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath) ?? ".";
        }
    }
}
