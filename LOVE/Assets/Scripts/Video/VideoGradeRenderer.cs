using System.Collections.Generic;
using UnityEngine;

namespace Love.Video
{
    /// <summary>
    /// 调色渲染核心。故意做成普通 C# 类而不是 MonoBehaviour——
    /// 这样它既能被场景里的 VideoPostProcessor 用来处理视频，
    /// 也能被编辑器工具直接拿去处理静态图片，不需要场景、不需要进 Play。
    ///
    /// 输入是任意 Texture，输出到任意 RenderTexture，中间的管线完全一样。
    /// </summary>
    public class VideoGradeRenderer
    {
        /// <summary>这一次渲染的开关项，和调色参数分开，因为它们是"看"的方式而不是"调"的内容。</summary>
        public struct Options
        {
            public bool bypass;          // 直接输出原图
            public bool splitCompare;    // 左原图右成片
            public float splitPosition;

            /// <summary>AI 主体蒙版。每张图各不相同，所以放在这里而不是 settings 里。</summary>
            public Texture externalMask;

            /// <summary>导入的 .cube LUT。每个 look 一张，不属于参数集。</summary>
            public Texture3D lut;
            public float lutAmount;

            /// <summary>深度图。深度范围蒙版要用，没有时那种部件退化成全选。</summary>
            public Texture depthMap;

            /// <summary>
            /// 天空蒙版。和主体蒙版一样是逐图算出来的，不属于参数集。
            /// 已经是几何变换之后的构图，所以直接按显示 uv 采样就对得上。
            /// </summary>
            public Texture skyMask;

            /// <summary>
            /// 手绘笔刷贴图，按 <see cref="MaskPart.brushId"/> 取。
            /// 贴图进不了 JSON，所以由窗口持有、渲染时递进来。
            /// </summary>
            public IList<Texture> brushes;

            public static Options Default => new Options { splitPosition = 0.5f };
        }

        #region Shader 属性 ID

        static readonly int IdThreshold   = Shader.PropertyToID("_Threshold");
        static readonly int IdSoftKnee    = Shader.PropertyToID("_SoftKnee");
        static readonly int IdBloomTex    = Shader.PropertyToID("_BloomTex");
        static readonly int IdBlurTex     = Shader.PropertyToID("_BlurTex");
        static readonly int IdBloomInt    = Shader.PropertyToID("_BloomIntensity");
        static readonly int IdBlurAmount  = Shader.PropertyToID("_BlurAmount");
        static readonly int IdSharpen     = Shader.PropertyToID("_Sharpen");
        static readonly int IdExposure    = Shader.PropertyToID("_Exposure");
        static readonly int IdBalance     = Shader.PropertyToID("_ColorBalance");
        static readonly int IdTonemap     = Shader.PropertyToID("_TonemapMode");
        static readonly int IdLevels      = Shader.PropertyToID("_Levels");
        static readonly int IdLevelsGamma = Shader.PropertyToID("_LevelsGamma");
        static readonly int IdLift        = Shader.PropertyToID("_Lift");
        static readonly int IdGamma       = Shader.PropertyToID("_Gamma");
        static readonly int IdGain        = Shader.PropertyToID("_Gain");
        static readonly int IdOffset      = Shader.PropertyToID("_Offset");
        static readonly int IdContrast    = Shader.PropertyToID("_Contrast");
        static readonly int IdHighlights  = Shader.PropertyToID("_Highlights");
        static readonly int IdShadows     = Shader.PropertyToID("_Shadows");
        static readonly int IdShadowTint  = Shader.PropertyToID("_ShadowTint");
        static readonly int IdHighTint    = Shader.PropertyToID("_HighlightTint");
        static readonly int IdSplitBal    = Shader.PropertyToID("_SplitBalance");
        static readonly int IdSaturation  = Shader.PropertyToID("_Saturation");
        static readonly int IdSkinProtect = Shader.PropertyToID("_SkinProtect");
        static readonly int IdHueShift    = Shader.PropertyToID("_HueShift");
        static readonly int IdVigInt      = Shader.PropertyToID("_VignetteIntensity");
        static readonly int IdVigSmooth   = Shader.PropertyToID("_VignetteSmoothness");
        static readonly int IdGrain       = Shader.PropertyToID("_Grain");
        static readonly int IdChromatic   = Shader.PropertyToID("_Chromatic");
        static readonly int IdDither      = Shader.PropertyToID("_Dither");
        static readonly int IdAspect      = Shader.PropertyToID("_Aspect");
        static readonly int IdGrainSeed   = Shader.PropertyToID("_GrainSeed");
        static readonly int IdSplitOn     = Shader.PropertyToID("_SplitEnabled");
        static readonly int IdSplitPos    = Shader.PropertyToID("_SplitPos");
        static readonly int IdCurveLut    = Shader.PropertyToID("_CurveLut");
        static readonly int IdWinShape    = Shader.PropertyToID("_WindowShape");
        static readonly int IdWinCenter   = Shader.PropertyToID("_WindowCenter");
        static readonly int IdWinSize     = Shader.PropertyToID("_WindowSize");
        static readonly int IdWinRot      = Shader.PropertyToID("_WindowRot");
        static readonly int IdWinFeather  = Shader.PropertyToID("_WindowFeather");
        static readonly int IdWinInvert   = Shader.PropertyToID("_WindowInvert");
        static readonly int IdQualOn      = Shader.PropertyToID("_QualEnabled");
        static readonly int IdQualHue     = Shader.PropertyToID("_QualHue");
        static readonly int IdQualSat     = Shader.PropertyToID("_QualSat");
        static readonly int IdQualLum     = Shader.PropertyToID("_QualLum");
        static readonly int IdSecExposure = Shader.PropertyToID("_SecExposure");
        static readonly int IdSecContrast = Shader.PropertyToID("_SecContrast");
        static readonly int IdSecSat      = Shader.PropertyToID("_SecSaturation");
        static readonly int IdSecHue      = Shader.PropertyToID("_SecHueShift");
        static readonly int IdSecTint     = Shader.PropertyToID("_SecTint");
        static readonly int IdShowMask    = Shader.PropertyToID("_ShowMask");
        static readonly int IdMaskTex     = Shader.PropertyToID("_MaskTex");
        static readonly int IdMaskInvert  = Shader.PropertyToID("_MaskInvert");
        static readonly int IdMaskRemap   = Shader.PropertyToID("_MaskRemap");
        static readonly int IdBgBlur      = Shader.PropertyToID("_BgBlur");
        static readonly int IdSecUseMask  = Shader.PropertyToID("_SecUseMask");
        static readonly int IdLogMode     = Shader.PropertyToID("_LogMode");
        static readonly int IdMatR        = Shader.PropertyToID("_ColorMatrixR");
        static readonly int IdMatG        = Shader.PropertyToID("_ColorMatrixG");
        static readonly int IdMatB        = Shader.PropertyToID("_ColorMatrixB");
        static readonly int IdMatOn       = Shader.PropertyToID("_ColorMatrixOn");
        static readonly int IdDenoise     = Shader.PropertyToID("_Denoise");
        static readonly int IdClarity     = Shader.PropertyToID("_Clarity");
        static readonly int IdTexture     = Shader.PropertyToID("_Texture");
        static readonly int IdClarityRad  = Shader.PropertyToID("_ClarityRadius");
        static readonly int IdSharpenFocus = Shader.PropertyToID("_SharpenFocusOnly");
        static readonly int IdDistK1      = Shader.PropertyToID("_DistortK1");
        static readonly int IdDistK2      = Shader.PropertyToID("_DistortK2");
        static readonly int IdDistScale   = Shader.PropertyToID("_DistortScale");
        static readonly int IdLutTex      = Shader.PropertyToID("_LutTex");
        static readonly int IdLutSize     = Shader.PropertyToID("_LutSize");
        static readonly int IdLutAmount   = Shader.PropertyToID("_LutAmount");
        static readonly int IdSixCurveTex = Shader.PropertyToID("_SixCurveTex");
        static readonly int IdZebra       = Shader.PropertyToID("_Zebra");
        static readonly int IdDehaze      = Shader.PropertyToID("_Dehaze");
        static readonly int IdHslHueA     = Shader.PropertyToID("_HslHueA");
        static readonly int IdHslHueB     = Shader.PropertyToID("_HslHueB");
        static readonly int IdHslSatA     = Shader.PropertyToID("_HslSatA");
        static readonly int IdHslSatB     = Shader.PropertyToID("_HslSatB");
        static readonly int IdHslLumA     = Shader.PropertyToID("_HslLumA");
        static readonly int IdHslLumB     = Shader.PropertyToID("_HslLumB");
        static readonly int IdCropRect    = Shader.PropertyToID("_CropRect");
        static readonly int IdStraighten  = Shader.PropertyToID("_Straighten");
        static readonly int IdGeoFlags    = Shader.PropertyToID("_GeoFlags");
        static readonly int IdRawTex      = Shader.PropertyToID("_RawTex");
        static readonly int IdPrevMask    = Shader.PropertyToID("_PrevMask");
        static readonly int IdPartTex     = Shader.PropertyToID("_PartTex");
        static readonly int IdDepthTex    = Shader.PropertyToID("_DepthTex");
        static readonly int IdPartShape   = Shader.PropertyToID("_PartShape");
        static readonly int IdPartOp      = Shader.PropertyToID("_PartOp");
        static readonly int IdPartInvert  = Shader.PropertyToID("_PartInvert");
        static readonly int IdPartOpacity = Shader.PropertyToID("_PartOpacity");
        static readonly int IdPartCenter  = Shader.PropertyToID("_PartCenter");
        static readonly int IdPartSize    = Shader.PropertyToID("_PartSize");
        static readonly int IdPartRot     = Shader.PropertyToID("_PartRot");
        static readonly int IdPartFeather = Shader.PropertyToID("_PartFeather");
        static readonly int IdPartHue     = Shader.PropertyToID("_PartHue");
        static readonly int IdPartSat     = Shader.PropertyToID("_PartSat");
        static readonly int IdPartLum     = Shader.PropertyToID("_PartLum");
        static readonly int IdPartDepth   = Shader.PropertyToID("_PartDepth");
        static readonly int IdGroupMask   = Shader.PropertyToID("_GroupMask");
        static readonly int IdGExposure   = Shader.PropertyToID("_GExposure");
        static readonly int IdGContrast   = Shader.PropertyToID("_GContrast");
        static readonly int IdGHighlights = Shader.PropertyToID("_GHighlights");
        static readonly int IdGShadows    = Shader.PropertyToID("_GShadows");
        static readonly int IdGSaturation = Shader.PropertyToID("_GSaturation");
        static readonly int IdGHueShift   = Shader.PropertyToID("_GHueShift");
        static readonly int IdGTint       = Shader.PropertyToID("_GTint");
        static readonly int IdGOverlay    = Shader.PropertyToID("_GOverlay");

        #endregion

        const int CurveLutSize = 256;

        readonly Material _material;
        Texture2D _curveLut;
        int _curveSignature = int.MinValue;

        /// <summary>颗粒的随机种子。视频每帧递增，图片固定，否则一张静态图每次导出的噪点都不一样。</summary>
        public float GrainSeed { get; set; }

        public bool IsValid => _material != null;

        public VideoGradeRenderer(Material material) => _material = material;

        void SetKeyword(string keyword, bool on)
        {
            if (on) _material.EnableKeyword(keyword);
            else _material.DisableKeyword(keyword);
        }

        // Pass 序号，和 shader 里的顺序一一对应
        const int PassBloomPrefilter = 0;
        const int PassDownsample = 1;
        const int PassUpsample = 2;
        const int PassDetail = 3;
        const int PassComposite = 4;
        const int PassGeometry = 5;
        const int PassMaskBuild = 6;
        const int PassMaskApply = 7;
        const int PassFinish = 8;

        /// <summary>把 src 过一遍完整的调色管线，结果写进 dst。</summary>
        public void Render(Texture src, RenderTexture dst, VideoGradeSettings settings, Options options)
        {
            if (_material == null || src == null || dst == null || settings == null) return;

            if (options.bypass)
            {
                Graphics.Blit(src, dst);
                return;
            }

            // ---- 几何：裁剪 / 拉直 / 旋转。必须在最前面，
            // 因为它改变画面尺寸和构图中心，后面的宽高比、暗角、Power Window 都要按新画面算 ----
            RenderTexture geo = null;
            if (settings.HasGeometry)
            {
                settings.OutputSize(src.width, src.height, out int gw, out int gh);
                geo = RenderTexture.GetTemporary(gw, gh, 0,
                                                 RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                ApplyGeometryUniforms(src, settings);
                Graphics.Blit(src, geo, _material, PassGeometry);
                src = geo;
            }

            ApplyUniforms(src, settings, options);

            // ---- 细节滤波层。用不到时整趟跳过，不多占一次全分辨率带宽 ----
            RenderTexture detail = null;
            Texture stageSrc = src;
            bool needDetail = settings.sharpen > 0.001f
                           || settings.denoise > 0.001f
                           || Mathf.Abs(settings.clarity) > 0.001f
                           || Mathf.Abs(settings.texture) > 0.001f
                           || Mathf.Abs(settings.distortK1) > 0.0005f
                           || Mathf.Abs(settings.distortK2) > 0.0005f
                           || Mathf.Abs(settings.distortScale - 1f) > 0.0005f;
            if (needDetail)
            {
                detail = RenderTexture.GetTemporary(src.width, src.height, 0,
                                                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                Graphics.Blit(src, detail, _material, PassDetail);
                stageSrc = detail;
            }

            // ---- 辉光与模糊。两条链都按需构建，强度为 0 时完全不跑 ----
            RenderTexture bloom = null;
            RenderTexture blur = null;

            if (settings.bloomIntensity > 0.001f)
                bloom = BuildBlurChain(stageSrc, true, Mathf.Clamp(1 + Mathf.RoundToInt(settings.bloomScatter * 5f), 1, 6));
            // 背景虚化和整体模糊共用同一条链，谁的强度大就按谁的半径建
            bool hasMask = options.externalMask != null;
            float blurNeed = Mathf.Max(settings.blur, hasMask ? settings.backgroundBlur : 0f);
            // 去朦胧要拿一张大范围模糊当局部大气光的估计。
            // 即使模糊强度是 0 也得把链建起来——但 _BlurAmount 仍然是 0，
            // 所以合成时那次 lerp 是空操作，模糊不会真的糊到画面上
            float chainNeed = Mathf.Max(blurNeed, Mathf.Abs(settings.dehaze) > 0.001f ? 0.6f : 0f);
            if (chainNeed > 0.001f)
                blur = BuildBlurChain(stageSrc, false, Mathf.Clamp(1 + Mathf.RoundToInt(chainNeed * 4f), 1, 5));

            _material.SetTexture(IdBloomTex, bloom != null ? (Texture)bloom : Texture2D.blackTexture);
            _material.SetTexture(IdBlurTex,  blur  != null ? (Texture)blur  : stageSrc);

            // 合成 -> 蒙版组 -> 收尾。风格化被挪进收尾 Pass，
            // 就是为了让蒙版组的调整排在暗角、颗粒之前
            var ping = GetTemp(src.width, src.height);
            Graphics.Blit(stageSrc, ping, _material, PassComposite);

            if (settings.ActiveMaskGroups > 0)
                ping = RunMaskGroups(ping, settings, options);

            // 分屏对比要的是几何变换之后、调色之前的画面。
            // 用 stageSrc 的话降噪、通透度这些会同时出现在"原图"那一侧，比不出东西来
            _material.SetTexture(IdRawTex, src);
            Graphics.Blit(ping, dst, _material, PassFinish);
            RenderTexture.ReleaseTemporary(ping);

            if (bloom  != null) RenderTexture.ReleaseTemporary(bloom);
            if (blur   != null) RenderTexture.ReleaseTemporary(blur);
            if (detail != null) RenderTexture.ReleaseTemporary(detail);
            if (geo    != null) RenderTexture.ReleaseTemporary(geo);
        }

        /// <summary>
        /// 跑一遍所有启用的蒙版组，返回处理后的画面。
        ///
        /// 每组两步：先把它的部件按「加 / 减 / 交」乒乓累积成一张蒙版，
        /// 再拿这张蒙版把这一组的调整混回画面。所以 N 个组 = N 趟蒙版构建 + N 趟应用。
        /// 对图片无所谓，视频上组数多了要留意。
        /// </summary>
        RenderTexture RunMaskGroups(RenderTexture image, VideoGradeSettings s, Options o)
        {
            var pong = GetTemp(image.width, image.height);
            // 蒙版是数据不是颜色，必须用 Linear：走 sRGB 的话写进去 0.5 读出来就不是 0.5 了
            var maskA = GetTempLinear(image.width, image.height);
            var maskB = GetTempLinear(image.width, image.height);

            float aspect = image.height > 0 ? (float)image.width / image.height : 1.7778f;
            _material.SetFloat(IdAspect, aspect);
            _material.SetTexture(IdDepthTex, o.depthMap != null ? o.depthMap : Texture2D.blackTexture);

            foreach (var g in s.maskGroups)
            {
                if (!s.GroupRenders(g)) continue;

                Texture prev = Texture2D.blackTexture;
                var cur = maskA;
                bool first = true;

                for (int i = 0; i < g.parts.Count; i++)
                {
                    var part = g.parts[i];
                    if (part == null || part.muted) continue;

                    // 第一个部件恒按「加」处理：从全黑起步，减和交都无从谈起。
                    // 注意是第一个**没被静音**的，不是下标 0 ——
                    // 把第一个静音掉之后，第二个就成了起点，那时它还按「减」算的话
                    // 结果恒为全黑，界面上看就是"这组突然没了"
                    ApplyPartUniforms(part, first, o);
                    _material.SetTexture(IdPrevMask, prev);
                    Graphics.Blit(image, cur, _material, PassMaskBuild);

                    first = false;
                    prev = cur;
                    cur = ReferenceEquals(cur, maskA) ? maskB : maskA;
                }

                ApplyGroupUniforms(g);
                _material.SetTexture(IdGroupMask, prev);
                Graphics.Blit(image, pong, _material, PassMaskApply);

                var swap = image; image = pong; pong = swap;
            }

            RenderTexture.ReleaseTemporary(pong);
            RenderTexture.ReleaseTemporary(maskA);
            RenderTexture.ReleaseTemporary(maskB);
            return image;
        }

        void ApplyPartUniforms(MaskPart p, bool forceAdd, Options o)
        {
            _material.SetFloat(IdPartShape, p.shape);
            _material.SetFloat(IdPartOp, forceAdd ? 0f : p.op);
            _material.SetFloat(IdPartInvert, p.invert ? 1f : 0f);
            _material.SetFloat(IdPartOpacity, Mathf.Clamp01(p.opacity));

            _material.SetVector(IdPartCenter, p.center);
            _material.SetVector(IdPartSize, new Vector4(Mathf.Max(p.size.x, 0.001f),
                                                        Mathf.Max(p.size.y, 0.001f), 0f, 0f));
            float rad = p.rotation * Mathf.Deg2Rad;
            _material.SetVector(IdPartRot, new Vector4(Mathf.Cos(rad), Mathf.Sin(rad), 0f, 0f));
            _material.SetFloat(IdPartFeather, Mathf.Clamp(p.feather, 0.001f, 1f));

            _material.SetVector(IdPartHue, new Vector4(p.hueCenter, p.hueRange, p.hueSoft, 0f));
            _material.SetVector(IdPartSat, new Vector4(p.satMin, p.satMax, p.satSoft, 0f));
            _material.SetVector(IdPartLum, new Vector4(p.lumMin, p.lumMax, p.lumSoft, 0f));
            _material.SetVector(IdPartDepth, new Vector4(p.depthMin, p.depthMax, p.depthSoft, 0f));

            _material.SetTexture(IdPartTex, ResolvePartTexture(p, o));
        }

        /// <summary>贴图类部件的来源。取不到时给纯黑——宁可这个部件不选中任何东西，也别乱选一片。</summary>
        static Texture ResolvePartTexture(MaskPart p, Options o)
        {
            if (p.Shape == MaskShape.Subject)
                return o.externalMask != null ? o.externalMask : Texture2D.blackTexture;

            if (p.Shape == MaskShape.Sky)
                return o.skyMask != null ? o.skyMask : Texture2D.blackTexture;

            if (p.Shape == MaskShape.Brush && o.brushes != null &&
                p.brushId >= 0 && p.brushId < o.brushes.Count && o.brushes[p.brushId] != null)
                return o.brushes[p.brushId];

            return Texture2D.blackTexture;
        }

        void ApplyGroupUniforms(MaskGroup g)
        {
            _material.SetFloat(IdGExposure, g.exposure);
            _material.SetFloat(IdGContrast, g.contrast);
            _material.SetFloat(IdGHighlights, g.highlights);
            _material.SetFloat(IdGShadows, g.shadows);
            _material.SetFloat(IdGSaturation, g.saturation);
            _material.SetFloat(IdGHueShift, g.hueShift);
            _material.SetVector(IdGTint, g.TintRGB());
            _material.SetFloat(IdGOverlay, g.showOverlay ? 1f : 0f);
        }

        static RenderTexture GetTempLinear(int w, int h) =>
            RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);

        /// <summary>
        /// 逐级降采样再逐级帐篷升采样，得到一张柔和的模糊图。
        /// threshold=true 时第一级会先做亮度阈值提取，那就是 Bloom。
        /// </summary>
        RenderTexture BuildBlurChain(Texture src, bool threshold, int iterations)
        {
            int w = Mathf.Max(1, src.width  / 2);
            int h = Mathf.Max(1, src.height / 2);

            var levels = new RenderTexture[iterations];

            var cur = GetTemp(w, h);
            Graphics.Blit(src, cur, _material, threshold ? PassBloomPrefilter : PassDownsample);
            levels[0] = cur;

            for (int i = 1; i < iterations; i++)
            {
                w = Mathf.Max(1, w / 2);
                h = Mathf.Max(1, h / 2);
                var next = GetTemp(w, h);
                Graphics.Blit(cur, next, _material, PassDownsample);
                levels[i] = next;
                cur = next;
            }

            // 从最小一级往回放大，放完一级就把它释放掉
            for (int i = iterations - 2; i >= 0; i--)
            {
                Graphics.Blit(cur, levels[i], _material, PassUpsample);
                RenderTexture.ReleaseTemporary(cur);
                cur = levels[i];
            }

            return cur;   // 半分辨率的模糊结果
        }

        static RenderTexture GetTemp(int w, int h) =>
            RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);

        void ApplyUniforms(Texture src, VideoGradeSettings s, Options o)
        {
            _material.SetFloat(IdThreshold, s.bloomThreshold);
            _material.SetFloat(IdSoftKnee, 0.5f);
            _material.SetFloat(IdBloomInt, s.bloomIntensity);
            _material.SetFloat(IdBlurAmount, s.blur);
            _material.SetFloat(IdSharpen, s.sharpen);
            _material.SetFloat(IdDenoise, s.denoise);
            _material.SetFloat(IdClarity, s.clarity);
            _material.SetFloat(IdTexture, s.texture);
            _material.SetFloat(IdClarityRad, Mathf.Max(1f, s.clarityRadius));
            _material.SetFloat(IdSharpenFocus, s.sharpenFocusOnly);
            _material.SetFloat(IdDistK1, s.distortK1);
            _material.SetFloat(IdDistK2, s.distortK2);
            _material.SetFloat(IdDistScale, Mathf.Max(0.1f, s.distortScale));

            _material.SetFloat(IdLogMode, s.logMode);

            var m = s.colorMatrix != null && s.colorMatrix.Length >= 12
                ? s.colorMatrix : VideoGradeSettings.IdentityMatrix();
            _material.SetVector(IdMatR, new Vector4(m[0], m[1], m[2], m[3]));
            _material.SetVector(IdMatG, new Vector4(m[4], m[5], m[6], m[7]));
            _material.SetVector(IdMatB, new Vector4(m[8], m[9], m[10], m[11]));
            _material.SetFloat(IdMatOn, s.colorMatrixEnabled ? 1f : 0f);

            _material.SetFloat(IdExposure, s.exposure);
            _material.SetVector(IdBalance, s.ComputeColorBalance());
            _material.SetFloat(IdTonemap, s.tonemap);

            // 输入白点必须大于输入黑点，否则 shader 里会除零
            float inBlack = Mathf.Clamp01(s.inBlack);
            float inWhite = Mathf.Max(inBlack + 0.001f, s.inWhite);
            _material.SetVector(IdLevels, new Vector4(inBlack, inWhite, s.outBlack, s.outWhite));
            _material.SetFloat(IdLevelsGamma, Mathf.Max(0.01f, s.levelsGamma));

            _material.SetVector(IdLift, s.LiftRGB);
            _material.SetVector(IdGamma, s.GammaRGB);
            _material.SetVector(IdGain, s.GainRGB);
            _material.SetVector(IdOffset, s.OffsetRGB);

            _material.SetFloat(IdContrast, s.contrast);
            _material.SetFloat(IdHighlights, s.highlights);
            _material.SetFloat(IdShadows, s.shadows);

            _material.SetVector(IdShadowTint, s.ShadowTint);
            _material.SetVector(IdHighTint, s.HighlightTint);
            _material.SetFloat(IdSplitBal, s.splitBalance);

            _material.SetFloat(IdSaturation, s.saturation);
            _material.SetFloat(IdSkinProtect, s.skinProtect);
            _material.SetFloat(IdHueShift, s.hueShift);

            _material.SetFloat(IdVigInt, s.vignetteIntensity);
            _material.SetFloat(IdVigSmooth, s.vignetteSmoothness);
            _material.SetFloat(IdGrain, s.grain);
            _material.SetFloat(IdChromatic, s.chromatic);
            _material.SetFloat(IdDither, s.dither);
            _material.SetFloat(IdAspect, src.height > 0 ? (float)src.width / src.height : 1.7778f);
            _material.SetFloat(IdGrainSeed, GrainSeed);

            // 旁路时分屏没有意义，两边都是原图
            _material.SetFloat(IdSplitOn, (o.splitCompare && !o.bypass) ? 1f : 0f);
            _material.SetFloat(IdSplitPos, Mathf.Clamp01(o.splitPosition));

            EnsureCurveLut(s);
            _material.SetTexture(IdCurveLut, _curveLut != null ? (Texture)_curveLut : Texture2D.whiteTexture);

            // 用关键字而不是 uniform 开关：关掉的功能在编译期就消失，
            // 省下的是指令数和寄存器占用，不只是每像素少判一次分支
            EnsureSixCurveLut(s);
            _material.SetTexture(IdSixCurveTex, _sixCurveLut != null ? (Texture)_sixCurveLut : Texture2D.grayTexture);

            bool lutOn = o.lut != null && o.lutAmount > 0.001f;
            if (lutOn)
            {
                _material.SetTexture(IdLutTex, o.lut);
                _material.SetFloat(IdLutSize, o.lut.width);
                _material.SetFloat(IdLutAmount, Mathf.Clamp01(o.lutAmount));
            }
            _material.SetVector(IdZebra, new Vector4(s.zebraHigh, s.zebraLow, 0f, 0f));

            _material.SetFloat(IdDehaze, s.dehaze);

            PackBands(s.hslHue, out var hslH0, out var hslH1);
            PackBands(s.hslSat, out var hslS0, out var hslS1);
            PackBands(s.hslLum, out var hslL0, out var hslL1);
            _material.SetVector(IdHslHueA, hslH0); _material.SetVector(IdHslHueB, hslH1);
            _material.SetVector(IdHslSatA, hslS0); _material.SetVector(IdHslSatB, hslS1);
            _material.SetVector(IdHslLumA, hslL0); _material.SetVector(IdHslLumB, hslL1);

            SetKeyword("LOVE_LUT_ON", lutOn);
            // 面板开着但八个色带全是 0 时输出和关掉完全一样，那就别让它进编译
            SetKeyword("LOVE_HSL_ON", s.hslEnabled && (AnyNonZero(s.hslHue) || AnyNonZero(s.hslSat) || AnyNonZero(s.hslLum)));
            SetKeyword("LOVE_SIXCURVE_ON", s.sixCurveEnabled && _sixCurveLut != null);
            SetKeyword("LOVE_CURVE_ON", s.curveEnabled && _curveLut != null);
            SetKeyword("LOVE_SECONDARY_ON", s.secondaryEnabled);

            _material.SetFloat(IdShowMask, (s.secondaryEnabled && s.showMask) ? 1f : 0f);

            _material.SetFloat(IdWinShape, s.windowShape);
            _material.SetVector(IdWinCenter, s.windowCenter);
            _material.SetVector(IdWinSize, s.windowSize);
            _material.SetVector(IdWinRot, s.WindowRotationVector);
            _material.SetFloat(IdWinFeather, s.windowFeather);
            _material.SetFloat(IdWinInvert, s.windowInvert ? 1f : 0f);

            _material.SetFloat(IdQualOn, s.qualifierEnabled ? 1f : 0f);
            _material.SetVector(IdQualHue, new Vector4(s.qualHueCenter, s.qualHueRange, s.qualHueSoft, 0f));
            _material.SetVector(IdQualSat, new Vector4(s.qualSatMin, s.qualSatMax, s.qualSatSoft, 0f));
            _material.SetVector(IdQualLum, new Vector4(s.qualLumMin, s.qualLumMax, s.qualLumSoft, 0f));

            // 没有蒙版时指向纯白：shader 里 remap 后恒为 1，背景虚化和二级叠加自动失效
            _material.SetTexture(IdMaskTex, o.externalMask != null ? o.externalMask : Texture2D.whiteTexture);
            _material.SetFloat(IdMaskInvert, s.maskInvert ? 1f : 0f);
            float lo = Mathf.Clamp01(s.maskLow);
            float hi = Mathf.Max(lo + 0.001f, Mathf.Clamp01(s.maskHigh));
            _material.SetVector(IdMaskRemap, new Vector4(lo, hi, 0f, 0f));
            _material.SetFloat(IdBgBlur, o.externalMask != null ? s.backgroundBlur : 0f);
            _material.SetFloat(IdSecUseMask, s.secondaryUseMask ? 1f : 0f);

            _material.SetFloat(IdSecExposure, s.secExposure);
            _material.SetFloat(IdSecContrast, s.secContrast);
            _material.SetFloat(IdSecSat, s.secSaturation);
            _material.SetFloat(IdSecHue, s.secHueShift);
            _material.SetVector(IdSecTint, s.SecondaryTint);
        }

        /// <summary>八个色带打成两个 Vector4——shader 端没有 float[] 这种东西。</summary>
        static void PackBands(float[] v, out Vector4 a, out Vector4 b)
        {
            a = new Vector4(Band(v, 0), Band(v, 1), Band(v, 2), Band(v, 3));
            b = new Vector4(Band(v, 4), Band(v, 5), Band(v, 6), Band(v, 7));
        }

        // 数组可能是 null 或长度不对（老预设 JSON 里根本没有这几个字段）
        static float Band(float[] v, int i) => v != null && i < v.Length ? v[i] : 0f;

        static bool AnyNonZero(float[] v)
        {
            if (v == null) return false;
            for (int i = 0; i < v.Length; i++)
                if (Mathf.Abs(v[i]) > 0.001f) return true;
            return false;
        }

        void ApplyGeometryUniforms(Texture src, VideoGradeSettings s)
        {
            float cw = s.cropEnabled ? Mathf.Clamp(s.cropW, 0.01f, 1f) : 1f;
            float ch = s.cropEnabled ? Mathf.Clamp(s.cropH, 0.01f, 1f) : 1f;
            // 夹一下防止裁剪框被拖出画面
            float cx = s.cropEnabled ? Mathf.Clamp(s.cropX, 0f, 1f - cw) : 0f;
            float cy = s.cropEnabled ? Mathf.Clamp(s.cropY, 0f, 1f - ch) : 0f;
            _material.SetVector(IdCropRect, new Vector4(cx, cy, cw, ch));

            float rad = s.straighten * Mathf.Deg2Rad;
            _material.SetVector(IdStraighten, new Vector4(Mathf.Cos(rad), Mathf.Sin(rad), 0f, 0f));

            int rot = ((s.rotate90 % 4) + 4) % 4;
            // 拉直是在"转过 90 度之后"的画面里做的，所以宽高比要用转过之后的
            float rw = (rot & 1) == 0 ? src.width : src.height;
            float rh = (rot & 1) == 0 ? src.height : src.width;
            _material.SetVector(IdGeoFlags, new Vector4(s.flipH ? 1f : 0f, s.flipV ? 1f : 0f,
                                                        rot, rh > 0.5f ? rw / rh : 1f));
        }

        /// <summary>
        /// 把四条曲线烘成一张 256x1 的查找贴图。
        /// 主曲线在这里就和单通道曲线复合掉（先过通道曲线再过主曲线），
        /// 于是 shader 里每个通道只要采一次。只有曲线形状真的变了才会重烘。
        /// </summary>
        void EnsureCurveLut(VideoGradeSettings s)
        {
            if (!s.curveEnabled) return;

            int sig = s.CurveSignature();
            if (_curveLut != null && sig == _curveSignature) return;
            _curveSignature = sig;

            if (_curveLut == null)
            {
                _curveLut = new Texture2D(CurveLutSize, 1, TextureFormat.RGBA32, false, true)
                {
                    name = "GradeCurveLut",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            var px = new Color32[CurveLutSize];
            for (int i = 0; i < CurveLutSize; i++)
            {
                float x = i / (float)(CurveLutSize - 1);
                px[i] = new Color32(Sample(s.curveR, x), Sample(s.curveG, x), Sample(s.curveB, x), 255);
            }
            _curveLut.SetPixels32(px);
            _curveLut.Apply(false, false);

            byte Sample(AnimationCurve channel, float x)
            {
                float v = channel != null && channel.length > 0 ? channel.Evaluate(x) : x;
                if (s.curveMaster != null && s.curveMaster.length > 0)
                    v = s.curveMaster.Evaluate(Mathf.Clamp01(v));
                return (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
            }
        }

        const int SixCurveSize = 256;
        Texture2D _sixCurveLut;
        int _sixCurveSignature = int.MinValue;

        /// <summary>
        /// 把六条曲线烘成一张 256x2 的 RGBA 贴图。
        /// 第 0 行 = 色相vs色相 / 色相vs饱和 / 色相vs亮度 / 亮度vs饱和
        /// 第 1 行 = 饱和vs饱和 / 饱和vs亮度
        /// </summary>
        void EnsureSixCurveLut(VideoGradeSettings s)
        {
            if (!s.sixCurveEnabled) return;

            int sig = s.SixCurveSignature();
            if (_sixCurveLut != null && sig == _sixCurveSignature) return;
            _sixCurveSignature = sig;

            if (_sixCurveLut == null)
            {
                _sixCurveLut = new Texture2D(SixCurveSize, 2, TextureFormat.RGBA32, false, true)
                {
                    name = "SixCurveLut",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }

            var px = new Color32[SixCurveSize * 2];
            for (int i = 0; i < SixCurveSize; i++)
            {
                float x = i / (float)(SixCurveSize - 1);
                px[i] = new Color32(Eval(s.hueVsHue, x), Eval(s.hueVsSat, x),
                                    Eval(s.hueVsLum, x), Eval(s.lumVsSat, x));
                px[SixCurveSize + i] = new Color32(Eval(s.satVsSat, x), Eval(s.satVsLum, x), 128, 255);
            }
            _sixCurveLut.SetPixels32(px);
            _sixCurveLut.Apply(false, false);

            byte Eval(AnimationCurve c, float x)
            {
                float v = c != null && c.length > 0 ? c.Evaluate(x) : 0.5f;
                return (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
            }
        }

        public void Dispose()
        {
            SafeDestroy(_curveLut);
            _curveLut = null;
            SafeDestroy(_sixCurveLut);
            _sixCurveLut = null;
            _sixCurveSignature = int.MinValue;
            _curveSignature = int.MinValue;
        }

        /// <summary>编辑模式下 Destroy 不会立刻生效，必须用 DestroyImmediate。</summary>
        internal static void SafeDestroy(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
