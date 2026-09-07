// Copyright (c) 2026 MMCKB
// Licensed under the MIT License. See LICENSE file in the project root.

using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace BtAudioSink.Models;

/// <summary>设备列表中的一项，承载展示所需的派生属性。</summary>
public sealed class BtDeviceItem : INotifyPropertyChanged
{
    private static readonly SolidColorBrush ConnectedBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x1D, 0x9E, 0x75));

    private static readonly SolidColorBrush IdleBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x88, 0x87, 0x80));

    private bool _isConnected;
    private string _statusText = "未连接";

    public event PropertyChangedEventHandler? PropertyChanged;

    public BtDeviceItem(string id, string name)
    {
        Id = id;
        Name = name;
        Initial = GetInitial(name);
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>头像上显示的首字符（中文取首字，英文取首字母大写）。</summary>
    public string Initial { get; }

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            StatusText = value ? "正在接收音频" : "未连接";
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusBrush));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public Brush StatusBrush => _isConnected ? ConnectedBrush : IdleBrush;

    private static string GetInitial(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var info = new StringInfo(name.Trim());
        return info.LengthInTextElements > 0
            ? info.SubstringByTextElements(0, 1).ToUpperInvariant()
            : "?";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
