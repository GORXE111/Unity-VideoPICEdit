// 只为把 SonyRawImporter.cs 原样拿出来跑而写的最小 UnityEngine 桩。
// 桩里的每个方法都必须和 Unity 的语义一致，否则验的就不是真实行为了。
using System;

namespace UnityEngine
{
    public static class Mathf
    {
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Pow(float a, float b) => (float)Math.Pow(a, b);
        // Unity 的 Mathf.Round 走的是 Math.Round 的默认行为（银行家舍入），
        // 这里必须一致，否则环上的采样点会差一个像素
        public static int RoundToInt(float f) => (int)Math.Round(f, MidpointRounding.ToEven);
        public static int CeilToInt(float f) => (int)Math.Ceiling(f);
        public const float PI = 3.14159265358979f;
        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        public static float Max(float a, float b, float c) => Max(Max(a, b), c);
        public static float Abs(float f) => Math.Abs(f);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float InverseLerp(float a, float b, float v)
            => Math.Abs(b - a) < 1e-9f ? 0f : Clamp01((v - a) / (b - a));
        public static float Log(float f, float b) => (float)Math.Log(f, b);
        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static int FloorToInt(float f) => (int)Math.Floor(f);
        public static float Repeat(float t, float len) => Clamp(t - (float)Math.Floor(t / len) * len, 0f, len);
        public static bool Approximately(float a, float b) => Math.Abs(b - a) < 1e-6f;
        public const float Deg2Rad = 0.0174532924f;
        public const float Rad2Deg = 57.29578f;
        // Unity 的 sRGB 换算就是这两条近似公式
        public static float GammaToLinearSpace(float v)
            => v <= 0.04045f ? v / 12.92f : (float)Math.Pow((v + 0.055f) / 1.055f, 2.4);
        public static float LinearToGammaSpace(float v)
            => v <= 0.0031308f ? v * 12.92f : 1.055f * (float)Math.Pow(v, 1.0 / 2.4) - 0.055f;
        public static float Cos(float f) => (float)Math.Cos(f);
        public static float Sin(float f) => (float)Math.Sin(f);
    }

    public struct RectInt
    {
        public int x, y, width, height;
        public RectInt(int x, int y, int w, int h) { this.x = x; this.y = y; width = w; height = h; }
        public int xMax => x + width;
        public int yMax => y + height;
    }

    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    [Flags] public enum HideFlags { None = 0, HideAndDontSave = 61 }
    public enum TextureFormat { RGBA32 = 4 }
    public enum TextureWrapMode { Clamp = 1 }

    public class Object
    {
        public string name;
        public HideFlags hideFlags;
        public static void DestroyImmediate(Object o) { }
    }

    public class Texture2D : Texture
    {
        public Color32[] Pixels;
        public Color[] Colors;

        public Texture2D(int w, int h, TextureFormat f, bool mip, bool linear) { width = w; height = h; }
        public void SetPixels32(Color32[] px) { Pixels = px; }
        public void Apply(bool a, bool b) { }
        public bool LoadImage(byte[] data, bool markNonReadable) => false;   // 测试里不走预览分支
        public void ReadPixels(Rect r, int x, int y, bool mip) { }
        public Color[] GetPixels() => Colors;
    }
}

// ---- 为了把 VideoFrameStream / FfmpegTool 原样拿出来跑，补的几个桩 ----
namespace UnityEngine
{
    public enum RuntimePlatform { WindowsEditor = 7 }

    public static class Application
    {
        public static RuntimePlatform platform => RuntimePlatform.WindowsEditor;
        public static bool isBatchMode => true;
        // 桩里只要它存在。落盘那部分靠 Unity 验，这里测的是快照的淘汰规则
        public static string dataPath => System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "offlinetests", "Assets");
    }

    public static class Debug
    {
        public static void Log(object m) => System.Console.WriteLine("[Log] " + m);
        public static void LogWarning(object m) => System.Console.WriteLine("[Warn] " + m);
        public static void LogError(object m) => System.Console.WriteLine("[Error] " + m);
    }
}

namespace UnityEngine
{
    /// 桩里不做真正的序列化，AutoTone 的测试不碰它
    public static class JsonUtility
    {
        public static string ToJson(object o, bool pretty) => "{}";
        public static string ToJson(object o) => "{}";
        public static T FromJson<T>(string s) where T : new() => new T();
        public static void FromJsonOverwrite(string s, object o) { }
    }
}

namespace UnityEditor
{
    public static class EditorPrefs
    {
        static readonly System.Collections.Generic.Dictionary<string, string> S = new();
        public static string GetString(string k, string d) => S.TryGetValue(k, out var v) ? v : d;
        public static void SetString(string k, string v) => S[k] = v;
        public static void DeleteKey(string k) { S.Remove(k); }
    }
}

// ---- 为了把 ImageRepair 原样拿出来跑，补的桩 ----
namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static implicit operator Vector4(Vector2 v) => new Vector4(v.x, v.y, 0f, 0f);
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t)
            => new Vector2(Mathf.Lerp(a.x, b.x, t), Mathf.Lerp(a.y, b.y, t));
        public override string ToString() => $"({x:0.#####},{y:0.#####})";
    }

    public struct Vector2Int
    {
        public int x, y;
        public Vector2Int(int x, int y) { this.x = x; this.y = y; }
    }

    public struct Vector4
    {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }

        public static Color white => new Color(1f, 1f, 1f, 1f);
        public Color(float r, float g, float b) : this(r, g, b, 1f) { }
        public static Color black => new Color(0, 0, 0, 1);
        public static Color HSVToRGB(float h, float s, float v)
        {
            float c = v * s, x = c * (1 - Math.Abs((h * 6f) % 2f - 1f)), m = v - c;
            float r, g, b;
            int seg = (int)(h * 6f) % 6;
            if (seg == 0) { r = c; g = x; b = 0; }
            else if (seg == 1) { r = x; g = c; b = 0; }
            else if (seg == 2) { r = 0; g = c; b = x; }
            else if (seg == 3) { r = 0; g = x; b = c; }
            else if (seg == 4) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return new Color(r + m, g + m, b + m, 1f);
        }
        public static Color operator -(Color x, Color y) => new Color(x.r - y.r, x.g - y.g, x.b - y.b, x.a - y.a);
        public override string ToString() => $"({r:0.####},{g:0.####},{b:0.####})";
    }

    public enum RenderTextureFormat { ARGB32 = 0 }
    public enum RenderTextureReadWrite { Default = 0, Linear = 1 }

    public class Texture : Object { public int width, height; public TextureWrapMode wrapMode; }

    public class RenderTexture : Texture
    {
        public RenderTexture(int w, int h, int d, RenderTextureFormat f, RenderTextureReadWrite rw)
        { width = w; height = h; }
        public bool Create() => true;
        public void Release() { }
        public static RenderTexture active { get; set; }
        public static RenderTexture GetTemporary(int w, int h, int d, RenderTextureFormat f, RenderTextureReadWrite rw)
            => new RenderTexture(w, h, d, f, rw);
        public static void ReleaseTemporary(RenderTexture rt) { }
    }

    public class Shader { public static Shader Find(string n) => null; }

    public class Material : Object
    {
        public Material(Shader s) { }
        public void SetVector(string n, Vector4 v) { }
        public void SetColor(string n, Color c) { }
        public void SetFloat(string n, float f) { }
    }

    public static class Graphics
    {
        public static void Blit(Texture s, RenderTexture d) { }
        public static void Blit(Texture s, RenderTexture d, Material m, int p) { }
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
    }
}

// ---- 为了把 VideoGradeSettings / AutoTone / WhiteBalancePicker 原样拿出来跑 ----
namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float f) => new Vector3(a.x * f, a.y * f, a.z * f);
        public static Vector3 operator /(Vector3 a, float f) => new Vector3(a.x / f, a.y / f, a.z / f);
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static implicit operator Vector4(Vector3 v) => new Vector4(v.x, v.y, v.z, 0f);
        public override string ToString() => $"({x:0.####},{y:0.####},{z:0.####})";
    }

    /// 这里只需要它"能存在、能取值"，AutoTone 不碰曲线
    public class AnimationCurve
    {
        public struct Keyframe { public float time, value, inTangent, outTangent; }
        public Keyframe[] keys = new Keyframe[0];
        public AnimationCurve() { }
        public AnimationCurve(Keyframe[] k) { keys = k; }
        public int length => keys.Length;
        public float Evaluate(float t) => t;
        public static AnimationCurve Linear(float t0, float v0, float t1, float v1)
        {
            var c = new AnimationCurve();
            c.keys = new[] { new Keyframe { time = t0, value = v0 }, new Keyframe { time = t1, value = v1 } };
            return c;
        }
    }
}
