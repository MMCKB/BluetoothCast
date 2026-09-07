// Copyright (c) 2026 MMCKB
// Licensed under the MIT License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Principal;
using BtAudioSink.Models;
using BtAudioSink.Services;
using BtAudioSink.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BtAudioSink;

public partial class App : Application
{
    /// <summary>蓝牙音频接收服务（全应用唯一）。</summary>
    internal static AudioPlaybackService Playback { get; } = new();

    /// <summary>托盘宿主。</summary>
    internal static TrayHost Tray { get; } = new();

    internal static AppSettings Settings { get; private set; } = new();

    internal static MainWindow? MainWin { get; private set; }

    private static DispatcherQueue? _queue;

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            Log.Write("UI 未处理异常", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("AppDomain 未处理异常", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write("未观察的任务异常", e.Exception);
            e.SetObserved();
        };
    }

    private static Mutex? _singleInstanceMutex;

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 单实例保护：多个实例同时管理同一条蓝牙连接会互相干扰
        _singleInstanceMutex = new Mutex(true, "BluetoothCast_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            IntPtr hwnd = Native.Win32.FindWindowW(null, AppWindowTitle());
            if (hwnd != IntPtr.Zero)
            {
                Native.Win32.ShowWindow(hwnd, Native.Win32.SW_RESTORE);
                Native.Win32.SetForegroundWindow(hwnd);
            }
            Environment.Exit(0);
            return;
        }

        _queue = DispatcherQueue.GetForCurrentThread();
        Settings = AppSettings.Load();

        Log.Clear();
        Log.Write($"启动，命令行：{string.Join(' ', Environment.GetCommandLineArgs())}");
        Log.Write($"AudioPlaybackConnection 可用：{AudioPlaybackService.IsSupported}");

        Playback.StartWatching();
        Playback.ConnectionChanged += (_, _) => _queue?.TryEnqueue(UpdateTray);
        Playback.DevicesChanged += (_, _) => _queue?.TryEnqueue(UpdateTray);

        // 立即枚举一次，便于在日志中确认 A2DP Sink 是否真的可用
        _ = Task.Run(async () =>
        {
            await Playback.RefreshAsync();
            foreach (var d in Playback.Devices)
                Log.Write($"发现音源设备：{d.Name}  [{d.Id}]");
            Log.Write($"共 {Playback.Devices.Count} 个可用音源设备");
            _queue?.TryEnqueue(UpdateTray);
        });

        Tray.MenuCommand += cmd => _queue?.TryEnqueue(() => OnTrayCommand(cmd));
        Tray.Activated += () => _queue?.TryEnqueue(ShowWindow);
        Tray.Start();
        UpdateTray();

        bool silent = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

        if (!silent && !Settings.StartMinimized)
            ShowWindow();

        if (Settings.AutoReconnect && !string.IsNullOrEmpty(Settings.LastDeviceId))
        {
            string id = Settings.LastDeviceId!;
            _ = Task.Delay(1500).ContinueWith(_ => _queue?.TryEnqueue(async () =>
            {
                // 窗口可能还没创建，先确保存在，便于把结果反馈到界面
                ShowWindow();
                var (ok, _) = await Playback.ConnectAsync(id);
                if (ok && Settings.ShowNotifications && MainWin is not null)
                    Tray.ShowBalloon("投音通", $"已自动重连到「{Settings.LastDeviceName ?? "上次的设备"}」");
                UpdateTray();
            }));
        }
    }

    /// <summary>窗口标题：含当前是否以管理员权限运行的标识。</summary>
    internal static string AppWindowTitle()
    {
        string suffix = IsRunningAsAdmin() ? "管理员权限" : "标准权限";
        return $"投音通（{suffix}）";
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ---------------- 窗口 ----------------

    internal static void ShowWindow()
    {
        if (MainWin is null)
        {
            MainWin = new MainWindow();
        }
        MainWin.BringToFront();
    }

    // ---------------- 托盘 ----------------

    internal static void UpdateTray()
    {
        string? connectedId = Playback.ConnectedDeviceId;
        string connectedName = connectedId is not null && Playback.TryGetDevice(connectedId, out var info)
            ? info.Name
            : Settings.LastDeviceName ?? "上次的设备";

        Tray.SetTip(connectedId is not null
            ? $"投音通 · 正在接收「{connectedName}」"
            : "投音通 · 等待音源连接");

        var items = new List<TrayMenuItem>
        {
            new(TrayHost.CmdShow, "打开主窗口"),
            new(TrayHost.CmdConnectLast, "连接上次设备", Grayed: string.IsNullOrEmpty(Settings.LastDeviceId)),
            new(TrayHost.CmdDisconnect, "断开当前连接", Grayed: connectedId is null),
            new(0, string.Empty, IsSeparator: true),
            new(TrayHost.CmdBluetoothSettings, "Windows 蓝牙设置"),
            new(TrayHost.CmdSoundSettings, "Windows 声音设置"),
            new(0, string.Empty, IsSeparator: true),
            new(TrayHost.CmdExit, "退出投音通"),
        };
        Tray.SetMenu(items);
    }

    private static void OnTrayCommand(int cmd)
    {
        switch (cmd)
        {
            case TrayHost.CmdShow:
                ShowWindow();
                break;

            case TrayHost.CmdConnectLast:
                if (!string.IsNullOrEmpty(Settings.LastDeviceId) && MainWin is not null)
                    _ = MainWin.ConnectByIdAsync(Settings.LastDeviceId!);
                else if (!string.IsNullOrEmpty(Settings.LastDeviceId))
                    _ = Playback.ConnectAsync(Settings.LastDeviceId!);
                break;

            case TrayHost.CmdDisconnect:
                Playback.DisconnectAll();
                UpdateTray();
                break;

            case TrayHost.CmdBluetoothSettings:
                OpenSettings("ms-settings:bluetooth");
                break;

            case TrayHost.CmdSoundSettings:
                OpenSettings("ms-settings:sound");
                break;

            case TrayHost.CmdExit:
                RequestExit();
                break;
        }
    }

    private static void OpenSettings(string uri)
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

    /// <summary>彻底退出（托盘“退出”或窗口关闭且未启用隐藏到托盘）。</summary>
    internal static void RequestExit()
    {
        Log.Write("正在退出");
        try
        {
            Playback.DisconnectAll();
            Playback.StopWatching();
            Tray.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write("退出清理失败", ex);
        }
        Environment.Exit(0);
    }
}
