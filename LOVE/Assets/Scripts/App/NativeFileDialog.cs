using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Love.App
{
    /// <summary>
    /// 系统的打开 / 保存对话框。
    ///
    /// 独立程序里没有 `EditorUtility.OpenFilePanel`，只能自己叫 Win32 的通用对话框。
    /// 这是唯一一处平台相关代码，其余部分都是跨平台的 Unity API。
    /// </summary>
    public static class NativeFileDialog
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OpenFileNameW
        {
            public int structSize;
            public IntPtr owner;
            public IntPtr instance;
            public string filter;
            public string customFilter;
            public int maxCustFilter;
            public int filterIndex;
            public string file;
            public int maxFile;
            public string fileTitle;
            public int maxFileTitle;
            public string initialDir;
            public string title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public string defExt;
            public IntPtr custData;
            public IntPtr hook;
            public string templateName;
            public IntPtr reservedPtr;
            public int reservedInt;
            public int flagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetOpenFileNameW(ref OpenFileNameW ofn);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetSaveFileNameW(ref OpenFileNameW ofn);

        const int OfnFileMustExist = 0x00001000;
        const int OfnPathMustExist = 0x00000800;
        const int OfnOverwritePrompt = 0x00000002;

        /// <summary>
        /// **这一条必须带上。** 不带的话对话框会把进程的当前目录改成用户选的那个，
        /// 之后所有相对路径（Unity 自己也用）全指到别处去，而且不会报错。
        /// </summary>
        const int OfnNoChangeDir = 0x00000008;

        const int OfnExplorer = 0x00080000;
        const int OfnAllowMultiSelect = 0x00000200;

        static OpenFileNameW Make(string title, string filter, string initialDir, string defaultName)
        {
            var ofn = new OpenFileNameW();
            ofn.structSize = Marshal.SizeOf(typeof(OpenFileNameW));

            // 过滤器是「说明\0通配\0说明\0通配\0\0」这种双零结尾的怪格式
            ofn.filter = filter.Replace('|', '\0') + "\0\0";
            ofn.filterIndex = 1;

            // 缓冲区得预先撑够，返回的路径直接写进这块内存
            ofn.file = new string('\0', 2048);
            ofn.maxFile = ofn.file.Length;
            ofn.fileTitle = new string('\0', 512);
            ofn.maxFileTitle = ofn.fileTitle.Length;

            if (!string.IsNullOrEmpty(defaultName))
                ofn.file = defaultName.PadRight(2048, '\0');

            ofn.initialDir = string.IsNullOrEmpty(initialDir) ? null : initialDir;
            ofn.title = title;
            ofn.flags = OfnExplorer | OfnNoChangeDir | OfnPathMustExist;
            return ofn;
        }
#endif

        /// <summary>选一个文件。取消返回 null。</summary>
        /// <param name="filter">形如 "图片|*.jpg;*.png;*.arw|全部|*.*"</param>
        public static string Open(string title, string filter, string initialDir = null)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            var ofn = Make(title, filter, initialDir, null);
            ofn.flags |= OfnFileMustExist;
            return GetOpenFileNameW(ref ofn) ? Trim(ofn.file) : null;
#else
            return null;
#endif
        }

        /// <summary>选一个保存路径。取消返回 null。</summary>
        public static string Save(string title, string filter, string defaultName,
                                  string initialDir = null)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            var ofn = Make(title, filter, initialDir, defaultName);
            ofn.flags |= OfnOverwritePrompt;
            ofn.defExt = Path.GetExtension(defaultName ?? "").TrimStart('.');
            return GetSaveFileNameW(ref ofn) ? Trim(ofn.file) : null;
#else
            return null;
#endif
        }

        /// <summary>
        /// 一次选多个文件。取消返回空数组。
        ///
        /// 多选时 Win32 回填的不是一个路径，而是「目录\0文件1\0文件2\0\0」，
        /// 只选一个时又退回成单个完整路径。两种都要认。
        /// </summary>
        public static string[] OpenMany(string title, string filter, string initialDir = null)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            var ofn = Make(title, filter, initialDir, null);
            ofn.flags |= OfnFileMustExist | OfnAllowMultiSelect;

            // 多选时缓冲区要够大，几百张图的路径加起来很容易过万
            ofn.file = new string('\0', 65536);
            ofn.maxFile = ofn.file.Length;

            if (!GetOpenFileNameW(ref ofn)) return new string[0];
            return SplitMulti(ofn.file);
#else
            return new string[0];
#endif
        }

        /// <summary>这个平台上能不能弹对话框。不能的话界面上要给别的路子。</summary>
        public static bool Supported
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            get => true;
#else
            get => false;
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        static string[] SplitMulti(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return new string[0];

            // 双零才是真结尾；单个零只是分隔
            int end = raw.IndexOf("\0\0", System.StringComparison.Ordinal);
            if (end < 0) end = raw.Length;

            var parts = raw.Substring(0, end).Split('\0');
            if (parts.Length == 0) return new string[0];

            // 只选了一个的时候返回的就是完整路径，没有目录那一段
            if (parts.Length == 1) return new[] { parts[0] };

            var dir = parts[0];
            var list = new System.Collections.Generic.List<string>(parts.Length - 1);
            for (int i = 1; i < parts.Length; i++)
                if (parts[i].Length > 0) list.Add(System.IO.Path.Combine(dir, parts[i]));
            return list.ToArray();
        }

        static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int z = s.IndexOf('\0');
            if (z >= 0) s = s.Substring(0, z);
            return s.Length == 0 ? null : s;
        }
#endif
    }
}
