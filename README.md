# GalgameLOVE

影视游戏（互动影像 / FMV）：**播一段预渲染视频 → 到点弹出选项 → 画面定格 → 玩家选择 → 下一段**。

附带一套完整的调色 / 后期管线，既用于游戏内的视频实时调色，也能当独立修图工具处理静态照片。

Unity 2022.3.62f3 · 内置渲染管线 · Linear 色彩空间

---

## 目录

```
GalgameLOVE/
├── CLAUDE.md          给 Claude Code 看的工程约定
├── README.md          本文件
├── Build/Windows/     打包输出（LOVE.exe）
└── LOVE/              Unity 工程
    └── Assets/
        ├── README_影视游戏.md      详细参考手册
        ├── Scripts/               运行时代码
        ├── Editor/                编辑器工具
        ├── GameAssets/            字体 / 预制体 / Shader / 音效
        ├── Resources/Audio/BGM/   背景音乐
        └── StreamingAssets/
            ├── Story/story.json   剧情配置
            ├── Story/grade.json   调色参数
            └── Videos/            视频文件
```

## 克隆

模型、视频、音频走 **Git LFS**，克隆前先确保装了它：

```bash
git lfs install
git clone https://github.com/GORXE111/Unity-VideoPICEdit.git
```

没装 LFS 直接克隆的话，这些文件会是几百字节的指针文本而不是真实内容。
补救：装好 LFS 后在仓库里执行 `git lfs pull`。

`Tools/fetch-models.sh` 仍然保留——LFS 流量用完时可以用它从上游直接取模型。

## 第一次打开工程

菜单 **Tools → 影视游戏 → 一键搭建全部**。

它会生成中文字体资产、选项按钮预制体、UI 面板预制体、调色材质，以及接好线的 `Main.unity`。
中途会导入 TMP 基础资源并触发一次程序集重载，重载后会自动继续，不用点第二次。

跑完打开 `Assets/Scenes/Main.unity` 直接 Play。

## 改内容不用重新打包

`StreamingAssets` 是原样拷进包里的，出包之后照样能改：

| 想改什么 | 改哪里 |
|---|---|
| 剧情流程、选项文案、跳转 | `StreamingAssets/Story/story.json` |
| 视频 | `StreamingAssets/Videos/*.mp4`（H.264 + AAC） |
| 调色 | `StreamingAssets/Story/grade.json`，或在调色台里改完点保存 |
| 调色预设 | `StreamingAssets/Story/Grades/*.json`，名字可填进 story.json 的 `grade` 字段 |
| BGM | `Resources/Audio/BGM/`（这个要回 Unity 重新打包） |

story.json 的完整字段说明见 `LOVE/Assets/README_影视游戏.md`。

## 工具

菜单都在 **Tools → 影视游戏** 下。

**调色台** — 编辑器窗口，停靠到 Game 视图旁边，进 Play 后实时调色。

- 一级校色：**Lift/Gamma/Gain/Offset 色轮**、色阶、色调映射、色调分离
- 曲线：主曲线 + R/G/B，**HSL 八色带混合器**，另有**六条曲线**（色相vs色相 等）
- 二级校色：Power Window（椭圆 / 矩形 / **线性渐变**）∩ HSL 限定器
- 素材解码：**LOG**（S-Log3 / V-Log / C-Log3 / LogC3 / D-Log）+ **24 色卡校色矩阵**
- 画质：双边降噪、通透度、纹理、**去朦胧**、**只锐对焦区**的智能锐化
- 几何：**裁剪 / 拉直 / 90 度旋转 / 翻转**，跑在管线最前面，暗角跟着新构图走
- 效果：Bloom、**镜头畸变**、暗角、颗粒、色差、抖动
- 监看：直方图、波形图、矢量示波器、分屏对比、**斑马纹**
- **LUT 导入导出**（.cube），**预设库**，一键**胶片化**

**修图台** — 同一条管线用来处理静态图片。导入单张或整个文件夹，
大预览可缩放平移（`F` 适应 / `1` 看原像素 / 空格拖拽 / 按住 `\` 看原图），
底部胶片条切换，布局可拖拽，支持批量导出 PNG/JPG。不需要打开场景，也不需要进 Play。

可视化**裁剪框**（框外压暗 + 三分线 + 八个控制点）、**白平衡吸管**（点中性灰反解色温色调）、
**自动色调**（按直方图给一组起手参数）。

还能直接导入**索尼 ARW**：黑白电平 → 相机白平衡 → 去马赛克 → 相机色彩矩阵 → sRGB。
只支持未压缩 ARW，压缩格式会明确报错并退回机内 JPEG 预览。

装了 `com.unity.sentis` 之后还能用 **AI 主体蒙版**（IS-Net 分割），
拿到干净的人物边缘后做背景虚化、只锐主体、主体单独调色。

**打包 Windows 版本** — 输出到 `Build/Windows/`。打包前会体检：
主场景在不在、story.json 引用的每个视频文件是否都存在、中文字体资产有没有生成。

## 运行时按键

| 键 | 作用 |
|---|---|
| `F11` / `Alt+Enter` | 切换全屏（默认窗口化，初始尺寸按桌面分辨率自动计算） |
| `F1` | 运行时调色面板（正式包里默认禁用，开发版和编辑器里可用） |

## 按剧情段自动切换调色

在调色台调好一套 look、存进预设库，然后在 `story.json` 里给某一段填上：

```jsonc
"grade": "memory_sepia",
"gradeFade": 2.5
```

就能做出「回忆段逐渐褪成褒色」这类效果。渐变覆盖 75 个连续参数。

## 已知限制

- 调色台的实时预览需要进 Play——视频只有播放模式才解码出画
- Unity 免费版的启动 Logo 无法去除
- 视频体积直接进包体，当前四段共 180 MB
