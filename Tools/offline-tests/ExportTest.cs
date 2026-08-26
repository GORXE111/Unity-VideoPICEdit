using System;
using System.Collections.Generic;
using Love.Tools;

// 导出的命名 / 尺寸 / 重名逻辑。
// 这类东西最容易出"看着对、批量跑一遍才发现全叠在一个文件上"的问题，
// 而它们没有 Unity 依赖，正好离线测。
static class ExportTest
{
    static int _fail;

    static void Eq(string got, string want, string what)
    {
        if (got == want) { Console.WriteLine("  OK   " + what); return; }
        _fail++;
        Console.WriteLine("  FAIL {0}   期望「{1}」实得「{2}」", what, want, got);
    }

    static void True(bool ok, string what)
    {
        if (ok) { Console.WriteLine("  OK   " + what); return; }
        _fail++;
        Console.WriteLine("  FAIL " + what);
    }

    public static int Run(string[] args)
    {
        Console.WriteLine("导出命名 / 尺寸 / 重名");

        var ctx = new ExportContext
        {
            sourceName = "_DSC0018", index = 7, total = 42,
            width = 2048, height = 1365, rating = 4,
            time = new DateTime(2026, 8, 26, 14, 30, 52),
        };

        // ---- 模板 ----
        Eq(ExportNaming.Expand("{name}_graded", ctx), "_DSC0018_graded", "{name}");
        Eq(ExportNaming.Expand("{index}", ctx), "7", "{index}");
        Eq(ExportNaming.Expand("{index2}", ctx), "07", "{index2} 补两位");
        Eq(ExportNaming.Expand("{index3}", ctx), "007", "{index3} 补三位");
        Eq(ExportNaming.Expand("{date}_{time}", ctx), "20260826_143052", "{date} {time}");
        Eq(ExportNaming.Expand("{w}x{h}", ctx), "2048x1365", "{w} {h}");
        Eq(ExportNaming.Expand("star{rating}_{index3}_{name}", ctx),
           "star4_007__DSC0018", "多个记号混排");

        // {index3} 必须先于 {index} 替换，否则会被拆成 "7" + "3"
        Eq(ExportNaming.Expand("{index3}", ctx), "007", "{index3} 不会被 {index} 抢先匹配");

        // 认不出来的记号原样留着，别悄悄吃掉
        Eq(ExportNaming.Expand("{name}_{bogus}", ctx), "_DSC0018_{bogus}", "不认识的记号原样保留");

        // ---- 非法字符 ----
        Eq(ExportNaming.Sanitize("a/b\\c:d*e?f"), "abcdef", "洗掉路径分隔符和通配符");
        Eq(ExportNaming.Sanitize("  末尾空格  "), "末尾空格", "去掉首尾空格");
        Eq(ExportNaming.Sanitize("结尾的点..."), "结尾的点", "去掉结尾的点");
        Eq(ExportNaming.Sanitize("///"), "untitled", "洗完什么都不剩时给兜底名");
        Eq(ExportNaming.Sanitize(""), "untitled", "空串给兜底名");
        Eq(ExportNaming.Sanitize("中文名字 OK"), "中文名字 OK", "中文和中间的空格留着");

        // ---- 尺寸 ----
        var p = new ExportPreset { maxLongEdge = 2048, noUpscale = true };
        ExportNaming.ComputeSize(6000, 4000, p, out int w, out int h);
        True(w == 2048 && h == 1365, $"横图按长边缩到 2048（得到 {w}x{h}）");

        ExportNaming.ComputeSize(4000, 6000, p, out w, out h);
        True(w == 1365 && h == 2048, $"竖图按长边缩（得到 {w}x{h}）");

        ExportNaming.ComputeSize(800, 600, p, out w, out h);
        True(w == 800 && h == 600, "比上限小时不放大");

        p.noUpscale = false;
        ExportNaming.ComputeSize(800, 600, p, out w, out h);
        True(w == 2048 && h == 1536, $"关掉「只缩不放」才会拉大（得到 {w}x{h}）");

        p.maxLongEdge = 0;
        ExportNaming.ComputeSize(6000, 4000, p, out w, out h);
        True(w == 6000 && h == 4000, "上限为 0 表示不限制");

        p.maxLongEdge = 1;
        ExportNaming.ComputeSize(6000, 4000, p, out w, out h);
        True(w >= 1 && h >= 1, $"极端上限也不会算出 0（得到 {w}x{h}）");

        // ---- 重名 ----
        var disk = new HashSet<string>();
        Func<string, bool> exists = x => disk.Contains(x);

        var taken = new HashSet<string>();
        string a = ExportNaming.Resolve("D:/out", "img", ".jpg", 0, taken, exists);
        Eq(a, System.IO.Path.Combine("D:/out", "img.jpg"), "首次直接用原名");

        // 同一批里第二张算出同名：文件还没落地，只看磁盘会漏
        string b = ExportNaming.Resolve("D:/out", "img", ".jpg", 0, taken, exists);
        Eq(b, System.IO.Path.Combine("D:/out", "img_2.jpg"), "同批重名要加序号（磁盘上还不存在）");

        string c = ExportNaming.Resolve("D:/out", "img", ".jpg", 0, taken, exists);
        Eq(c, System.IO.Path.Combine("D:/out", "img_3.jpg"), "第三张继续往后排");

        disk.Add(System.IO.Path.Combine("D:/out", "old.jpg"));
        string d = ExportNaming.Resolve("D:/out", "old", ".jpg", 0, new HashSet<string>(), exists);
        Eq(d, System.IO.Path.Combine("D:/out", "old_2.jpg"), "磁盘上已存在也要避开");

        string e = ExportNaming.Resolve("D:/out", "old", ".jpg", 1, new HashSet<string>(), exists);
        Eq(e, System.IO.Path.Combine("D:/out", "old.jpg"), "覆盖模式就用原名");

        string f = ExportNaming.Resolve("D:/out", "old", ".jpg", 2, new HashSet<string>(), exists);
        True(f == null, "跳过模式返回 null");

        // ---- 水印摆位 ----
        // 四个角的正负号最容易写反，而写反了只有出图才看得见
        void Corner(int c, float wx, float wy, string what)
        {
            var r = ExportNaming.WatermarkRect(2000, 1000, 200f, 50f, c, 0.03f);
            True(Math.Abs(r.x - wx) < 0.01f && Math.Abs(r.y - wy) < 0.01f,
                 what + $"（得到 {r.x},{r.y}）");
        }

        // 长边 2000 × 3% = 60
        Corner(0, 60f, 60f, "左上");
        Corner(1, 2000f - 200f - 60f, 60f, "右上");
        Corner(2, 60f, 1000f - 50f - 60f, "左下");
        Corner(3, 2000f - 200f - 60f, 1000f - 50f - 60f, "右下");

        var r0 = ExportNaming.WatermarkRect(2000, 1000, 200f, 50f, 0, 0f);
        True(r0.x == 0f && r0.y == 0f, "边距为 0 时贴着角");

        // 边距按长边算，两个方向同一个绝对值——按各自边长算的话
        // 横图上下会比左右窄一半，看着就是"没对齐"
        var r1 = ExportNaming.WatermarkRect(2000, 1000, 200f, 50f, 0, 0.05f);
        True(Math.Abs(r1.x - r1.y) < 0.01f, $"横图上下左右的边距一样宽（{r1.x} vs {r1.y}）");

        var r2 = ExportNaming.WatermarkRect(1000, 2000, 200f, 50f, 0, 0.05f);
        True(Math.Abs(r2.x - 100f) < 0.01f, $"竖图也按长边算边距（得到 {r2.x}）");

        var r3 = ExportNaming.WatermarkRect(2000, 1000, 200f, 50f, 0, 5f);
        True(r3.x <= 2000f, "边距给个离谱的值也不会算飞");

        // 水印比画面还大时会算出负坐标，那是对的：宁可露出一部分，也别悄悄挪位置
        var r4 = ExportNaming.WatermarkRect(400, 300, 800f, 200f, 3, 0.03f);
        True(r4.x < 0f, "水印比画面宽时靠右摆会溢出到左边（不做遮掩）");

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "全部通过" : ("失败 " + _fail + " 项"));
        return _fail == 0 ? 0 : 1;
    }
}
