# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目

两块内容长在同一套管线上：

1. **影视游戏**（interactive film / FMV galgame）：播一段预渲染视频 → 到点弹出选项 → 画面定格 → 玩家选择 → 下一段
2. **调色 / 修图工具链**：达芬奇量级的后期管线，同时服务于游戏内视频、编辑器调色台和静态图片修图

- Unity **2022.3.62f3**，**内置渲染管线**，**Linear 色彩空间**，**旧版 Input Manager**（`Input.GetKeyDown`）
- Unity 工程根目录是 `LOVE/`，打包输出在 `Build/`
- 没有测试框架，没有包管理器脚本；一切通过 Unity 编辑器菜单或 batchmode 驱动
- **代码注释、UI 文案、菜单项一律用中文**

## 常用命令

Unity 可执行文件：`C:\Program Files\Unity 2022.3.62f3\Editor\Unity.exe`

```bash
# 重建场景+预制体+字体+材质，然后打包 Windows（单进程，推荐）
Unity.exe -batchmode -quit -projectPath E:\GalgameLOVE\LOVE \
  -executeMethod Love.EditorTools.BuildTool.SetupAndBuildWindows -logFile build.log

# 只打包 / 只重建
-executeMethod Love.EditorTools.BuildTool.BuildWindows
-executeMethod Love.EditorTools.MovieGameSetup.SetupAll
```

**绝不要连着起两个 Unity 进程。** 第一个 `-quit` 返回时工程锁还没释放，第二个会崩在
`HandleProjectAlreadyOpenInAnotherInstance`，而且退出码仍是 0，看起来像成功了。
需要多步就写进一个 `-executeMethod` 入口。

日志里认这两个前缀：`[BuildResult]`、`[MovieGameSetup]`。

### 不打开 Unity 也能编译检查

改完 C# 想立刻验证语法/API，不必启动编辑器。Unity 的 Bee 会把完整的 csc 命令行留在 rsp 里：

```bash
ls LOVE/Library/Bee/artifacts/*.dag/Assembly-CSharp.rsp          # 运行时
ls LOVE/Library/Bee/artifacts/*.dag/Assembly-CSharp-Editor.rsp   # 编辑器

# 改掉 -out/-refout 指向临时目录后执行（工作目录必须是 LOVE/）
dotnet "C:\Program Files\Unity 2022.3.62f3\Editor\Data\DotNetSdkRoslyn\csc.dll" @tmp.rsp
```

几个必须注意的点：

- **rsp 最后一行没有换行符。** 直接 `>>` 追加会把新参数粘到 `/additionalfile:` 那行尾部，
  报的错却是「找不到某个类型」，极其误导。追加前先 `echo ""`。
- 新增 .cs 文件时要手动补一行源文件路径（Unity 没重新生成 rsp 之前它不在列表里）。
- 编辑器程序集的 rsp 引用 dag 目录里的 `Assembly-CSharp.ref.dll`（可能是旧的），
  改了运行时代码后要把那条 `-r:` 换成刚编出来的运行时 dll。
- 装了新包之后 rsp 里的路径会失效（包缓存重排），重新从 Bee 产物生成一份。
- 要编到 `#if LOVE_SENTIS` 里的代码，rsp 得带 `-define:LOVE_SENTIS`。

**这只能验 C#。HLSL 无法离线编译，shader 改动必须在 Unity 里确认。**

## 架构

### 场景是生成的，不是手搭的

`Assets/Editor/MovieGameSetup.cs`（约 940 行）用代码构建 `Main.unity`、`GameplayUI.prefab`、
`ChoiceButton.prefab`、TMP 中文字体资产、调色材质，并应用 PlayerSettings。

**不要手改 `Main.unity` 或 `GameplayUI.prefab`。** 改生成器再跑一次「一键搭建全部」。
布局常数（按钮尺寸、间距、字号）都在该文件顶部。

### 运行时流程

```
StoryDirector（总导演，协程状态机）
  ├── VideoScreen        VideoPlayer → RenderTexture
  ├── VideoPostProcessor 每帧把画面过一遍调色管线 → RawImage，兼直方图/示波器
  ├── ChoicePanel        选项按钮池
  ├── TitleScreen        标题界面
  ├── ScreenFader        段间黑场
  ├── AudioManager       双 AudioSource 交叉过渡的 BGM
  └── ScreenSetup        窗口模式，按桌面分辨率算初始尺寸
```

- **定格最后一帧**靠 RenderTexture + 播完只 `Pause()` 不 `Stop()`。`Stop()` 会归零播放头并清画面。
- 组件之间用 `GameplayUIRoot.Find()` 自动寻址，所以 `GameplayUI.prefab` 拖到任何场景都能自己接上线。
- 选项层挂 `AspectFollower`，抄视频的 `AspectRatioFitter` 比例，窗口不是 16:9 时按钮不会掉进黑边。

### 内容是数据驱动的

| 文件 | 作用 | 要重编译吗 |
|---|---|---|
| `StreamingAssets/Story/story.json` | 剧情流程、选项、出选项时机、**按段调色** | 否 |
| `StreamingAssets/Story/grade.json` | 全局调色参数 | 否 |
| `StreamingAssets/Story/Grades/*.json` | 调色预设库，段落按名字引用 | 否 |
| `StreamingAssets/Videos/*.mp4` | 视频（H.264 + AAC） | 否 |
| `Resources/Audio/BGM/*` | BGM，JSON 按文件名引用 | 是（Resources 要打包） |

**StreamingAssets 是原样拷贝进包的**，出包之后可以直接替换视频/JSON 重新发版。
这是当初不用 VideoClip 资源的主要理由。

`bgm` 填同名时 `AudioManager` 会跳过重播，所以连续几段填同一首 = BGM 不间断。

### 调色管线

```
VideoGradeSettings   纯数据（JsonUtility 可序列化，含 AnimationCurve），带 Lerp
VideoGradeRenderer   渲染核心，普通 C# 类 —— 不依赖 MonoBehaviour / 场景 / Play 模式
GradePresetStore     预设读写，编辑器和运行时共用
VideoGrade.shader    5 个 Pass，算法在 4 个 .cginc 里
```

`VideoGradeRenderer.Render(任意Texture, 任意RenderTexture, settings, options)`。
抽出来正是为了让编辑器的修图台复用同一条管线处理静态图片。

四个消费方：`VideoPostProcessor`（场景组件）、`VideoGradeWindow`（调色台）、
`PhotoGradeWindow`（修图台）、`VideoGradePanel`（运行时面板，正式包默认禁用）。

参数界面 100+ 控件，由 `Editor/GradeSettingsGUI.cs` 一份实现供两个窗口共用。

**Pass 编排**（顺序有讲究）：

```
0-2  Bloom 阈值 / 降采样 / 帐篷升采样
3    细节滤波：镜头畸变 → 双边降噪 → 通透度 → 纹理 → 智能锐化
4    合成：LOG 解码 → 校色矩阵 → 一级 → 曲线 → 六条曲线 → LUT → 二级 → 风格化 → 监看
```

- **镜头畸变必须在 Pass 3 最前面**。它的输出是下游模糊链和合成的输入，这样整条管线天然对齐；
  放到合成 Pass 里做，Bloom 和模糊图按原坐标算，会和画面错位。
- **LOG 解码必须在合成 Pass 最前面**。素材还是 LOG 编码时，曝光和白平衡这些线性运算的前提不成立。
- 关掉的功能靠 `shader_feature_local` 在编译期消失（CURVE / SECONDARY / LUT / SIXCURVE）。
- 细节层和两条模糊链都按需构建，参数为 0 时整趟跳过。

**色彩空间约定**：曝光、白平衡、Bloom、色调映射、**校色矩阵**在**线性空间**做；
色阶、Lift/Gamma/Gain、对比度、饱和度、曲线、LUT 转到 **gamma 空间**做。
Shader 末尾必须 `GammaToLinearSpace` 还原，因为写回 sRGB RT 时 Unity 会自己编码。

### AI 蒙版（可选依赖）

`com.unity.sentis@2.1.3`（**2.x 才支持 2022.3，1.3~1.4 要求 2023.2**）。
模型在 `Assets/GameAssets/Models/`，都核实过可商用：
IS-Net / U²-Net（Apache 2.0）、MiDaS（MIT）。RobustVideoMatting 是 GPL-3.0，不要用。

所有相关代码包在 `#if LOVE_SENTIS` 里，`SentisDefineSetup` 自动开关这个宏。
**检测包是否安装要用 `PackageManager.PackageInfo`，不能用 `AppDomain.GetAssemblies()`**——
程序集是按需加载的，没代码碰过 Sentis 类型时它根本不在应用域里。

结论：深度估计（MiDaS）不适合抠主体，**分割模型（IS-Net）才是对的工具**。

## 反复踩到的坑

这些都不会在编译期报错，只在运行时暴露：

- **`Graphics.Blit` 绝不能在 `OnGUI` 里调用。** IMGUI 正在往窗口渲染目标里画时切换
  `RenderTexture.active`，GUI 状态会乱掉，表现为边缘干净的黑块、图像画到 UI 上、裁剪失效。
  编辑器窗口里的渲染/回读/缩略图/导出一律排队到 `Update()`，OnGUI 只画已经渲好的贴图。
- **`EditorSceneManager.NewScene` 会卸载未使用资源**，之前拿到的资产引用变成 Unity 的"假 null"：
  `== null` 为真、`.gameObject` 取不到，**但赋值给序列化字段时 instanceID 仍能解析出正确的 GUID**。
  于是磁盘上的 YAML 看着完全正常，只有部分代码路径失效。`NewScene` 之后必须重新 `LoadAssetAtPath`。
- **`PrefabUtility.SaveAsPrefabAsset` 的返回值在 batchmode 下不可靠**，一律保存后重新从磁盘读。
- **batchmode 里所有 `EditorUtility.DisplayDialog` / `RevealInFinder` 都要用
  `Application.isBatchMode` 跳过**，否则进程会一直卡着等人点。
- **分支和循环内 `tex2D` 的隐式梯度是未定义的**，要用 `tex2Dlod` 显式指定 lod。
- **`GUILayoutUtility.GetRect` 在 Layout 事件返回占位矩形**，绘制要判 `Event.current.type == Repaint`。
- **`PopupWindow` 是跨帧的**，回调触发时调用方早已返回，`ref` 参数指向的栈位置失效。
  弹窗类控件要把值直接写回引用类型对象，并且 `BeginChangeCheck` 捕捉不到它们的改动。
- **`Undo.RecordObject` 必须在改动之前调用**，事后记录存进撤销栈的是新状态，Ctrl+Z 回不去。
  拖拽类控件在 `MouseDown` 时登记一次即可。
- **`EditorGUIUtility.currentViewWidth` 在 `BeginArea` 里返回的是整个窗口宽度**，不是面板宽度。
- **PlayerPrefs 会悄悄盖掉 Inspector 配置**。`AudioManager.rememberPlayerSettings` 和
  `VideoPostProcessor.loadOnStart` 关掉时才以 Inspector / 场景为准。
- **渐变插值的起点要先快照**。直接拿被写入的对象当插值基准，每帧基准都在动，
  结果是一条越来越慢、永远到不了终点的曲线。

## 字体

TMP 字体资产是 **Dynamic 图集**模式：运行时遇到哪个汉字烘哪个，不预烘几千字。
正文用 Noto Serif SC（SIL OFL，可随包发布），思源黑体作后备。

换字体：ttf 丢进 `Assets/GameAssets/Fonts/`，改 `MovieGameSetup.cs` 顶部的 `FontTtfPath` /
`FontAssetPath`，跑「单步：重建字体资产」（普通的「生成」有"已存在就返回"的短路，换字体时不生效）。

**不要用 simsun/msyh 等系统商业字体**——本地看效果可以，不能随游戏分发。
