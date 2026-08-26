using System;
using System.Collections.Generic;
using System.IO;
using Love.Video;
using UnityEngine;

namespace Love.EditorTools
{
    /// <summary>一份存档。存的是整套参数，恢复就是原样盖回去。</summary>
    [Serializable]
    public class GradeSnapshot
    {
        public string name;
        public long time;        // DateTime.Ticks
        public bool auto;        // true = 覆盖性操作之前自动存的
        public VideoGradeSettings settings;

        public string TimeText
        {
            get
            {
                try { return new DateTime(time).ToString("MM-dd HH:mm"); }
                catch { return ""; }
            }
        }
    }

    /// <summary>
    /// 快照列表的增删和淘汰。
    ///
    /// 单拎出来是因为淘汰规则有点绕（手动的比自动的金贵），而这种规则错了
    /// 表现是"我存的那份不见了"——用户会以为工具吃了他的东西。正好它没有 Unity
    /// 依赖，可以离线测。
    /// </summary>
    public static class Snapshots
    {
        /// <summary>每张图最多留多少份。再多就该用预设库了。</summary>
        public const int MaxPerPhoto = 24;

        public static GradeSnapshot Add(List<GradeSnapshot> list, VideoGradeSettings s,
                                        string name, bool auto, DateTime now)
        {
            if (list == null || s == null) return null;

            var snap = new GradeSnapshot
            {
                name = string.IsNullOrEmpty(name) ? (auto ? "自动" : "快照") : name,
                time = now.Ticks,
                auto = auto,
                settings = s.Clone(),
            };

            list.Add(snap);
            Evict(list);
            return snap;
        }

        /// <summary>
        /// 满了先挤自动存的，最老的先走。全是手动的才动手动的。
        ///
        /// 反过来的话，用户手起的"暖调版"会被一串自动快照挤掉——
        /// 那是他真正在意的那一份。
        /// </summary>
        static void Evict(List<GradeSnapshot> list)
        {
            while (list.Count > MaxPerPhoto)
            {
                int victim = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    if (!list[i].auto) continue;
                    victim = i;
                    break;      // 列表按时间递增，第一个自动的就是最老的自动的
                }
                if (victim < 0) victim = 0;
                list.RemoveAt(victim);
            }
        }
    }

    /// <summary>一张图的全部编辑记录。</summary>
    [Serializable]
    public class PhotoEdit
    {
        public string path;
        public int rating;
        public int flag;

        /// <summary>
        /// 这张到底有没有调过。
        ///
        /// 不能拿 <c>settings == null</c> 判断：JsonUtility 不保留 null 引用，
        /// 存进去是 null，读出来会变成一个默认构造的对象。那样"还没调过、
        /// 沿用当前参数当起点"这条就永远不成立，翻到下一张会被默认值糊掉。
        /// </summary>
        public bool hasSettings;

        public VideoGradeSettings settings;
        public List<RepairSpot> repairs = new List<RepairSpot>();
        public List<GradeSnapshot> snapshots = new List<GradeSnapshot>();
    }

    /// <summary>
    /// 逐图编辑记录的落盘。
    ///
    /// 起因是个实打实的坑：逐图参数原来存在窗口的 <c>Dictionary</c> 字段里，
    /// 而 Unity 序列化不了 Dictionary，也就是说——<b>改一行 C# 触发程序集重载，
    /// 或者干脆关掉窗口，几十张图的调色和修补就全没了</b>。
    /// 批处理做得再顺，这样也是个陷阱。
    ///
    /// 存在 <c>UserSettings/</c> 下：那是 Unity 约定的"每个人自己的编辑器状态"目录，
    /// 已经在 .gitignore 里，而且不会往用户的照片目录里扔东西。
    /// </summary>
    public class PhotoEditStore
    {
        const string FileName = "PhotoGradeEdits.json";
        const int Version = 1;

        [Serializable]
        class Data
        {
            public int version = Version;
            public List<PhotoEdit> items = new List<PhotoEdit>();
        }

        readonly Dictionary<string, PhotoEdit> _map = new Dictionary<string, PhotoEdit>();
        bool _dirty;
        double _lastSave;

        public int Count => _map.Count;
        public bool Dirty => _dirty;

        public static string FilePath
        {
            get
            {
                // Application.dataPath 是 <工程>/Assets，往上一级才是工程根
                string root = Path.GetDirectoryName(Application.dataPath) ?? ".";
                return Path.Combine(root, "UserSettings", FileName);
            }
        }

        // ---------------- 读写 ----------------

        public void Load()
        {
            _map.Clear();
            _dirty = false;

            string p = FilePath;
            if (!File.Exists(p)) return;

            try
            {
                var d = JsonUtility.FromJson<Data>(File.ReadAllText(p));
                if (d?.items == null) return;
                if (d.version > Version)
                    Debug.LogWarning($"[修图台] 编辑记录来自更新的版本（{d.version} > {Version}），" +
                                     "可能有字段读不出来。");

                foreach (var it in d.items)
                {
                    if (it == null || string.IsNullOrEmpty(it.path)) continue;
                    if (it.repairs == null) it.repairs = new List<RepairSpot>();
                    if (it.snapshots == null) it.snapshots = new List<GradeSnapshot>();
                    if (!it.hasSettings) it.settings = null;
                    // 老文件里可能没有 maskGroups 这类后加的字段，补齐一下防 null
                    it.settings?.MigrateSecondary();
                    _map[it.path] = it;
                }
            }
            catch (Exception e)
            {
                // 读坏了就当空的重来。为一个缓存文件把窗口卡住不值得
                Debug.LogWarning($"[修图台] 编辑记录读取失败，已忽略：{e.Message}");
                _map.Clear();
            }
        }

        /// <summary>写盘。<paramref name="force"/> 为假时会限流，避免每次拖滑条都写文件。</summary>
        public void Save(bool force = false)
        {
            if (!_dirty) return;
            if (!force && EditorTime() - _lastSave < 8.0) return;

            string p = FilePath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(p) ?? ".");

                var d = new Data();
                foreach (var kv in _map)
                {
                    // 什么都没改过的不用存，免得文件被一堆空壳撑大
                    if (IsEmpty(kv.Value)) continue;
                    d.items.Add(kv.Value);
                }

                // 先写临时文件再替换：写到一半崩掉的话，至少上一份还是完整的
                string tmp = p + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(d, true));
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);

                _dirty = false;
                _lastSave = EditorTime();
            }
            catch (Exception e)
            {
                Debug.LogError($"[修图台] 编辑记录写入失败：{e.Message}");
            }
        }

        static bool IsEmpty(PhotoEdit e) =>
            e.rating == 0 && e.flag == 0 && !e.hasSettings &&
            (e.repairs == null || e.repairs.Count == 0) &&
            (e.snapshots == null || e.snapshots.Count == 0);

        static double EditorTime() =>
#if UNITY_EDITOR
            UnityEditor.EditorApplication.timeSinceStartup;
#else
            0.0;
#endif

        // ---------------- 存取 ----------------

        public PhotoEdit Get(string path) =>
            !string.IsNullOrEmpty(path) && _map.TryGetValue(path, out var e) ? e : null;

        public PhotoEdit GetOrCreate(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_map.TryGetValue(path, out var e)) return e;
            e = new PhotoEdit { path = path };
            _map[path] = e;
            return e;
        }

        public void Remove(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (_map.Remove(path)) _dirty = true;
        }

        public void MarkDirty() => _dirty = true;

        /// <summary>把某张的参数记下来。</summary>
        public void PutSettings(string path, VideoGradeSettings s)
        {
            var e = GetOrCreate(path);
            if (e == null) return;
            e.settings = s?.Clone();
            e.hasSettings = s != null;
            _dirty = true;
        }

        /// <summary>把某张的修补记下来。</summary>
        public void PutRepairs(string path, List<RepairSpot> spots)
        {
            var e = GetOrCreate(path);
            if (e == null) return;
            e.repairs = spots != null && spots.Count > 0 ? new List<RepairSpot>(spots) : new List<RepairSpot>();
            _dirty = true;
        }

        public void PutMeta(string path, int rating, int flag)
        {
            var e = GetOrCreate(path);
            if (e == null) return;
            e.rating = rating;
            e.flag = flag;
            _dirty = true;
        }

        /// <summary>清掉全部记录。文件也一并删掉。</summary>
        public void Clear()
        {
            _map.Clear();
            _dirty = true;
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
            _dirty = false;
        }
    }
}
