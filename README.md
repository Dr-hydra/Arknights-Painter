<p align="center">
  <img src="docs/app-icon.png" width="96" height="96" alt="Arknights Painter 画板图标">
</p>

<h1 align="center">Arknights Painter</h1>

<p align="center">把图片转换为 24×24 像素画，或生成 96×96 并自动拆成 16 张子画。</p>

> 本项目是非官方开源工具，与游戏及其开发、发行方无关联。请在遵守目标软件规则的前提下使用。

一个 Windows 11 风格的 WinUI 3 工具，将常见图片转换成 24×24 像素画，并通过 ADB 或 Win32 窗口输入到目标绘画页面。96×96 模式会整体量化后按 4×4 拆成 16 张 24×24 草稿。

## 下载

前往 [GitHub Releases](https://github.com/Dr-hydra/Arknights-Painter/releases/latest) 下载最新版 ZIP，完整解压后运行 `ArknightsPainter.App.exe`。发布包已包含 .NET 10、Windows App SDK、电脑版所需 MaaFramework 组件和备用 ADB，不需要另外安装运行库或 Android Platform Tools。

## 功能

- 支持 PNG、JPEG、BMP、WebP 和 SVG；SVG 会安全栅格化并保留透明通道。
- 支持 24×24 单画和 96×96 自动分片；96 模式按游戏槽位从右下到左上保存 16 张草稿，并支持分片级断点续画。
- 96×96 模式默认使用居中裁切铺满正方形画面，避免非正方形输入在预览上下产生白边。
- 支持直接截取桌面区域：在当前显示器上拖动框选后立即导入，并适配多显示器混合 DPI。
- 完整适配、居中裁切、拉伸三种构图方式，默认完整适配并使用白色留白。
- 提供亮度、对比度和饱和度调整，预览会实时反映最终色板结果和 4×4 分片边界。
- 三种转换算法：适合照片的拼豆均色、适合图标和已有像素画的拼豆主色，以及原有感知平滑。
- CIEDE2000 感知色差量化，内置经过截图与真机采集双重验证的 40 色固定色板。
- 支持无抖动、Atkinson、Floyd-Steinberg 和 Bayer 4×4，实时显示网格预览和颜色用量。
- 自动查找 MuMu、雷电、蓝叠或 PATH 中的 ADB，也可手动指定 `adb.exe`；均不可用时自动回退到内置 ADB。
- 支持原生电脑版窗口：选择“电脑版窗口（PID）”，填写游戏进程 PID 即可连接，不需要 ADB。
- 自动识别 24×24 画布与滚动颜料区，并按实际色块边缘适配不同分辨率；失败时可在截图上拖动、缩放校准框。
- 按颜料分组、20 格分批点击，支持暂停、继续、取消和漏格重试。
- 96 模式的进入编辑页、单片绘制和保存操作失败时会自动尝试最多 3 次，仍失败才停止。
- 可选实验性滑动绘制，将同一行连续至少 3 个同色格合并为直线滑动。
- 可选实验性快速校验，可跳过已匹配色块，减少绘画过程耗时。
- 每次选择颜料后识别青色发光边框，界面或色板不匹配时停止操作。
- 非纯白浅色会独立检查纯白漏点并自动补画，即使关闭常规绘制安全校验也会保留该检查。

24×24 单画模式只负责绘制，不会点击保存或发布。96×96 分片模式会自动保存 16 张个人草稿并进入下一张空画布，但不会点击“完成并发布”。

## 使用

1. 完整解压发布包，启动安卓模拟器或原生电脑版，并进入横屏的 24×24 绘画页面。
2. 启动 `ArknightsPainter.App.exe`，在“连接设备”中选择“模拟器”或“电脑版”。电脑版会按需申请管理员权限并自动发现进程，也可填写任务管理器中的 `Arknights.exe` PID；安卓用户确认设备状态为 `device`。
3. 选择“24×24 单画”或“96×96 分片”，再打开、拖入图片或点击“截图”框选桌面区域；随后设置构图、转换算法、亮度、对比度、饱和度、背景和抖动。
4. 点击“自动识别”；识别失败时使用“手动调整”。
5. 检查预览后点击“开始绘制”。96 模式可从画像册或绘画编辑页开始，但画像册必须至少有 16 个空位；绘制过程中不要切换页面或改变窗口方向。

完整的参数说明、校准步骤和故障排查请参阅[使用说明](docs/使用说明.md)。

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

输出位于 `artifacts/ArknightsPainter-win-x64`。这是用于开发检查的普通目录版本；目标机器需安装 .NET 10 Desktop Runtime。发布脚本会自动启动成品进行烟雾检查，启动失败时不会静默交付。

单文件、自包含发布：

```powershell
./scripts/publish.ps1 -SingleFile
```

输出位于 `artifacts/ArknightsPainter-win-x64-single-file`，运行时不要求预装 .NET 10 Desktop Runtime。该目录包含单文件 EXE、MaaFramework 组件、备用 ADB、许可证和使用文档，正式发布时应将整个目录打成一个 ZIP。快速测试发布可附加 `-SkipSmoke`，正式交付仍建议保留默认启动检查。单文件首次启动时会由 .NET 将 WinUI 原生组件解压到用户临时目录。

## 许可证

本项目按 [GNU Affero General Public License v3.0](LICENSE) 发布。ADB、截图和触控设计参考了 MaaAssistantArknights，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
