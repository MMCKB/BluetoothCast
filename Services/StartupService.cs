// Copyright (c) 2026 MMCKB
// Licensed under the MIT License. See LICENSE file in the project root.

using System;
using Microsoft.Win32;

namespace BtAudioSink.Services;

/// <summary>开机自启管理（写入 HKCU Run 项，无需管理员权限）。</summary>
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BtAudioSink";

    public static string? ExecutablePath => Environment.ProcessPath;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex)
        {
            Log.Write("读取开机自启状态失败", ex);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                string exe = ExecutablePath ?? string.Empty;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\" --silent");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            Log.Write("设置开机自启失败", ex);
        }
    }
}
