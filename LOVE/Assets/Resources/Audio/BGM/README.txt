把 BGM 音频文件放在这个目录下（.wav / .ogg / .mp3 都行）。

story.json 里 "bgm" 字段填文件名（不含扩展名），例如：
  "bgm": "theater"   ->  加载 Assets/Resources/Audio/BGM/theater.ogg

特殊值：
  ""            保持上一段的 BGM 继续播（不打断）
  "none"/"stop" 淡出并停止 BGM

导入建议：
- 循环 BGM 的 Load Type 设成 Streaming 或 Compressed In Memory，避免占内存。
- 勾掉 3D（Unity 里是在 AudioSource 上控制，这里已经设成 2D 了）。
