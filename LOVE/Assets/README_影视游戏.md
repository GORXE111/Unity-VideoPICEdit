# 影视游戏框架 · 参考手册

两块内容长在同一套管线上：

1. **影视游戏**：播一段视频 → 到点弹出选项 → 画面定格 → 玩家选择 → 黑场过渡 → 下一段
2. **调色 / 修图工具链**：同一条后期管线，服务于游戏内视频、编辑器调色台和静态图片修图

Unity 2022.3.62f3 · 内置渲染管线 · Linear 色彩空间 · 旧版 Input Manager

---

## 一、第一次使用

菜单 **Tools → 影视游戏 → 一键搭建全部**，依次完成：

1. 导入 TMP 基础资源（会触发一次程序集重载，重载后**自动继续**，不用再点）
2. 生成中文字体资产并设为 TMP 默认字体 + 全局后备
3. 配置选项按钮底图的九宫格
4. 生成调色材质
5. 生成 `ChoiceButton.prefab` 和 `GameplayUI.prefab`
6. 生成 `Main.unity`，接好全部引用，加入 Build Settings 第 0 位
7. 应用推荐的显示设置（默认窗口化、可调整大小）

> **场景和预制体是代码生成的，不要手改。** 要调布局请改 `Editor/MovieGameSetup.cs`
> 顶部的常数再重跑，否则下次有人重建就把改动冲掉了。

---

## 二、剧情配置 `StreamingAssets/Story/story.json`

运行时读取，**改完直接 Play 就生效**。

```jsonc
{
  "startId": "v1",
  "titleBgm": "can_i_have_the_day_with_you",
  "transitionDuration": 0.35,

  "segments": [
    {
      "id": "v1",
      "title": "桥段 1",                     // 备注名，只用于日志和占位画面
      "video": "v1.mp4",
      "placeholderSeconds": 66.7,            // 视频没导入时占位画面停留几秒

      "bgm": "can_i_have_the_day_with_you",
      "bgmFade": 2.0,

      "grade": "memory_sepia",               // 调色预设名，留空 = 沿用上一段
      "gradeFade": 2.5,                      // 调色渐变时长，0 = 瞬切

      "loopWhileWaiting": false,
      "loopStart": 0,
      "choiceShowTime": -1,
      "waitVideoEndAfterSelect": false,

      "next": "",
      "choices": [
        { "text": "去电影院吧", "next": "v2" },
        { "text": "去网吧吧", "next": "v2" }
      ]
    }
  ]
}
```

### 字段要点

**`choiceShowTime`** — `-1`（或省略）= 视频播完后出选项，此时已定格最后一帧；填正数 = 播到第 N 秒浮出。

**选项出现即暂停** — 不管哪种时机，选项一出现视频就定在那一帧
（`StoryDirector.pauseVideoWhenChoicesShow`，默认开）。

**`loopWhileWaiting`** — 打开后播到结尾跳回 `loopStart` 继续播，循环到玩家选完。
适用于**片尾本身有可循环的待机镜头**。比如「前 8 秒剧情 + 后 3 秒待机」就填 `loopStart: 8`。
开启时 `pauseVideoWhenChoicesShow` 和 `waitVideoEndAfterSelect` 都不生效。

**`bgm`** — 填名字 = 交叉淡入，**和当前同名时什么都不做**（所以连续几段填同一首 = 不间断）；
留空 = 保持不变；`"none"` / `"stop"` = 淡出停止。

**`grade`** — 填预设库里的名字。配合 `gradeFade` 可以做「回忆段逐渐褪成褒色」这类效果。
插值覆盖 75 个连续参数；曲线、开关这类离散量在过半程时切换。

**`choices`** — 目前最多 2 个（`ChoicePanel.maxChoices` 可改）。空数组 = 播完走 `next`。

配置里写了不存在的 id，加载时 Console 会直接指出是哪一段的哪个选项。

### 还没有视频时怎么测

视频文件不存在时自动切到**占位模式**：顶一张带标题和进度条的画面，按 `placeholderSeconds`
走完这一段，然后照常出选项、走分支。BGM、黑场、`choiceShowTime`、Loop 全都真的在跑。
视频丢进 `StreamingAssets/Videos/` 后自动切回真视频，**JSON 一个字都不用改**。

---

## 三、素材放哪

| 目录 | 内容 | 出包后可替换 |
|---|---|---|
| `StreamingAssets/Videos/` | 视频，H.264 + AAC 的 mp4 | 是 |
| `StreamingAssets/Story/` | `story.json`、`grade.json` | 是 |
| `StreamingAssets/Story/Grades/` | 调色预设库 | 是 |
| `Resources/Audio/BGM/` | BGM | 否 |
| `GameAssets/Audio/SFX/` | UI 音效 | 否 |
| `GameAssets/Models/` | AI 模型（onnx） | 否 |

各段视频分辨率保持一致，切段时不用重建 RenderTexture，过渡更干净。

---

## 四、代码结构

```
Scripts/
  Core/ScreenSetup.cs         窗口模式、按桌面算初始尺寸、F11 全屏
  Audio/AudioManager.cs       BGM 交叉过渡 + 分轨音量
  Video/VideoScreen.cs        VideoPlayer → RenderTexture
  Video/VideoGradeSettings.cs 调色参数（纯数据 + Lerp）
  Video/VideoGradeRenderer.cs 调色渲染核心（普通类，不依赖场景）
  Video/VideoPostProcessor.cs 视频后处理 + 直方图 + 示波器
  Video/GradePresetStore.cs   预设读写，编辑器和运行时共用
  Story/StoryData.cs          剧情 JSON 结构 + 配置校验
  Story/StoryDirector.cs      总导演，玩法流程 + 按段调色渐变
  UI/                         标题界面、选项面板、占位画面、黑场、运行时调色面板
Editor/
  MovieGameSetup.cs           一键搭建
  BuildTool.cs                打包 + 打包前体检
  VideoGradeWindow.cs         调色台
  PhotoGradeWindow.cs         修图台
  GradeSettingsGUI.cs         两个窗口共用的参数界面
  ColorWheelGUI.cs            色轮控件
  ColorCheckerSolver.cs       24 色卡最小二乘求解
  CubeLutIO.cs                .cube 读写与烘焙
  AiMaskGenerator.cs          Sentis 推理（条件编译）
GameAssets/Shaders/
  VideoGrade.shader           Pass 编排
  VideoGradeCommon.cginc      采样核、色彩基础、色调映射
  VideoGradeLog.cginc         LOG 解码 + 校色矩阵
  VideoGradeColor.cginc       调色本体 + LUT + 六条曲线
  VideoGradeMasks.cginc       Power Window + HSL 限定器
  DepthRefine.shader          联合双边上采样，精修 AI 蒙版边缘
```

### 画面为什么能定格在最后一帧

`VideoScreen` 用 **RenderTexture** 输出而不是 `CameraNearPlane`。RenderTexture 在视频停下来之后
仍保留最后一次渲染的内容，配合"播完只 `Pause()` 不 `Stop()`"，画面就停在最后一帧。

### UI 自动寻址

`GameplayUIRoot` 把面板内部的部件集中暴露出来，`StoryDirector` / `VideoScreen` /
`VideoPostProcessor` 在自己引用为空时会自动去取。所以 `GameplayUI.prefab` 拖到任何新场景都能直接用。

选项层挂了 `AspectFollower`，抄视频的 `AspectRatioFitter` 比例——窗口被拉成非 16:9 时按钮不会掉进黑边。

---

## 五、音频

双 AudioSource 轮换实现交叉过渡，切歌时不会有静音间隙。淡入淡出走 `unscaledDeltaTime`，
`Time.timeScale = 0` 时也不卡住。

四条独立音量，**BGM 和视频音量互不影响**：

| 字段 | 默认 |
|---|---|
| masterVolume | 1.0 |
| **bgmVolume** | **0.5**（压低以免盖住台词） |
| **videoVolume** | 1.0 |
| sfxVolume | 1.0 |

运行中直接在 Inspector 里拖就能听到变化。

> **`rememberPlayerSettings` 开着时，Awake 会用 PlayerPrefs 覆盖上面这些值**，
> Inspector 改了跟没改一样。开发期保持关闭。做玩家设置界面时再打开，
> 滑条绑到 `AudioManager.Instance.BgmVolume` 这类**属性**（属性会自动应用+存档，改字段不会）。

---

## 六、调色与修图

### 三个入口

**调色台**（`Tools → 影视游戏 → 调色台`）— 停靠到 Game 视图旁边，进 Play 实时调。

**修图台**（`Tools → 影视游戏 → 修图台`）— 同一条管线处理静态图片。导入单张或整个文件夹，
大预览可缩放平移，底部胶片条切换，批量导出。不需要场景，也不需要进 Play。

**运行时面板**（游戏内 `F1`）— 正式包默认禁用，编辑器和开发版可用。

### 修图台快捷键

| 键 | 作用 |
|---|---|
| 空格 + 拖拽 | 抓手平移 |
| `Ctrl+0` / `F` | 适应窗口 |
| `Ctrl+1` / `1` | 100% 原像素 |
| **按住 `\`** | 临时看原图 |

布局的分隔条可拖拽，尺寸存 EditorPrefs 跨会话保留。

### 裁剪、拉直、旋转

工具栏「裁剪」进入裁剪模式。**这时画布故意显示未裁剪的整幅**——看不见要切掉什么，
就没法判断裁得对不对。框外压暗，框内画三分线，八个控制点可拖，框内拖动整体平移。

比例预设（1:1 / 4:3 / 3:2 / 16:9 / 9:16 / 2.39:1）在参数栏「裁剪与旋转」里，
旁边还有 90 度旋转、水平/垂直翻转、拉直（±45°）。

几何变换跑在**整条管线的最前面**，所以暗角、Power Window、颗粒都跟着新构图走——
和 Camera Raw 一致，裁完图暗角在新的画面中心，而不是原图中心。

> 色卡标定的四角存在**源图** uv 里，画布显示的却是变换后的画面。两者同时开会对不上，
> 界面上会给出警告。先标定，再裁剪。

### 白平衡吸管与自动色调

**白平衡吸管**：工具栏开启后点画面上一处本该是中性灰的地方，反解出色温和色调。
取 5×5 的平均（单像素的噪点足以让色温差出几百 K），取一次就自动退出。

反解用的是粗到细的数值搜索而不是解析求逆——正向映射里色温经过一个带条件分支的偏移、
色调又经过一条二次曲线，复合之后没有干净的闭式逆。参数只有两个、范围只有 ±1，
扫五轮就收敛到肉眼分辨不出。

> 吸管取的是**调色之前**的源像素。素材开了 LOG 解码或色卡校色矩阵时结果会偏，
> 因为那两步排在白平衡之前而这里没有复现。

**自动色调**：只改曝光和输入黑白点三项，别的一律不碰。曝光把中位亮度推到中级灰，
色阶取 0.2% / 99.8% 两端分位数（不是最小最大值——一两个坏点就能把曲线拽歪）。
统计走 512×512 的缩略图，6100 万像素直接 `GetPixels` 会分配将近 1GB。

### 索尼 ARW

修图台可以直接导入 `.arw`，和 png/jpg 一样拖进去就行。

解码流程：TIFF 目录树 → 找拜耳原始数据 → 黑白电平归一化 → 相机白平衡 →
双线性去马赛克 → 相机色彩矩阵 → sRGB 编码。裁到 `DefaultCropSize`（传感器边上
那圈遮光像素不裁掉会是一条黑边）。

| 选项 | 说明 |
|---|---|
| 半尺寸导入 | 直接用 2×2 拜耳块合成，**不插值，反而比全尺寸更干净**。6100 万像素建议开 |
| 自动曝光归一化 | 把高光推到 0.75。只是一个标量不是曲线，关掉就是相机原始电平 |
| 套用相机色彩矩阵 | 内置 ILCE-7RM4 的系数。要准确颜色就关掉它，用色卡自己解一个 |

**限制**：只支持**未压缩** ARW。压缩 ARW（有损 / 无损）用的是索尼私有格式，
手上没有样本可验证，遇到时会明确报错并退回机内 JPEG 预览。相机上把
「RAW 文件类型」改成「未压缩」即可。

缩略图走机内 JPEG 预览而不是全解码——一张 6100 万像素的 ARW 全解要十秒，
而缩略图只有 128 像素。

> 已验证：ILCE-7RM4A 未压缩 14bit，和机内 JPEG 逐通道比对，
> R/G 与 B/G 比值误差 1% 左右（剩下的差异是机内那条创意曲线）。

### 参数

| 分组 | 内容 |
|---|---|
| **预设库** | 存/读 `Story/Grades/*.json`，名字可直接填进 story.json |
| **素材解码** | LOG 编码（S-Log3 / V-Log / C-Log3 / LogC3 / D-Log）、色卡校色矩阵 |
| **画质提升** | 降噪、通透度 + 半径、纹理、**去朦胧**、锐化 + 只锐对焦区 |
| 曝光与白平衡 | 曝光、色温、色调、色调映射（无 / Reinhard / Filmic / ACES） |
| 色阶 | 输入黑白点、中间调、输出黑白点 |
| **色轮** | Lift / Gamma / Gain / Offset，各带主控滑条，双击回中 |
| 反差与色彩 | 对比度、高光、阴影、饱和度、肤色保护、色相、色调分离 |
| 曲线 | 主曲线 + R/G/B |
| **裁剪与旋转** | 比例预设、90 度旋转、水平/垂直翻转、拉直、裁剪框数值 |
| **HSL 八色带混合器** | 红/橙/黄/绿/青/蓝/紫/品红 × 色相/饱和度/明亮度，分三页 |
| **六条曲线** | 色相vs色相 / 色相vs饱和 / 色相vs亮度 / 亮度vs饱和 / 饱和vs饱和 / 饱和vs亮度 |
| **AI 蒙版用法** | 背景虚化、反选、边缘收缩扩张、二级校色叠加 |
| 二级校色 | Power Window（椭圆 / 矩形 / **线性渐变**）∩ HSL 限定器 |
| 效果 | Bloom、模糊、**镜头畸变**、暗角、颗粒、色差、抖动、**斑马纹** |
| 监看 | 直方图、波形图、分量波形、矢量示波器、分屏对比 |

调二级校色时**必须开「显示遮罩」**看边界，纯靠猜调不出来。

**六条曲线的恒等状态是 y=0.5 的水平线，不是 y=x**——它们表达的是「增减量」而不是「映射后的值」。

### 色轮怎么工作

RGB 和轮盘位置之间是**可逆投影**（R 在 0°、G 在 120°、B 在 240°），三通道之和恒为 0。
所以色轮**纯粹改色偏、不动亮度**，亮度归主控滑条。展开「显示数值」用滑条微调时，
色轮手柄会同步跟着走。

### LUT

**导入** `.cube` → Texture3D，支持 3D 和 1D，也处理非 [0,1] 定义域。有强度滑条。

**导出**把当前参数烘成 33³ 的 `.cube`。做法是拿单位 LUT 条带图过一遍管线再读回。
烘之前会**把暗角、颗粒、模糊、畸变、蒙版这些依赖坐标的效果清零**——LUT 只能表达 RGB→RGB，
塞进去没有意义，而且会让导出的 LUT 带一圈莫名其妙的暗角。

### 色卡校色

载入一张拍了 24 色卡的照片 → 勾「标定模式」→ 拖四角框住色卡（**黄色手柄是左上角**）→ 解算。

网格用双线性插值定位，**色卡有透视变形也能对上**；采样半径按格子尺寸自动算。

残差 < 0.02 很好，< 0.06 可用，更大说明角点没对准或曝光有问题。

> 求解在**线性空间**做（实测值和参考值都先过 sRGB→线性），因为 shader 里矩阵是在
> LOG 解码之后的线性空间应用的。在 gamma 空间拟合出来的矩阵拿去线性空间用，
> 结果是错的而且很隐蔽。

### AI 主体蒙版

需要 `com.unity.sentis`。修图台里选模型 → 生成蒙版 → 自动跑联合双边精修。

| 模型 | 输入 | 适合 | 授权 |
|---|---|---|---|
| **IS-Net 分割** | 1024px | **人物边缘、发丝，首选** | Apache 2.0 |
| U²-Net 人像分割 | 320px | 人像，兼容性更稳 | Apache 2.0 |
| MiDaS 深度 large/small | 384/256px | 场景远近，**不适合抠主体** | MIT |

拿到蒙版后可做：背景虚化（伪景深）、只锐主体、主体单独调色（和二级校色取交集）。

### 几个实现约定

**色彩空间**：曝光、白平衡、Bloom、色调映射、校色矩阵在**线性空间**做；
色阶、LGGO、对比度、饱和度、曲线、LUT 在 **gamma 空间**做。

**色温色调**走 CIE xy → LMS 的标准白平衡算法，不是简单乘 RGB，调暖时蓝通道不会被压死。

**LOG 解码在管线最前面**——素材还是 LOG 时，曝光和白平衡这些线性运算的前提不成立。

**镜头畸变在细节 Pass 最前面**——它的输出是下游模糊链的输入，这样整条管线天然对齐。

**按需构建**：Bloom、模糊、细节层在参数为 0 时完全不跑；曲线 / 二级 / LUT / 六条曲线 /
HSL 用 shader 关键字在编译期消失。

**HSL 八色带用三角基权重**：每个色带在自己的中心色相处权重为 1，到左右相邻中心衰减到 0，
相邻两条恒好凑成 1。所以色带交界处不会忽强忽弱。八个中心不等距（橙黄挤在前 1/6），
和 Camera Raw 一致——人眼对肤色那一段最敏感。

**接近中性灰的像素不参与 HSL 调整**：`RgbToHsv` 对灰色返回的色相基本是噪声，
不加这道闸门的话灰墙、雪地、白衬衫会被判进某个色带然后染上颜色。

**去朦胧走大气散射模型**：`I = J·t + A·(1-t)`，把 A 取成局部模糊值（那正是糊在景物上的
那层纱），反解得 `J = (I-A)/t + A`。形式上是绕局部均值的对比度拉伸，但强度由透射率
自适应——雾越浓（暗通道越亮）拉得越狠。用模糊值而不是逐像素窗口最小值，是为了绕开
暗通道先验典型的边缘光晕。它**不带饱和度补偿**，通常要配合饱和度一起调。

**几何在最前，白平衡在其后**：裁剪 / 拉直 / 旋转是独立的 Pass 5，跑在细节和调色之前。
C# 端的 `VideoGradeSettings.DisplayUvToSource` 必须和它逐步对应——画布上的吸管和
色卡角点都靠它换算，两边一旦不同步，取色会取到别处的像素，而且偏移往往不大、不容易发现。

---

## 七、窗口与分辨率

默认**窗口化**，可调整大小。初始尺寸由 `Systems/ScreenSetup` 按桌面算：
取能塞进桌面 80% 区域内的最大 16:9 窗口（2560×1440 桌面 → 2048×1152）。

`F11` 或 `Alt+Enter` 切全屏。窗口状态存 PlayerPrefs；换显示器导致尺寸超出桌面时自动退回默认值。

---

## 八、字体

**Dynamic 图集**模式：运行时遇到哪个汉字就烘哪个，包里没有几十 MB 的静态图集。

正文用 **Noto Serif SC**（SIL OFL，可随包发布），思源黑体作后备。采样点数 90、边距 9——
宋体这类衬线字横画很细，采样太低 SDF 会把细横糊掉。

**换字体**：ttf 丢进 `GameAssets/Fonts/`，改 `MovieGameSetup.cs` 顶部的路径常数，
跑 **「单步：重建字体资产」**（普通的「生成」有"已存在就返回"的短路，换字体时不生效）。

> 别用 simsun / msyh 等系统商业字体，本地看效果可以，不能随游戏分发。

---

## 九、打包

`Tools → 影视游戏 → 打包 Windows 版本`，输出到工程同级的 `Build/Windows/`。

打包前体检，这几项打完才发现的话排查很费时间：

| 检查 | 级别 |
|---|---|
| 主场景存在且在 Build Settings 里 | 阻断 |
| `story.json` 存在 | 阻断 |
| **story.json 引用的每个视频文件都在** | 阻断 |
| TMP 中文字体资产存在 | 阻断 |
| 调色材质、`grade.json` | 警告 |

打完会回验输出目录的 StreamingAssets，报告视频个数、总体积、剧情表和调色参数是否带到。

---

## 十、常用扩展点

**从中途某段开始调试**：`StoryDirector.debugStartId` 填段 id，Play 直接从那段开始并跳过标题。

**监听剧情事件**：

```csharp
director.onSegmentStart   += seg => Debug.Log($"开始播 {seg.id}");
director.onChoiceSelected += (seg, index) => Debug.Log($"{seg.id} 选了第 {index} 个");
director.onStoryFinished  += () => Debug.Log("流程结束");
```

**跳过当前视频**：`videoScreen.SkipToEnd()`

**改选项布局**：`GameplayUI.prefab` 的 `ChoiceLayer/Container`，或改 `MovieGameSetup.cs` 顶部的
`BtnWidth` / `BtnHeight` / `BtnSpacing` / `BtnBottom` / `BtnFontSize` 后重建。

**标题界面文案**：`TitleLayer` 上 `TitleScreen` 组件的 `gameTitle` / `subtitle` /
`startText` / `quitText`。把 sprite 拖进 `Logo` 子物体就显示图形标题。

**一键胶片化**：两个窗口工具条上的「胶片化」按钮，一次配齐颗粒、暗角、色差、桶形畸变、
只锐对焦区、背景虚化等十几项——**「只锐对焦区」+「背景虚化」是去 AI 感的核心**，
因为 AI 图最大的破绽是全画面等锐、没有焦平面。

---

## 十一、还没做的

- **选项按钮的装饰图标和气泡尾巴** —— 缺美术素材，需要透明背景 PNG
- **旁白 / 对白层** —— 参考图里按钮下方那行字
- **压缩 ARW** —— 索尼私有格式，缺样本无法验证
- **选图工作流** —— 单张移除 / 多选 / 星级标记 / 排序筛选 / 同步设置到多张
- **局部调整画笔** —— 手绘蒙版，和 Power Window / AI 蒙版互补
- **历史记录面板** —— 现在只有 Ctrl+Z，没有快照和步骤列表
- **倒计时圆环** —— `ChoicePanel.Show()` 里预留了接口
- **玩家音量设置界面** —— `AudioManager` 的属性接口已就绪
- **独立可执行的修图 App** —— 现在只有编辑器版
