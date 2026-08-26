using System;
using System.Collections.Generic;
using UnityEngine;

namespace Love.Video
{
    /// <summary>蒙版部件的来源。</summary>
    public enum MaskShape
    {
        Ellipse = 0,          // 径向：椭圆，带羽化
        Rect = 1,
        LinearGradient = 2,   // 线性渐变：压天空、提前景
        ColorRange = 3,       // 按色相 + 饱和度圈一块
        LuminanceRange = 4,   // 按亮度圈一块，比如"只调高光"
        DepthRange = 5,       // 按深度圈一块，需要深度图
        Subject = 6,          // AI 主体分割
        Brush = 7,            // 手绘
        Sky = 8,              // 天空。从顶边漫延出来的，不是 AI
    }

    /// <summary>部件之间怎么合。第一个部件恒按「加」处理——总得先有东西才谈得上减和交。</summary>
    public enum MaskOp
    {
        Add = 0,        // 并集
        Subtract = 1,   // 差集
        Intersect = 2,  // 交集
    }

    /// <summary>
    /// 蒙版里的一个部件。
    ///
    /// 一个部件只有一种来源，形状类和范围类的参数混在同一个类里——
    /// JsonUtility 不支持多态，拆成继承体系就存不下来了。用不到的字段留着默认值，
    /// 反正每个才几个 float。
    /// </summary>
    [Serializable]
    public class MaskPart
    {
        public int shape = (int)MaskShape.Ellipse;
        public int op = (int)MaskOp.Add;
        public bool invert;

        /// <summary>
        /// 临时不参与合成。
        ///
        /// **叫 muted 而不是 enabled 是有原因的。** JsonUtility 读老预设时，
        /// 文件里没有的 bool 一律是 false；如果字段叫 enabled，
        /// 那所有老预设里的部件读进来全部失效，而且是静默的。
        /// 用"默认 false = 维持原样"的命名，加字段才安全。
        /// </summary>
        public bool muted;
        public float opacity = 1f;

        // ---- 形状类（椭圆 / 矩形 / 线性渐变共用）----
        public Vector2 center = new Vector2(0.5f, 0.5f);
        public Vector2 size = new Vector2(0.35f, 0.35f);
        public float rotation;          // 度
        public float feather = 0.35f;

        // ---- 颜色范围 ----
        public float hueCenter = 0.06f;   // 默认对准肤色
        public float hueRange = 0.06f;
        public float hueSoft = 0.04f;
        public float satMin = 0.10f, satMax = 1f, satSoft = 0.08f;

        // ---- 亮度范围 ----
        public float lumMin = 0f, lumMax = 1f, lumSoft = 0.08f;

        // ---- 深度范围 ----
        public float depthMin = 0f, depthMax = 1f, depthSoft = 0.08f;

        // ---- 手绘 ----
        /// <summary>指向窗口里那一批笔刷贴图的下标。贴图本身进不了 JSON。</summary>
        public int brushId = -1;

        public MaskShape Shape => (MaskShape)shape;
        public MaskOp Op => (MaskOp)op;

        /// <summary>形状类的部件才有几何参数，界面据此决定画哪一组控件。</summary>
        public bool IsGeometric =>
            Shape == MaskShape.Ellipse || Shape == MaskShape.Rect || Shape == MaskShape.LinearGradient;

        public MaskPart Clone() => (MaskPart)MemberwiseClone();
    }

    /// <summary>
    /// 一个蒙版组：若干部件合成一张蒙版，外加这一组自己的调整参数。
    ///
    /// 这是 Lightroom 的结构。区别于我们原来那套「一个 Power Window ∩ 一个限定器」——
    /// 那样只能有一块选区。现在可以压天空一组、提亮人脸一组、单独调地面一组，互不干扰。
    /// </summary>
    [Serializable]
    public class MaskGroup
    {
        public string name = "蒙版";
        public bool enabled = true;

        /// <summary>把这一组的蒙版以红色叠加显示出来。调选区时必须能看见边界，靠猜是调不出来的。</summary>
        public bool showOverlay;

        /// <summary>
        /// 独看。只要有任何一组开着，就只有开着的那些参与渲染。
        ///
        /// 和 <see cref="enabled"/> 分开：enabled 是"这组要不要"，
        /// 独看是"这会儿我只想看这组"。两者混用的话，
        /// 关掉独看之后没法把原来关掉的那些恢复回去。
        /// </summary>
        public bool solo;

        public List<MaskPart> parts = new List<MaskPart>();

        // ---- 组内调整。都是 gamma 空间的逐像素运算，不需要额外的贴图 ----
        public float exposure = 0f;      // -2 .. 2
        public float contrast = 1f;      //  0 .. 2
        public float highlights = 0f;    // -1 .. 1
        public float shadows = 0f;       // -1 .. 1
        public float saturation = 1f;    //  0 .. 2
        public float hueShift = 0f;      // -0.5 .. 0.5
        public float tintHue = 0.08f;
        public float tintStrength = 0f;

        public MaskGroup Clone()
        {
            var g = (MaskGroup)MemberwiseClone();
            g.parts = new List<MaskPart>(parts.Count);
            foreach (var p in parts) g.parts.Add(p.Clone());
            return g;
        }

        /// <summary>染色算成 RGB。和二级校色那套换算保持一致。</summary>
        public Vector3 TintRGB()
        {
            if (tintStrength <= 0.0001f) return Vector3.one;
            Color c = Color.HSVToRGB(Mathf.Repeat(tintHue, 1f), 1f, 1f);
            return new Vector3(Mathf.Lerp(1f, c.r, tintStrength),
                               Mathf.Lerp(1f, c.g, tintStrength),
                               Mathf.Lerp(1f, c.b, tintStrength));
        }

        /// <summary>这一组是不是根本没改动。全是中性值就没必要为它跑两趟全分辨率的 Pass。</summary>
        public bool HasEffect =>
            Mathf.Abs(exposure) > 0.001f ||
            Mathf.Abs(contrast - 1f) > 0.001f ||
            Mathf.Abs(highlights) > 0.001f ||
            Mathf.Abs(shadows) > 0.001f ||
            Mathf.Abs(saturation - 1f) > 0.001f ||
            Mathf.Abs(hueShift) > 0.001f ||
            tintStrength > 0.001f ||
            showOverlay;      // 只看选区时也得跑

        public static MaskGroup Radial(string name = "径向")
        {
            var g = new MaskGroup { name = name };
            g.parts.Add(new MaskPart { shape = (int)MaskShape.Ellipse });
            return g;
        }

        public static MaskGroup Gradient(string name = "渐变")
        {
            var g = new MaskGroup { name = name };
            g.parts.Add(new MaskPart
            {
                shape = (int)MaskShape.LinearGradient,
                center = new Vector2(0.5f, 0.75f),
                size = new Vector2(0.5f, 0.25f),
            });
            return g;
        }

        /// <summary>天空蒙版。压蓝天、救过曝的白天空，这是最常用的一个。</summary>
        public static MaskGroup SkyMask(string name = "天空")
        {
            var g = new MaskGroup { name = name };
            g.parts.Add(new MaskPart { shape = (int)MaskShape.Sky });
            return g;
        }

        public static MaskGroup SubjectMask(string name = "主体")
        {
            var g = new MaskGroup { name = name };
            g.parts.Add(new MaskPart { shape = (int)MaskShape.Subject });
            return g;
        }
    }
}
