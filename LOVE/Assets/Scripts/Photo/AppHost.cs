using System;
using UnityEngine;

namespace Love.Tools
{
    /// <summary>
    /// 工具链跑在哪儿。
    ///
    /// 同一份代码要在编辑器窗口里跑，也要在独立程序里跑。两边只有两处不一样：
    /// 偏好设置存哪儿（EditorPrefs / PlayerPrefs），和"开机到现在多久"从哪儿取。
    ///
    /// **用注入而不是 `#if UNITY_EDITOR`。** 条件编译会让运行时程序集里出现
    /// `UnityEditor` 这个名字——哪怕包在 `#if` 里，也等于把边界交给宏去守。
    /// 换成委托的话，运行时这半根本不认识编辑器，编译器自己就把边界守住了。
    ///
    /// 编辑器侧由 <c>AppHostEditor</c> 在 <c>InitializeOnLoad</c> 时换掉这几个委托。
    /// </summary>
    public static class AppHost
    {
        public static Func<string, string, string> GetPref = PlayerPrefs.GetString;

        public static Action<string, string> SetPref = (k, v) =>
        {
            PlayerPrefs.SetString(k, v);
            PlayerPrefs.Save();
        };

        /// <summary>开机到现在多少秒。只用来做限流，不需要绝对时刻。</summary>
        public static Func<double> TimeSinceStartup = () => Time.realtimeSinceStartupAsDouble;
    }
}
