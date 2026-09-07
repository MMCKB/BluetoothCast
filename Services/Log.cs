// Copyright (c) 2026 MMCKB
// Licensed under the MIT License. See LICENSE file in the project root.

using System;
using System.IO;
using System.Text;

namespace BtAudioSink.Services;

/// <summary>轻量文件日志，用于排查蓝牙连接问题。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    private static string FilePath
    {
        get
        {
            if (_file is null)
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BtAudioSink", "logs");
                Directory.CreateDirectory(dir);
                _file = Path.Combine(dir, "app.log");
            }
            return _file;
        }
    }

    public static void Clear()
    {
        try
        {
            lock (Gate) File.WriteAllText(FilePath, string.Empty);
        }
        catch { /* 日志失败不影响主流程 */ }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
        }
        catch { }
    }

    public static void Write(string message, Exception ex)
    {
        Write($"{message} :: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
    }
}
