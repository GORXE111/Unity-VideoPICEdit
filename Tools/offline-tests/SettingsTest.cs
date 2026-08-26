using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Love.Video;
using UnityEngine;

// VideoGradeSettings 的 Clone / CopyFrom / Lerp 都是手写的字段清单。
// 加了新字段却忘了往里补，表现是"参数会莫名其妙丢一部分"，而且不报错。
// 这里用反射把每个字段都设成一个特征值，再看克隆之后还在不在。
static class SettingsTest
{
    static int _fail;

    static void Check(bool ok, string what)
    {
        if (!ok) { _fail++; Console.WriteLine("  FAIL " + what); }
    }

    /// <summary>把每个字段塞一个不等于默认值的东西。</summary>
    static void Fill(VideoGradeSettings s, int seed)
    {
        var rng = new Random(seed);
        foreach (var f in typeof(VideoGradeSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var t = f.FieldType;
            if (t == typeof(float)) f.SetValue(s, (float)Math.Round(rng.NextDouble() * 1.7 + 0.11, 4));
            else if (t == typeof(int)) f.SetValue(s, rng.Next(1, 4));
            else if (t == typeof(bool)) f.SetValue(s, true);
            else if (t == typeof(Vector2)) f.SetValue(s, new Vector2(0.23f, 0.71f));
            else if (t == typeof(float[]))
            {
                var a = new float[8];
                for (int i = 0; i < 8; i++) a[i] = (float)Math.Round(rng.NextDouble(), 4);
                f.SetValue(s, a);
            }
            else if (t == typeof(AnimationCurve))
                f.SetValue(s, AnimationCurve.Linear(0f, 0.25f, 1f, 0.75f));
            else if (t == typeof(List<MaskGroup>))
            {
                var g = new MaskGroup { name = "组" + seed, exposure = 0.4f, showOverlay = true };
                g.parts.Add(new MaskPart { shape = 2, op = 1, invert = true, feather = 0.42f });
                g.parts.Add(new MaskPart { shape = 4, op = 2, lumMin = 0.3f, lumMax = 0.8f });
                f.SetValue(s, new List<MaskGroup> { g });
            }
        }

        // colorMatrix 是 12 个数，上面那段按 float[8] 填会把它弄短
        s.colorMatrix = new float[12];
        for (int i = 0; i < 12; i++) s.colorMatrix[i] = 0.1f * i + 0.03f;
    }

    static string Describe(object v)
    {
        if (v == null) return "null";
        if (v is AnimationCurve c) return c.length == 0 ? "curve[]" :
            "curve(" + string.Join(",", c.keys.Select(k => k.time + ":" + k.value)) + ")";
        if (v is float[] fa) return "[" + string.Join(",", fa) + "]";
        if (v is List<MaskGroup> gs)
            return "groups(" + string.Join(";", gs.Select(g =>
                g.name + "/" + g.exposure + "/" + g.parts.Count + "/" +
                string.Join(",", g.parts.Select(p => p.shape + ":" + p.op + ":" + p.invert + ":" + p.feather)))) + ")";
        if (v is Vector2 v2) return v2.ToString();
        return v.ToString();
    }

    public static int Run(string[] args)
    {
        Console.WriteLine("VideoGradeSettings 字段完整性");

        var src = new VideoGradeSettings();
        Fill(src, 1);

        var dst = src.Clone();
        var fields = typeof(VideoGradeSettings).GetFields(BindingFlags.Public | BindingFlags.Instance);

        int missed = 0;
        foreach (var f in fields)
        {
            string a = Describe(f.GetValue(src));
            string b = Describe(f.GetValue(dst));
            if (a == b) continue;
            missed++;
            Console.WriteLine("  Clone 丢了 {0,-22} 源={1}  克隆={2}", f.Name, a, b);
        }
        Check(missed == 0, $"Clone 覆盖全部 {fields.Length} 个字段（漏了 {missed} 个）");
        if (missed == 0) Console.WriteLine($"  OK   Clone 覆盖全部 {fields.Length} 个字段");

        // 深拷贝：改克隆体不能影响原件
        dst.maskGroups[0].parts[0].feather = 0.999f;
        dst.curveR = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        dst.hslHue[0] = -0.9f;
        dst.colorMatrix[0] = -5f;
        Check(Math.Abs(src.maskGroups[0].parts[0].feather - 0.42f) < 1e-4f, "蒙版是深拷贝");
        Check(Math.Abs(src.hslHue[0] - (-0.9f)) > 1e-6f, "HSL 数组是深拷贝");
        Check(Math.Abs(src.colorMatrix[0] - (-5f)) > 1e-6f, "校色矩阵是深拷贝");
        if (_fail == 0) Console.WriteLine("  OK   蒙版 / 数组 / 矩阵都是深拷贝");

        // Lerp 到两端应该分别等于两个源
        var a2 = new VideoGradeSettings(); Fill(a2, 2);
        var b2 = new VideoGradeSettings(); Fill(b2, 3);
        var into = new VideoGradeSettings();

        VideoGradeSettings.Lerp(a2, b2, 0f, into);
        int lerpMiss = 0;
        foreach (var f in fields)
        {
            if (f.FieldType != typeof(float)) continue;
            float x = (float)f.GetValue(a2), y = (float)f.GetValue(into);
            if (Math.Abs(x - y) < 1e-4f) continue;
            lerpMiss++;
            Console.WriteLine("  Lerp(t=0) 没覆盖 {0,-20} 期望={1} 实得={2}", f.Name, x, y);
        }
        Check(lerpMiss == 0, $"Lerp 覆盖全部连续字段（漏了 {lerpMiss} 个）");
        if (lerpMiss == 0) Console.WriteLine("  OK   Lerp(t=0) 等于起点，连续字段一个不漏");

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "全部通过" : ("失败 " + _fail + " 项"));
        return _fail == 0 ? 0 : 1;
    }
}
