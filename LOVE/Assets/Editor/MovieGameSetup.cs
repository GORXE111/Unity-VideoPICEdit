using System.IO;
using Love.Audio;
using Love.Core;
using Love.Story;
using Love.UI;
using Love.Video;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using UnityEngine.Video;

namespace Love.EditorTools
{
    /// <summary>
    /// 影视游戏一键搭建工具。
    /// 负责：导入 TMP 资源 → 生成中文字体资产 → 配置按钮九宫格贴图 → 生成选项按钮预制体 → 生成并接线 Main 场景。
    /// 菜单在 Unity 顶栏 Tools/影视游戏 下。
    /// </summary>
    public static class MovieGameSetup
    {
        // 正文字体：Noto Serif SC（思源宋体同源，SIL OFL，可随包发布）。
        // 想换字体就把 ttf/otf 丢进 Assets/GameAssets/Fonts，改这两行，然后跑
        // 「单步：重建字体资产」。系统自带的 simsun / STSONG 是商业字体，只能本地看效果，不能随游戏分发。
        const string FontTtfPath      = "Assets/GameAssets/Fonts/NotoSerifSC-VF.ttf";
        const string FontAssetPath    = "Assets/GameAssets/Fonts/NotoSerifSC SDF.asset";

        // 后备字体：正文字体缺字时顶上，用之前那套黑体
        const string FallbackTtfPath   = "Assets/GameAssets/Fonts/SourceHanSansCN-Normal.ttf";
        const string FallbackAssetPath = "Assets/GameAssets/Fonts/SourceHanSansCN SDF.asset";
        const string ButtonSpritePath = "Assets/GameAssets/UI/ChoiceButton_BG.png";
        const string ButtonPrefabPath = "Assets/GameAssets/Prefabs/ChoiceButton.prefab";
        const string UIPrefabPath     = "Assets/GameAssets/Prefabs/GameplayUI.prefab";
        const string ScenePath        = "Assets/Scenes/Main.unity";
        const string GradeShaderPath  = "Assets/GameAssets/Shaders/VideoGrade.shader";
        const string GradeMatPath     = "Assets/GameAssets/Materials/VideoGrade.mat";
        const string ClickSfxPath     = "Assets/GameAssets/Audio/SFX/ui_choice_click.wav";
        const string HoverSfxPath     = "Assets/GameAssets/Audio/SFX/ui_choice_hover.wav";
        const string TmpPackageRoot   = "Packages/com.unity.textmeshpro";

        // 选项布局参数。数值是从参考截图（1202x676，16:9）逐像素量出来再换算到 1920x1080 的：
        //   按钮 493x65 -> 788x104，间距 10 -> 16，按钮下边距屏底 160 -> 256
        // 想微调的话改这几个常数重跑一次，或者直接在 GameplayUI 预制体里拖 Container。
        const float RefWidth   = 1920f;
        const float RefHeight  = 1080f;
        const float BtnWidth   = 788f;    // 屏宽的 41.0%
        const float BtnHeight  = 130f;    // 参考图量出来是 104，按要求加高 25%
        const float BtnSpacing = 16f;
        const float BtnBottom  = 256f;    // 按钮下边缘距屏幕底部，屏高的 23.7%
        const float BtnFontSize = 22f;    // 参考图实测 em ≈ 13.4px，换算过来约 21.4
        const float MenuBtnWidth = 520f;  // 标题界面的菜单按钮比选项按钮窄

        // 按钮底图原始高度。九宫格的边框按 源高/目标高 缩放，圆角才不会被拉粗
        const float ButtonSourceHeight = 162f;
        const float ButtonBorder = 48f;

        const string SetupContinueKey = "Love.MovieGameSetup.ContinueAfterTmpImport";

        [MenuItem("Tools/影视游戏/一键搭建全部", false, 0)]
        public static void SetupAll()
        {
            if (!EnsureTmpEssentials()) return;

            var font = CreateChineseFontAsset();
            var sprite = ConfigureButtonSprite();
            CreateGradeMaterial();
            var prefab = CreateChoiceButtonPrefab(font, sprite);
            CreateMainScene(prefab, font);

            ApplyDisplaySettings();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (Application.isBatchMode)
            {
                Debug.Log("[MovieGameSetup] 搭建完成（命令行模式）");
                return;
            }

            EditorUtility.DisplayDialog("影视游戏",
                "搭建完成。\n\n" +
                "已生成：\n" +
                "· 中文字体资产（动态图集，支持全部中文）\n" +
                "· 选项按钮预制体（已挂好点击/悬停音效）\n" +
                "· UI 面板预制体 GameplayUI.prefab\n" +
                "· 视频调色材质\n" +
                "· Main 场景（已接好线，直接 Play 即可）\n\n" +
                "运行时按 F1 打开调色面板。\n\n" +
                "接下来把视频放进 Assets/StreamingAssets/Videos/，\n" +
                "并编辑 Assets/StreamingAssets/Story/story.json 配置剧情。", "好");
        }

        #region ① TMP 资源

        [MenuItem("Tools/影视游戏/单步：导入 TMP 基础资源", false, 20)]
        public static void MenuEnsureTmp() => EnsureTmpEssentials();

        /// <summary>TMP 的着色器和默认设置在一个 unitypackage 里，新项目必须先导入，否则字体资产无法生成。</summary>
        static bool EnsureTmpEssentials()
        {
            if (Directory.Exists("Assets/TextMesh Pro/Resources")) return true;

            string packagePath = ResolveTmpEssentialsPath();
            if (string.IsNullOrEmpty(packagePath))
            {
                EditorUtility.DisplayDialog("影视游戏",
                    "找不到 TMP 基础资源包，请手动执行：\nWindow → TextMeshPro → Import TMP Essential Resources\n然后再点一次一键搭建。", "好");
                return false;
            }

            // 导入完会触发一次程序集重载，脚本会被打断。
            // 打个标记，重载完成后自动接着往下跑，用户只需要点一次菜单。
            EditorPrefs.SetBool(SetupContinueKey, true);
            AssetDatabase.ImportPackage(packagePath, false);
            AssetDatabase.Refresh();
            Debug.Log("[MovieGameSetup] 已导入 TMP 基础资源，编译完成后会自动继续搭建。");
            return false;
        }

        /// <summary>
        /// 找到 TMP 基础资源包的真实磁盘路径。
        /// "Packages/com.unity.textmeshpro/..." 只是 AssetDatabase 的虚拟路径，
        /// 注册表包的实体其实在 Library/PackageCache 下，File 相关 API 直接查是查不到的。
        /// </summary>
        static string ResolveTmpEssentialsPath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(TmpPackageRoot + "/package.json");
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
            {
                string p = Path.Combine(info.resolvedPath, "Package Resources", "TMP Essential Resources.unitypackage");
                if (File.Exists(p)) return p;
            }

            // 兜底：直接在 PackageCache 里找
            if (Directory.Exists("Library/PackageCache"))
            {
                foreach (var dir in Directory.GetDirectories("Library/PackageCache", "com.unity.textmeshpro*"))
                {
                    string p = Path.Combine(dir, "Package Resources", "TMP Essential Resources.unitypackage");
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }

        /// <summary>程序集重载后的续跑入口（TMP 资源导入会打断一次流程）。</summary>
        [InitializeOnLoadMethod]
        static void ContinueAfterDomainReload()
        {
            if (!EditorPrefs.GetBool(SetupContinueKey, false)) return;
            EditorPrefs.DeleteKey(SetupContinueKey);
            EditorApplication.delayCall += () =>
            {
                if (Directory.Exists("Assets/TextMesh Pro/Resources")) SetupAll();
            };
        }

        #endregion

        #region 显示设置

        [MenuItem("Tools/影视游戏/单步：应用推荐的显示设置", false, 27)]
        public static void MenuApplyDisplaySettings()
        {
            ApplyDisplaySettings();
            AssetDatabase.SaveAssets();
            Debug.Log("[MovieGameSetup] 显示设置已应用：默认窗口化、可调整大小、允许 Alt+Enter 切全屏");
        }

        /// <summary>
        /// 默认窗口化 + 可调整窗口大小。
        /// 具体的初始窗口尺寸交给运行时的 ScreenSetup 按桌面分辨率算，
        /// 因为这里只能填死一个值，在不同尺寸的显示器上总有一头不合适。
        /// </summary>
        static void ApplyDisplaySettings()
        {
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.allowFullscreenSwitch = true;   // Alt+Enter
            PlayerSettings.runInBackground = true;
            PlayerSettings.defaultIsNativeResolution = false;

            // 兜底值。ScreenSetup 会在启动时按桌面重新算，
            // 填 1600x900 是为了万一 ScreenSetup 被删了，在 1080p 桌面上也还能正常开窗。
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            // 宽高比白名单 SetAspectRatio 在新版 Unity 里已废弃且功能被移除，
            // 现在所有比例都原生支持，不需要显式放开
        }

        #endregion

        #region ② 中文字体

        [MenuItem("Tools/影视游戏/单步：生成中文字体资产", false, 21)]
        public static void MenuCreateFont() { CreateChineseFontAsset(); AssetDatabase.SaveAssets(); }

        [MenuItem("Tools/影视游戏/单步：重建字体资产（换字体后用）", false, 22)]
        public static void MenuRebuildFont()
        {
            // 换字体时用：删掉旧资产强制重烘，否则会命中"已存在就直接返回"的分支
            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath) != null)
                AssetDatabase.DeleteAsset(FontAssetPath);

            var font = CreateChineseFontAsset();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (font == null) return;

            // 预制体和场景里的文本还指着旧字体，重建一遍才会换过来
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(ButtonSpritePath);
            CreateChoiceButtonPrefab(font, sprite);
            EditorUtility.DisplayDialog("影视游戏",
                $"字体已重建：{Path.GetFileName(FontAssetPath)}\n\n" +
                "选项按钮预制体也一并更新了。\n" +
                "场景里的标题文字要生效的话，再跑一次「一键搭建全部」。", "好");
        }

        /// <summary>
        /// 生成 TMP 字体资产，图集模式为 Dynamic：
        /// 运行时遇到哪个字就往图集里烘哪个字，不需要预先勾选几千个汉字，也不会有 30MB 的静态图集。
        /// </summary>
        static TMP_FontAsset CreateChineseFontAsset()
        {
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            if (existing != null)
            {
                ApplyToTmpSettings(existing);
                return existing;
            }

            var fontAsset = BuildFontAsset(FontTtfPath, FontAssetPath);
            if (fontAsset == null) return null;

            ApplyToTmpSettings(fontAsset);
            Debug.Log($"[MovieGameSetup] 已生成字体资产：{FontAssetPath}");
            return fontAsset;
        }

        static TMP_FontAsset BuildFontAsset(string ttfPath, string assetPath)
        {
            var ttf = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
            if (ttf == null)
            {
                Debug.LogError($"[MovieGameSetup] 找不到字体文件：{ttfPath}");
                return null;
            }

            // 源字体必须是 Dynamic 并且带字模数据，TMP 才能在运行时动态烘字
            if (AssetImporter.GetAtPath(ttfPath) is TrueTypeFontImporter ttfImporter)
            {
                bool dirty = false;
                if (ttfImporter.fontTextureCase != FontTextureCase.Dynamic) { ttfImporter.fontTextureCase = FontTextureCase.Dynamic; dirty = true; }
                if (!ttfImporter.includeFontData) { ttfImporter.includeFontData = true; dirty = true; }
                if (dirty) ttfImporter.SaveAndReimport();
                ttf = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
            }

            // 采样点数比之前调高了：宋体这类衬线字有很细的横画，
            // 采样太低的话 SDF 会把细横糊掉，小字号下尤其明显。
            var fontAsset = TMP_FontAsset.CreateFontAsset(
                ttf,
                samplingPointSize: 90,
                atlasPadding: 9,
                renderMode: GlyphRenderMode.SDFAA,
                atlasWidth: 1024,
                atlasHeight: 1024,
                atlasPopulationMode: AtlasPopulationMode.Dynamic,
                enableMultiAtlasSupport: true);

            if (fontAsset == null)
            {
                Debug.LogError($"[MovieGameSetup] 字体资产创建失败：{ttfPath}");
                return null;
            }

            fontAsset.name = Path.GetFileNameWithoutExtension(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath));
            AssetDatabase.CreateAsset(fontAsset, assetPath);

            // 图集贴图和材质要作为子资产存进去，否则重启 Unity 后会丢
            if (fontAsset.atlasTextures != null && fontAsset.atlasTextures.Length > 0 && fontAsset.atlasTextures[0] != null)
            {
                fontAsset.atlasTextures[0].name = fontAsset.name + " Atlas";
                AssetDatabase.AddObjectToAsset(fontAsset.atlasTextures[0], fontAsset);
            }
            if (fontAsset.material != null)
            {
                fontAsset.material.name = fontAsset.name + " Material";
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            }

            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath);

            return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
        }

        /// <summary>
        /// 把正文字体设成 TMP 默认字体，并把它和黑体后备一起挂进全局后备列表。
        /// 这样任何 TMP 文本都不会出现中文豆腐块，正文字体缺字时也能顶上。
        /// </summary>
        static void ApplyToTmpSettings(TMP_FontAsset fontAsset)
        {
            var settings = AssetDatabase.LoadAssetAtPath<TMP_Settings>("Assets/TextMesh Pro/Resources/TMP Settings.asset");
            if (settings == null || fontAsset == null) return;

            var so = new SerializedObject(settings);

            var defaultFont = so.FindProperty("m_defaultFontAsset");
            if (defaultFont != null) defaultFont.objectReferenceValue = fontAsset;

            var fallbacks = so.FindProperty("m_fallbackFontAssets");
            if (fallbacks != null)
            {
                AddFallback(fallbacks, fontAsset);

                // 黑体后备：正文字体缺字时顶上。没有就现做一个
                var fallbackFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FallbackAssetPath);
                if (fallbackFont == null && File.Exists(FallbackTtfPath))
                    fallbackFont = BuildFontAsset(FallbackTtfPath, FallbackAssetPath);
                if (fallbackFont != null) AddFallback(fallbacks, fallbackFont);
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
        }

        static void AddFallback(SerializedProperty list, TMP_FontAsset font)
        {
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == font) return;

            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = font;
        }

        #endregion

        #region ③ 按钮贴图

        [MenuItem("Tools/影视游戏/单步：配置选项按钮贴图", false, 23)]
        public static void MenuConfigureSprite() { ConfigureButtonSprite(); }

        /// <summary>把按钮底图设成 Sprite 并配置九宫格，这样按钮宽高随便改都不会把圆角拉变形。</summary>
        static Sprite ConfigureButtonSprite()
        {
            if (!(AssetImporter.GetAtPath(ButtonSpritePath) is TextureImporter importer))
            {
                Debug.LogError($"[MovieGameSetup] 找不到按钮贴图：{ButtonSpritePath}");
                return null;
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spriteBorder = new Vector4(ButtonBorder, ButtonBorder, ButtonBorder, ButtonBorder);   // 左 下 右 上
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();

            return AssetDatabase.LoadAssetAtPath<Sprite>(ButtonSpritePath);
        }

        #endregion

        #region ④ 选项按钮预制体

        [MenuItem("Tools/影视游戏/单步：生成选项按钮预制体", false, 24)]
        public static void MenuCreatePrefab()
        {
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(ButtonSpritePath);
            CreateChoiceButtonPrefab(font, sprite);
        }

        static ChoiceButtonView CreateChoiceButtonPrefab(TMP_FontAsset font, Sprite sprite)
        {
            var root = new GameObject("ChoiceButton", typeof(RectTransform), typeof(CanvasRenderer),
                                      typeof(Image), typeof(Button), typeof(CanvasGroup), typeof(ChoiceButtonView));
            var rt = (RectTransform)root.transform;
            rt.sizeDelta = new Vector2(BtnWidth, BtnHeight);

            var image = root.GetComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            // 让九宫格边框按 源高/目标高 缩放，圆角和描边保持原图的视觉比例
            image.pixelsPerUnitMultiplier = ButtonSourceHeight / BtnHeight;
            image.raycastTarget = true;

            var button = root.GetComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;   // 悬停效果由 ChoiceButtonView 自己做

            var layout = root.AddComponent<LayoutElement>();
            layout.preferredWidth = BtnWidth;
            layout.preferredHeight = BtnHeight;
            layout.minWidth = BtnWidth;
            layout.minHeight = BtnHeight;

            // 文字
            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(root.transform, false);
            var labelRt = (RectTransform)labelGo.transform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            // 参考图里文字是相对整个按钮居中的，左边那个装饰图标不参与排版，所以左右留白对称
            labelRt.offsetMin = new Vector2(56f, 6f);
            labelRt.offsetMax = new Vector2(-56f, -6f);

            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.text = "选项文字";
            tmp.fontSize = BtnFontSize;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 16f;
            tmp.fontSizeMax = BtnFontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.enableWordWrapping = false;

            var view = root.GetComponent<ChoiceButtonView>();
            view.button = button;
            view.background = image;
            view.label = tmp;
            view.canvasGroup = root.GetComponent<CanvasGroup>();
            view.normalColor = Color.white;
            view.hoverColor = new Color(1f, 1f, 1f, 1f);
            view.clickSfx = AssetDatabase.LoadAssetAtPath<AudioClip>(ClickSfxPath);
            view.hoverSfx = AssetDatabase.LoadAssetAtPath<AudioClip>(HoverSfxPath);

            Directory.CreateDirectory(Path.GetDirectoryName(ButtonPrefabPath));
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, ButtonPrefabPath, out bool saved);
            Object.DestroyImmediate(root);

            if (!saved) Debug.LogError($"[MovieGameSetup] 预制体保存失败：{ButtonPrefabPath}");

            // SaveAsPrefabAsset 的返回值在命令行模式下不一定可用，
            // 统一重新从磁盘加载一次，拿到的引用才稳。
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(ButtonPrefabPath, ImportAssetOptions.ForceSynchronousImport);

            var prefabView = LoadButtonPrefab();
            if (prefabView == null)
                Debug.LogError($"[MovieGameSetup] 预制体存下来了但读不回 ChoiceButtonView：{ButtonPrefabPath}");
            else
                Debug.Log($"[MovieGameSetup] 已生成选项按钮预制体：{ButtonPrefabPath}");

            return prefabView;
        }

        /// <summary>从磁盘读回选项按钮预制体上的组件。</summary>
        static ChoiceButtonView LoadButtonPrefab()
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(ButtonPrefabPath);
            return go != null ? go.GetComponent<ChoiceButtonView>() : null;
        }

        #endregion

        #region ⑤ 调色材质

        [MenuItem("Tools/影视游戏/单步：生成调色材质", false, 25)]
        public static void MenuCreateGradeMaterial() { CreateGradeMaterial(); AssetDatabase.SaveAssets(); }

        /// <summary>
        /// 生成调色用的材质资产。
        /// 必须是资产而不是运行时 new Material(Shader.Find(...))，否则打包时这个 Shader 不会被收进去。
        /// </summary>
        static Material CreateGradeMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(GradeMatPath);
            if (existing != null) return existing;

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(GradeShaderPath);
            if (shader == null)
            {
                Debug.LogError($"[MovieGameSetup] 找不到调色 Shader：{GradeShaderPath}");
                return null;
            }

            var mat = new Material(shader) { name = "VideoGrade" };
            Directory.CreateDirectory(Path.GetDirectoryName(GradeMatPath));
            AssetDatabase.CreateAsset(mat, GradeMatPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[MovieGameSetup] 已生成调色材质：{GradeMatPath}");
            return AssetDatabase.LoadAssetAtPath<Material>(GradeMatPath);
        }

        #endregion

        #region ⑥ 场景

        [MenuItem("Tools/影视游戏/单步：生成 Main 场景", false, 26)]
        public static void MenuCreateScene()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ButtonPrefabPath);
            CreateMainScene(prefab != null ? prefab.GetComponent<ChoiceButtonView>() : null,
                            AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath));
        }

        static void CreateMainScene(ChoiceButtonView buttonPrefab, TMP_FontAsset font)
        {
            // 命令行无人值守时不能弹窗，否则会一直卡着等人点
            bool batch = Application.isBatchMode;

            // 兜底：调用方没传进来就自己从磁盘读。
            // 少了它，选项按钮和标题菜单会全是空的，而且要跑起来才发现。
            if (buttonPrefab == null) buttonPrefab = LoadButtonPrefab();
            if (buttonPrefab == null)
            {
                Debug.LogError($"[MovieGameSetup] 选项按钮预制体不可用，中止场景生成：{ButtonPrefabPath}");
                return;
            }

            // 生成场景会替换当前打开的场景，先给用户保存的机会
            if (!batch && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.LogWarning("[MovieGameSetup] 用户取消了场景保存，已跳过场景生成。");
                return;
            }
            if (!batch && File.Exists(ScenePath) &&
                !EditorUtility.DisplayDialog("影视游戏", $"{ScenePath} 已存在，要覆盖重建吗？", "覆盖", "跳过"))
            {
                Debug.Log("[MovieGameSetup] 已跳过场景生成。");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // NewScene 会顺带卸载未使用的资源，把预制体的原生对象也卸掉。
            // 之后那个 C# 引用会变成 Unity 特有的"假 null"：== null 为真、.gameObject 取不到，
            // 但赋值给序列化字段时 instanceID 仍能解析回正确的 GUID——
            // 于是 ChoicePanel.buttonPrefab 写对了，标题菜单却实例化不出来，非常难查。
            // 所以这里必须重新读一次。
            buttonPrefab = LoadButtonPrefab();
            if (buttonPrefab == null)
            {
                Debug.LogError($"[MovieGameSetup] 新建场景后读不回选项按钮预制体，中止：{ButtonPrefabPath}");
                return;
            }

            // ---- 摄像机 ----
            var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.orthographic = true;
            camGo.transform.position = new Vector3(0f, 0f, -10f);

            // ---- EventSystem ----
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            // ---- Canvas ----
            var canvasGo = new GameObject("UICanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(RefWidth, RefHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // ---- 视频层 ----
            var videoLayer = CreateFullScreenChild(canvasGo.transform, "VideoLayer");
            var bg = videoLayer.gameObject.AddComponent<Image>();
            bg.color = Color.black;                 // 视频比例不满屏时的黑边
            bg.raycastTarget = false;

            var screenGo = new GameObject("VideoScreen", typeof(RectTransform), typeof(CanvasRenderer),
                                          typeof(RawImage), typeof(AspectRatioFitter));
            screenGo.transform.SetParent(videoLayer, false);
            var screenRt = (RectTransform)screenGo.transform;
            screenRt.anchorMin = Vector2.zero;
            screenRt.anchorMax = Vector2.one;
            screenRt.offsetMin = Vector2.zero;
            screenRt.offsetMax = Vector2.zero;
            var rawImage = screenGo.GetComponent<RawImage>();
            rawImage.raycastTarget = false;
            rawImage.color = Color.white;
            var fitter = screenGo.GetComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = 16f / 9f;

            // ---- 占位层（视频未导入时顶上来，盖住视频层）----
            var placeholderLayer = CreateFullScreenChild(canvasGo.transform, "PlaceholderLayer");
            var placeholderBg = placeholderLayer.gameObject.AddComponent<Image>();
            placeholderBg.color = new Color(0.16f, 0.18f, 0.26f);
            placeholderBg.raycastTarget = false;

            var phTitle = CreateLabel(placeholderLayer, "Title", font, 92f, TextAlignmentOptions.Center);
            var phTitleRt = (RectTransform)phTitle.transform;
            phTitleRt.anchorMin = new Vector2(0f, 0.5f);
            phTitleRt.anchorMax = new Vector2(1f, 0.5f);
            phTitleRt.pivot = new Vector2(0.5f, 0.5f);
            phTitleRt.anchoredPosition = new Vector2(0f, 40f);
            phTitleRt.sizeDelta = new Vector2(-200f, 140f);
            phTitle.color = new Color(1f, 1f, 1f, 0.92f);

            var phInfo = CreateLabel(placeholderLayer, "Info", font, 34f, TextAlignmentOptions.Center);
            var phInfoRt = (RectTransform)phInfo.transform;
            phInfoRt.anchorMin = new Vector2(0f, 0.5f);
            phInfoRt.anchorMax = new Vector2(1f, 0.5f);
            phInfoRt.pivot = new Vector2(0.5f, 1f);
            phInfoRt.anchoredPosition = new Vector2(0f, -40f);
            phInfoRt.sizeDelta = new Vector2(-200f, 160f);
            phInfo.color = new Color(1f, 1f, 1f, 0.5f);

            // 进度条：底槽 + 填充
            var barBgGo = new GameObject("ProgressBar", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            barBgGo.transform.SetParent(placeholderLayer, false);
            var barBgRt = (RectTransform)barBgGo.transform;
            barBgRt.anchorMin = new Vector2(0.5f, 0f);
            barBgRt.anchorMax = new Vector2(0.5f, 0f);
            barBgRt.pivot = new Vector2(0.5f, 0f);
            barBgRt.anchoredPosition = new Vector2(0f, 220f);
            barBgRt.sizeDelta = new Vector2(900f, 6f);
            // Filled 类型必须有 sprite 才认 fillAmount，这里用 Unity 内置的 UISprite
            var uiSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            var barBg = barBgGo.GetComponent<Image>();
            barBg.sprite = uiSprite;
            barBg.type = Image.Type.Sliced;
            barBg.color = new Color(1f, 1f, 1f, 0.15f);
            barBg.raycastTarget = false;

            var barFillGo = new GameObject("Fill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            barFillGo.transform.SetParent(barBgGo.transform, false);
            var barFillRt = (RectTransform)barFillGo.transform;
            barFillRt.anchorMin = Vector2.zero;
            barFillRt.anchorMax = Vector2.one;
            barFillRt.offsetMin = Vector2.zero;
            barFillRt.offsetMax = Vector2.zero;
            var barFill = barFillGo.GetComponent<Image>();
            barFill.sprite = uiSprite;
            barFill.color = new Color(1f, 1f, 1f, 0.75f);
            barFill.raycastTarget = false;
            barFill.type = Image.Type.Filled;
            barFill.fillMethod = Image.FillMethod.Horizontal;
            barFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            barFill.fillAmount = 0f;

            var placeholderView = placeholderLayer.gameObject.AddComponent<PlaceholderView>();
            placeholderView.background = placeholderBg;
            placeholderView.titleLabel = phTitle;
            placeholderView.infoLabel = phInfo;
            placeholderView.progressFill = barFill;

            // ---- 选项层 ----
            // 挂 AspectRatioFitter + AspectFollower，让它的区域和视频画面完全重合。
            // 否则窗口比例和视频不一致时（玩家随手拉窗口就会这样），
            // 按钮会按整个屏幕定位，掉进上下黑边里。
            var choiceLayer = CreateFullScreenChild(canvasGo.transform, "ChoiceLayer");
            var choiceFitter = choiceLayer.gameObject.AddComponent<AspectRatioFitter>();
            choiceFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            choiceFitter.aspectRatio = 16f / 9f;
            var choiceFollower = choiceLayer.gameObject.AddComponent<AspectFollower>();
            choiceFollower.source = fitter;

            var choiceCg = choiceLayer.gameObject.AddComponent<CanvasGroup>();
            choiceCg.alpha = 0f;
            choiceCg.blocksRaycasts = false;

            var containerGo = new GameObject("Container", typeof(RectTransform),
                                             typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
            containerGo.transform.SetParent(choiceLayer, false);
            var containerRt = (RectTransform)containerGo.transform;
            containerRt.anchorMin = new Vector2(0.5f, 0f);
            containerRt.anchorMax = new Vector2(0.5f, 0f);
            containerRt.pivot = new Vector2(0.5f, 0f);
            containerRt.anchoredPosition = new Vector2(0f, BtnBottom);
            containerRt.sizeDelta = new Vector2(BtnWidth * 2f + BtnSpacing, BtnHeight);

            var hlg = containerGo.GetComponent<HorizontalLayoutGroup>();
            hlg.spacing = BtnSpacing;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            var csf = containerGo.GetComponent<ContentSizeFitter>();
            csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var choicePanel = choiceLayer.gameObject.AddComponent<ChoicePanel>();
            choicePanel.container = containerRt;
            choicePanel.buttonPrefab = buttonPrefab;
            choicePanel.canvasGroup = choiceCg;
            choicePanel.maxChoices = 2;

            // ---- 标题界面（盖住视频和选项，但在黑场遮罩之下）----
            var titleLayer = CreateFullScreenChild(canvasGo.transform, "TitleLayer");
            var titleBg = titleLayer.gameObject.AddComponent<Image>();
            titleBg.color = new Color(0.05f, 0.06f, 0.09f, 1f);
            var titleCg = titleLayer.gameObject.AddComponent<CanvasGroup>();
            titleCg.alpha = 0f;

            var gameTitle = CreateLabel(titleLayer, "GameTitle", font, 120f, TextAlignmentOptions.Center);
            var gameTitleRt = (RectTransform)gameTitle.transform;
            gameTitleRt.anchorMin = new Vector2(0f, 0.5f);
            gameTitleRt.anchorMax = new Vector2(1f, 0.5f);
            gameTitleRt.pivot = new Vector2(0.5f, 0.5f);
            gameTitleRt.sizeDelta = new Vector2(-200f, 160f);
            gameTitleRt.anchoredPosition = new Vector2(0f, 210f);
            gameTitle.color = Color.white;

            var subtitle = CreateLabel(titleLayer, "Subtitle", font, 30f, TextAlignmentOptions.Center);
            var subtitleRt = (RectTransform)subtitle.transform;
            subtitleRt.anchorMin = new Vector2(0f, 0.5f);
            subtitleRt.anchorMax = new Vector2(1f, 0.5f);
            subtitleRt.pivot = new Vector2(0.5f, 1f);
            subtitleRt.sizeDelta = new Vector2(-200f, 60f);
            subtitleRt.anchoredPosition = new Vector2(0f, 130f);
            subtitle.color = new Color(1f, 1f, 1f, 0.55f);

            // Logo 占位，默认隐藏；把 sprite 拖进去就会显示
            var logoGo = new GameObject("Logo", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            logoGo.transform.SetParent(titleLayer, false);
            var logoRt = (RectTransform)logoGo.transform;
            logoRt.anchorMin = new Vector2(0.5f, 0.5f);
            logoRt.anchorMax = new Vector2(0.5f, 0.5f);
            logoRt.pivot = new Vector2(0.5f, 0.5f);
            logoRt.sizeDelta = new Vector2(600f, 300f);
            logoRt.anchoredPosition = new Vector2(0f, 220f);
            var logoImg = logoGo.GetComponent<Image>();
            logoImg.raycastTarget = false;
            logoImg.preserveAspect = true;
            logoGo.SetActive(false);

            // 菜单按钮竖排，复用选项按钮预制体，样式和音效自动一致
            var menuGo = new GameObject("Menu", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            menuGo.transform.SetParent(titleLayer, false);
            var menuRt = (RectTransform)menuGo.transform;
            menuRt.anchorMin = new Vector2(0.5f, 0f);
            menuRt.anchorMax = new Vector2(0.5f, 0f);
            menuRt.pivot = new Vector2(0.5f, 0f);
            menuRt.anchoredPosition = new Vector2(0f, 220f);
            var menuLayout = menuGo.GetComponent<VerticalLayoutGroup>();
            menuLayout.spacing = 24f;
            menuLayout.childAlignment = TextAnchor.MiddleCenter;
            menuLayout.childControlWidth = false;
            menuLayout.childControlHeight = false;
            menuLayout.childForceExpandWidth = false;
            menuLayout.childForceExpandHeight = false;
            var menuCsf = menuGo.GetComponent<ContentSizeFitter>();
            menuCsf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            menuCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var startBtn = InstantiateMenuButton(buttonPrefab, menuRt, "StartButton", "开始游戏");
            var quitBtn  = InstantiateMenuButton(buttonPrefab, menuRt, "QuitButton",  "退出游戏");

            var titleScreen = titleLayer.gameObject.AddComponent<TitleScreen>();
            titleScreen.canvasGroup = titleCg;
            titleScreen.background = titleBg;
            titleScreen.logo = logoImg;
            titleScreen.titleLabel = gameTitle;
            titleScreen.subtitleLabel = subtitle;
            titleScreen.startButton = startBtn;
            titleScreen.quitButton = quitBtn;
            titleScreen.gameTitle = "LOVE";
            titleScreen.subtitle = string.Empty;

            // ---- 黑场遮罩（放最上层）----
            var fadeLayer = CreateFullScreenChild(canvasGo.transform, "FadeOverlay");
            var fadeImage = fadeLayer.gameObject.AddComponent<Image>();
            fadeImage.color = Color.black;
            fadeImage.raycastTarget = false;
            var fader = fadeLayer.gameObject.AddComponent<ScreenFader>();
            fader.overlay = fadeImage;
            fader.fadeColor = Color.black;

            // ---- 把整套 UI 存成预制体，场景里留一个连着的实例 ----
            // 这样你可以在预制体模式里改布局，改完所有场景同步，不用在场景里翻着改
            var uiRoot = canvasGo.AddComponent<GameplayUIRoot>();
            uiRoot.canvas = canvas;
            uiRoot.scaler = scaler;
            uiRoot.videoImage = rawImage;
            uiRoot.videoAspectFitter = fitter;
            uiRoot.placeholderView = placeholderView;
            uiRoot.choicePanel = choicePanel;
            uiRoot.titleScreen = titleScreen;
            uiRoot.fader = fader;

            Directory.CreateDirectory(Path.GetDirectoryName(UIPrefabPath));
            PrefabUtility.SaveAsPrefabAssetAndConnect(canvasGo, UIPrefabPath, InteractionMode.AutomatedAction);
            Debug.Log($"[MovieGameSetup] 已生成 UI 面板预制体：{UIPrefabPath}");

            // ---- 系统层 ----
            var systems = new GameObject("Systems");

            var screenGo2 = new GameObject("ScreenSetup", typeof(ScreenSetup));
            screenGo2.transform.SetParent(systems.transform, false);
            var screenSetup = screenGo2.GetComponent<ScreenSetup>();
            screenSetup.startWindowed = true;
            screenSetup.initialScreenFraction = 0.8f;
            screenSetup.windowAspect = new Vector2Int(16, 9);
            screenSetup.toggleFullscreenKey = KeyCode.F11;
            screenSetup.rememberWindowState = true;
            var audioGo = new GameObject("AudioManager", typeof(AudioManager));
            audioGo.transform.SetParent(systems.transform, false);
            var audioMgr = audioGo.GetComponent<AudioManager>();
            audioMgr.masterVolume = 1f;
            audioMgr.bgmVolume = 0.5f;    // BGM 压在视频对白之下，避免盖住台词
            audioMgr.videoVolume = 1f;
            audioMgr.sfxVolume = 1f;
            audioMgr.rememberPlayerSettings = false;   // 开发期关掉，Inspector 改了才立刻生效

            var videoGo = new GameObject("VideoPlayer", typeof(VideoPlayer), typeof(AudioSource), typeof(VideoScreen));
            videoGo.transform.SetParent(systems.transform, false);
            var videoScreen = videoGo.GetComponent<VideoScreen>();
            videoScreen.screen = rawImage;
            videoScreen.aspectFitter = fitter;
            videoScreen.videoFolder = "Videos";
            var videoAudio = videoGo.GetComponent<AudioSource>();
            videoAudio.playOnAwake = false;
            videoAudio.spatialBlend = 0f;

            // 视频后处理：接在 VideoScreen 和 RawImage 中间，每帧把画面过一遍调色管线
            var post = videoGo.AddComponent<VideoPostProcessor>();
            post.videoScreen = videoScreen;
            post.target = rawImage;
            post.aspectFitter = fitter;
            post.material = CreateGradeMaterial();
            post.presetFile = "Story/grade.json";
            post.loadOnStart = true;

            // 开发期用的调色面板，UI 是运行时自己搭的，场景里不需要预制体
            var gradePanelGo = new GameObject("VideoGradePanel", typeof(VideoGradePanel));
            gradePanelGo.transform.SetParent(systems.transform, false);
            var gradePanel = gradePanelGo.GetComponent<VideoGradePanel>();
            gradePanel.postProcessor = post;
            gradePanel.placeholderView = placeholderView;
            gradePanel.toggleKey = KeyCode.F1;
            gradePanel.openOnStart = false;

            var directorGo = new GameObject("StoryDirector", typeof(StoryDirector));
            directorGo.transform.SetParent(systems.transform, false);
            var director = directorGo.GetComponent<StoryDirector>();
            director.videoScreen = videoScreen;
            director.choicePanel = choicePanel;
            director.fader = fader;
            director.placeholderView = placeholderView;
            director.titleScreen = titleScreen;
            director.storyFile = "Story/story.json";
            director.autoStart = true;
            director.showTitleScreen = true;

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            AddSceneToBuildSettings(ScenePath);
            Debug.Log($"[MovieGameSetup] 已生成场景：{ScenePath}");
        }

        /// <summary>往标题菜单里放一个按钮。直接实例化选项按钮预制体，样式和点击音效自动一致。</summary>
        static ChoiceButtonView InstantiateMenuButton(ChoiceButtonView prefab, Transform parent, string name, string text)
        {
            // 引用可能已经被资源卸载弄成"假 null"，兜底再读一次
            if (prefab == null) prefab = LoadButtonPrefab();
            if (prefab == null)
            {
                Debug.LogError("[MovieGameSetup] 选项按钮预制体缺失，标题菜单按钮生成失败");
                return null;
            }

            // 传 gameObject 而不是 Component，返回值类型才是确定的 GameObject
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject, parent);
            var view = go.GetComponent<ChoiceButtonView>();
            if (view == null)
            {
                Debug.LogError("[MovieGameSetup] 实例化出来的按钮上没有 ChoiceButtonView");
                return null;
            }
            go.name = name;
            ((RectTransform)view.transform).sizeDelta = new Vector2(MenuBtnWidth, BtnHeight);

            var le = view.GetComponent<LayoutElement>();
            if (le != null)
            {
                le.preferredWidth = MenuBtnWidth;
                le.minWidth = MenuBtnWidth;
            }
            if (view.label != null) view.label.text = text;
            return view;
        }

        static TextMeshProUGUI CreateLabel(Transform parent, string name, TMP_FontAsset font,
                                          float fontSize, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.fontSize = fontSize;
            tmp.alignment = alignment;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            return tmp;
        }

        static RectTransform CreateFullScreenChild(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return rt;
        }

        static void AddSceneToBuildSettings(string path)
        {
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.Exists(s => s.path == path)) return;
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        #endregion
    }
}
