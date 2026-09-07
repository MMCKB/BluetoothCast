// Copyright (c) 2026 MMCKB
// Licensed under the MIT License. See LICENSE file in the project root.

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BtAudioSink.Models;

/// <summary>应用设置，持久化到 %LOCALAPPDATA%\BtAudioSink\settings.json。</summary>
internal sealed class AppSettings
{
    /// <summary>开机自动启动。</summary>
    public bool LaunchAtStartup { get; set; }

    /// <summary>启动时自动重连上次使用的设备。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>启动时最小化到托盘，不显示主窗口。</summary>
    public bool StartMinimized { get; set; }

    /// <summary>连接状态变化时弹出 Windows 通知。</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>关闭窗口时隐藏到托盘而不是退出。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>上次成功连接的设备名（用于自动重连）。</summary>
    public string? LastDeviceId { get; set; }

    /// <summary>上次成功连接的设备名（仅用于展示）。</summary>
    public string? LastDeviceName { get; set; }

    /// <summary>期望的输出端点 ID（留空表示跟随系统默认）。</summary>
    public string? PreferredOutputDeviceId { get; set; }

    /// <summary>
    /// 连接方式：0 = 主动连接（Start 后立即 Open，由本机发起）；
    /// 1 = 仅监听（只 Start，等手机端主动发起音频连接）。
    /// 某些手机在「本机主动发起」时状态显示已连接但不推流，改用仅监听可解决。
    /// </summary>
    public int ConnectionMode { get; set; }

    [JsonIgnore]
    public static AppSettings Current { get; set; } = new();

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BtAudioSink", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            string path = FilePath;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Services.Log.Write("读取设置失败，使用默认值", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch (Exception ex)
        {
            Services.Log.Write("保存设置失败", ex);
        }
    }
}
