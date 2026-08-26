using Love.Tools;
using Unity.Sentis;
using UnityEditor;

namespace Love.EditorTools
{
    /// <summary>
    /// 编辑器里告诉 AI 那两个类去哪儿拿模型。
    ///
    /// ONNX 只能在编辑器里导入（Unity.Sentis.ONNX 是编辑器程序集），
    /// 所以编辑器直接按路径问 AssetDatabase 要；独立程序里那条路走不通，
    /// 得靠场景上序列化的引用，见 ToolApp。
    /// </summary>
    [InitializeOnLoad]
    static class AiHostEditor
    {
        static AiHostEditor()
        {
            AiDenoiser.ResolveModel = p => AssetDatabase.LoadAssetAtPath<ModelAsset>(p);
            AiMaskGenerator.ResolveModel = p => AssetDatabase.LoadAssetAtPath<ModelAsset>(p);
        }
    }
}
