# 离线测试

不开 Unity，直接把 `Assets/` 里的真实源文件编成控制台程序跑。

```bash
cd Tools/offline-tests
dotnet build

dotnet bin/Debug/net8.0/offlinetests.dll --library          # 图片库：排序 / 筛选 / 多选
dotnet bin/Debug/net8.0/offlinetests.dll --arw2 42 128 8    # ARW2 解压（配合 arw2_ref.py）
dotnet bin/Debug/net8.0/offlinetests.dll --stream <视频>     # ffmpeg 常驻解码流的吞吐
dotnet bin/Debug/net8.0/offlinetests.dll <某张.ARW>          # 整张 RAW 解码

# 需要 numpy + pillow，并且要有对照用的图片
python diff_repair.py       # 污点修复找源：和 double 精度的参考实现比
python test_autotone.py     # 自适应起手值：造问题片，量处理前后
python arw2_ref.py          # ARW2 的 dcraw 参考实现（被 --arw2 调用）
```

## 它是怎么做到的

`Shim.cs` 里是一份最小的 UnityEngine / UnityEditor 桩：`Mathf`、`Color`、`Vector2/3/4`、
`Texture2D`、`EditorPrefs` 这些。**桩里每个方法的语义必须和 Unity 一致**，否则验的就不是真实行为了——
比如 `Mathf.RoundToInt` 走的是银行家舍入，写成"四舍五入"的话，环上的采样点会差一个像素。

`csproj` 直接引用 `Assets/` 下的源文件，不是拷贝。所以测的永远是要发布的那份代码。

## 为什么值得

这套东西抓出来过的真 bug：

- 索尼 `WB_RGGBLevels` 的通道顺序读错（画面偏黄，但绿色是对的，肉眼很难断定是白平衡问题）
- ARW2 解码在 `imax == imin` 时下标越界
- 自动色调把曝光和色阶分开算，导致一张本来正常的照片被推到过亮
- `PhotoLibrary` 里"选中"和"载入"耦合，多选时大图不跟着换

共同点是：**看着都对，跑起来才错**。GUI 那半没法自动验，所以能从窗口里分出来的纯逻辑
（`PhotoLibrary`、`ImageRepair` 的找源、`AutoTone`、`SonyRawImporter`）就该分出来验到位。

## 局限

- 只验逻辑和数值，不验画面观感，也不验 IMGUI 的交互
- Shader 另有 `Tools/shadercheck.py`，那个只验 HLSL 能不能编译
- 几个 Python 脚本需要本机有对照图片（`arw_decoded.jpg` 之类），换机器要自己准备
