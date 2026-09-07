// Copyright (c) 2026 MMCKB
// Licensed under the MIT License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BtAudioSink.Models;
using BtAudioSink.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace BtAudioSink.Views;

internal sealed partial class MainWindow : Window
{
    private readonly AudioPlaybackService _playback = App.Playback;
    private bool _loadingOutputs;
    private bool _reallyClose;
    private bool _initialized;
    private bool _settingsReady;

    /// <summary>设备列表数据源（x:Bind 使用，必须在 InitializeComponent 前就绪）。</summary>
    public ObservableCollection<BtDeviceItem> Devices { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        // 用传统 Binding 绑定（x:Bind 在 unpackaged 的 MarkupCompilePass2 下解析不到本地类型）
        DeviceList.ItemsSource = Devices;

        Title = App.AppWindowTitle();
        TitleText.Text = App.AppWindowTitle();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();

        TrySetIcon();
        CenterOnScreen(760, 840);

        AppWindow.Closing += OnWindowClosing;

        _playback.DevicesChanged += (_, _) => DispatcherQueue.TryEnqueue(RebuildDeviceList);
        _playback.ConnectionChanged += (_, _) => DispatcherQueue.TryEnqueue(OnConnectionChanged);

        Root.Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;

            LoadSettings();
            await RefreshCoreAsync();
            await LoadOutputsAsync();

            if (!AudioPlaybackService.IsSupported)
            {
                NoticeBar.Severity = InfoBarSeverity.Error;
                NoticeBar.Title = "当前系统不支持蓝牙音频接收";
                NoticeBar.Message = "需要 Windows 10 2004（内部版本 19041）或更高版本，以及支持 A2DP Sink 的蓝牙适配器。";
                NoticeBar.IsOpen = true;
            }
        };
    }

    // ---------------- 初始化辅助 ----------------

    private void TrySetIcon()
    {
        try
        {
            string? dir = AppContext.BaseDirectory;
            string ico = Path.Combine(dir, "Assets", "bluetooth.ico");
            if (File.Exists(ico)) AppWindow.SetIcon(ico);
        }
        catch (Exception ex)
        {
            Log.Write("设置窗口图标失败", ex);
        }
    }

    private void CenterOnScreen(int width, int height)
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            if (area is null) return;
            RectInt32 work = area.WorkArea;
            int w = Math.Min(width, work.Width - 40);
            int h = Math.Min(height, work.Height - 40);
            AppWindow.Resize(new SizeInt32(w, h));
            AppWindow.Move(new PointInt32(
                work.X + (work.Width - w) / 2,
                work.Y + (work.Height - h) / 2));
        }
        catch (Exception ex)
        {
            Log.Write("居中窗口失败", ex);
        }
    }

    private void LoadSettings()
    {
        var s = App.Settings;
        StartupSwitch.IsOn = StartupService.IsEnabled();
        AutoReconnectSwitch.IsOn = s.AutoReconnect;
        StartMinimizedSwitch.IsOn = s.StartMinimized;
        CloseToTraySwitch.IsOn = s.CloseToTray;
        NotifySwitch.IsOn = s.ShowNotifications;
        ConnectModeCombo.SelectedIndex = s.ConnectionMode == 1 ? 1 : 0;
        _settingsReady = true;
    }

    private void OnConnectModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady) return;
        if (ConnectModeCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        App.Settings.ConnectionMode = tag == "1" ? 1 : 0;
        App.Settings.Save();
    }

    // ---------------- 设备列表 ----------------

    private async Task RefreshCoreAsync()
    {
        await _playback.RefreshAsync();
        RebuildDeviceList();
    }

    private void RebuildDeviceList()
    {
        var infos = _playback.Devices;

        for (int i = Devices.Count - 1; i >= 0; i--)
        {
            if (!infos.Any(x => x.Id == Devices[i].Id))
                Devices.RemoveAt(i);
        }

        foreach (var info in infos)
        {
            var existing = Devices.FirstOrDefault(x => x.Id == info.Id);
            if (existing is null)
            {
                var item = new BtDeviceItem(info.Id, string.IsNullOrWhiteSpace(info.Name) ? "(未命名设备)" : info.Name)
                {
                    IsConnected = _playback.IsConnected(info.Id),
                };
                Devices.Add(item);
            }
            else
            {
                existing.IsConnected = _playback.IsConnected(info.Id);
            }
        }

        EmptyHint.Visibility = Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateHero();
    }

    private void OnConnectionChanged()
    {
        string? connectedId = _playback.ConnectedDeviceId;
        foreach (var item in Devices)
            item.IsConnected = item.Id == connectedId;

        UpdateHero();
        App.UpdateTray();
    }

    private void UpdateHero()
    {
        string? id = _playback.ConnectedDeviceId;
        if (id is not null)
        {
            _playback.TryGetDevice(id, out var info);
            string name = info?.Name ?? App.Settings.LastDeviceName ?? "已配对设备";
            HeroTitle.Text = "正在接收音频";
            HeroSubtitle.Text = $"来自「{name}」的媒体声音正通过本机扬声器播放";
            HeroBadge.Background = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush;
        }
        else
        {
            HeroTitle.Text = AudioPlaybackService.IsSupported ? "等待连接" : "当前系统不支持";
            HeroSubtitle.Text = Devices.Count == 0
                ? "尚无可连接设备，请先打开 Windows 设置完成蓝牙配对"
                : "在下方选择一个设备开始接收音频";
            HeroBadge.Background = Application.Current.Resources["ControlFillColorDefaultBrush"] as Brush;
        }
    }

    private async void OnDeviceItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not BtDeviceItem item) return;

        // 已连接 → 断开
        if (_playback.IsConnected(item.Id))
        {
            _playback.Disconnect(item.Id);
            App.Settings.LastDeviceId = null;
            App.Settings.Save();
            ShowNotice("已断开", $"已停止接收来自「{item.Name}」的音频", InfoBarSeverity.Informational);
            return;
        }

        // 同一时间只保留一个音源，先断开其他连接
        _playback.DisconnectAll();

        bool listenOnly = App.Settings.ConnectionMode == 1;

        ShowNotice("正在连接…",
            listenOnly ? $"正在让本机进入可连接状态，等待「{item.Name}」发起" : $"正在连接到「{item.Name}」",
            InfoBarSeverity.Informational);

        var (ok, message) = await _playback.ConnectAsync(item.Id, listenOnly);

        if (ok)
        {
            App.Settings.LastDeviceId = item.Id;
            App.Settings.LastDeviceName = item.Name;
            App.Settings.Save();
            Log.Write($"连接成功 {item.Id}（listenOnly={listenOnly}）；当前默认输出 = {AudioEndpointService.GetDefaultRenderId()}");

            // 连接后系统可能新增或改变默认端点，重新读取一次
            await LoadOutputsAsync();

            if (listenOnly)
            {
                ShowNotice("本机已就绪，等待手机发起",
                    "现在请在手机上打开「设置 → 蓝牙」，点击这台电脑的名称主动连接一次，然后播放音乐。",
                    InfoBarSeverity.Informational);
            }
            else
            {
                ShowNotice("已连接",
                    $"已与「{item.Name}」建立音频连接。若听不到声音，请依次检查：" +
                    "① 手机正在播放的是音乐/视频（通话与通知不走这条通道）；" +
                    "② 手机蓝牙设置里本机这一项的「媒体音频」开关已开启——这是最常见的原因；" +
                    "③ 下方「当前默认输出」是你要出声的设备（装了 FxSound 等音效软件时要注意）；" +
                    "④ 手机与电脑音量均未静音。",
                    InfoBarSeverity.Success);
            }
        }
        else
        {
            ShowNotice("连接失败", message, InfoBarSeverity.Warning);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            await RefreshCoreAsync();
            if (Devices.Count == 0)
                ShowNotice("没有找到设备", "请打开 Windows 设置完成蓝牙配对，然后再次刷新。", InfoBarSeverity.Warning);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    // ---------------- 输出设备 ----------------

    private async Task LoadOutputsAsync()
    {
        _loadingOutputs = true;
        try
        {
            var list = await AudioEndpointService.GetRenderDevicesAsync();
            OutputCombo.Items.Clear();
            foreach (var d in list)
            {
                OutputCombo.Items.Add(new ComboBoxItem
                {
                    Content = d.IsDefault ? $"{d.Name}（当前默认）" : d.Name,
                    Tag = d.Id,
                });
            }

            int index = list.FindIndex(x => x.IsDefault);
            if (index >= 0) OutputCombo.SelectedIndex = index;

            var def = list.FirstOrDefault(x => x.IsDefault);
            DefaultOutputText.Text = def?.Name is null
                ? "当前默认输出：未能确定，请打开系统声音设置确认"
                : $"当前默认输出：{def.Name}";

            UpdateOutputWarning(def);
        }
        finally
        {
            _loadingOutputs = false;
        }
    }

    /// <summary>已知会拦截/吞掉音频流的虚拟声卡与音效增强软件。</summary>
    private static readonly string[] VirtualAudioKeywords =
    {
        "fxsound", "vb-audio", "vb-cable", "voicemeeter", "virtual", "nahimic",
        "dolby", "dts", "sonic studio", "sonic radar", "steam streaming",
        "cable input", "blackhole", "loopback", "mixdown",
    };

    /// <summary>默认输出落在虚拟声卡上时给出醒目警告——这是"连上却没声音"的常见原因。</summary>
    private void UpdateOutputWarning(AudioEndpoint? def)
    {
        if (def is null || string.IsNullOrWhiteSpace(def.Name))
        {
            OutputWarningBar.IsOpen = false;
            return;
        }

        bool isVirtual = VirtualAudioKeywords.Any(
            k => def.Name.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (!isVirtual)
        {
            OutputWarningBar.IsOpen = false;
            return;
        }

        OutputWarningBar.Title = "默认输出落在虚拟声卡上";
        OutputWarningBar.Message =
            $"当前默认输出是「{def.Name}」，这类音效增强/虚拟声卡软件可能拦截蓝牙音频导致无声。" +
            "请点上方「系统声音设置」把输出改为物理扬声器或耳机，或暂时退出该软件后重试。";
        OutputWarningBar.IsOpen = true;
        Log.Write($"警告：默认输出疑似虚拟设备「{def.Name}」");
    }

    private void OnOutputSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOutputs) return;
        if (OutputCombo.SelectedItem is not ComboBoxItem { Tag: string id }) return;

        if (AudioEndpointService.SetDefaultEndpoint(id))
            ShowNotice("已切换输出设备", "后续的蓝牙音频将从此设备播放。", InfoBarSeverity.Success);
        else
            ShowNotice("切换失败", "无法通过程序切换默认播放设备，请在系统声音设置中手动选择。", InfoBarSeverity.Warning);
    }

    // ---------------- 设置 ----------------

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        StartupService.SetEnabled(StartupSwitch.IsOn);
        App.Settings.LaunchAtStartup = StartupSwitch.IsOn;
        App.Settings.Save();
    }

    private void OnAutoReconnectToggled(object sender, RoutedEventArgs e)
    {
        App.Settings.AutoReconnect = AutoReconnectSwitch.IsOn;
        App.Settings.Save();
    }

    private void OnStartMinimizedToggled(object sender, RoutedEventArgs e)
    {
        App.Settings.StartMinimized = StartMinimizedSwitch.IsOn;
        App.Settings.Save();
    }

    private void OnCloseToTrayToggled(object sender, RoutedEventArgs e)
    {
        App.Settings.CloseToTray = CloseToTraySwitch.IsOn;
        App.Settings.Save();
    }

    private void OnNotifyToggled(object sender, RoutedEventArgs e)
    {
        App.Settings.ShowNotifications = NotifySwitch.IsOn;
        App.Settings.Save();
    }

    // ---------------- 外部入口 ----------------

    private static void OpenSettingsUri(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Write($"打开 {uri} 失败", ex);
        }
    }

    private void OnSoundSettingsClick(object sender, RoutedEventArgs e) =>
        OpenSettingsUri("ms-settings:sound");

    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        NoticeBar.Title = title;
        NoticeBar.Message = message;
        NoticeBar.Severity = severity;
        NoticeBar.IsOpen = true;
    }

    /// <summary>供托盘调用：显示或激活窗口。</summary>
    public void BringToFront()
    {
        if (AppWindow.IsVisible == false) AppWindow.Show();
        AppWindow.MoveInZOrderAtTop();
        Activate();
    }

    /// <summary>供托盘调用：连接指定设备（自动重连使用）。</summary>
    public async Task ConnectByIdAsync(string deviceId)
    {
        var (ok, message) = await _playback.ConnectAsync(deviceId, App.Settings.ConnectionMode == 1);
        ShowNotice(ok ? "已连接" : "连接失败",
            ok ? "已自动重连到上次的音源设备。" : message,
            ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    /// <summary>真正退出应用（托盘“退出”菜单使用）。</summary>
    public void ForceClose()
    {
        _reallyClose = true;
        Close();
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_reallyClose) return;

        if (App.Settings.CloseToTray)
        {
            args.Cancel = true;
            sender.Hide();
            App.Tray.ShowBalloon("投音通", "已最小化到托盘，继续在后台接收音频。");
            return;
        }

        args.Cancel = true;
        sender.Hide();
        App.RequestExit();
    }
}
