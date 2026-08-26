using Love.Tools;
using Love.Video;
using UnityEngine;

namespace Love.App
{
    /// <summary>
    /// 修图台的 AI 那三块：主体蒙版、降噪、天空。
    ///
    /// 天空不需要 Sentis——它是从画面顶边往下漫延的纯 CPU 算法，
    /// 所以只有前两个包在条件编译里。
    /// </summary>
    public partial class PhotoStation
    {
        bool _pAi, _pSky;

        // ---- 天空 ----
        SkyDetect.Options _skyOpt = SkyDetect.Options.Default;
        SkyDetect.Result _skyResult;
        string _skyDetected;
        Texture2D _skyTex;
        string _skyTexPath;
        int _skyTexGeo = int.MinValue;

        void ReleaseSky()
        {
            if (_skyTex != null) { Object.Destroy(_skyTex); _skyTex = null; }
            _skyTexPath = null;
            _skyTexGeo = int.MinValue;
            _skyResult = default;
            _skyDetected = null;
        }

        /// <summary>有没有哪个还开着的蒙版组用到了天空。没人用就一点活都别干。</summary>
        bool NeedsSky()
        {
            var gs = _settings.maskGroups;
            if (gs == null) return false;

            for (int i = 0; i < gs.Count; i++)
            {
                var g = gs[i];
                if (g == null || !g.enabled || g.parts == null) continue;
                for (int j = 0; j < g.parts.Count; j++)
                    if (g.parts[j] != null && g.parts[j].Shape == MaskShape.Sky) return true;
            }
            return false;
        }

        /// <summary>
        /// 取当前这张的天空蒙版，必要时算一遍。只在 Tick 里调。
        ///
        /// 漫延只跟源图有关，几何只影响重采样，所以两步分开缓存——
        /// 合在一起的话，拖裁剪框的每一帧都要重新漫延一遍。
        /// </summary>
        Texture2D EnsureSky()
        {
            if (_full == null || _loadedPath == null || !NeedsSky())
            {
                _guiMasks.HasSky = false;
                return null;
            }

            if (_skyDetected != _loadedPath)
            {
                // 检测用缩略图就够：蒙版边缘的精度到不了更高
                var thumb = _lib.Current != null ? _lib.Current.thumb : null;
                _skyResult = SkyMaskBuilder.Detect(thumb != null ? thumb : _full, _skyOpt);
                _skyDetected = _loadedPath;
                if (_skyTex != null) { Object.Destroy(_skyTex); _skyTex = null; }
                _skyTexGeo = int.MinValue;
            }

            _guiMasks.HasSky = _skyResult.found;
            _guiMasks.SkyCoverage = _skyResult.coverage;
            if (!_skyResult.found) return null;

            int geo = SkyMaskBuilder.GeometryKey(_settings);
            if (_skyTex != null && _skyTexPath == _loadedPath && _skyTexGeo == geo) return _skyTex;

            if (_skyTex != null) { Object.Destroy(_skyTex); _skyTex = null; }
            _skyTex = SkyMaskBuilder.ToTexture(_skyResult, _settings);
            _skyTexPath = _loadedPath;
            _skyTexGeo = geo;
            return _skyTex;
        }

        void DrawSky(RuntimeGui ui)
        {
            ui.Slider("相邻色差", ref _skyOpt.localTol, 0.02f, 0.3f, "0.000");
            ui.Slider("纹理上限", ref _skyOpt.texture, 0.02f, 0.3f, "0.000");
            ui.Slider("最低亮度", ref _skyOpt.minValue, 0.05f, 0.6f, "0.00");
            ui.Slider("最多吃到", ref _skyOpt.maxDepth, 0.2f, 1f, "0.00");

            if (ui.Changed) { _skyDetected = null; _dirty = true; }

            if (ui.Btn("重新检测")) { _skyDetected = null; _dirty = true; }

            if (_skyResult.found)
                ui.Info($"天空占画面 {_skyResult.coverage * 100f:F0}%。" +
                        "去蒙版那一节加一个「天空」部件才会起作用。");
            else if (_skyDetected != null)
                ui.Info("这张没找到天空。检测是从顶边往下漫延的：\n" +
                        "天空不在顶边（比如从窗户往外拍）、或者顶边全是树枝屋檐时找不到。");
            else
                ui.Info("在蒙版里加一个「天空」部件，这里就会自动算。");
        }

#if LOVE_SENTIS
        // ---- AI 主体蒙版 ----
        AiMaskGenerator _maskGen;
        int _maskModelIndex;
        Texture2D _maskRaw;
        string _maskStatus = "";

        // ---- AI 降噪 ----
        AiDenoiser _denoiser;
        int _dnModelIndex;
        float _dnStrength = 1f;
        RenderTexture _dnRaw, _dnBlended;
        string _dnPath;
        string _dnStatus = "";
        NoiseEstimate.Result _dnNoise;
        string _dnNoisePath;

        Texture CurrentMask => _maskRaw;

        /// <summary>降噪之后的图才是下游的源。降噪排在修补和调色前面。</summary>
        Texture DenoisedOrFull =>
            _dnBlended != null && _dnPath == _loadedPath ? (Texture)_dnBlended : _full;

        void ReleaseAi()
        {
            if (_maskRaw != null) { Object.Destroy(_maskRaw); _maskRaw = null; }
            ReleaseDenoise();
            _maskGen?.Dispose();
            _maskGen = null;
            _denoiser?.Dispose();
            _denoiser = null;
        }

        void ReleaseDenoise()
        {
            _denoiser?.Cancel();
            if (_dnRaw != null) { _dnRaw.Release(); Object.Destroy(_dnRaw); _dnRaw = null; }
            if (_dnBlended != null) { _dnBlended.Release(); Object.Destroy(_dnBlended); _dnBlended = null; }
            _dnPath = null;
            _dirty = true;
        }

        /// <summary>降噪是分步跑的，一帧一块。由 Tick 调。</summary>
        void StepDenoise()
        {
            if (_denoiser == null || !_denoiser.Running) return;

            _denoiser.Step();
            _dnStatus = $"降噪中 {_denoiser.TileDone}/{_denoiser.TileCount} 块" +
                        $"（每块 {_denoiser.LastMs:0} ms）";
            if (!_denoiser.Running) FinishDenoise();
        }

        void FinishDenoise()
        {
            if (_denoiser == null) return;

            if (_dnRaw != null) { _dnRaw.Release(); Object.Destroy(_dnRaw); }
            _dnRaw = _denoiser.Result;
            _denoiser.ReleaseResult();   // 所有权交出来，下一轮 Begin 不要再动它

            _dnStatus = _dnRaw != null ? $"降噪完成，{_denoiser.TileCount} 块" : "没有结果";
            BlendDenoise();
        }

        /// <summary>
        /// 按强度把降噪结果和原图混起来。
        ///
        /// 用 DrawTexture 的 alpha 混合直接做 lerp，省一个 shader——
        /// 和贴水印是同一个路子。
        /// </summary>
        void BlendDenoise()
        {
            if (_dnRaw == null || _full == null) return;

            if (_dnBlended == null || _dnBlended.width != _dnRaw.width ||
                _dnBlended.height != _dnRaw.height)
            {
                if (_dnBlended != null) { _dnBlended.Release(); Object.Destroy(_dnBlended); }
                _dnBlended = new RenderTexture(_dnRaw.width, _dnRaw.height, 0,
                                               RenderTextureFormat.ARGB32,
                                               RenderTextureReadWrite.sRGB);
                _dnBlended.Create();
            }

            Graphics.Blit(_full, _dnBlended);

            float a = Mathf.Clamp01(_dnStrength);
            if (a > 0.001f)
            {
                var prev = RenderTexture.active;
                RenderTexture.active = _dnBlended;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, _dnBlended.width, _dnBlended.height, 0f);
                Graphics.DrawTexture(new Rect(0f, 0f, _dnBlended.width, _dnBlended.height), _dnRaw,
                                     new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0,
                                     new Color(1f, 1f, 1f, a));
                GL.PopMatrix();
                RenderTexture.active = prev;
            }

            _repairDirty = true;   // 修补是拿降噪后的图重放的
            _dirty = true;
        }

        void DrawAi(RuntimeGui ui)
        {
            // ---- 主体蒙版 ----
            ui.Info("AI 主体蒙版");
            _maskModelIndex = ui.Popup2("模型", _maskModelIndex,
                System.Array.ConvertAll(AiMaskGenerator.Presets, m => m.label));

            GUILayout.BeginHorizontal();
            if (ui.Btn(_maskRaw == null ? "生成蒙版" : "重新生成")) _pendingAction = GenerateMask;
            using (new GuiEnabled(_maskRaw != null))
                if (ui.Btn("清除")) { if (_maskRaw != null) { Object.Destroy(_maskRaw); _maskRaw = null; } _dirty = true; }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_maskStatus)) ui.Info(_maskStatus);

            GUILayout.Space(6f);

            // ---- 降噪 ----
            ui.Info("AI 降噪");

            if (_full != null && _dnNoisePath != _loadedPath && _lib.Current?.thumb != null)
            {
                var t = _lib.Current.thumb;
                _dnNoise = NoiseEstimate.Analyze(t.GetPixels32(), t.width, t.height);
                _dnNoisePath = _loadedPath;
            }
            if (_dnNoise.valid)
                ui.Info($"实测噪声：亮 {_dnNoise.luma * 255f:F1}／色 {_dnNoise.chroma * 255f:F1}");

            _dnModelIndex = ui.Popup2("模型", _dnModelIndex,
                System.Array.ConvertAll(AiDenoiser.Presets, m => m.label));

            bool busy = _denoiser != null && _denoiser.Running;

            using (new GuiEnabled(_full != null && !busy))
                if (ui.Btn(_dnRaw == null ? "生成降噪" : "重新生成")) _pendingAction = StartDenoise;

            if (busy)
            {
                var r = GUILayoutUtility.GetRect(0f, 18f, GUILayout.ExpandWidth(true));
                GUI.HorizontalSlider(r, _denoiser.Progress, 0f, 1f);
                if (ui.Btn("取消")) { _denoiser.Cancel(); _dnStatus = "已取消"; }
            }
            else if (_dnRaw != null)
            {
                ui.Slider("强度", ref _dnStrength, 0f, 1f);
                if (ui.Changed) _pendingAction = BlendDenoise;
                if (ui.Btn("清除降噪")) _pendingAction = ReleaseDenoise;
            }

            if (!string.IsNullOrEmpty(_dnStatus)) ui.Info(_dnStatus);
            ui.Info("降噪必须走 GPU 后端。CPU 上一块 576² 要 7 秒，" +
                    "6100 万像素切 247 块就是半小时。");
        }

        void GenerateMask()
        {
            if (_full == null) return;
            if (_maskGen == null) _maskGen = new AiMaskGenerator();

            var spec = AiMaskGenerator.Presets[
                Mathf.Clamp(_maskModelIndex, 0, AiMaskGenerator.Presets.Length - 1)];

            if (_maskRaw != null) { Object.Destroy(_maskRaw); _maskRaw = null; }
            _maskRaw = _maskGen.Generate(_full, spec, Unity.Sentis.BackendType.GPUCompute,
                                         true, out string err);

            _maskStatus = _maskRaw != null
                ? $"已生成 {_maskRaw.width}px，{_maskGen.LastMs:0} ms"
                : "失败：" + err;
            _guiMasks.HasSubjectMask = _maskRaw != null;
            _dirty = true;
        }

        void StartDenoise()
        {
            if (_full == null || _loadedPath == null) return;
            if (_denoiser == null) _denoiser = new AiDenoiser();

            var spec = AiDenoiser.Presets[
                Mathf.Clamp(_dnModelIndex, 0, AiDenoiser.Presets.Length - 1)];

            if (!_denoiser.Begin(_full, spec, Unity.Sentis.BackendType.GPUCompute, out string err))
            {
                _dnStatus = "失败：" + err;
                return;
            }

            _dnPath = _loadedPath;
            _dnStatus = $"切成 {_denoiser.TileCount} 块，开始…";

            // 起手强度按实测噪声给，别让人对着一张本来就干净的图拉满
            if (_dnNoise.valid) _dnStrength = NoiseEstimate.SuggestStrength(_dnNoise.luma);
        }
#else
        Texture CurrentMask => null;
        Texture DenoisedOrFull => _full;
        void ReleaseAi() { }
        void StepDenoise() { }

        void DrawAi(RuntimeGui ui)
        {
            ui.Info("这个包没带 Sentis，AI 主体蒙版和 AI 降噪都用不了。\n" +
                    "天空蒙版不受影响——它是纯 CPU 的。");
        }
#endif
    }
}
