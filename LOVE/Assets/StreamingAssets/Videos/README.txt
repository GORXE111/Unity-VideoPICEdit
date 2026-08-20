把渲染好的视频放在这个目录下。

- story.json 里的 "video" 字段填的就是这里的文件名，例如 "s01.mp4"。
- 推荐编码：H.264 (AVC) + AAC，MP4 封装。这是 Unity VideoPlayer 在 Windows/Android/iOS 上兼容性最好的组合。
- 分辨率保持一致（例如都是 1920x1080），切段时不会重建 RenderTexture，过渡更顺。
- 这个目录不会被 Unity 导入为资源，出包之后可以直接替换里面的视频文件，不用重新打包。

注意：StreamingAssets 里的文件会原样拷进最终包体，视频有多大包就有多大。
