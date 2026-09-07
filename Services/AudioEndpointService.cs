// Copyright (c) 2026 MMCKB
// Licensed under the MIT License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BtAudioSink.Native;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace BtAudioSink.Services;

internal sealed class AudioEndpoint
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; set; }
}

/// <summary>
/// 播放（渲染）端点枚举与默认端点切换。
/// 接收到的蓝牙音频固定送到系统默认输出设备，因此切换默认端点即可改变出声位置。
/// </summary>
internal static class AudioEndpointService
{
    public static async Task<List<AudioEndpoint>> GetRenderDevicesAsync()
    {
        var list = new List<AudioEndpoint>();
        string? defaultId = GetDefaultRenderId();
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);
            foreach (var d in devices)
            {
                list.Add(new AudioEndpoint
                {
                    Id = d.Id,
                    Name = string.IsNullOrWhiteSpace(d.Name) ? "(未命名设备)" : d.Name,
                    IsDefault = d.Id == defaultId,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Write("枚举播放设备失败", ex);
        }
        return list.OrderByDescending(x => x.IsDefault).ThenBy(x => x.Name).ToList();
    }

    public static string? GetDefaultRenderId()
    {
        try
        {
            return MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
        }
        catch (Exception ex)
        {
            Log.Write("获取默认播放设备失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 通过未文档化的 IPolicyConfig 设置默认播放设备。
    /// 该接口在 Windows 10/11 上长期稳定，但不属于公开契约，任何失败都只降级为“不切换”。
    /// </summary>
    public static bool SetDefaultEndpoint(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;

        Log.Write($"尝试切换默认端点，目标 ID = {deviceId}");
        Log.Write($"切换前默认 ID = {GetDefaultRenderId()}");

        object? policy = null;
        try
        {
            policy = new Win32.PolicyConfigClient();
            var config = (Win32.IPolicyConfig)policy;
            // ERole: eConsole = 0, eMultimedia = 1, eCommunications = 2
            int hr = config.SetDefaultEndpoint(deviceId, 0);
            if (hr < 0)
            {
                Log.Write($"SetDefaultEndpoint(eConsole) 失败，HRESULT=0x{hr:X8}（接口为未文档化实现，可能因系统版本变化而失效）");
                return false;
            }
            // 同步切换多媒体角色，避免不同 API 路径拿到不同设备
            config.SetDefaultEndpoint(deviceId, 1);
            Log.Write($"切换后默认 ID = {GetDefaultRenderId()}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("切换默认播放设备失败", ex);
            return false;
        }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy))
                Marshal.ReleaseComObject(policy);
        }
    }
}
