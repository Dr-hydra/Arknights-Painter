<p align="center">
  <img src="docs/app-icon.png" width="96" height="96" alt="Arknights Painter 画板图标">
</p>

<h1 align="center">Arknights Painter</h1>

<p align="center">把图片转换为 24×24 像素画，并通过 ADB 自动绘制到安卓模拟器。</p>

> 本项目是非官方开源工具，与游戏及其开发、发行方无关联。请在遵守目标软件规则的前提下使用。

一个 Windows 11 风格的 WinUI 3 工具，将常见图片转换成 24×24 像素画，并通过 ADB 输入到目标绘画页面。

## 下载

前往 [GitHub Releases](https://github.com/Dr-hydra/Arknights-Painter/releases/latest) 下载最新版。推荐使用 `ArknightsPainter-v1.0.0-win-x64.exe` 单文件版本；ADB 不包含在程序中，需要使用模拟器自带的 ADB 或另行安装 Android Platform Tools。

## 功能

- 完整适配、居中裁切、拉伸三种构图方式，默认完整适配并使用白色留白。
- 三种转换算法：适合照片的拼豆均色、适合图标和已有像素画的拼豆主色，以及原有感知平滑。
- CIEDE2000 感知色差量化，内置经过截图与真机采集双重验证的 40 色固定色板。
- 支持无抖动、Atkinson、Floyd-Steinberg 和 Bayer 4×4，实时显示 24×24 网格预览和颜色用量。
- 自动查找 MuMu、雷电、蓝叠或 PATH 中的 ADB，也可手动指定 `adb.exe`。
- 自动识别 24×24 画布与滚动颜料区；失败时可在截图上拖动、缩放校准框。
- 按颜料分组、20 格分批点击，支持暂停、继续、取消和漏格重试。
- 每次选择颜料后识别青色发光边框，界面或色板不匹配时停止操作。

应用只负责绘制，不会点击保存或发布。

## 使用

1. 启动模拟器并进入横屏的 24×24 绘画页面。
2. 启动 `ArknightsPainter.App.exe`，确认设备状态为 `device`。MuMu 用户应优先使用模拟器安装目录 `shell\adb.exe`。
3. 导入或拖入 PNG、JPEG、BMP、WebP 图片，设置构图、转换算法、背景和抖动。
4. 点击“自动识别”；识别失败时使用“手动调整”。
5. 检查预览后点击“开始绘制”。绘制过程中不要切换模拟器页面或改变窗口方向。

校准数据按设备序列号与截图分辨率保存在 `%LOCALAPPDATA%\ArknightsPainter\settings.json`。

## 开发

要求 Windows 11 x64、.NET 10 SDK 和可联网的 NuGet 源。

```powershell
dotnet restore ArknightsPainter.sln -p:Platform=x64
dotnet build ArknightsPainter.sln -c Debug -p:Platform=x64 --no-restore
dotnet test tests/ArknightsPainter.Core.Tests -c Debug --no-restore
```

项目结构：

- `src/ArknightsPainter.Core`：图像量化、视觉识别、ADB 与绘制状态机。
- `src/ArknightsPainter.App`：WinUI 3 桌面应用。
- `tools/ArknightsPainter.PaletteCapture`：完整固定色板的维护工具。
- `tests/ArknightsPainter.Core.Tests`：不依赖模拟器的自动化测试。

## 更新色板

将颜料列表置于任意位置、保持绘画页面可见，然后运行：

```powershell
dotnet run --project tools/ArknightsPainter.PaletteCapture -c Release -- `
  --adb "C:\Program Files\Netease\MuMuPlayer-12.0\shell\adb.exe" `
  --serial 127.0.0.1:16384 `
  --output src/ArknightsPainter.App/Assets/palette.v1.json
```

工具会滚动到顶部、按重叠页采样色块中心、到达底部后生成带 SHA-256 短签名的 JSON。采集结果必须经过人工检查后再发布。

## 发布

```powershell
./scripts/publish.ps1
```

输出位于 `artifacts/ArknightsPainter-win-x64`。该目录使用 .NET 10 和 Windows App SDK 1.8，不包含 Android Platform Tools；目标机器需安装 .NET 10 Desktop Runtime。发布脚本会自动启动成品进行烟雾检查，启动失败时不会静默交付。

单文件、自包含发布：

```powershell
./scripts/publish.ps1 -SingleFile
```

输出位于 `artifacts/ArknightsPainter-win-x64-single-file`，运行时不要求预装 .NET 10 Desktop Runtime。快速测试发布可附加 `-SkipSmoke`，正式交付仍建议保留默认启动检查。单文件首次启动时会由 .NET 将 WinUI 原生组件解压到用户临时目录。

## 许可证

本项目按 [GNU Affero General Public License v3.0](LICENSE) 发布。ADB、截图和触控设计参考了 MaaAssistantArknights，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
