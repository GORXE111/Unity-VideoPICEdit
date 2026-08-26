using System;
using Love.Video;

// 蒙版的显隐 / 独看规则。
//
// 这套规则错了的表现是"我明明开着它怎么没效果"或者"界面说 3 组生效实际只渲染 2 组"——
// 两种都不会报错，也没人会怀疑到判定本身。而它是纯逻辑，正好验死。
static class MaskVisibilityTest
{
    static int _fail;

    static void True(bool ok, string what, string detail = "")
    {
        if (ok) { Console.WriteLine("  OK   " + what); return; }
        _fail++;
        Console.WriteLine("  FAIL " + what + (detail.Length > 0 ? "   " + detail : ""));
    }

    /// <summary>造一个"有部件、有效果"的组，这样它默认是会渲染的。</summary>
    static MaskGroup Group(string name)
    {
        var g = new MaskGroup { name = name };
        g.parts.Add(new MaskPart { shape = (int)MaskShape.Ellipse });
        g.exposure = 0.5f;                   // 得有实际改动，否则 HasEffect 是 false
        return g;
    }

    public static int Run(string[] args)
    {
        Console.WriteLine("蒙版显隐 / 独看");

        var s = new VideoGradeSettings();
        s.maskGroups.Clear();
        var a = Group("甲"); var b = Group("乙"); var c = Group("丙");
        s.maskGroups.Add(a); s.maskGroups.Add(b); s.maskGroups.Add(c);

        // ---- 基本 ----
        True(s.ActiveMaskGroups == 3, $"三组都开着时全部生效（{s.ActiveMaskGroups}）");
        True(s.GroupRenders(a) && s.GroupRenders(b), "逐组判定和总数一致");

        b.enabled = false;
        True(s.ActiveMaskGroups == 2, $"关掉一组之后剩两组（{s.ActiveMaskGroups}）");
        True(!s.GroupRenders(b), "关掉的那组不渲染");
        b.enabled = true;

        // ---- 独看 ----
        True(!s.AnySolo, "默认没有独看");

        a.solo = true;
        True(s.AnySolo, "开了独看能认出来");
        True(s.GroupRenders(a), "独看的那组渲染");
        True(!s.GroupRenders(b) && !s.GroupRenders(c), "没独看的组被挡掉");
        True(s.ActiveMaskGroups == 1, $"独看时只算一组（{s.ActiveMaskGroups}）");

        // 独看和"关掉"是两回事：关掉的组即便开了独看也不该渲染
        c.solo = true;
        c.enabled = false;
        True(!s.GroupRenders(c), "关掉的组即便开了独看也不渲染");
        True(s.ActiveMaskGroups == 1, $"它也不该算进生效数（{s.ActiveMaskGroups}）");

        c.enabled = true;
        True(s.ActiveMaskGroups == 2, $"两组一起独看（{s.ActiveMaskGroups}）");

        a.solo = false; c.solo = false;
        True(s.ActiveMaskGroups == 3, "取消独看之后全部恢复");

        // 取消独看不该动 enabled——否则原来关掉的那些会被"恢复"出来
        b.enabled = false;
        a.solo = true;
        a.solo = false;
        True(!b.enabled, "取消独看不会把手动关掉的组打开");
        b.enabled = true;

        // ---- 部件静音 ----
        var multi = Group("多部件");
        multi.parts.Add(new MaskPart { shape = (int)MaskShape.Rect, op = (int)MaskOp.Subtract });
        s.maskGroups.Add(multi);

        True(VideoGradeSettings.CountAudibleParts(multi) == 2, "两个部件都参与");

        multi.parts[1].muted = true;
        True(VideoGradeSettings.CountAudibleParts(multi) == 1, "静音一个之后剩一个");
        True(s.GroupRenders(multi), "还剩一个部件时组仍然渲染");

        multi.parts[0].muted = true;
        True(VideoGradeSettings.CountAudibleParts(multi) == 0, "全静音时一个不剩");
        True(!s.GroupRenders(multi), "部件全静音的组不渲染（等于没有部件）");

        // 只留第二个：它的合成方式是「减」，但它成了第一个，
        // 渲染器必须按「加」处理，否则结果恒为全黑
        multi.parts[0].muted = true;
        multi.parts[1].muted = false;
        True(VideoGradeSettings.CountAudibleParts(multi) == 1, "只留下原本是「减」的那个");
        True(s.GroupRenders(multi), "只剩「减」部件时组仍要渲染（渲染器会把它当第一个按加处理）");

        // ---- 新字段的默认值必须是「维持原样」----
        // JsonUtility 读老预设时，文件里没有的 bool 一律是 false
        var fresh = new MaskPart();
        True(!fresh.muted, "新部件默认不静音（老预设读进来不会全失效）");
        var freshGroup = new MaskGroup();
        True(!freshGroup.solo, "新组默认不独看");
        True(freshGroup.enabled, "新组默认是显示的");

        // ---- 边界 ----
        True(!s.GroupRenders(null), "null 组不渲染");
        True(VideoGradeSettings.CountAudibleParts(null) == 0, "null 组的部件数是 0");

        var empty = new MaskGroup { name = "空的" };
        empty.exposure = 1f;
        s.maskGroups.Add(empty);
        True(!s.GroupRenders(empty), "没有部件的组不渲染");

        var noEffect = Group("没改动");
        noEffect.exposure = 0f;
        s.maskGroups.Add(noEffect);
        True(!s.GroupRenders(noEffect), "参数没改动的组不渲染");

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "全部通过" : ("失败 " + _fail + " 项"));
        return _fail == 0 ? 0 : 1;
    }
}
