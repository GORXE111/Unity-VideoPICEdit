using System;
using UnityEngine;

namespace Love.Video
{
    public enum TonemapMode
    {
        None = 0,
        Reinhard = 1,
        Filmic = 2,
        ACES = 3,
    }

    /// <summary>拍摄素材的 LOG 编码。解码要在其它一切之前做。</summary>
    public enum LogMode
    {
        None = 0,
        SLog3 = 1,      // Sony
        VLog = 2,       // Panasonic
        CLog3 = 3,      // Canon
        LogC3 = 4,      // ARRI
        DLog = 5,       // DJI
    }

    /// <summary>
    /// 一套调色参数。全局一套，管所有视频。
    /// 纯数据类，能直接用 JsonUtility 存读。
    /// </summary>
    [Serializable]
    public class VideoGradeSettings
    {
        // ---------- 基础校色 ----------
        public float exposure = 0f;        // -3 .. 3   （档，2 的幂）
        public float contrast = 1f;        //  0 .. 2
        public float saturation = 1f;      //  0 .. 2
        public float skinProtect = 0f;     //  0 .. 1   调饱和度时保护肤色不被带跑
        public float temperature = 0f;     // -1 .. 1   负=偏冷 正=偏暖
        public float tint = 0f;            // -1 .. 1   负=偏绿 正=偏品红
        public float hueShift = 0f;        // -0.5 .. 0.5
        public float highlights = 0f;      // -1 .. 1
        public float shadows = 0f;         // -1 .. 1

        // ---------- LOG 解码 ----------
        // 素材是 LOG 时必须先解回线性，再谈曝光和调色。
        // 直接对 LOG 拉对比度会得到又灰又脏的结果。
        public int logMode = (int)LogMode.None;

        // ---------- 色卡校色矩阵 ----------
        // 由 24 色卡最小二乘解出来的 3x4 矩阵（3x3 线性 + 偏移），行优先。
        // 单位矩阵表示不校色。
        public bool colorMatrixEnabled = false;
        public float[] colorMatrix = IdentityMatrix();

        // ---------- 裁剪 / 旋转 ----------
        // 裁剪框定义在「已做过 90 度旋转和翻转」的图像空间里，
        // 也就是用户在画布上看到的那个方向——所见即所裁。
        public bool cropEnabled = false;
        public float cropX = 0f, cropY = 0f;    // 裁剪框左下角，归一化
        public float cropW = 1f, cropH = 1f;    // 裁剪框尺寸，归一化
        public float straighten = 0f;           // -45 .. 45 度，绕裁剪框中心转
        public int rotate90 = 0;                // 0..3，90 度步进
        public bool flipH = false, flipV = false;

        // ---------- 去朦胧 ----------
        // 正=去雾，负=加雾。走大气散射模型，见 shader 里的 ApplyDehaze
        public float dehaze = 0f;               // -1 .. 1

        // ---------- HSL 八色带混合器 ----------
        // 八个色带各三根滑条。表达能力不如六条曲线，但调天空、调肤色时
        // 拖一根滑条就完事，比在曲线上找控制点快得多——这是使用频率最高的面板。
        public bool hslEnabled = false;
        public float[] hslHue = new float[HslBandCount];   // -1..1，映射到 ±36 度
        public float[] hslSat = new float[HslBandCount];   // -1..1
        public float[] hslLum = new float[HslBandCount];   // -1..1

        // ---------- 镜头畸变 ----------
        // k1 为负=桶形（广角），为正=枕形（长焦）。
        // AI 图最明显的破绽之一就是完全没有镜头畸变。
        public float distortK1 = 0f;      // -0.5 .. 0.5
        public float distortK2 = 0f;      // -0.3 .. 0.3  高阶项，控制边缘的非线性
        public float distortScale = 1f;   // 0.8 .. 1.3   缩放补偿，避免画面边缘露出空白

        // ---------- 画质提升 ----------
        public float denoise = 0f;          // 0..1   双边降噪，保边
        public float clarity = 0f;          // -1..1  通透度：大半径局部对比，只作用中间调
        public float texture = 0f;          // -1..1  纹理：小半径细节
        public float clarityRadius = 8f;    // 2..16  通透度的半径，按像素
        public float sharpenFocusOnly = 0f; // 0..1   锐化只作用于对焦区域的程度

        // ---------- 色调映射 ----------
        public int tonemap = (int)TonemapMode.None;

        // ---------- 色阶 ----------
        public float inBlack = 0f;         // 0 .. 0.5    输入黑点
        public float inWhite = 1f;         // 0.5 .. 1    输入白点
        public float levelsGamma = 1f;     // 0.2 .. 3    中间调
        public float outBlack = 0f;        // 0 .. 0.5    输出黑点
        public float outWhite = 1f;        // 0.5 .. 1    输出白点

        // ---------- Lift（暗部，白点不动） ----------
        public float lift = 0f;            // -0.3 .. 0.3  主控
        public float liftR = 0f, liftG = 0f, liftB = 0f;

        // ---------- Gamma（中间调） ----------
        public float gammaMaster = 1f;     // 0.2 .. 3
        public float gammaR = 1f, gammaG = 1f, gammaB = 1f;

        // ---------- Gain（亮部，黑点不动） ----------
        public float gainMaster = 1f;      // 0 .. 2
        public float gainR = 1f, gainG = 1f, gainB = 1f;

        // ---------- Offset（整体平移） ----------
        public float offset = 0f;          // -0.2 .. 0.2
        public float offsetR = 0f, offsetG = 0f, offsetB = 0f;

        // ---------- 色调分离 ----------
        public float shadowHue = 0.58f;        // 0 .. 1  默认偏青蓝
        public float shadowStrength = 0f;      // 0 .. 1
        public float highlightHue = 0.08f;     // 0 .. 1  默认偏橙
        public float highlightStrength = 0f;   // 0 .. 1
        public float splitBalance = 0f;        // -0.5 .. 0.5  阴影/高光的分界点

        // ---------- 曲线 ----------
        // AnimationCurve 能被 JsonUtility 直接序列化，所以曲线也能跟着预设一起存读。
        // 运行时会烘成一张 256x1 的查找贴图给 shader 用。
        public bool curveEnabled = false;
        public AnimationCurve curveMaster = Linear();
        public AnimationCurve curveR = Linear();
        public AnimationCurve curveG = Linear();
        public AnimationCurve curveB = Linear();

        // ---------- 六条曲线 ----------
        // 值域 0~1，中性值 0.5。恒等曲线是一条 y=0.5 的水平线，
        // 而不是 y=x——因为这六条表达的是"增减量"而不是"映射后的值"。
        public bool sixCurveEnabled = false;
        public AnimationCurve hueVsHue = Flat();
        public AnimationCurve hueVsSat = Flat();
        public AnimationCurve hueVsLum = Flat();
        public AnimationCurve lumVsSat = Flat();
        public AnimationCurve satVsSat = Flat();
        public AnimationCurve satVsLum = Flat();

        // ---------- 监看：斑马纹 ----------
        public float zebraHigh = 0f;   // 0=关，否则是过曝阈值（0.9~1）
        public float zebraLow = 0f;    // 0=关，否则是欠曝阈值（0~0.1）

        // ---------- 二级校色：Power Window ----------
        public bool secondaryEnabled = false;
        public int windowShape = 1;                              // 0 不限 1 椭圆 2 矩形 3 线性渐变
        public Vector2 windowCenter = new Vector2(0.5f, 0.5f);   // 画面 uv
        public Vector2 windowSize = new Vector2(0.45f, 0.45f);   // 半径（已做宽高比校正）
        public float windowRotation = 0f;                        // 度
        public float windowFeather = 0.3f;
        public bool windowInvert = false;

        // ---------- 二级校色：HSL 限定器 ----------
        public bool qualifierEnabled = false;
        public float qualHueCenter = 0.06f;   // 默认对准肤色
        public float qualHueRange = 0.06f;
        public float qualHueSoft = 0.04f;
        public float qualSatMin = 0.12f, qualSatMax = 1f, qualSatSoft = 0.08f;
        public float qualLumMin = 0.08f, qualLumMax = 1f, qualLumSoft = 0.08f;

        // ---------- 二级校色：遮罩内的调整 ----------
        public float secExposure = 0f;      // -2 .. 2
        public float secContrast = 1f;      //  0 .. 2
        public float secSaturation = 1f;    //  0 .. 2
        public float secHueShift = 0f;      // -0.5 .. 0.5
        public float secTintHue = 0.08f;
        public float secTintStrength = 0f;

        /// <summary>把遮罩本身以灰度输出，调限定器和窗口时必须靠它看边界。</summary>
        public bool showMask = false;

        // ---------- AI 主体蒙版 ----------
        // 蒙版贴图本身不在这里（它是每张图各自的，不能塞进 JSON 预设），
        // 这里只放"拿到蒙版之后怎么用"的参数。
        public bool maskInvert = false;        // 反选：改成作用于背景
        public float maskLow = 0f;             // 重映射下限，往上抬可以收缩边缘
        public float maskHigh = 1f;            // 重映射上限，往下压可以扩张边缘
        public float backgroundBlur = 0f;      // 0..1  蒙版外的虚化强度，伪景深靠它
        public bool secondaryUseMask = true;   // 二级校色是否叠加这张蒙版

        // ---------- 辉光与模糊 ----------
        public float bloomThreshold = 1f;  //  0 .. 2
        public float bloomIntensity = 0f;  //  0 .. 3
        public float bloomScatter = 0.6f;  //  0 .. 1   越大扩散越远
        public float blur = 0f;            //  0 .. 1   整体柔化

        // ---------- 细节与风格化 ----------
        public float sharpen = 0f;              // 0 .. 2
        public float vignetteIntensity = 0f;    // 0 .. 1
        public float vignetteSmoothness = 0.5f; // 0 .. 1
        public float grain = 0f;                // 0 .. 0.3
        public float chromatic = 0f;            // 0 .. 2
        public float dither = 0f;               // 0 .. 1   消除渐变处的色带

        /// <summary>
        /// 两套参数之间插值，剧情段之间的调色渐变用。
        ///
        /// 曲线、开关、矩阵这些没法有意义地插值，过半程直接切到目标值。
        /// 实际用起来察觉不到，因为渐变过程中真正在动的是那几十个连续量。
        /// </summary>
        public static void Lerp(VideoGradeSettings a, VideoGradeSettings b, float t, VideoGradeSettings into)
        {
            if (a == null || b == null || into == null) return;
            t = Mathf.Clamp01(t);

            into.exposure = Mathf.Lerp(a.exposure, b.exposure, t);
            into.contrast = Mathf.Lerp(a.contrast, b.contrast, t);
            into.saturation = Mathf.Lerp(a.saturation, b.saturation, t);
            into.skinProtect = Mathf.Lerp(a.skinProtect, b.skinProtect, t);
            into.temperature = Mathf.Lerp(a.temperature, b.temperature, t);
            into.tint = Mathf.Lerp(a.tint, b.tint, t);
            into.hueShift = Mathf.Lerp(a.hueShift, b.hueShift, t);
            into.highlights = Mathf.Lerp(a.highlights, b.highlights, t);
            into.shadows = Mathf.Lerp(a.shadows, b.shadows, t);
            into.distortK1 = Mathf.Lerp(a.distortK1, b.distortK1, t);
            into.distortK2 = Mathf.Lerp(a.distortK2, b.distortK2, t);
            into.distortScale = Mathf.Lerp(a.distortScale, b.distortScale, t);
            into.denoise = Mathf.Lerp(a.denoise, b.denoise, t);
            into.clarity = Mathf.Lerp(a.clarity, b.clarity, t);
            into.texture = Mathf.Lerp(a.texture, b.texture, t);
            into.clarityRadius = Mathf.Lerp(a.clarityRadius, b.clarityRadius, t);
            into.sharpenFocusOnly = Mathf.Lerp(a.sharpenFocusOnly, b.sharpenFocusOnly, t);
            into.inBlack = Mathf.Lerp(a.inBlack, b.inBlack, t);
            into.inWhite = Mathf.Lerp(a.inWhite, b.inWhite, t);
            into.levelsGamma = Mathf.Lerp(a.levelsGamma, b.levelsGamma, t);
            into.outBlack = Mathf.Lerp(a.outBlack, b.outBlack, t);
            into.outWhite = Mathf.Lerp(a.outWhite, b.outWhite, t);
            into.lift = Mathf.Lerp(a.lift, b.lift, t);
            into.liftR = Mathf.Lerp(a.liftR, b.liftR, t);
            into.gammaMaster = Mathf.Lerp(a.gammaMaster, b.gammaMaster, t);
            into.gammaR = Mathf.Lerp(a.gammaR, b.gammaR, t);
            into.gainMaster = Mathf.Lerp(a.gainMaster, b.gainMaster, t);
            into.gainR = Mathf.Lerp(a.gainR, b.gainR, t);
            into.offset = Mathf.Lerp(a.offset, b.offset, t);
            into.offsetR = Mathf.Lerp(a.offsetR, b.offsetR, t);
            into.shadowHue = Mathf.Lerp(a.shadowHue, b.shadowHue, t);
            into.shadowStrength = Mathf.Lerp(a.shadowStrength, b.shadowStrength, t);
            into.highlightHue = Mathf.Lerp(a.highlightHue, b.highlightHue, t);
            into.highlightStrength = Mathf.Lerp(a.highlightStrength, b.highlightStrength, t);
            into.splitBalance = Mathf.Lerp(a.splitBalance, b.splitBalance, t);
            into.zebraHigh = Mathf.Lerp(a.zebraHigh, b.zebraHigh, t);
            into.zebraLow = Mathf.Lerp(a.zebraLow, b.zebraLow, t);
            into.windowRotation = Mathf.Lerp(a.windowRotation, b.windowRotation, t);
            into.windowFeather = Mathf.Lerp(a.windowFeather, b.windowFeather, t);
            into.qualHueCenter = Mathf.Lerp(a.qualHueCenter, b.qualHueCenter, t);
            into.qualHueRange = Mathf.Lerp(a.qualHueRange, b.qualHueRange, t);
            into.qualHueSoft = Mathf.Lerp(a.qualHueSoft, b.qualHueSoft, t);
            into.qualSatMin = Mathf.Lerp(a.qualSatMin, b.qualSatMin, t);
            into.qualLumMin = Mathf.Lerp(a.qualLumMin, b.qualLumMin, t);
            into.secExposure = Mathf.Lerp(a.secExposure, b.secExposure, t);
            into.secContrast = Mathf.Lerp(a.secContrast, b.secContrast, t);
            into.secSaturation = Mathf.Lerp(a.secSaturation, b.secSaturation, t);
            into.secHueShift = Mathf.Lerp(a.secHueShift, b.secHueShift, t);
            into.secTintHue = Mathf.Lerp(a.secTintHue, b.secTintHue, t);
            into.secTintStrength = Mathf.Lerp(a.secTintStrength, b.secTintStrength, t);
            into.maskLow = Mathf.Lerp(a.maskLow, b.maskLow, t);
            into.maskHigh = Mathf.Lerp(a.maskHigh, b.maskHigh, t);
            into.backgroundBlur = Mathf.Lerp(a.backgroundBlur, b.backgroundBlur, t);
            into.bloomThreshold = Mathf.Lerp(a.bloomThreshold, b.bloomThreshold, t);
            into.bloomIntensity = Mathf.Lerp(a.bloomIntensity, b.bloomIntensity, t);
            into.bloomScatter = Mathf.Lerp(a.bloomScatter, b.bloomScatter, t);
            into.blur = Mathf.Lerp(a.blur, b.blur, t);
            into.sharpen = Mathf.Lerp(a.sharpen, b.sharpen, t);
            into.vignetteIntensity = Mathf.Lerp(a.vignetteIntensity, b.vignetteIntensity, t);
            into.vignetteSmoothness = Mathf.Lerp(a.vignetteSmoothness, b.vignetteSmoothness, t);
            into.grain = Mathf.Lerp(a.grain, b.grain, t);
            into.chromatic = Mathf.Lerp(a.chromatic, b.chromatic, t);
            into.dither = Mathf.Lerp(a.dither, b.dither, t);
            into.liftG = Mathf.Lerp(a.liftG, b.liftG, t);
            into.liftB = Mathf.Lerp(a.liftB, b.liftB, t);
            into.gammaG = Mathf.Lerp(a.gammaG, b.gammaG, t);
            into.gammaB = Mathf.Lerp(a.gammaB, b.gammaB, t);
            into.gainG = Mathf.Lerp(a.gainG, b.gainG, t);
            into.gainB = Mathf.Lerp(a.gainB, b.gainB, t);
            into.offsetG = Mathf.Lerp(a.offsetG, b.offsetG, t);
            into.offsetB = Mathf.Lerp(a.offsetB, b.offsetB, t);
            into.qualSatMax = Mathf.Lerp(a.qualSatMax, b.qualSatMax, t);
            into.qualSatSoft = Mathf.Lerp(a.qualSatSoft, b.qualSatSoft, t);
            into.qualLumMax = Mathf.Lerp(a.qualLumMax, b.qualLumMax, t);
            into.qualLumSoft = Mathf.Lerp(a.qualLumSoft, b.qualLumSoft, t);
            into.dehaze = Mathf.Lerp(a.dehaze, b.dehaze, t);
            into.straighten = Mathf.Lerp(a.straighten, b.straighten, t);
            into.cropX = Mathf.Lerp(a.cropX, b.cropX, t);
            into.cropY = Mathf.Lerp(a.cropY, b.cropY, t);
            into.cropW = Mathf.Lerp(a.cropW, b.cropW, t);
            into.cropH = Mathf.Lerp(a.cropH, b.cropH, t);
            LerpBands(a.hslHue, b.hslHue, t, ref into.hslHue);
            LerpBands(a.hslSat, b.hslSat, t, ref into.hslSat);
            LerpBands(a.hslLum, b.hslLum, t, ref into.hslLum);
            into.windowCenter = Vector2.Lerp(a.windowCenter, b.windowCenter, t);
            into.windowSize = Vector2.Lerp(a.windowSize, b.windowSize, t);

            // 离散量：过半程才切换
            var d = t < 0.5f ? a : b;
            into.logMode = d.logMode;
            into.colorMatrixEnabled = d.colorMatrixEnabled;
            into.colorMatrix = (float[])(d.colorMatrix ?? IdentityMatrix()).Clone();
            into.tonemap = d.tonemap;
            into.curveEnabled = d.curveEnabled;
            into.curveMaster = CopyCurve(d.curveMaster);
            into.curveR = CopyCurve(d.curveR);
            into.curveG = CopyCurve(d.curveG);
            into.curveB = CopyCurve(d.curveB);
            into.sixCurveEnabled = d.sixCurveEnabled;
            into.hueVsHue = CopyCurve(d.hueVsHue);
            into.hueVsSat = CopyCurve(d.hueVsSat);
            into.hueVsLum = CopyCurve(d.hueVsLum);
            into.lumVsSat = CopyCurve(d.lumVsSat);
            into.satVsSat = CopyCurve(d.satVsSat);
            into.satVsLum = CopyCurve(d.satVsLum);
            into.secondaryEnabled = d.secondaryEnabled;
            into.windowShape = d.windowShape;
            into.windowInvert = d.windowInvert;
            into.qualifierEnabled = d.qualifierEnabled;
            into.showMask = d.showMask;
            into.maskInvert = d.maskInvert;
            into.secondaryUseMask = d.secondaryUseMask;
            into.cropEnabled = d.cropEnabled;
            into.rotate90 = d.rotate90;
            into.flipH = d.flipH;
            into.flipV = d.flipV;
            into.hslEnabled = d.hslEnabled;
        }

        static void LerpBands(float[] a, float[] b, float t, ref float[] into)
        {
            if (into == null || into.Length != HslBandCount) into = Zero8();
            for (int i = 0; i < HslBandCount; i++)
            {
                float x = a != null && i < a.Length ? a[i] : 0f;
                float y = b != null && i < b.Length ? b[i] : 0f;
                into[i] = Mathf.Lerp(x, y, t);
            }
        }

        /// <summary>float[] 是引用类型，预设之间必须深拷贝，长度不对时补齐。</summary>
        static float[] CopyBands(float[] o)
        {
            var r = Zero8();
            if (o != null)
                for (int i = 0; i < HslBandCount && i < o.Length; i++) r[i] = o[i];
            return r;
        }

        public VideoGradeSettings Clone()
        {
            var c = new VideoGradeSettings();
            c.CopyFrom(this);
            return c;
        }

        public void CopyFrom(VideoGradeSettings o)
        {
            if (o == null) return;

            exposure = o.exposure; contrast = o.contrast; saturation = o.saturation;
            skinProtect = o.skinProtect; temperature = o.temperature; tint = o.tint;
            hueShift = o.hueShift; highlights = o.highlights; shadows = o.shadows;

            tonemap = o.tonemap;

            inBlack = o.inBlack; inWhite = o.inWhite; levelsGamma = o.levelsGamma;
            outBlack = o.outBlack; outWhite = o.outWhite;

            lift = o.lift; liftR = o.liftR; liftG = o.liftG; liftB = o.liftB;
            gammaMaster = o.gammaMaster; gammaR = o.gammaR; gammaG = o.gammaG; gammaB = o.gammaB;
            gainMaster = o.gainMaster; gainR = o.gainR; gainG = o.gainG; gainB = o.gainB;
            offset = o.offset; offsetR = o.offsetR; offsetG = o.offsetG; offsetB = o.offsetB;

            shadowHue = o.shadowHue; shadowStrength = o.shadowStrength;
            highlightHue = o.highlightHue; highlightStrength = o.highlightStrength;
            splitBalance = o.splitBalance;

            bloomThreshold = o.bloomThreshold; bloomIntensity = o.bloomIntensity;
            bloomScatter = o.bloomScatter; blur = o.blur;

            sharpen = o.sharpen;
            vignetteIntensity = o.vignetteIntensity; vignetteSmoothness = o.vignetteSmoothness;
            grain = o.grain; chromatic = o.chromatic; dither = o.dither;

            // 曲线是引用类型，必须深拷贝，否则两套设置会共用同一条曲线
            curveEnabled = o.curveEnabled;
            curveMaster = CopyCurve(o.curveMaster);
            curveR = CopyCurve(o.curveR);
            curveG = CopyCurve(o.curveG);
            curveB = CopyCurve(o.curveB);

            secondaryEnabled = o.secondaryEnabled;
            windowShape = o.windowShape; windowCenter = o.windowCenter; windowSize = o.windowSize;
            windowRotation = o.windowRotation; windowFeather = o.windowFeather; windowInvert = o.windowInvert;

            qualifierEnabled = o.qualifierEnabled;
            qualHueCenter = o.qualHueCenter; qualHueRange = o.qualHueRange; qualHueSoft = o.qualHueSoft;
            qualSatMin = o.qualSatMin; qualSatMax = o.qualSatMax; qualSatSoft = o.qualSatSoft;
            qualLumMin = o.qualLumMin; qualLumMax = o.qualLumMax; qualLumSoft = o.qualLumSoft;

            secExposure = o.secExposure; secContrast = o.secContrast; secSaturation = o.secSaturation;
            secHueShift = o.secHueShift; secTintHue = o.secTintHue; secTintStrength = o.secTintStrength;

            showMask = o.showMask;

            logMode = o.logMode;
            colorMatrixEnabled = o.colorMatrixEnabled;
            colorMatrix = (float[])(o.colorMatrix ?? IdentityMatrix()).Clone();

            distortK1 = o.distortK1; distortK2 = o.distortK2; distortScale = o.distortScale;

            denoise = o.denoise; clarity = o.clarity; texture = o.texture;
            clarityRadius = o.clarityRadius; sharpenFocusOnly = o.sharpenFocusOnly;

            sixCurveEnabled = o.sixCurveEnabled;
            hueVsHue = CopyCurve(o.hueVsHue); hueVsSat = CopyCurve(o.hueVsSat);
            hueVsLum = CopyCurve(o.hueVsLum); lumVsSat = CopyCurve(o.lumVsSat);
            satVsSat = CopyCurve(o.satVsSat); satVsLum = CopyCurve(o.satVsLum);
            zebraHigh = o.zebraHigh; zebraLow = o.zebraLow;

            maskInvert = o.maskInvert; maskLow = o.maskLow; maskHigh = o.maskHigh;
            backgroundBlur = o.backgroundBlur; secondaryUseMask = o.secondaryUseMask;

            cropEnabled = o.cropEnabled;
            cropX = o.cropX; cropY = o.cropY; cropW = o.cropW; cropH = o.cropH;
            straighten = o.straighten; rotate90 = o.rotate90;
            flipH = o.flipH; flipV = o.flipV;

            dehaze = o.dehaze;

            hslEnabled = o.hslEnabled;
            hslHue = CopyBands(o.hslHue);
            hslSat = CopyBands(o.hslSat);
            hslLum = CopyBands(o.hslLum);
        }

        public const int HslBandCount = 8;

        /// <summary>
        /// 八个色带的中心色相。刻意不等距——橙色带（肤色）和黄色带挤在前 1/6，
        /// 因为人眼对这一段最敏感，而绿到蓝之间大片色相实际很少单独去调。
        /// 这套分法跟 Camera Raw / Lightroom 一致。
        /// </summary>
        public static readonly float[] HslCenters =
        {
            0f / 360f, 30f / 360f, 60f / 360f, 120f / 360f,
            180f / 360f, 240f / 360f, 280f / 360f, 320f / 360f,
        };

        public static readonly string[] HslNames =
        { "红", "橙", "黄", "绿", "青", "蓝", "紫", "品红" };

        static float[] Zero8() => new float[HslBandCount];

        /// <summary>裁剪 / 旋转 / 翻转里只要有一项不是恒等，就得走几何 Pass。</summary>
        public bool HasGeometry =>
            rotate90 != 0 || flipH || flipV || Mathf.Abs(straighten) > 0.001f ||
            (cropEnabled && (cropX > 0.0005f || cropY > 0.0005f ||
                             cropW < 0.9995f || cropH < 0.9995f));

        /// <summary>裁剪 / 旋转之后的输出尺寸。导出和预览都要按它来分配 RT。</summary>
        public void OutputSize(int srcW, int srcH, out int w, out int h)
        {
            // 90 / 270 度时长宽互换
            int bw = (rotate90 & 1) == 0 ? srcW : srcH;
            int bh = (rotate90 & 1) == 0 ? srcH : srcW;

            float cw = cropEnabled ? Mathf.Clamp(cropW, 0.01f, 1f) : 1f;
            float ch = cropEnabled ? Mathf.Clamp(cropH, 0.01f, 1f) : 1f;

            w = Mathf.Max(1, Mathf.RoundToInt(bw * cw));
            h = Mathf.Max(1, Mathf.RoundToInt(bh * ch));
        }

        /// <summary>
        /// 把「显示画面」上的 uv 反解回源图 uv。
        ///
        /// 这段必须和 VideoGrade.shader 的几何 Pass 逐步对应——画布上的吸管、
        /// 色卡角点这些都靠它换算，两边一旦不同步，取色就会取到别处的像素，
        /// 而且因为偏移量往往不大，肉眼还不容易发现。
        /// </summary>
        public Vector2 DisplayUvToSource(Vector2 uv, int srcW, int srcH)
        {
            float cw = cropEnabled ? Mathf.Clamp(cropW, 0.01f, 1f) : 1f;
            float ch = cropEnabled ? Mathf.Clamp(cropH, 0.01f, 1f) : 1f;
            float cx = cropEnabled ? Mathf.Clamp(cropX, 0f, 1f - cw) : 0f;
            float cy = cropEnabled ? Mathf.Clamp(cropY, 0f, 1f - ch) : 0f;

            int rot = ((rotate90 % 4) + 4) % 4;
            float rw = (rot & 1) == 0 ? srcW : srcH;
            float rh = (rot & 1) == 0 ? srcH : srcW;
            float aspect = rh > 0.5f ? rw / rh : 1f;

            // 裁剪框内的偏移 -> 拉成正方形像素 -> 旋转 -> 拉回去
            var d = new Vector2((uv.x - 0.5f) * cw, (uv.y - 0.5f) * ch);
            d.x *= aspect;
            float rad = straighten * Mathf.Deg2Rad;
            float co = Mathf.Cos(rad), si = Mathf.Sin(rad);
            d = new Vector2(d.x * co - d.y * si, d.x * si + d.y * co);
            d.x /= aspect;

            var t = new Vector2(cx + cw * 0.5f + d.x, cy + ch * 0.5f + d.y);

            // 正向是 显示 = 旋转(翻转(源))，反解要先撤旋转再撤翻转
            if (rot == 1)      t = new Vector2(1f - t.y, t.x);
            else if (rot == 2) t = new Vector2(1f - t.x, 1f - t.y);
            else if (rot == 3) t = new Vector2(t.y, 1f - t.x);

            if (flipH) t.x = 1f - t.x;
            if (flipV) t.y = 1f - t.y;
            return t;
        }

        /// <summary>把裁剪框恢复成整幅，旋转翻转不动。</summary>
        public void ResetCrop()
        {
            cropX = cropY = 0f;
            cropW = cropH = 1f;
            straighten = 0f;
        }

                /// <summary>3x4 单位矩阵，行优先：[r0 r1 r2 off | ...]</summary>
        public static float[] IdentityMatrix() => new float[]
        {
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
        };

        /// <summary>
        /// 胶片化预设：把"去 AI 感"的零件一次配齐。
        ///
        /// AI 图一眼假的根源是太干净——没有传感器噪点、没有镜头缺陷、
        /// 全画面等锐没有焦平面。这里逐项补回来。
        /// 背景虚化需要先生成 AI 主体蒙版才会生效。
        /// </summary>
        public void ApplyFilmLook()
        {
            grain = 0.045f;               // 传感器噪点
            vignetteIntensity = 0.28f;    // 镜头暗角
            vignetteSmoothness = 0.55f;
            chromatic = 0.35f;            // 紫边色散
            distortK1 = -0.045f;          // 轻微桶形畸变
            distortScale = 1.02f;         // 补偿畸变露出的边缘

            texture = -0.12f;             // 稍微压一点塑料感的过锐细节
            clarity = 0.18f;              // 中间调通透度找回来
            sharpen = 0.25f;
            sharpenFocusOnly = 0.8f;      // 只锐对焦区，制造焦平面

            backgroundBlur = 0.45f;       // 有主体蒙版时才生效

            shadowHue = 0.58f;            // 阴影偏青、高光偏暖，胶片的典型偏色
            shadowStrength = 0.12f;
            highlightHue = 0.08f;
            highlightStrength = 0.10f;

            contrast = 1.06f;
            dither = 0.5f;
        }

        public void ResetColorMatrix()
        {
            colorMatrix = IdentityMatrix();
            colorMatrixEnabled = false;
        }

        static AnimationCurve Linear() => AnimationCurve.Linear(0f, 0f, 1f, 1f);

        /// <summary>六条曲线的恒等状态：一条 y=0.5 的水平线。</summary>
        public static AnimationCurve Flat() => AnimationCurve.Linear(0f, 0.5f, 1f, 0.5f);

        /// <summary>六条曲线的形状签名，用来判断要不要重烘查找贴图。</summary>
        public int SixCurveSignature()
        {
            unchecked
            {
                int h = sixCurveEnabled ? 19 : 7;
                foreach (var c in new[] { hueVsHue, hueVsSat, hueVsLum, lumVsSat, satVsSat, satVsLum })
                    h = h * 31 + CurveHash(c);
                return h;
            }
        }

        static AnimationCurve CopyCurve(AnimationCurve c) =>
            c == null || c.length == 0 ? Linear() : new AnimationCurve(c.keys);

        /// <summary>把曲线的形状压成一个数字，用来判断要不要重新烘查找贴图。</summary>
        public int CurveSignature()
        {
            unchecked
            {
                int hash = curveEnabled ? 17 : 31;
                hash = hash * 31 + CurveHash(curveMaster);
                hash = hash * 31 + CurveHash(curveR);
                hash = hash * 31 + CurveHash(curveG);
                hash = hash * 31 + CurveHash(curveB);
                return hash;
            }
        }

        static int CurveHash(AnimationCurve c)
        {
            if (c == null) return 0;
            unchecked
            {
                int hash = c.length;
                var keys = c.keys;
                for (int i = 0; i < keys.Length; i++)
                {
                    var k = keys[i];
                    hash = hash * 31 + k.time.GetHashCode();
                    hash = hash * 31 + k.value.GetHashCode();
                    hash = hash * 31 + k.inTangent.GetHashCode();
                    hash = hash * 31 + k.outTangent.GetHashCode();
                }
                return hash;
            }
        }

        /// <summary>二级校色的染色，中性时是 (1,1,1)。</summary>
        public Vector3 SecondaryTint => HueTint(secTintHue, secTintStrength);

        /// <summary>Power Window 旋转角的 cos/sin，省得每像素算一遍三角函数。</summary>
        public Vector4 WindowRotationVector
        {
            get
            {
                float rad = windowRotation * Mathf.Deg2Rad;
                return new Vector4(Mathf.Cos(rad), Mathf.Sin(rad), 0f, 0f);
            }
        }

        public void Reset() => CopyFrom(new VideoGradeSettings());

        public string ToJson() => JsonUtility.ToJson(this, true);

        public static VideoGradeSettings FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<VideoGradeSettings>(json); }
            catch (Exception e)
            {
                Debug.LogError($"[VideoGrade] 调色参数解析失败：{e.Message}");
                return null;
            }
        }

        #region 传给 Shader 前的换算

        // 主控和单通道合并成一个三维向量再传给 shader，
        // 免得在片元着色器里每像素都算一遍这些常量。
        public Vector3 LiftRGB   => new Vector3(lift + liftR, lift + liftG, lift + liftB);
        public Vector3 OffsetRGB => new Vector3(offset + offsetR, offset + offsetG, offset + offsetB);
        public Vector3 GainRGB   => new Vector3(gainMaster * gainR, gainMaster * gainG, gainMaster * gainB);

        public Vector3 GammaRGB => new Vector3(
            Mathf.Max(0.01f, gammaMaster * gammaR),
            Mathf.Max(0.01f, gammaMaster * gammaG),
            Mathf.Max(0.01f, gammaMaster * gammaB));

        /// <summary>阴影染色，中性时是 (1,1,1)。</summary>
        public Vector3 ShadowTint => HueTint(shadowHue, shadowStrength);

        /// <summary>高光染色，中性时是 (1,1,1)。</summary>
        public Vector3 HighlightTint => HueTint(highlightHue, highlightStrength);

        static Vector3 HueTint(float hue, float strength)
        {
            if (strength <= 0.0001f) return Vector3.one;
            Color c = Color.HSVToRGB(Mathf.Repeat(hue, 1f), 1f, 1f);
            // 往白色方向插值，强度 0 时完全不染色
            return new Vector3(
                Mathf.Lerp(1f, c.r, strength),
                Mathf.Lerp(1f, c.g, strength),
                Mathf.Lerp(1f, c.b, strength));
        }

        /// <summary>
        /// 把色温/色调换算成 LMS 空间的缩放系数。
        /// 走的是 CIE xy 色度坐标 -> LMS 的标准白平衡做法，比直接乘 RGB 准得多，
        /// 不会出现调暖的时候蓝色通道被压死的情况。
        /// </summary>
        public Vector3 ComputeColorBalance()
        {
            float t1 = temperature * 0.05f;
            float t2 = tint * 0.05f;

            // 以 D65 为基准偏移
            float x = 0.31271f - t1 * (t1 < 0f ? 0.1f : 0.05f);
            float y = StandardIlluminantY(x) + t2 * 0.05f;

            Vector3 w1 = new Vector3(0.949237f, 1.03542f, 1.08728f);   // D65 的 LMS
            Vector3 w2 = CIExyToLMS(x, y);

            return new Vector3(w1.x / w2.x, w1.y / w2.y, w1.z / w2.z);
        }

        static float StandardIlluminantY(float x) => 2.87f * x - 3f * x * x - 0.27509507f;

        static Vector3 CIExyToLMS(float x, float y)
        {
            float Y = 1f;
            float X = Y * x / y;
            float Z = Y * (1f - x - y) / y;

            float L =  0.7328f * X + 0.4296f * Y - 0.1624f * Z;
            float M = -0.7036f * X + 1.6975f * Y + 0.0061f * Z;
            float S =  0.0030f * X + 0.0136f * Y + 0.9834f * Z;

            return new Vector3(L, M, S);
        }

        #endregion
    }
}
