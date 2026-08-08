using System.Collections.ObjectModel;
using ArknightsPainter.App.Services;
using ArknightsPainter.Core;
using ArknightsPainter.Core.Abstractions;
using ArknightsPainter.Core.Adb;
using ArknightsPainter.Core.Automation;
using ArknightsPainter.Core.Imaging;
using ArknightsPainter.Core.Models;
using ArknightsPainter.Core.Vision;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ArknightsPainter.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
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
    private string? _currentImagePath;
    private ImageCropRect? _currentCrop;
    private string _currentImageName = "尚未选择图片";
    private FitOption _selectedFitOption;
    private PixelArtOption _selectedPixelArtOption;
    private DitherOption _selectedDitherOption;
    private PaletteOption _selectedBackground;
    private DeviceOption? _selectedDevice;
    private string _adbPath;
    private string _endpoint;
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
        _adbPath = AdbPathResolver.Find(_settings.AdbPath) ?? _settings.AdbPath ?? string.Empty;
        _endpoint = _settings.Endpoint;
        TryCreateAdb();
    }

    public event EventHandler<byte[]>? PreviewChanged;

    public IReadOnlyList<FitOption> FitOptions { get; }

    public IReadOnlyList<PixelArtOption> PixelArtOptions { get; }

    public IReadOnlyList<DitherOption> DitherOptions { get; }

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

    public Visibility EmptyPreviewVisibility => _artwork is null ? Visibility.Visible : Visibility.Collapsed;

    public string PreviewSummary => _artwork is null ? string.Empty : $"{_artwork.ColorUsage.Count} 种颜料 · 576 格";

    public bool IsPaletteIncomplete => !_palette.Complete;

    public bool CanStart => !_isDrawing && _artwork is not null &&
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
                _currentCrop);
            _artwork = await _quantizer.ConvertAsync(_currentImagePath, _palette, options, _conversionCts.Token);
            var preview = _quantizer.RenderPreview(_artwork, _palette);
            PreviewChanged?.Invoke(this, preview);
            RefreshColorUsage();
            OnPropertyChanged(nameof(EmptyPreviewVisibility));
            OnPropertyChanged(nameof(PreviewSummary));
            OnPropertyChanged(nameof(CanStart));
            StatusMessage = "图片已转换，可开始绘制。";
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task RefreshDevicesAsync()
    {
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
        var artwork = _artwork ?? throw new InvalidOperationException("请先导入图片。");
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

        var navigator = new PaletteNavigator(_adb!, _paletteVision);
        var executor = new DrawExecutor(_adb!, _locator, _paletteVision, navigator);
        var progress = new Progress<DrawProgress>(UpdateProgress);
        try
        {
            await executor.ExecuteAsync(
                device.Serial,
                profile,
                _palette,
                DrawPlan.Create(artwork, _palette),
                new DrawExecutionOptions(),
                _pauseController,
                progress,
                _drawingCts.Token);
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
        if (_artwork is null)
        {
            return;
        }

        foreach (var pair in _artwork.ColorUsage.OrderByDescending(pair => pair.Value))
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
    }

    private static bool IsFullCrop(ImageCropRect crop) =>
        Math.Abs(crop.X) < 0.000001 && Math.Abs(crop.Y) < 0.000001 &&
        Math.Abs(crop.Width - 1) < 0.000001 && Math.Abs(crop.Height - 1) < 0.000001;

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogText = string.IsNullOrEmpty(LogText) ? line : $"{LogText}{Environment.NewLine}{line}";
    }
}

public sealed record FitOption(ImageFitMode Value, string Label);

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
