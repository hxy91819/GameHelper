# GameHelper 视觉与品牌

界面采用「私人游戏典藏」方向，让游戏海报承担主要内容表达。桌面库与统计页以 [A / Studio 契约](../architecture/studio-visual-contract.md) 为准；全屏界面使用独立的 [主机模式视觉契约](console-mode.md)。通用交互与验证范围见 [UI 品质标准](ui-quality.md)。

## Logo

`GameHelper.Desktop/Assets/gamehelper-logo.svg` 是唯一几何源，`Brand.xaml` 保留其导出的路径和画布：开放的 G 轨道结合播放切口，表达收藏、持续计时与启动。使用平面轮廓与两种前景色，避免纹理、光晕和小尺寸无法辨认的装饰；不包含游戏或平台商标。侧栏和主机窗口直接使用矢量，缩放不依赖位图。

`gamehelper-logo.png` 为 512 像素导出；`gamehelper.ico` 包含 16 / 24 / 32 / 48 / 64 / 128 / 256 像素图层，用于 WPF、ConsoleHost、窗口和桌面快捷方式。修改矢量后，在 Windows 上执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -STA -File scripts/export-brand.ps1
```

脚本用 WPF 从同一矢量生成全部位图和 ICO，不依赖图像编辑器或在线服务。
