using System.Collections.ObjectModel;
using ArknightsPainter.App.Services;
using ArknightsPainter.Core;
using ArknightsPainter.Core.Abstractions;
using ArknightsPainter.Core.Adb;
using ArknightsPainter.Core.Automation;
using ArknightsPainter.Core.Imaging;
using ArknightsPainter.Core.Models;
using ArknightsPainter.Core.Vision;
using ArknightsPainter.Core.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ArknightsPainter.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int MosaicLayoutVersion = 2;

    private readonly SettingsStore _settingsStore = new();
    private readonly AppSettings _settings;
    private readonly PaletteDefinition _palette;
    private readonly IImageQuantizer _quantizer = new SkiaImageQuantizer();
    private readonly IScreenLocator _locator = new ScreenLocator();
    private readonly IPaletteVision _paletteVision = new PaletteVision();
    private readonly PauseController _pauseController = new();
    private CancellationTokenSource? _conversionCts;
    private CancellationTokenSource? _drawingCts;
    private IAdbClient? _adb;
    private Artwork24? _artwork;
    private Artwork96? _mosaicArtwork;
    private bool _isMosaicMode;
    private string? _currentImagePath;
    private ImageCropRect? _currentCrop;
    private string _currentImageName = "尚未选择图片";
    private FitOption _selectedFitOption;
    private PixelArtOption _selectedPixelArtOption;
    private DitherOption _selectedDitherOption;
    private PaletteOption _selectedBackground;
    private double _brightness;
    private double _contrast;
    private double _saturation;
    private ConnectionModeOption _selectedConnectionMode;
    private DeviceOption? _selectedDevice;
    private string _adbPath;
    private string _endpoint;
    private string _desktopPid;
    private bool _ignoreVisualValidation;
    private bool _experimentalSwipeDrawing;
    private bool _experimentalCanvasValidation;
    private string _deviceStatus = "正在查找 ADB…";
    private InfoBarSeverity _deviceSeverity = InfoBarSeverity.Informational;
    private string _calibrationStatus = "选择在线设备后进行校准。";
    private bool _isDrawing;
    private double _progressPercent;
    private string _progressLabel = "0 / 576";
    private string _statusMessage = "等待图片和设备。";
    private string _pauseButtonLabel = "暂停";
    private string _logText = string.Empty;

    public MainViewModel()
    {
        _settings = _settingsStore.Load();
        _palette = PaletteDefinition.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "palette.v1.json"));
        FitOptions =
        [
            new FitOption(ImageFitMode.Contain, "完整适配"),
            new FitOption(ImageFitMode.Cover, "居中裁切"),
            new FitOption(ImageFitMode.Stretch, "直接拉伸")
        ];
        _selectedFitOption = FitOptions[0];
        PixelArtOptions =
        [
            new PixelArtOption(PixelArtAlgorithm.BeadAverage, "拼豆均色"),
            new PixelArtOption(PixelArtAlgorithm.BeadDominant, "拼豆主色"),
            new PixelArtOption(PixelArtAlgorithm.Perceptual, "感知平滑")
        ];
        _selectedPixelArtOption = PixelArtOptions[0];
        DitherOptions =
        [
            new DitherOption(DitherMode.None, "无抖动"),
            new DitherOption(DitherMode.Atkinson, "Atkinson"),
            new DitherOption(DitherMode.FloydSteinberg, "Floyd-Steinberg"),
            new DitherOption(DitherMode.Bayer4x4, "Bayer 4×4")
        ];
        _selectedDitherOption = DitherOptions[0];
        PaletteOptions = new ObservableCollection<PaletteOption>(_palette.Colors.Select(PaletteOption.From));
        _selectedBackground = PaletteOptions.FirstOrDefault(option => option.Color.Index == 3)
            ?? PaletteOptions[0];
        ConnectionModeOptions =
        [
            new ConnectionModeOption("adb", "Android 模拟器（ADB）"),
            new ConnectionModeOption("win32", "电脑版窗口（PID）")
        ];
        _selectedConnectionMode = ConnectionModeOptions.FirstOrDefault(option =>
            string.Equals(option.Value, _settings.ConnectionMode, StringComparison.OrdinalIgnoreCase))
            ?? ConnectionModeOptions[0];
        _adbPath = AdbPathResolver.Find(_settings.AdbPath) ?? _settings.AdbPath ?? string.Empty;
        _endpoint = _settings.Endpoint;
        _desktopPid = _settings.DesktopPid;
        _ignoreVisualValidation = _settings.IgnoreVisualValidation;
        _experimentalSwipeDrawing = _settings.ExperimentalSwipeDrawing;
        _experimentalCanvasValidation = _settings.ExperimentalCanvasValidation;
        _isMosaicMode = string.Equals(_settings.ArtworkMode, "96", StringComparison.OrdinalIgnoreCase);
        if (_isMosaicMode)
        {
            _selectedFitOption = FitOptions.First(option => option.Value == ImageFitMode.Cover);
        }
        if (IsAdbMode)
        {
            TryCreateAdb();
        }
    }

    public event EventHandler<byte[]>? PreviewChanged;

    public IReadOnlyList<FitOption> FitOptions { get; }

    public IReadOnlyList<PixelArtOption> PixelArtOptions { get; }

    public IReadOnlyList<DitherOption> DitherOptions { get; }

    public IReadOnlyList<ConnectionModeOption> ConnectionModeOptions { get; }

    public ObservableCollection<PaletteOption> PaletteOptions { get; }

    public ObservableCollection<DeviceOption> Devices { get; } = [];

    public ObservableCollection<ColorUsageItem> ColorUsage { get; } = [];

    public FitOption SelectedFitOption
    {
        get => _selectedFitOption;
        set => SetProperty(ref _selectedFitOption, value);
    }

    public PixelArtOption SelectedPixelArtOption
    {
        get => _selectedPixelArtOption;
        set => SetProperty(ref _selectedPixelArtOption, value);
    }

    public DitherOption SelectedDitherOption
    {
        get => _selectedDitherOption;
        set => SetProperty(ref _selectedDitherOption, value);
    }

    public PaletteOption SelectedBackground
    {
        get => _selectedBackground;
        set => SetProperty(ref _selectedBackground, value);
    }

    public double Brightness
    {
        get => _brightness;
        set
        {
            if (SetProperty(ref _brightness, Math.Clamp(value, -100, 100)))
            {
                OnPropertyChanged(nameof(BrightnessLabel));
            }
        }
    }

    public string BrightnessLabel => FormatAdjustment(Brightness);

    public double Contrast
    {
        get => _contrast;
        set
        {
            if (SetProperty(ref _contrast, Math.Clamp(value, -100, 100)))
            {
                OnPropertyChanged(nameof(ContrastLabel));
            }
        }
    }

    public string ContrastLabel => FormatAdjustment(Contrast);

    public double Saturation
    {
        get => _saturation;
        set
        {
            if (SetProperty(ref _saturation, Math.Clamp(value, -100, 100)))
            {
                OnPropertyChanged(nameof(SaturationLabel));
            }
        }
    }

    public string SaturationLabel => FormatAdjustment(Saturation);

    public ConnectionModeOption SelectedConnectionMode
    {
        get => _selectedConnectionMode;
        set
        {
            if (SetProperty(ref _selectedConnectionMode, value))
            {
                _settings.ConnectionMode = value.Value;
                _settingsStore.Save(_settings);
                _adb = null;
                Devices.Clear();
                SelectedDevice = null;
                UpdateConnectionModeVisibility();
                UpdateDeviceStatus();
                UpdateCalibrationStatus();
                OnPropertyChanged(nameof(CanStart));
            }
        }
    }

    public DeviceOption? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                UpdateDeviceStatus();
                UpdateCalibrationStatus();
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(MosaicResumeText));
                OnPropertyChanged(nameof(CanResetMosaicProgress));
            }
        }
    }

    public string AdbPath
    {
        get => _adbPath;
        set
        {
            if (SetProperty(ref _adbPath, value))
            {
                _settings.AdbPath = value;
                _settingsStore.Save(_settings);
                TryCreateAdb();
            }
        }
    }

    public string Endpoint
    {
        get => _endpoint;
        set
        {
            if (SetProperty(ref _endpoint, value))
            {
                _settings.Endpoint = value;
                _settingsStore.Save(_settings);
            }
        }
    }

    public string DesktopPid
    {
        get => _desktopPid;
        set
        {
            if (SetProperty(ref _desktopPid, value))
            {
                _settings.DesktopPid = value;
                _settingsStore.Save(_settings);
            }
        }
    }

    public bool VisualValidationEnabled
    {
        get => !_ignoreVisualValidation;
        set
        {
            var ignoreValidation = !value;
            if (_ignoreVisualValidation != ignoreValidation)
            {
                _ignoreVisualValidation = ignoreValidation;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IgnoreVisualValidation));
                _settings.IgnoreVisualValidation = ignoreValidation;
                _settingsStore.Save(_settings);
            }
        }
    }

    public bool IgnoreVisualValidation => _ignoreVisualValidation;

    public bool ExperimentalSwipeDrawing
    {
        get => _experimentalSwipeDrawing;
        set
        {
            if (SetProperty(ref _experimentalSwipeDrawing, value))
            {
                _settings.ExperimentalSwipeDrawing = value;
                _settingsStore.Save(_settings);
            }
        }
    }

    public bool ExperimentalCanvasValidation
    {
        get => _experimentalCanvasValidation;
        set
        {
            if (SetProperty(ref _experimentalCanvasValidation, value))
            {
                _settings.ExperimentalCanvasValidation = value;
                _settingsStore.Save(_settings);
            }
        }
    }

    public bool IsAdbMode => string.Equals(SelectedConnectionMode.Value, "adb", StringComparison.OrdinalIgnoreCase);

    public bool IsDesktopMode => !IsAdbMode;

    public Visibility AdbSettingsVisibility => IsAdbMode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DesktopSettingsVisibility => IsAdbMode ? Visibility.Collapsed : Visibility.Visible;

    public string CurrentImageName
    {
        get => _currentImageName;
        private set => SetProperty(ref _currentImageName, value);
    }

    public string? CurrentImagePath => _currentImagePath;

    public ImageCropRect? CurrentCrop => _currentCrop;

    public string DeviceStatus
    {
        get => _deviceStatus;
        private set => SetProperty(ref _deviceStatus, value);
    }

    public InfoBarSeverity DeviceSeverity
    {
        get => _deviceSeverity;
        private set => SetProperty(ref _deviceSeverity, value);
    }

    public string CalibrationStatus
    {
        get => _calibrationStatus;
        private set => SetProperty(ref _calibrationStatus, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public string ProgressLabel
    {
        get => _progressLabel;
        private set => SetProperty(ref _progressLabel, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string PauseButtonLabel
    {
        get => _pauseButtonLabel;
        private set => SetProperty(ref _pauseButtonLabel, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public Visibility EmptyPreviewVisibility =>
        (IsMosaicMode ? _mosaicArtwork is null : _artwork is null) ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSingleArtworkMode => !_isMosaicMode;

    public bool IsMosaicMode => _isMosaicMode;

    public Visibility MosaicModeVisibility => IsMosaicMode ? Visibility.Visible : Visibility.Collapsed;

    public string PreviewTitle => IsMosaicMode ? "96×96 分片预览" : "24×24 预览";

    public string ArtworkSizeLabel => IsMosaicMode ? "96×96 · 4×4 分片" : "24×24";

    public string PreviewSummary => IsMosaicMode
        ? _mosaicArtwork is null ? string.Empty : $"{_mosaicArtwork.ColorUsage.Count} 种颜料 · 9216 格 · 16 分片"
        : _artwork is null ? string.Empty : $"{_artwork.ColorUsage.Count} 种颜料 · 576 格";

    public string MosaicResumeText
    {
        get
        {
            var nextTile = GetMosaicResumeIndex(SelectedDevice?.Device.Serial);
            return nextTile > 0
                ? $"检测到进度：将从分片 {nextTile + 1}/16 继续。"
                : "将按游戏槽位顺序从右下到左上绘制并保存 16 张草稿。";
        }
    }

    public bool CanResetMosaicProgress => GetMosaicResumeIndex(SelectedDevice?.Device.Serial) > 0 && !_isDrawing;

    public bool IsPaletteIncomplete => !_palette.Complete;

    public bool CanStart => !_isDrawing && (IsMosaicMode ? _mosaicArtwork is not null : _artwork is not null) &&
                            SelectedDevice?.Device.State == AdbDeviceState.Device && GetCalibration() is not null;

    public bool CanPause => _isDrawing;

    public bool CanCancel => _isDrawing;

    public bool CanCrop => !_isDrawing && !string.IsNullOrWhiteSpace(_currentImagePath);

    public CalibrationProfile? CurrentCalibration => GetCalibration();

    public async Task InitializeAsync()
    {
        AppendLog($"内置色板 {_palette.Version}，{_palette.Colors.Count} 色，签名 {_palette.ComputeSignature()}。");
        await RefreshDevicesAsync();
    }

    public void SelectConnectionMode(string value)
    {
        SelectedConnectionMode = ConnectionModeOptions.First(option =>
            string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase));
    }

    public void SelectArtworkMode(bool mosaic)
    {
        if (_isMosaicMode == mosaic)
        {
            return;
        }

        _isMosaicMode = mosaic;
        if (mosaic && SelectedFitOption.Value == ImageFitMode.Contain)
        {
            _selectedFitOption = FitOptions.First(option => option.Value == ImageFitMode.Cover);
            OnPropertyChanged(nameof(SelectedFitOption));
        }

        _settings.ArtworkMode = mosaic ? "96" : "24";
        _settingsStore.Save(_settings);
        ProgressPercent = 0;
        ProgressLabel = mosaic ? "0 / 9216" : "0 / 576";
        OnPropertyChanged(nameof(IsSingleArtworkMode));
        OnPropertyChanged(nameof(IsMosaicMode));
        OnPropertyChanged(nameof(MosaicModeVisibility));
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(ArtworkSizeLabel));
        OnPropertyChanged(nameof(EmptyPreviewVisibility));
        OnPropertyChanged(nameof(PreviewSummary));
        OnPropertyChanged(nameof(MosaicResumeText));
        OnPropertyChanged(nameof(CanResetMosaicProgress));
        OnPropertyChanged(nameof(CanStart));
    }

    public async Task LoadImageAsync(string path)
    {
        _currentImagePath = path;
        _currentCrop = null;
        CurrentImageName = Path.GetFileName(path);
        OnPropertyChanged(nameof(CurrentImagePath));
        OnPropertyChanged(nameof(CurrentCrop));
        OnPropertyChanged(nameof(CanCrop));
        await ReconvertAsync();
        AppendLog($"已导入 {CurrentImageName}。");
    }

    public async Task ApplyCropAsync(ImageCropRect crop)
    {
        if (!crop.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(crop), "裁切区域超出图片范围。");
        }

        _currentCrop = IsFullCrop(crop) ? null : crop;
        OnPropertyChanged(nameof(CurrentCrop));
        await ReconvertAsync();
        AppendLog(_currentCrop is null ? "已恢复使用完整图片。" : "已应用图片裁切。" );
    }

    public async Task ReconvertAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentImagePath))
        {
            return;
        }

        _conversionCts?.Cancel();
        _conversionCts?.Dispose();
        _conversionCts = new CancellationTokenSource();
        try
        {
            var options = new ImageConversionOptions(
                SelectedFitOption.Value,
                SelectedBackground.Color.Color,
                SelectedPixelArtOption.Value,
                SelectedDitherOption.Value,
                _currentCrop,
                Brightness,
                Contrast,
                Saturation);
            byte[] preview;
            if (IsMosaicMode)
            {
                _mosaicArtwork = await _quantizer.ConvertMosaicAsync(
                    _currentImagePath,
                    _palette,
                    options,
                    _conversionCts.Token);
                _artwork = null;
                preview = _quantizer.RenderPreview(_mosaicArtwork, _palette);
                ProgressLabel = "0 / 9216";
            }
            else
            {
                _artwork = await _quantizer.ConvertAsync(
                    _currentImagePath,
                    _palette,
                    options,
                    _conversionCts.Token);
                _mosaicArtwork = null;
                preview = _quantizer.RenderPreview(_artwork, _palette);
                ProgressLabel = "0 / 576";
            }

            PreviewChanged?.Invoke(this, preview);
            RefreshColorUsage();
            OnPropertyChanged(nameof(EmptyPreviewVisibility));
            OnPropertyChanged(nameof(PreviewSummary));
            OnPropertyChanged(nameof(MosaicResumeText));
            OnPropertyChanged(nameof(CanResetMosaicProgress));
            OnPropertyChanged(nameof(CanStart));
            StatusMessage = IsMosaicMode
                ? "图片已转换为 96×96，可开始自动分片绘制。"
                : "图片已转换，可开始绘制。";
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void ResetImageAdjustments()
    {
        Brightness = 0;
        Contrast = 0;
        Saturation = 0;
    }

    public void ResetMosaicProgress()
    {
        _settings.MosaicResume = null;
        _settingsStore.Save(_settings);
        OnPropertyChanged(nameof(MosaicResumeText));
        OnPropertyChanged(nameof(CanResetMosaicProgress));
        AppendLog("已清除 96×96 分片续画进度，将从第 1 片开始。" );
    }

    public async Task RefreshDevicesAsync()
    {
        if (!IsAdbMode)
        {
            try
            {
                var previous = SelectedDevice?.Device.Serial;
                // Window discovery is deliberately independent from Maa
                // controller creation. Connecting a controller can wait on
                // the game window and must never block the startup UI thread.
                var devices = await Task.Run(Win32DesktopClient.DiscoverDevices);

                Devices.Clear();
                foreach (var device in devices)
                {
                    Devices.Add(new DeviceOption(device));
                }

                SelectedDevice = Devices.FirstOrDefault(item => item.Device.Serial == previous)
                    ?? Devices.FirstOrDefault();
                if (SelectedDevice is not null &&
                    Win32DesktopClient.TryParseProcessId(SelectedDevice.Device.Serial, out var processId))
                {
                    _desktopPid = processId.ToString();
                    _settings.DesktopPid = _desktopPid;
                    _settingsStore.Save(_settings);
                    OnPropertyChanged(nameof(DesktopPid));
                }

                UpdateDeviceStatus();
                AppendLog(devices.Count == 0
                    ? "未自动找到明日方舟电脑版窗口，请确认游戏已启动。"
                    : $"已自动找到 {devices.Count} 个电脑版窗口。");
            }
            catch (Exception ex)
            {
                Devices.Clear();
                SelectedDevice = null;
                DeviceStatus = ex.Message;
                DeviceSeverity = InfoBarSeverity.Error;
                AppendLog(ex.Message);
            }

            return;
        }

        if (!TryCreateAdb())
        {
            DeviceStatus = "未找到 adb.exe，请填写路径。";
            DeviceSeverity = InfoBarSeverity.Error;
            return;
        }

        try
        {
            var previous = SelectedDevice?.Device.Serial;
            var devices = await _adb!.GetDevicesAsync();
            Devices.Clear();
            foreach (var device in devices)
            {
                Devices.Add(new DeviceOption(device));
            }

            SelectedDevice = Devices.FirstOrDefault(item => item.Device.Serial == previous)
                ?? Devices.FirstOrDefault(item => item.Device.State == AdbDeviceState.Device)
                ?? Devices.FirstOrDefault();
            UpdateDeviceStatus();
            AppendLog($"ADB 返回 {Devices.Count} 个设备。" );
        }
        catch (Exception ex)
        {
            DeviceStatus = ex.Message;
            DeviceSeverity = InfoBarSeverity.Error;
            AppendLog(ex.Message);
        }
    }

    public async Task ConnectAsync()
    {
        if (!IsAdbMode)
        {
            if (!await EnsureDesktopClientAsync())
            {
                await RefreshDevicesAsync();
                if (!await EnsureDesktopClientAsync() || SelectedDevice is null)
                {
                    throw new InvalidOperationException("未找到明日方舟电脑版窗口，请先启动游戏。");
                }
            }

            await _adb!.ConnectAsync($"pid:{DesktopPid}");
            AppendLog($"已绑定电脑版窗口 PID {DesktopPid}。");
            await RefreshDevicesAsync();
            return;
        }

        if (!TryCreateAdb())
        {
            throw new InvalidOperationException("请先设置有效的 adb.exe 路径。");
        }

        await _adb!.ConnectAsync(Endpoint);
        AppendLog($"已请求连接 {Endpoint}。" );
        await RefreshDevicesAsync();
    }

    public async Task<CalibrationCapture> CaptureCalibrationAsync()
    {
        var device = RequireOnlineDevice();
        if (!IsAdbMode && !await EnsureDesktopClientAsync())
        {
            throw new InvalidOperationException("未找到明日方舟电脑版窗口，请先刷新设备列表。");
        }

        if (_adb is null)
        {
            throw new InvalidOperationException("设备连接尚未建立，请先连接设备。");
        }

        var screenshot = await _adb!.CaptureScreenshotAsync(device.Serial);
        var result = _locator.Locate(device.Serial, screenshot);
        return new CalibrationCapture(screenshot, result);
    }

    public void SaveCalibration(CalibrationProfile profile)
    {
        _settings.Calibrations.RemoveAll(item => item.Matches(
            profile.DeviceSerial, profile.ScreenWidth, profile.ScreenHeight));
        _settings.Calibrations.Add(profile);
        _settingsStore.Save(_settings);
        CalibrationStatus = $"已校准 {profile.ScreenWidth}×{profile.ScreenHeight}，置信度 {profile.Confidence:P0}。";
        AppendLog(CalibrationStatus);
        OnPropertyChanged(nameof(CanStart));
    }

    public async Task StartDrawingAsync()
    {
        var device = RequireOnlineDevice();
        var profile = GetCalibration() ?? throw new InvalidOperationException("请先完成画面校准。");
        if ((IsMosaicMode && _mosaicArtwork is null) || (!IsMosaicMode && _artwork is null))
        {
            throw new InvalidOperationException("请先导入图片。");
        }
        if (!IsAdbMode && !await EnsureDesktopClientAsync())
        {
            throw new InvalidOperationException("未找到明日方舟电脑版窗口，请先刷新设备列表。");
        }

        if (_adb is null)
        {
            throw new InvalidOperationException("设备连接尚未建立，请先连接设备。");
        }

        if (_isDrawing)
        {
            return;
        }

        _drawingCts = new CancellationTokenSource();
        _isDrawing = true;
        _pauseController.Resume();
        PauseButtonLabel = "暂停";
        NotifyDrawingState();
        if (!_palette.Complete)
        {
            AppendLog("警告：当前使用截图可见的 24 色预览色板。" );
        }

        var ignoreVisualValidation = IgnoreVisualValidation;
        var useSwipeDrawing = ExperimentalSwipeDrawing;
        var useCanvasValidation = ExperimentalCanvasValidation;
        if (ignoreVisualValidation)
        {
            AppendLog("警告：已启用强制绘制，将忽略常规视觉校验；非纯白浅色仍保留防漏检查。");
        }

        if (useSwipeDrawing)
        {
            AppendLog("已启用实验性滑动绘制：连续至少 3 个同色格将按行合并滑动。");
        }

        if (useCanvasValidation)
        {
            AppendLog("已启用实验性快速校验：绘制前将跳过画布中已经匹配的格子。");
        }

        var navigator = new PaletteNavigator(_adb!, _paletteVision, ignoreVisualValidation);
        var executor = new DrawExecutor(_adb!, _locator, _paletteVision, navigator);
        var progress = new Progress<DrawProgress>(UpdateProgress);
        var executionOptions = new DrawExecutionOptions(
            SkipVisualValidation: ignoreVisualValidation,
            UseSwipeDrawing: useSwipeDrawing,
            UseCanvasValidation: useCanvasValidation);
        try
        {
            if (IsMosaicMode)
            {
                var startTileIndex = GetMosaicResumeIndex(device.Serial);
                AppendLog(startTileIndex > 0
                    ? $"继续 96×96 分片任务，将从第 {startTileIndex + 1}/16 片开始。"
                    : "开始 96×96 分片任务；请确保画册至少有 16 个空位。程序只保存草稿，不会发布。" );
                var screenNavigator = new MosaicScreenNavigator(_adb!, _locator);
                var coordinator = new MosaicDrawCoordinator(executor, screenNavigator);
                await coordinator.ExecuteAsync(
                    device.Serial,
                    profile,
                    _palette,
                    _mosaicArtwork!,
                    startTileIndex,
                    executionOptions,
                    _pauseController,
                    nextTile => SaveMosaicProgressAsync(device.Serial, nextTile),
                    progress,
                    _drawingCts.Token);
            }
            else
            {
                await executor.ExecuteAsync(
                    device.Serial,
                    profile,
                    _palette,
                    DrawPlan.Create(_artwork!, _palette),
                    executionOptions,
                    _pauseController,
                    progress,
                    _drawingCts.Token);
            }
        }
        finally
        {
            _isDrawing = false;
            _drawingCts.Dispose();
            _drawingCts = null;
            NotifyDrawingState();
        }
    }

    public void TogglePause()
    {
        if (!_isDrawing)
        {
            return;
        }

        if (_pauseController.IsPaused)
        {
            _pauseController.Resume();
            PauseButtonLabel = "暂停";
            AppendLog("继续绘制。" );
        }
        else
        {
            _pauseController.Pause();
            PauseButtonLabel = "继续";
            AppendLog("将在当前批次结束后暂停。" );
        }
    }

    public void CancelDrawing() => _drawingCts?.Cancel();

    private bool TryCreateAdb()
    {
        var resolved = AdbPathResolver.Find(AdbPath);
        if (resolved is null)
        {
            _adb = null;
            return false;
        }

        if (_adb?.ExecutablePath != resolved)
        {
            _adb = new AdbClient(resolved);
            if (AdbPath != resolved)
            {
                _adbPath = resolved;
                OnPropertyChanged(nameof(AdbPath));
            }
        }

        return true;
    }

    private bool TryCreateDesktop()
    {
        if (!Win32DesktopClient.TryParseProcessId(DesktopPid, out var pid))
        {
            _adb = null;
            return false;
        }

        if (pid == Environment.ProcessId)
        {
            _adb = null;
            return false;
        }

        if (_adb is not Win32DesktopClient desktop || desktop.ProcessId != pid)
        {
            try
            {
                _adb = new Win32DesktopClient(pid);
            }
            catch (Exception)
            {
                _adb = null;
                return false;
            }
        }

        return true;
    }

    private Task<bool> EnsureDesktopClientAsync() =>
        IsAdbMode ? Task.FromResult(TryCreateAdb()) : Task.Run(TryCreateDesktop);

    private AdbDevice RequireOnlineDevice() => SelectedDevice?.Device is { State: AdbDeviceState.Device } device
        ? device
        : throw new InvalidOperationException("请选择状态为 device 的在线模拟器。");

    private CalibrationProfile? GetCalibration()
    {
        var device = SelectedDevice?.Device;
        if (device is null)
        {
            return null;
        }

        return _settings.Calibrations.LastOrDefault(profile =>
            string.Equals(profile.DeviceSerial, device.Serial, StringComparison.OrdinalIgnoreCase));
    }

    private int GetMosaicResumeIndex(string? deviceSerial)
    {
        var state = _settings.MosaicResume;
        if (_mosaicArtwork is null || state is null || string.IsNullOrWhiteSpace(deviceSerial) ||
            state.LayoutVersion != MosaicLayoutVersion ||
            state.NextTileIndex is <= 0 or >= Artwork96.TileCount ||
            !string.Equals(state.DeviceSerial, deviceSerial, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(state.ArtworkSignature, _mosaicArtwork.ComputeSignature(), StringComparison.Ordinal))
        {
            return 0;
        }

        return state.NextTileIndex;
    }

    private Task SaveMosaicProgressAsync(string deviceSerial, int nextTileIndex)
    {
        _settings.MosaicResume = nextTileIndex >= Artwork96.TileCount
            ? null
            : new MosaicResumeState
            {
                LayoutVersion = MosaicLayoutVersion,
                ArtworkSignature = _mosaicArtwork!.ComputeSignature(),
                DeviceSerial = deviceSerial,
                NextTileIndex = nextTileIndex,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        _settingsStore.Save(_settings);
        OnPropertyChanged(nameof(MosaicResumeText));
        OnPropertyChanged(nameof(CanResetMosaicProgress));
        return Task.CompletedTask;
    }

    private void UpdateDeviceStatus()
    {
        if (SelectedDevice is null)
        {
            DeviceStatus = Devices.Count == 0 ? "未发现设备。" : "请选择设备。";
            DeviceSeverity = InfoBarSeverity.Warning;
            return;
        }

        DeviceStatus = SelectedDevice.Device.State switch
        {
            AdbDeviceState.Device => $"已连接：{SelectedDevice.DisplayName}",
            AdbDeviceState.Offline => $"设备离线：{SelectedDevice.Device.Serial}",
            AdbDeviceState.Unauthorized => "设备未授权，请在模拟器中确认 ADB 授权。",
            _ => SelectedDevice.Device.Description
        };
        DeviceSeverity = SelectedDevice.Device.State == AdbDeviceState.Device
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
    }

    private void UpdateConnectionModeVisibility()
    {
        OnPropertyChanged(nameof(IsAdbMode));
        OnPropertyChanged(nameof(IsDesktopMode));
        OnPropertyChanged(nameof(AdbSettingsVisibility));
        OnPropertyChanged(nameof(DesktopSettingsVisibility));
    }

    private void UpdateCalibrationStatus()
    {
        var profile = GetCalibration();
        CalibrationStatus = profile is null
            ? "当前设备尚未校准。"
            : $"已保存 {profile.ScreenWidth}×{profile.ScreenHeight} 校准，置信度 {profile.Confidence:P0}。";
    }

    private void RefreshColorUsage()
    {
        ColorUsage.Clear();
        var usage = IsMosaicMode ? _mosaicArtwork?.ColorUsage : _artwork?.ColorUsage;
        if (usage is null)
        {
            return;
        }

        foreach (var pair in usage.OrderByDescending(pair => pair.Value))
        {
            ColorUsage.Add(new ColorUsageItem(PaletteOption.From(_palette[pair.Key]), pair.Value));
        }
    }

    private void UpdateProgress(DrawProgress progress)
    {
        ProgressPercent = progress.Fraction * 100;
        ProgressLabel = $"{progress.CompletedCells} / {progress.TotalCells}";
        StatusMessage = progress.Message;
        AppendLog(progress.Message);
    }

    private void NotifyDrawingState()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanCrop));
        OnPropertyChanged(nameof(CanResetMosaicProgress));
    }

    private static bool IsFullCrop(ImageCropRect crop) =>
        Math.Abs(crop.X) < 0.000001 && Math.Abs(crop.Y) < 0.000001 &&
        Math.Abs(crop.Width - 1) < 0.000001 && Math.Abs(crop.Height - 1) < 0.000001;

    private static string FormatAdjustment(double value) => value switch
    {
        > 0 => $"+{value:0}",
        < 0 => $"{value:0}",
        _ => "0"
    };

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogText = string.IsNullOrEmpty(LogText) ? line : $"{LogText}{Environment.NewLine}{line}";
    }
}

public sealed record FitOption(ImageFitMode Value, string Label);

public sealed record ConnectionModeOption(string Value, string Label);

public sealed record PixelArtOption(PixelArtAlgorithm Value, string Label);

public sealed record DitherOption(DitherMode Value, string Label);

public sealed record DeviceOption(AdbDevice Device)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Device.Model)
        ? $"{Device.Serial} · {Device.State.ToString().ToLowerInvariant()}"
        : $"{Device.Model} · {Device.Serial} · {Device.State.ToString().ToLowerInvariant()}";
}

public sealed record PaletteOption(PaletteColor Color, SolidColorBrush Brush, string Label)
{
    public static PaletteOption From(PaletteColor color) => new(
        color,
        new SolidColorBrush(ColorHelper.FromArgb(255, color.Color.R, color.Color.G, color.Color.B)),
        $"{color.Name}  {color.Color.Hex}");
}

public sealed record ColorUsageItem(PaletteOption Palette, int Count)
{
    public SolidColorBrush Brush => Palette.Brush;

    public string Label => Palette.Color.Name;
}

public sealed record CalibrationCapture(byte[] Screenshot, ScreenLocationResult Result);
