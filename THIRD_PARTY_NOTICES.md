# Third-party notices

Arknights Painter is distributed under GNU Affero General Public License v3.0.

## MaaAssistantArknights

- Project: https://github.com/MaaAssistantArknights/MaaAssistantArknights
- License: GNU Affero General Public License v3.0
- Use in this project: reference implementation and behavior for bounded ADB reconnects, command timeouts, screenshot transport, coordinate validation, emulator-provided ADB discovery, and compatibility-first ADB Input control.

The Arknights Painter code remains a separate C# implementation. Redistribution and modification of either project must follow AGPL-3.0.

## MaaFramework

- Project: https://github.com/MaaXYZ/MaaFramework
- Version: 5.12.3 (Windows x64 runtime)
- License: GNU Affero General Public License v3.0
- Use in this project: the bundled MaaFramework v5.12.3 Windows x64 runtime provides the Win32 window capture and input backend for the desktop game mode. The application calls the public Maa C API and does not redistribute Maa source code.

The corresponding MaaFramework license text is included in its upstream distribution. Source and license information are available from the project link above.

## Android SDK Platform-Tools

- Project: https://developer.android.com/tools/releases/platform-tools
- Component: Android Debug Bridge 35.0.2
- Use in this project: the Windows x64 ADB executable and its two runtime DLLs are bundled as a fallback when no configured, emulator-provided, SDK, or PATH copy can be found.

The upstream `NOTICE.txt` is distributed with the bundled files under `Assets/Adb`.

## Pixel-art algorithm references

- douloom: https://github.com/lulu0119/douloom (AGPL-3.0)
- Image-to-Pixel: https://github.com/Tezumie/Image-to-Pixel (MIT library / Apache-2.0 application)
- Use in this project: behavior and option-set references for bead-grid sampling and selectable error-diffusion or ordered dithering.

The implementations in Arknights Painter are independent C# implementations. No source files from these projects are redistributed.

## NuGet dependencies

- Microsoft.WindowsAppSDK: MIT License
- CommunityToolkit.Mvvm: MIT License
- SkiaSharp and SkiaSharp.NativeAssets.Win32: MIT License
- Svg.Skia 5.2.0 and its Svg.* dependencies: MIT License
- HarfBuzzSharp and HarfBuzzSharp.NativeAssets.Win32: MIT License
- xUnit, Microsoft.NET.Test.Sdk, coverlet.collector: their respective open-source licenses; test-only dependencies are not shipped as part of the desktop application.

The corresponding package license metadata and source links are available from https://www.nuget.org/.
