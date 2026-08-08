# Third-party notices

Arknights Painter is distributed under GNU Affero General Public License v3.0.

## MaaAssistantArknights

- Project: https://github.com/MaaAssistantArknights/MaaAssistantArknights
- License: GNU Affero General Public License v3.0
- Use in this project: reference implementation and behavior for bounded ADB reconnects, command timeouts, screenshot transport, coordinate validation, emulator-provided ADB discovery, and compatibility-first ADB Input control.

The Arknights Painter code remains a separate C# implementation. Redistribution and modification of either project must follow AGPL-3.0.

## Pixel-art algorithm references

- douloom: https://github.com/lulu0119/douloom (AGPL-3.0)
- Image-to-Pixel: https://github.com/Tezumie/Image-to-Pixel (MIT library / Apache-2.0 application)
- Use in this project: behavior and option-set references for bead-grid sampling and selectable error-diffusion or ordered dithering.

The implementations in Arknights Painter are independent C# implementations. No source files from these projects are redistributed.

## NuGet dependencies

- Microsoft.WindowsAppSDK: MIT License
- CommunityToolkit.Mvvm: MIT License
- SkiaSharp and SkiaSharp.NativeAssets.Win32: MIT License
- xUnit, Microsoft.NET.Test.Sdk, coverlet.collector: their respective open-source licenses; test-only dependencies are not shipped as part of the desktop application.

The corresponding package license metadata and source links are available from https://www.nuget.org/.
