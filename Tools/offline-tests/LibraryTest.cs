using System;
using System.Collections.Generic;
using System.Linq;
using Love.Tools;

// PhotoLibrary 的逻辑测试。排序、筛选、多选这类东西的 bug 是"看着对、用起来串"，
// 靠肉眼试很难覆盖到边角，正好它没有 GUI 依赖，可以直接跑。
static class LibraryTest
{
    static int _fail;

    static void Check(bool ok, string what, string detail = "")
    {
        if (ok) { Console.WriteLine("  OK   " + what); return; }
        _fail++;
        Console.WriteLine("  FAIL " + what + (detail.Length > 0 ? "   " + detail : ""));
    }

    static string Names(IEnumerable<PhotoEntry> es) => string.Join(",", es.Select(e => e.name));

    static PhotoLibrary Build(params (string name, int rating, int flag, long time)[] rows)
    {
        var lib = new PhotoLibrary();
        foreach (var r in rows)
        {
            var e = lib.Add("/x/" + r.name + ".png", r.name, null);
            e.rating = r.rating;
            e.flag = r.flag;
            e.modified = r.time;
        }
        lib.Rebuild();
        return lib;
    }

    public static int Run(string[] args)
    {
        Console.WriteLine("PhotoLibrary");

        // 故意让加入顺序和字典序不同，才验得出排序真的在起作用
        var rows = new (string, int, int, long)[]
        {
            ("c", 3,  0, 300),
            ("a", 5,  1, 100),
            ("d", 0, -1, 400),
            ("b", 1,  1, 200),
        };

        // ---- 排序 ----
        var lib = Build(rows);
        Check(Names(lib.Visible) == "a,b,c,d", "按文件名升序", Names(lib.Visible));

        lib.Descending = true;
        Check(Names(lib.Visible) == "d,c,b,a", "按文件名降序", Names(lib.Visible));

        lib.Descending = false;
        lib.Sort = PhotoSort.Date;
        Check(Names(lib.Visible) == "a,b,c,d", "按日期升序", Names(lib.Visible));

        lib.Sort = PhotoSort.Rating;
        Check(Names(lib.Visible) == "d,b,c,a", "按星级升序", Names(lib.Visible));

        lib.Descending = true;
        Check(Names(lib.Visible) == "a,c,b,d", "按星级降序", Names(lib.Visible));

        // 同分时按名字兜底，排序必须是确定的
        var tie = Build(("z", 2, 0, 1), ("y", 2, 0, 1), ("x", 2, 0, 1));
        tie.Sort = PhotoSort.Rating;
        string first = Names(tie.Visible);
        tie.Rebuild(); tie.Rebuild();
        Check(Names(tie.Visible) == first && first == "x,y,z", "同分时顺序稳定", first);

        // ---- 筛选 ----
        lib = Build(rows);
        lib.Filter = PhotoFilter.Picked;
        Check(Names(lib.Visible) == "a,b", "只看留用", Names(lib.Visible));

        lib.Filter = PhotoFilter.NotRejected;
        Check(Names(lib.Visible) == "a,b,c", "排除的不看", Names(lib.Visible));

        lib.Filter = PhotoFilter.Rated3;
        Check(Names(lib.Visible) == "a,c", "星级 >= 3", Names(lib.Visible));

        lib.Filter = PhotoFilter.Rated5;
        Check(Names(lib.Visible) == "a", "星级 >= 5", Names(lib.Visible));

        // ---- 多选 ----
        lib = Build(rows);   // 视图 a,b,c,d
        var v = lib.Visible;

        lib.SelectOnly(v[0]);
        Check(lib.Selected.Count == 1 && lib.Current == v[0], "单选");

        lib.Toggle(v[2]);
        Check(lib.Selected.Count == 2 && lib.Current == v[2], "Ctrl 加选后当前跟着走");

        lib.Toggle(v[2]);
        Check(lib.Selected.Count == 1 && lib.Current == v[0],
              "取消掉当前那张，当前顺延到还选着的", "current=" + (lib.Current?.name ?? "null"));

        lib.SelectOnly(v[1]);
        lib.SelectRange(v[3]);
        Check(Names(lib.Selected.OrderBy(e => e.name)) == "b,c,d" && lib.Current == v[3], "Shift 连选");

        lib.SelectOnly(v[3]);
        lib.SelectRange(v[1]);
        Check(Names(lib.Selected.OrderBy(e => e.name)) == "b,c,d", "Shift 反向连选");

        // 连选要按视图顺序，不是加入顺序
        lib = Build(rows);
        lib.Sort = PhotoSort.Rating;
        lib.Descending = true;                 // 视图 a,c,b,d
        var v2 = lib.Visible;
        lib.SelectOnly(v2[0]);
        lib.SelectRange(v2[2]);
        Check(Names(lib.Selected.OrderBy(e => e.name)) == "a,b,c",
              "连选跟的是视图顺序而不是加入顺序", Names(lib.Selected));

        // ---- 前后走 ----
        lib = Build(rows);
        lib.Filter = PhotoFilter.Picked;       // 视图只剩 a,b
        lib.SelectOnly(lib.Visible[0]);
        lib.Step(1);
        Check(lib.Current.name == "b", "下一张走的是筛选后的视图");
        lib.Step(1);
        Check(lib.Current.name == "b", "走到头就停住");
        lib.Step(-5);
        Check(lib.Current.name == "a", "往回走会夹住");

        // ---- 成批打分 ----
        lib = Build(rows);
        lib.SelectOnly(lib.Visible[0]);
        lib.Toggle(lib.Visible[1]);
        lib.ApplyToSelection(e => e.rating = 4);
        Check(lib.All.Count(e => e.rating == 4) == 2, "评级套到所有选中的");

        // 边遍历边改集合会炸，这里验它不炸：筛选开着时改评级会触发 Rebuild
        lib = Build(rows);
        lib.Filter = PhotoFilter.Rated3;
        lib.SelectAllVisible();
        bool threw = false;
        try { lib.ApplyToSelection(e => e.rating = 0); }
        catch (Exception ex) { threw = true; Console.WriteLine("       " + ex.GetType().Name); }
        Check(!threw, "打分触发重建时不会炸");

        // ---- 删除 ----
        lib = Build(rows);
        lib.SelectAllVisible();
        var gone = lib.Visible[1];
        lib.Remove(gone);
        Check(lib.Count == 3 && !lib.Selected.Contains(gone), "删掉的同时退出选中集");

        lib = Build(rows);
        lib.SelectOnly(lib.Visible[0]);
        lib.Remove(lib.Current);
        Check(lib.Current == null, "删掉当前那张后 Current 置空");

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "全部通过" : ("失败 " + _fail + " 项"));
        return _fail == 0 ? 0 : 1;
    }
}
