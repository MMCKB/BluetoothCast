// Copyright (c) 2026 MMCKB
// Licensed under the MIT License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;
using Windows.Media.Audio;

namespace BtAudioSink.Services;

/// <summary>
/// 封装 Windows 内置的蓝牙 A2DP Sink 能力（Windows 10 2004 / build 19041 起提供）。
/// 编解码、AVDTP 协商、缓冲与音频路由全部由系统完成，本类只负责连接的生命周期。
/// </summary>
internal sealed class AudioPlaybackService
{
    private const string ApiTypeName = "Windows.Media.Audio.AudioPlaybackConnection";

    private readonly Dictionary<string, AudioPlaybackConnection> _connections =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, DeviceInformation> _devices =
        new(StringComparer.OrdinalIgnoreCase);

    private DeviceWatcher? _watcher;

    /// <summary>当前系统是否提供 AudioPlaybackConnection（win10 2004+）。</summary>
    public static bool IsSupported =>
        ApiInformation.IsTypePresent(ApiTypeName);

    /// <summary>设备列表发生变化（发现/移除/重命名）。</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>某个设备的连接状态变化，参数为设备 ID。</summary>
    public event EventHandler<string>? ConnectionChanged;

    public IReadOnlyList<DeviceInformation> Devices =>
        _devices.Values.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public bool TryGetDevice(string deviceId, out DeviceInformation info) =>
        _devices.TryGetValue(deviceId, out info!);

    /// <summary>该设备当前是否已建立音频连接。</summary>
    public bool IsConnected(string deviceId) =>
        _connections.TryGetValue(deviceId, out var c) &&
        c.State == AudioPlaybackConnectionState.Opened;

    public AudioPlaybackConnectionState StateOf(string deviceId) =>
        _connections.TryGetValue(deviceId, out var c)
            ? c.State
            : AudioPlaybackConnectionState.Closed;

    /// <summary>当前处于已连接状态的设备 ID（最多一个音源比较合理）。</summary>
    public string? ConnectedDeviceId =>
        _connections.FirstOrDefault(kv => kv.Value.State == AudioPlaybackConnectionState.Opened).Key;

    // ---------------- 设备枚举 ----------------

    public void StartWatching()
    {
        if (!IsSupported || _watcher is not null) return;

        try
        {
            _watcher = DeviceInformation.CreateWatcher(AudioPlaybackConnection.GetDeviceSelector());
            _watcher.Added += (_, info) => { Add(info); NotifyDevicesChanged(); };
            _watcher.Removed += (_, info) => { Remove(info.Id); NotifyDevicesChanged(); };
            _watcher.Updated += (_, _) => NotifyDevicesChanged();
            _watcher.EnumerationCompleted += (_, _) => NotifyDevicesChanged();
            _watcher.Start();
            Log.Write("DeviceWatcher 已启动");
        }
        catch (Exception ex)
        {
            Log.Write("启动 DeviceWatcher 失败", ex);
        }
    }

    public void StopWatching()
    {
        try
        {
            _watcher?.Stop();
        }
        catch (Exception ex)
        {
            Log.Write("停止 DeviceWatcher 失败", ex);
        }
        _watcher = null;
    }

    public async Task RefreshAsync()
    {
        if (!IsSupported) return;
        try
        {
            var found = await DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector());
            _devices.Clear();
            foreach (var d in found) Add(d);
            NotifyDevicesChanged();
        }
        catch (Exception ex)
        {
            Log.Write("刷新设备列表失败", ex);
        }
    }

    private void Add(DeviceInformation info)
    {
        if (string.IsNullOrWhiteSpace(info?.Id)) return;
        _devices[info.Id] = info;
    }

    private void Remove(string id)
    {
        _devices.Remove(id);
        if (_connections.TryGetValue(id, out var conn))
        {
            try { conn.Dispose(); } catch { }
            _connections.Remove(id);
        }
    }

    private void NotifyDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    // ---------------- 连接管理 ----------------

    /// <summary>
    /// 建立音频接收连接。
    /// listenOnly=true 时只让系统进入可连接状态，不主动 Open，改由手机端发起 A2DP 连接。
    /// </summary>
    public async Task<(bool Ok, string Message)> ConnectAsync(string deviceId, bool listenOnly = false)
    {
        if (!IsSupported)
            return (false, "当前系统不支持蓝牙音频接收（需要 Windows 10 2004 / build 19041 或更高版本）");

        try
        {
            if (_connections.TryGetValue(deviceId, out var existing))
            {
                try { existing.Dispose(); } catch { }
                _connections.Remove(deviceId);
            }

            var connection = AudioPlaybackConnection.TryCreateFromId(deviceId);
            if (connection is null)
                return (false, "无法为该设备创建连接，请确认设备已在 Windows 蓝牙设置中完成配对，且蓝牙适配器支持 A2DP Sink");

            connection.StateChanged += OnConnectionStateChanged;
            _connections[deviceId] = connection;

            Log.Write($"StartAsync: {deviceId}（listenOnly={listenOnly}）");
            await connection.StartAsync();

            if (listenOnly)
            {
                Log.Write("仅监听模式：等待手机端发起音频连接");
                ConnectionChanged?.Invoke(this, deviceId);
                return (true, "已进入等待状态");
            }

            var result = await connection.OpenAsync();
            Log.Write($"OpenAsync 结果: {result.Status}");

            if (result.Status != AudioPlaybackConnectionOpenResultStatus.Success)
                return (false, Describe(result.Status));

            ConnectionChanged?.Invoke(this, deviceId);
            return (true, "已连接");
        }
        catch (Exception ex)
        {
            Log.Write($"连接失败: {deviceId}", ex);
            return (false, ex.Message);
        }
    }

    /// <summary>断开指定设备的音频连接。</summary>
    public void Disconnect(string deviceId)
    {
        if (!_connections.TryGetValue(deviceId, out var conn)) return;
        try
        {
            conn.StateChanged -= OnConnectionStateChanged;
            conn.Dispose();
            Log.Write($"已断开: {deviceId}");
        }
        catch (Exception ex)
        {
            Log.Write("断开连接失败", ex);
        }
        finally
        {
            _connections.Remove(deviceId);
            ConnectionChanged?.Invoke(this, deviceId);
        }
    }

    public void DisconnectAll()
    {
        foreach (var id in _connections.Keys.ToList()) Disconnect(id);
    }

    private void OnConnectionStateChanged(AudioPlaybackConnection sender, object args)
    {
        string id = sender.DeviceId;
        Log.Write($"状态变化: {id} -> {sender.State}");

        if (sender.State == AudioPlaybackConnectionState.Closed)
            _connections.Remove(id);

        ConnectionChanged?.Invoke(this, id);
    }

    private static string Describe(AudioPlaybackConnectionOpenResultStatus status) => status switch
    {
        AudioPlaybackConnectionOpenResultStatus.Success => "已连接",
        AudioPlaybackConnectionOpenResultStatus.RequestTimedOut =>
            "连接超时。请确认手机端的媒体音频输出已切到本机，并靠近电脑重试。",
        AudioPlaybackConnectionOpenResultStatus.DeniedBySystem =>
            "系统拒绝了本次连接。请到「设置 → 蓝牙和其他设备」重新配对，或检查蓝牙适配器是否支持 A2DP Sink。",
        _ => "连接失败（未知原因）。可尝试删除配对后重新配对。",
    };
}
