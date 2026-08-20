using Love.Video;
using UnityEngine;
using UnityEngine.UI;

namespace Love.UI
{
    /// <summary>
    /// 直方图显示。把 VideoPostProcessor 统计出来的四条分布画进一张贴图。
    /// 用贴图而不是几十个 Image 画柱子，是因为后者每帧重建网格太浪费。
    /// </summary>
    [DisallowMultipleComponent]
    public class HistogramView : MonoBehaviour
    {
        public RawImage target;
        public VideoPostProcessor postProcessor;

        [Tooltip("显示哪条：0 红 1 绿 2 蓝 3 亮度 4 RGB 叠加")]
        public int channel = 4;

        const int TexW = 256, TexH = 128;

        static readonly Color32 BgColor   = new Color32(12, 14, 18, 235);
        static readonly Color32 GridColor = new Color32(70, 76, 88, 255);

        Texture2D _tex;
        Color32[] _pixels;

        void Awake()
        {
            _tex = new Texture2D(TexW, TexH, TextureFormat.RGBA32, false, true)
            {
                name = "HistogramTex",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _pixels = new Color32[TexW * TexH];
            if (target != null) target.texture = _tex;
            Repaint();
        }

        /// <summary>
        /// 接线入口。必须用它而不是直接赋字段——AddComponent 会立刻跑 Awake/OnEnable，
        /// 那时候 postProcessor 还是空的，订阅不上更新事件，直方图会一直是空的。
        /// </summary>
        public void Initialize(VideoPostProcessor processor, RawImage image)
        {
            if (postProcessor != null) postProcessor.OnHistogramUpdated -= Repaint;

            postProcessor = processor;
            target = image;

            if (postProcessor != null) postProcessor.OnHistogramUpdated += Repaint;
            Repaint();
        }

        void OnEnable()
        {
            if (postProcessor != null)
            {
                postProcessor.OnHistogramUpdated -= Repaint;   // 先退再订，避免重复订阅
                postProcessor.OnHistogramUpdated += Repaint;
            }
            Repaint();
        }

        void OnDisable()
        {
            if (postProcessor != null) postProcessor.OnHistogramUpdated -= Repaint;
        }

        void OnDestroy()
        {
            if (_tex != null) Destroy(_tex);
        }

        public void Repaint()
        {
            if (_tex == null || _pixels == null) return;

            for (int i = 0; i < _pixels.Length; i++) _pixels[i] = BgColor;

            // 四等分竖线，方便看黑位/中间调/白位落在哪
            for (int q = 1; q <= 3; q++)
            {
                int x = TexW * q / 4;
                for (int y = 0; y < TexH; y++) _pixels[y * TexW + x] = GridColor;
            }

            if (postProcessor != null)
            {
                if (channel == 4)
                {
                    // RGB 叠加：用加色混合，重叠处自然变白
                    DrawChannel(postProcessor.HistogramR, new Color32(255, 60, 60, 255), true);
                    DrawChannel(postProcessor.HistogramG, new Color32(60, 255, 90, 255), true);
                    DrawChannel(postProcessor.HistogramB, new Color32(70, 120, 255, 255), true);
                }
                else
                {
                    float[] data;
                    Color32 c;
                    switch (channel)
                    {
                        case 0: data = postProcessor.HistogramR; c = new Color32(255, 70, 70, 255); break;
                        case 1: data = postProcessor.HistogramG; c = new Color32(70, 255, 100, 255); break;
                        case 2: data = postProcessor.HistogramB; c = new Color32(80, 130, 255, 255); break;
                        default: data = postProcessor.HistogramLuma; c = new Color32(225, 228, 235, 255); break;
                    }
                    DrawChannel(data, c, false);
                }
            }

            _tex.SetPixels32(_pixels);
            _tex.Apply(false, false);
            if (target != null && !ReferenceEquals(target.texture, _tex)) target.texture = _tex;
        }

        void DrawChannel(float[] bins, Color32 color, bool additive)
        {
            if (bins == null || bins.Length == 0) return;

            for (int x = 0; x < TexW; x++)
            {
                // 贴图比分档数宽，插值一下柱子才不会是锯齿状的台阶
                float t = (float)x / (TexW - 1) * (bins.Length - 1);
                int i0 = Mathf.Clamp(Mathf.FloorToInt(t), 0, bins.Length - 1);
                int i1 = Mathf.Min(i0 + 1, bins.Length - 1);
                float v = Mathf.Lerp(bins[i0], bins[i1], t - i0);

                int h = Mathf.Clamp(Mathf.RoundToInt(v * (TexH - 2)), 0, TexH - 1);
                for (int y = 0; y < h; y++)
                {
                    int idx = y * TexW + x;
                    if (additive)
                    {
                        var p = _pixels[idx];
                        _pixels[idx] = new Color32(
                            (byte)Mathf.Min(255, p.r + color.r),
                            (byte)Mathf.Min(255, p.g + color.g),
                            (byte)Mathf.Min(255, p.b + color.b),
                            255);
                    }
                    else
                    {
                        _pixels[idx] = color;
                    }
                }
            }
        }
    }
}
