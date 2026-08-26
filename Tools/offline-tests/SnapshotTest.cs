using System;
using System.Collections.Generic;
using System.Linq;
using Love.EditorTools;
using Love.Video;

// 快照的淘汰规则。规则错了表现是"我存的那份不见了"，
// 用户会以为工具吃了他的东西——正好这块没有 Unity 依赖，能离线测。
static class SnapshotTest
{
    static int _fail;

    static void True(bool ok, string what, string detail = "")
    {
        if (ok) { Console.WriteLine("  OK   " + what); return; }
        _fail++;
        Console.WriteLine("  FAIL " + what + (detail.Length > 0 ? "   " + detail : ""));
    }

    static string Names(List<GradeSnapshot> l) => string.Join(",", l.Select(x => x.name));

    static GradeSnapshot Push(List<GradeSnapshot> l, string name, bool auto, int minute)
    {
        var s = new VideoGradeSettings { exposure = minute * 0.1f };
        return Snapshots.Add(l, s, name, auto, new DateTime(2026, 8, 26, 10, minute % 60, 0));
    }

    public static int Run(string[] args)
    {
        Console.WriteLine("快照淘汰规则");

        // ---- 基本 ----
        var l = new List<GradeSnapshot>();
        Push(l, "甲", false, 1);
        Push(l, "乙", true, 2);
        True(l.Count == 2 && Names(l) == "甲,乙", "按加入顺序排", Names(l));
        True(Math.Abs(l[0].settings.exposure - 0.1f) < 1e-4f, "存的是参数的副本");

        // 存进去之后再改原件，快照不该跟着变
        var live = new VideoGradeSettings { exposure = 1f };
        var snap = Snapshots.Add(l, live, "丙", false, DateTime.Now);
        live.exposure = 9f;
        True(Math.Abs(snap.settings.exposure - 1f) < 1e-4f, "是深拷贝，改原件不影响快照");

        // ---- 淘汰：自动的先走 ----
        l = new List<GradeSnapshot>();
        Push(l, "手动1", false, 0);
        for (int i = 1; i < Snapshots.MaxPerPhoto; i++) Push(l, "自动" + i, true, i);
        True(l.Count == Snapshots.MaxPerPhoto, $"刚好装满 {Snapshots.MaxPerPhoto} 份");

        Push(l, "手动2", false, 59);
        True(l.Count == Snapshots.MaxPerPhoto, "超了之后总数不变");
        True(l.Any(x => x.name == "手动1"), "手动存的那份没被挤掉");
        True(l.Any(x => x.name == "手动2"), "新存的在");
        True(!l.Any(x => x.name == "自动1"), "挤掉的是最老的自动快照");
        True(l.Any(x => x.name == "自动2"), "第二老的自动快照还在");

        // ---- 全是手动的时候只能动手动的 ----
        l = new List<GradeSnapshot>();
        for (int i = 0; i < Snapshots.MaxPerPhoto; i++) Push(l, "手动" + i, false, i);
        Push(l, "新的", false, 59);
        True(l.Count == Snapshots.MaxPerPhoto, "全手动时总数仍然不变");
        True(!l.Any(x => x.name == "手动0"), "全手动时挤掉最老的那份");
        True(l.Any(x => x.name == "新的"), "新的一定留下");

        // ---- 连续超额 ----
        l = new List<GradeSnapshot>();
        for (int i = 0; i < Snapshots.MaxPerPhoto * 2; i++) Push(l, "自动" + i, true, i);
        True(l.Count == Snapshots.MaxPerPhoto, "连着塞两倍也只留上限那么多");
        True(l[l.Count - 1].name == "自动" + (Snapshots.MaxPerPhoto * 2 - 1), "留下的是最新的那批");

        // ---- 边界 ----
        True(Snapshots.Add(null, new VideoGradeSettings(), "x", false, DateTime.Now) == null,
             "列表为 null 时安全返回");
        True(Snapshots.Add(new List<GradeSnapshot>(), null, "x", false, DateTime.Now) == null,
             "参数为 null 时安全返回");

        var l2 = new List<GradeSnapshot>();
        var s2 = Snapshots.Add(l2, new VideoGradeSettings(), "", false, DateTime.Now);
        True(!string.IsNullOrEmpty(s2.name), "名字为空时给个默认名");

        var s3 = Snapshots.Add(l2, new VideoGradeSettings(), null, true, DateTime.Now);
        True(!string.IsNullOrEmpty(s3.name), "自动快照名字为 null 时也有默认名");

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "全部通过" : ("失败 " + _fail + " 项"));
        return _fail == 0 ? 0 : 1;
    }
}
