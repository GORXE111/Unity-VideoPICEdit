#!/usr/bin/env bash
# 取回 AI 蒙版用的 ONNX 模型。
#
# 这些权重体积共约 800 MB，其中三个单文件超过 GitHub 的 100 MB 硬上限，
# 所以不进版本库。全部是可公开下载、可商用的第三方模型。
#
# 用法：在仓库根目录执行  bash Tools/fetch-models.sh
#
# 授权（都核实过）：
#   IS-Net / U^2-Net   Apache 2.0   github.com/xuebinqin
#   MiDaS              MIT          github.com/isl-org/MiDaS
# 注意：RobustVideoMatting 是 GPL-3.0，商业项目不要引入。

set -euo pipefail

DEST="LOVE/Assets/GameAssets/Models"
mkdir -p "$DEST"

REMBG="https://github.com/danielgatis/rembg/releases/download/v0.0.0"
MIDAS="https://github.com/isl-org/MiDaS/releases/download/v2_1"

fetch() {
  local url="$1" out="$2" note="$3"
  if [ -f "$DEST/$out" ]; then
    echo "  已存在，跳过  $out"
    return
  fi
  echo "  下载 $out  （$note）"
  curl -fL --progress-bar -o "$DEST/$out" "$url"
}

echo "取回 AI 模型到 $DEST"
echo

# 抠主体首选。输入 1024px，专为高精度细结构（发丝）设计
fetch "$REMBG/isnet-general-use.onnx" "isnet-general-use.onnx" "IS-Net 分割, 170MB, Apache 2.0"

# 备选，纯 CNN，Sentis 兼容性风险更小
fetch "$REMBG/u2net_human_seg.onnx"   "u2net_human_seg.onnx"   "U^2-Net 人像分割, 168MB, Apache 2.0"

# 深度估计。适合场景远近，不适合抠主体
fetch "$MIDAS/model-small.onnx"       "MiDaS-small.onnx"       "MiDaS 深度 small, 64MB, MIT"

echo
read -r -p "还要下载 MiDaS large 吗（397MB，只在需要更精细的深度层次时用）？[y/N] " ans
if [ "${ans:-N}" = "y" ] || [ "${ans:-N}" = "Y" ]; then
  fetch "$MIDAS/model-f6b98070.onnx" "MiDaS-large.onnx" "MiDaS 深度 large, 397MB, MIT"
fi

echo
echo "完成。回到 Unity 等它导入，然后菜单里就有「深度估计（实验）」和修图台的 AI 蒙版了。"
echo "（需要先安装 com.unity.sentis，manifest.json 里已经声明）"
