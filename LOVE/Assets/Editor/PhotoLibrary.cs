using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>胶片条里的一项。只常驻缩略图，原图按需加载。</summary>
    public class PhotoEntry
    {
        public string path;
        public string name;
        public Texture2D thumb;

        public int rating;      // 0~5 星
        public int flag;        // -1 排除  0 未标  1 留用
        public long modified;   // 文件修改时间，按日期排序用
    }

    public enum PhotoSort { Name = 0, Date = 1, Rating = 2 }

    /// <summary>筛选。数值 3~7 直接就是"星级不低于 N"，省一张映射表。</summary>
    public enum PhotoFilter
    {
        All = 0,
        Picked = 1,        // 只看留用
        NotRejected = 2,   // 排除的不看
        Rated1 = 3, Rated2 = 4, Rated3 = 5, Rated4 = 6, Rated5 = 7,
    }

    /// <summary>
    /// 图片库：排序、筛选、评级、多选。
    ///
    /// 从窗口里抽出来是因为这部分全是纯逻辑，可以离线跑测试——
    /// GUI 那半没法自动验，能分出来验的就分出来。
    ///
    /// 对外只暴露 <see cref="Visible"/>，也就是筛完排完的那个视图。
    /// 界面永远照着它画，不用自己再过一遍规则。
    /// </summary>
    public class PhotoLibrary
    {
        readonly List<PhotoEntry> _all = new List<PhotoEntry>();
        readonly List<PhotoEntry> _visible = new List<PhotoEntry>();

        /// <summary>选中的可以有多张。当前那张（大图显示的）永远也在里面。</summary>
        public readonly HashSet<PhotoEntry> Selected = new HashSet<PhotoEntry>();

        PhotoSort _sort = PhotoSort.Name;
        bool _descending;
        PhotoFilter _filter = PhotoFilter.All;

        public IReadOnlyList<PhotoEntry> All => _all;

        /// <summary>某张在视图里排第几。不在视图里（被筛掉了）返回 -1。</summary>
        public int IndexOfVisible(PhotoEntry e) => e == null ? -1 : _visible.IndexOf(e);
        public IReadOnlyList<PhotoEntry> Visible => _visible;
        public int Count => _all.Count;

        /// <summary>大图正在显示的那张。可能因为筛选而不在 Visible 里，那时候界面要给提示。</summary>
        public PhotoEntry Current { get; private set; }

        public PhotoSort Sort
        {
            get => _sort;
            set { if (_sort != value) { _sort = value; Rebuild(); } }
        }

        public bool Descending
        {
            get => _descending;
            set { if (_descending != value) { _descending = value; Rebuild(); } }
        }

        public PhotoFilter Filter
        {
            get => _filter;
            set { if (_filter != value) { _filter = value; Rebuild(); } }
        }

        // ---------------- 增删 ----------------

        public bool Contains(string path) => _all.Exists(e => e.path == path);

        public PhotoEntry Add(string path, string name, Texture2D thumb)
        {
            var e = new PhotoEntry { path = path, name = name, thumb = thumb };
            try { e.modified = File.GetLastWriteTimeUtc(path).Ticks; } catch { e.modified = 0; }
            LoadMeta(e);
            _all.Add(e);
            Rebuild();
            return e;
        }

        public void Remove(PhotoEntry e)
        {
            if (e == null) return;
            _all.Remove(e);
            Selected.Remove(e);
            if (ReferenceEquals(Current, e)) Current = null;
            Rebuild();
        }

        public void Clear()
        {
            _all.Clear();
            _visible.Clear();
            Selected.Clear();
            Current = null;
        }

        // ---------------- 视图 ----------------

        public void Rebuild()
        {
            _visible.Clear();
            foreach (var e in _all)
                if (Passes(e)) _visible.Add(e);

            _visible.Sort(Compare);
            if (_descending) _visible.Reverse();
        }

        bool Passes(PhotoEntry e)
        {
            switch (_filter)
            {
                case PhotoFilter.All: return true;
                case PhotoFilter.Picked: return e.flag > 0;
                case PhotoFilter.NotRejected: return e.flag >= 0;
                default: return e.rating >= (int)_filter - 2;   // Rated1 = 3 -> >=1
            }
        }

        int Compare(PhotoEntry a, PhotoEntry b)
        {
            switch (_sort)
            {
                case PhotoSort.Date:
                    int d = a.modified.CompareTo(b.modified);
                    return d != 0 ? d : NameCompare(a, b);
                case PhotoSort.Rating:
                    int r = a.rating.CompareTo(b.rating);
                    return r != 0 ? r : NameCompare(a, b);
                default:
                    return NameCompare(a, b);
            }
        }

        // 同名同分时按路径兜底，保证排序是确定的——否则每次 Rebuild 顺序都可能变
        static int NameCompare(PhotoEntry a, PhotoEntry b)
        {
            int c = string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.path, b.path, StringComparison.Ordinal);
        }

        // ---------------- 选择 ----------------

        /// <summary>单选：清掉其它，只留这一张。</summary>
        public void SelectOnly(PhotoEntry e)
        {
            Selected.Clear();
            if (e == null) return;
            Selected.Add(e);
            Current = e;
        }

        /// <summary>Ctrl+点：加选 / 取消。取消掉当前那张时，当前顺延到还选着的第一张。</summary>
        public void Toggle(PhotoEntry e)
        {
            if (e == null) return;
            if (!Selected.Remove(e))
            {
                Selected.Add(e);
                Current = e;
                return;
            }

            if (!ReferenceEquals(Current, e)) return;
            Current = null;
            foreach (var v in _visible)
                if (Selected.Contains(v)) { Current = v; break; }
        }

        /// <summary>Shift+点：从当前那张连选到这一张，按<b>视图顺序</b>而不是加入顺序。</summary>
        public void SelectRange(PhotoEntry e)
        {
            if (e == null) return;
            int to = _visible.IndexOf(e);
            int from = Current != null ? _visible.IndexOf(Current) : -1;
            if (to < 0) return;
            if (from < 0) { SelectOnly(e); return; }

            if (from > to) { int t = from; from = to; to = t; }
            Selected.Clear();
            for (int i = from; i <= to; i++) Selected.Add(_visible[i]);
            Current = e;
        }

        public void SelectAllVisible()
        {
            Selected.Clear();
            foreach (var v in _visible) Selected.Add(v);
            if (Current == null || !Selected.Contains(Current))
                Current = _visible.Count > 0 ? _visible[0] : null;
        }

        /// <summary>在视图里前后走一张。筛选之后要跟着视图走，不能按加入顺序。</summary>
        public PhotoEntry Step(int delta)
        {
            if (_visible.Count == 0) return null;
            int i = Current != null ? _visible.IndexOf(Current) : -1;
            i = i < 0 ? 0 : Mathf.Clamp(i + delta, 0, _visible.Count - 1);
            SelectOnly(_visible[i]);
            return Current;
        }

        public void SetCurrent(PhotoEntry e)
        {
            Current = e;
            if (e != null && !Selected.Contains(e)) SelectOnly(e);
        }

        // ---------------- 评级 ----------------

        public void SetRating(PhotoEntry e, int stars)
        {
            if (e == null) return;
            e.rating = Mathf.Clamp(stars, 0, 5);
            SaveMeta(e);
            if (_filter >= PhotoFilter.Rated1) Rebuild();
            else if (_sort == PhotoSort.Rating) Rebuild();
        }

        public void SetFlag(PhotoEntry e, int flag)
        {
            if (e == null) return;
            e.flag = Mathf.Clamp(flag, -1, 1);
            SaveMeta(e);
            if (_filter == PhotoFilter.Picked || _filter == PhotoFilter.NotRejected) Rebuild();
        }

        /// <summary>把评级 / 标记套到所有选中的。挑片时基本都是成批打分。</summary>
        public void ApplyToSelection(Action<PhotoEntry> act)
        {
            if (act == null) return;
            // 先拷一份：act 可能触发 Rebuild，边遍历边改集合会炸
            var list = new List<PhotoEntry>(Selected);
            foreach (var e in list) act(e);
            Rebuild();
        }

        // ---------------- 评级的持久化 ----------------
        //
        // 存 EditorPrefs 而不是在用户的照片旁边写 sidecar 文件：
        // 那是别人的目录，不该由我们往里扔东西。代价是换机器就没了。

        const string MetaPrefix = "PhotoGrade.meta.";

        static string Key(string path) => MetaPrefix + path.GetHashCode().ToString("X8");

        static void SaveMeta(PhotoEntry e)
        {
            if (e.rating == 0 && e.flag == 0) EditorPrefs.DeleteKey(Key(e.path));
            else EditorPrefs.SetString(Key(e.path), e.rating + "|" + e.flag + "|" + e.path);
        }

        static void LoadMeta(PhotoEntry e)
        {
            string v = EditorPrefs.GetString(Key(e.path), "");
            if (string.IsNullOrEmpty(v)) return;

            var p = v.Split('|');
            // 第三段是完整路径。哈希会撞，撞上了就当没存过，总比把别人的评级安到这张头上强
            if (p.Length < 3 || p[2] != e.path) return;
            int.TryParse(p[0], out e.rating);
            int.TryParse(p[1], out e.flag);
            e.rating = Mathf.Clamp(e.rating, 0, 5);
            e.flag = Mathf.Clamp(e.flag, -1, 1);
        }
    }
}
