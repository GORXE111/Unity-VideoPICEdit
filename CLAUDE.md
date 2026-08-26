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

### 运行时/编辑器边界

`Assets/Editor/` 里的代码**不进包**，`Assets/Scripts/` 里的进包。
独立修图程序（`Tools/修图程序/打包 Windows`）要用的都在后者。

```bash
python Tools/checkruntime.py     # 按出包条件编一遍运行时代码
```

**Unity 在编辑器里编 `Assembly-CSharp` 是带 UnityEditor 引用的**，
所以运行时代码碰了编辑器 API 在编辑器里发现不了。这个脚本摘掉那些引用和
`UNITY_EDITOR` 宏再编一遍。文本检查抓"光引了命名空间"，编译抓"真调了 API"，
两道合起来才盖全——只靠编译的话，没被用到的 `using UnityEditor;` 会漏过去
（包的编辑器程序集在 `UnityEditor.*` 下声明了类型，命名空间是存在的）。

编辑器专有的能力走 `Love.Tools.AppHost` 的委托注入，不要用 `#if UNITY_EDITOR`——
条件编译等于把边界交给宏去守，委托注入让编译器自己守。

### 离线测试

`Tools/offline-tests/` 用一份最小的 UnityEngine 桩，把 `Assets/` 里的**真实源文件**
编成控制台程序跑。引用的是源文件不是拷贝，所以测的永远是要发布的那份代码。

```bash
cd Tools/offline-tests && dotnet build
dotnet bin/Debug/net8.0/offlinetests.dll --library    # 图片库逻辑
python diff_repair.py                                 # 修复找源，和参考实现比
python test_autotone.py                               # 自适应起手值，量处理前后
```

**桩里每个方法的语义必须和 Unity 一致**，否则验的就不是真实行为。
比如 `Mathf.RoundToInt` 走的是银行家舍入。

能从窗口里分出来的纯逻辑就分出来（`PhotoLibrary`、`ImageRepair` 的找源、`AutoTone`、
`SonyRawImporter`）——GUI 那半没法自动验，分出来的部分就该验到位。

### HLSL 也能离线编译

```bash
python Tools/shadercheck.py                 # 默认 VideoGrade.shader
python Tools/shadercheck.py MaskBrush.shader
```

把 `CGPROGRAM..ENDCG` 抽出来（`CGINCLUDE` 块拼到前面），补上 Unity 会注入的那批宏，
交给 Windows SDK 的 `fxc.exe` 编顶点和片元两个入口。带 `shader_feature` 的 Pass
会把关键字全开和全关各编一遍。

- **必须带 `/Gec`**（向后兼容）。Unity 自己的 `UnityShaderVariables.cginc` 里有全局
  `half` 变量，SM4.0 严格模式下直接报错，看起来像是你的 shader 写错了。
- fxc 有 arm64 / x64 / x86 三份，`find` 出来的第一个可能是 arm64，跑不起来。

**这验的是能不能编译，不验画面对不对。** 着色逻辑仍然要进 Unity 看。

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
VideoGrade.shader    9 个 Pass，算法在 4 个 .cginc 里
MaskBrush.shader     手绘笔刷，靠 BlendOp Max/Min 直接画进蒙版
GradeMask            蒙版组与部件的数据（Lightroom 那套结构）
ImageRepair          污点修复 / 仿制图章（编辑器侧），修补记录可重放
PhotoLibrary         图片库：排序 / 筛选 / 评级 / 多选，纯逻辑无 GUI 依赖
PhotoEditStore       逐图的参数 / 修补 / 评级 / 快照落盘到 UserSettings/
ExportPreset         导出配置 + 命名 / 尺寸 / 重名的纯逻辑（可离线测）
ImageRepair.shader   修补一处：仿制 + 色调补偿
AutoTone             按直方图给一组起手曝光与色阶
SkyDetect            天空检测：从顶边漫延，纯 CPU，可离线测
DenoiseTiler         大图切块：读带上下文、写不重叠，可离线测
NoiseEstimate        噪声估计：二阶差分核，分块取低分位，可离线测
AiDenoiser           SCUNet 降噪推理（编辑器侧，#if LOVE_SENTIS，分步跑）
SkyMaskBuilder       把漫延结果搬到显示空间做成贴图（编辑器侧）
TextStamp            文字水印：字形 UV 拼四边形，GL 画进贴图
WhiteBalancePicker   从一个中性灰像素反解色温色调（数值搜索，不是解析求逆）
SonyRawImporter      索尼 ARW 解码（编辑器侧，未压缩 + ARW2 有损压缩）
GradeCanvas          预览画布：棋盘底 / 缩放平移 / 硬裁剪，只摆图不渲染
GradeToolbar         会自己收纳的工具栏：先声明、后测量、再绘制
GradeSkin            三个窗口统一的配色、尺寸与样式
IGradeGui            参数界面的控件层，编辑器 / 独立程序各一份实现
GradeSettingsGUI     97 个参数控件，三处共用（运行时，不碰编辑器 API）
FfmpegTool           ffmpeg 定位与命令行拼装（解码、编码、单帧抓取、ffprobe）
```

`VideoGradeRenderer.Render(任意Texture, 任意RenderTexture, settings, options)`。
抽出来正是为了让编辑器的修图台复用同一条管线处理静态图片。

五个消费方：`VideoPostProcessor`（场景组件）、`VideoGradeWindow`（调色台）、
`VideoStationWindow`（视频台）、`PhotoGradeWindow`（修图台）、
`VideoGradePanel`（运行时面板，正式包默认禁用）。

**视频台预览和导出都走 ffmpeg**，`VideoPlayer` 只是备选（准备超时 6 秒会自动倒过去）。
预览用 `VideoFrameStream`——一个常驻解码进程顺序吐裸 RGBA 帧。

**绝不要「要哪一帧就起一个进程抓一帧」**：实测 1080p 上每帧起进程要 145~180ms，
常驻进程顺序读只要 6.4ms，差二十倍。前者连 7fps 都跑不到。
定位策略是：顺着读 → 前跳 24 帧以内读掉扔掉 → 更远或往回才重开进程。

参数界面 97 个控件，由 `Scripts/Photo/Gui/GradeSettingsGUI.cs` 一份实现供
调色台、修图台、独立程序共用。它不碰任何编辑器 API，控件全走 `IGradeGui`：
编辑器侧 `EditorGradeGui` 是 `EditorGUILayout` 的薄壳，独立程序侧
`RuntimeGradeGui` 是纯 IMGUI。加一个参数仍然是加一行。

**蒙版组**：一个组 = 若干部件（加/减/交）+ 一组自己的调整。部件数量不定，
在 shader 里展不开，所以一个部件跑一趟 Pass、在两张单通道图之间乒乓累积。
**那两张图必须是 `ReadWrite.Linear`**——蒙版是数据不是颜色，走 sRGB 的话
写进去 0.5 读出来就不是 0.5。N 个组 = 2N 趟全分辨率 Pass。

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

**色彩空间约定**：曝光、白平衡、**去朦胧**、Bloom、色调映射、**校色矩阵**在**线性空间**做；
色阶、Lift/Gamma/Gain、对比度、饱和度、曲线、**HSL 八色带**、LUT 转到 **gamma 空间**做。
Shader 末尾必须 `GammaToLinearSpace` 还原，因为写回 sRGB RT 时 Unity 会自己编码。

**几何 Pass（裁剪 / 拉直 / 90 度旋转 / 翻转）跑在整条管线最前面**，所以暗角、
Power Window、颗粒都按裁剪后的构图走——和 Camera Raw 一致。
`VideoGradeSettings.DisplayUvToSource` 是它的 C# 镜像，画布上的吸管和色卡角点靠它把
屏幕坐标反解回源图；**改一边就必须改另一边**，否则取色会取到别处，而偏移往往不大、
肉眼不容易发现。

**管线全程 ARGB32**，所以源图给到 8bit sRGB 就够——RAW 解码直接输出 `RGBA32` 不算浪费，
更高位深也会在第一次 Blit 时被量化掉。真要吃满 14bit 得把所有临时 RT 换成 ARGBHalf，
那对 6100 万像素的素材是每张几百 MB。

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
- **`GUILayout` 排不下时不会报错，只会把控件默默挤没**。工具栏这种固定宽度的横排
  必须自己测量：三个窗口的工具栏都走 `GradeToolbar`，宽度不够就按优先级撤进 `⋯` 菜单。
- **画布的键盘快捷键要给文本框让位**。`OnGUI` 里画布排在参数栏之前，不判
  `EditorGUIUtility.editingTextField` 的话，在搜索框里敲 "f" 会被当成「适应窗口」。
- **`PopupWindow` 是跨帧的**，回调触发时调用方早已返回，`ref` 参数指向的栈位置失效。
  弹窗类控件要把值直接写回引用类型对象，并且 `BeginChangeCheck` 捕捉不到它们的改动。
- **`Undo.RecordObject` 必须在改动之前调用**，事后记录存进撤销栈的是新状态，Ctrl+Z 回不去。
  拖拽类控件在 `MouseDown` 时登记一次即可。
- **`EditorGUIUtility.currentViewWidth` 在 `BeginArea` 里返回的是整个窗口宽度**，不是面板宽度。
- **`Texture2D.GetPixels` 在大图上是内存炸弹**。6100 万像素 = 将近 1GB 的 `Color[]`。
  要统计就先 Blit 到小 RT 再回读，别直接取全图。
- **索尼的 `WB_RGGBLevels` 顺序是 R,G1,G2,B**，蓝色在第四位。当成 R,G,B,G2 读的话画面
  明显偏黄，但绿色是对的，第一眼不容易断定是白平衡问题。
- **数组类型的新字段要防 null**。`hslHue` 这些是后加的，早先存的 `grade.json` 里没有，
  `JsonUtility` 读进来可能是 null 或长度不对。
- **ffmpeg 的裸帧第一行是画面顶部，Unity 贴图数据第一行是底部**。解码和编码两头
  都要 `vflip` 才抵消。只翻一头的话画面在管线里是倒的——暗角这种中心对称的效果
  看不出来，但裁剪、渐变窗口、镜头畸变会全部上下颠倒。
- **子进程的 stderr 必须有人读**，否则管道写满时 ffmpeg 会卡死在那儿不退出。
- **不要在 `OnGUI` 里同步等子进程**。时间轴的 `MouseDrag` 一次拖拽甩出几十个事件，
  每个都真去定位的话请求会越堆越多。正确做法是只登记「想去哪一帧」，
  在 `update` 里一拍最多兑现一次、且只兑现最后那个请求，两次之间还要留出间隔
  让编辑器能处理输入和重绘。
- **播放推进不要「补齐」落后的帧**。按累积时间一次步进多帧，会变成
  「越慢越要多解码」的正反馈，一旦跟不上就再也追不回来。跟不上就丢时间。
- **`VideoPlayer.sendFrameReadyEvents` 官方文档写明会带来显著 CPU 开销**
  （要求解码线程每帧和主线程同步）。改成在 `update` 里轮询 `player.frame`。
- **别无条件 `Repaint()`**。渲染函数提前返回时脏标记清不掉，就变成每拍都重绘的空转，
  整个编辑器跟着发涩。只在真有变化时重绘。
- **风格化必须排在蒙版之后**。暗角、颗粒这些作用于整幅成片，放在蒙版之前的话，
  一块提亮天空的蒙版会连暗角一起提亮。Camera Raw 也是这个顺序。
- **窗口里的 `Dictionary` 字段活不过程序集重载**。Unity 序列化不了它，也就是说
  改一行 C# 或者关掉窗口，里面的东西全没。凡是用户会心疼的数据都得落盘
  （`PhotoEditStore` 写 `UserSettings/`，那个目录已经在 .gitignore 里）。
- **批量写文件时，重名判断不能只看磁盘**。同一批里两条算出同一个名字时文件还没落地，
  第二条会直接盖掉第一条。除了 `File.Exists` 还得记一份"这批已经用掉的名字"。
- **字符串要比内容不能比引用**。`ReferenceEquals(a.path, b.path)` 对两个内容相同的
  string 也可能是 false，而且不会报错——只会让某个分支永远走不到。
- **`JsonUtility` 不保留 null 引用**。`[Serializable]` 类字段存进去是 null，
  读出来会变成一个默认构造的对象。要区分"没有"和"默认值"，就得另加一个 bool 标志。
- **自动算参数时，管线上互相影响的两步要一起解**。`AutoTone` 里曝光排在色阶前面，
  但色阶的拉伸会把中位数再推一次，分开算的结果是"一张本来正常的照片被推到过亮"。
  迭代几轮反解即可。
- **验证的判据本身也可能是错的**。`AutoTone` 一开始拿"中位数靠近中级灰"当唯一判据，
  卡掉了一个正确的结果——一张只是发灰的片子，正确处理是抬黑位加反差，中位数反而往下走。
  出图看一眼比多调几轮阈值管用。
- **能从窗口里分出来的纯逻辑就分出来**。`PhotoLibrary`（排序/筛选/多选）和
  `ImageRepair`（找取样源）都没有 GUI 依赖，所以能用 `rawtest` 那套桩离线跑测试。
  GUI 那半没法自动验，分出来的部分就该验到位。
- **「选中」和「载入」必须分开**。多选时 `Current` 已经先动了，
  如果载入函数还拿 `Current` 判断"要不要换图"，就永远相等、图片反而不换。
  要单独记住"大图现在载的是哪一条路径"。
- **修补类工具的羽化要从笔尖边缘往外扩，不能往里收**。往里收的话污点的外圈补不到，
  会留下一圈残影。
- **启发式搜索的结果对浮点精度敏感**。`ImageRepair` 找取样源时，环上的采样点由
  `cos/sin` 定，碰上 `cos(60°)×33 = 16.5` 这种正好落在舍入边界的情况，
  float 和 double 会差一个像素，名次可能翻。所以这类算法的差分测试**不能比坐标是否逐位相同**，
  要比"选中的那个点按参考实现的尺子量够不够好"。
- **`IList<T>` 是不变的**。`List<RenderTexture>` 递不进 `IList<Texture>` 的参数，
  笔刷那批贴图只能直接存成 `List<Texture>`。
- **不要用 `Directory.GetFiles(..., AllDirectories)` 找可执行文件**。
  WinGet 的包目录实测有近 12000 个文件，递归一次 369ms，而这种查找往往被 `OnGUI` 间接调到。
- **ARW2 解码里 `imax == imin` 会多读一个增量**，`bit` 走到 128、下标越过整块。
  合法码流不会这样，但坏文件会，两个字节都得判界。dcraw 靠 `malloc(raw_width+1)`
  的越界读糊过去，那读的是未初始化内存、输出不确定，不能照抄。
- **RAW 解码相关的 LibRaw / DNGlab 都是 LGPL**，不要抄代码。格式本身不受版权保护，
  照着规范自己写即可（和当初拒掉 GPL 的 RobustVideoMatting 是同一条线）。
- **PlayerPrefs 会悄悄盖掉 Inspector 配置**。`AudioManager.rememberPlayerSettings` 和
  `VideoPostProcessor.loadOnStart` 关掉时才以 Inspector / 场景为准。
- **内置的 `GUI/Text Shader` 吃不了顶点色**。它是 `Color [_Color]` + `combine primary`，
  固定管线里那个 primary 是材质颜色，`GL.Color` 完全无效，画出来永远是材质上那个色。
  自己渲字要配自己的材质。
- **天空检测的亮度闸门不能用 luma**。luma 给蓝色的权重只有 0.114，深蓝天顶
  (30,60,160) 算出来才 0.24，会被当成"太暗"整片丢掉。要用 `max(r,g,b)`。
- **`GetPixels32` 的第一行是画面底部**。天空检测是从"顶边"往下漫延的，
  直接把它喂进去等于从地面开始找天空，什么都找不到。和 ffmpeg 裸帧是同一类坑。
- **降噪不能像蒙版那样缩图再放大**。蒙版本来就是低频的，缩了没关系；
  降噪缩一遍等于先把细节丢光再去"救"。必须原分辨率切块过。
- **切块的补边要镜像不能夹边界**。夹边界等于把边缘那列复制一大片，
  模型会把这片假的平坦区当真信号，边上一圈反而更糊。
- **量模型速度必须先预热**。第一次 `run` 里含建会话/图优化的开销，
  SCUNet 在 128px 上首次要 8.9 秒、预热后 576px 才 7.4 秒——不预热会得出
  "小图比大图还慢"这种反直觉结论，进而选错模型。
- **噪声估计要分块取低分位，不能整幅取均值**。草丛砖墙的二阶差分和噪声一样大，
  整幅平均会把纹理当噪声，强度拉满、细节全平。
- **`Assets/Editor/` 里的代码不进包**。想让某段逻辑能出包，它必须在
  `Assets/Scripts/` 下，而且完全不碰 `UnityEditor`。
- **不看子进程退出码的检查是假的**。编译器没跑起来时也没有 `error CS` 行，
  只 grep 输出的话报出来是"通过"——一个永远绿的检查比没有检查更糟。
- **`using UnityEditor;` 光有 using 编得过**。包的编辑器程序集在 `UnityEditor.*`
  下声明了类型，命名空间存在。所以查边界不能只靠编译，还得查文本。
- **Win32 文件对话框必须带 `OFN_NOCHANGEDIR`**。不带的话它会改掉进程的当前目录，
  之后所有相对路径都指到别处，而且不报错。
- **IMGUI 出包之后能用**，`EditorGUILayout` 不能。`GUI` / `GUILayout` / `Event` /
  `GUIStyle` 都在 UnityEngine 里，所以编辑器界面的结构能沿用，只要换控件那一层。
- **机械替换要用结构比对验，不能只看编译过没过**。编译只保证类型对，
  保证不了"有没有哪根滑条被换成了别的字段"。`GradeSettingsGUI` 抽接口时
  是把改前改后所有控件的「控件名 + 标签 + 字段 + 范围」抽出来逐个比的。
- **给编辑器 API 抽壳时，壳里一行都不要多做**。抽接口是为了换实现，
  不是趁机改行为；多做一点，窗口的手感就变了，而那种差别很难查。
- **渐变插值的起点要先快照**。直接拿被写入的对象当插值基准，每帧基准都在动，
  结果是一条越来越慢、永远到不了终点的曲线。

## 字体

TMP 字体资产是 **Dynamic 图集**模式：运行时遇到哪个汉字烘哪个，不预烘几千字。
正文用 Noto Serif SC（SIL OFL，可随包发布），思源黑体作后备。

换字体：ttf 丢进 `Assets/GameAssets/Fonts/`，改 `MovieGameSetup.cs` 顶部的 `FontTtfPath` /
`FontAssetPath`，跑「单步：重建字体资产」（普通的「生成」有"已存在就返回"的短路，换字体时不生效）。

**不要用 simsun/msyh 等系统商业字体**——本地看效果可以，不能随游戏分发。
