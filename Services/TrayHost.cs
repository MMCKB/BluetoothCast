// Copyright (c) 2026 MMCKB
// Licensed under the MIT License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BtAudioSink.Native;

namespace BtAudioSink.Services;

/// <summary>托盘菜单项。Id 为 0 且 IsSeparator 为 true 时表示分隔线。</summary>
internal readonly record struct TrayMenuItem(int Id, string Text, bool IsSeparator = false, bool Grayed = false);

/// <summary>
/// Win32 通知区域图标。运行在独立的后台 STA 线程上（隐藏消息窗口），
/// 事件回调发生在非 UI 线程，调用方需自行切回 UI 线程。
/// </summary>
internal sealed class TrayHost : IDisposable
{
    public const int CmdShow = 1001;
    public const int CmdConnectLast = 1002;
    public const int CmdDisconnect = 1003;
    public const int CmdBluetoothSettings = 1004;
    public const int CmdSoundSettings = 1005;
    public const int CmdExit = 1006;
    public const int CmdDeviceBase = 2000;

    private const uint TrayCallback = Win32.WM_USER + 1;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Win32.WindowProc _proc;
    private readonly object _gate = new();

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private string _tip = "投音通";
    private List<TrayMenuItem> _menu = new();
    private ushort _classAtom;
    private bool _disposed;

    /// <summary>托盘菜单被点击（参数为命令 ID）。</summary>
    public event Action<int>? MenuCommand;

    /// <summary>托盘图标被左键单击。</summary>
    public event Action? Activated;

    public TrayHost()
    {
        _proc = WndProc;   // 保持委托引用，防止被 GC 回收
    }

    public void Start()
    {
        if (_thread is not null) return;

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "TrayHost",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void SetTip(string tip)
    {
        _tip = tip;
        UpdateIcon();
    }

    public void SetMenu(IReadOnlyList<TrayMenuItem> items)
    {
        lock (_gate) _menu = new List<TrayMenuItem>(items);
    }

    /// <summary>显示通知区域气球提示。可从任意线程调用。</summary>
    public void ShowBalloon(string title, string text)
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            var data = new Win32.NOTIFYICONDATAW
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = Win32.NIF_INFO,
                dwInfoFlags = Win32.NIIF_INFO,
                szInfoTitle = Truncate(title, 63),
                szInfo = Truncate(text, 255),
                szTip = _tip,
            };
            Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
        }
        catch (Exception ex)
        {
            Log.Write("显示气球提示失败", ex);
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();

        var wcex = new Win32.WNDCLASSEXW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.WNDCLASSEXW>(),
            lpfnWndProc = _proc,
            lpszClassName = "BtAudioSinkTrayWindow",
        };
        _classAtom = Win32.RegisterClassExW(ref wcex);
        if (_classAtom == 0)
        {
            Log.Write($"注册托盘窗口类失败，错误码 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return;
        }

        _hwnd = Win32.CreateWindowExW(0, "BtAudioSinkTrayWindow", string.Empty, 0,
            0, 0, 0, 0, HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Log.Write($"创建托盘消息窗口失败，错误码 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return;
        }

        try
        {
            _hIcon = LoadAppIcon();
            AddIcon();

            while (Win32.GetMessageW(out Win32.MSG msg, IntPtr.Zero, 0, 0))
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessageW(ref msg);
            }

            RemoveIcon();
        }
        catch (Exception ex)
        {
            // 托盘线程异常不能拖垮整个进程
            Log.Write("托盘消息循环异常，托盘功能已停用", ex);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_DESTROY)
        {
            Win32.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        if (msg == TrayCallback)
        {
            int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == (int)Win32.WM_LBUTTONUP)
            {
                try { Activated?.Invoke(); }
                catch (Exception ex) { Log.Write("托盘左键回调异常", ex); }
            }
            else if (mouseMsg == (int)Win32.WM_RBUTTONUP)
            {
                ShowMenu(hWnd);
            }
            return IntPtr.Zero;
        }

        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu(IntPtr hWnd)
    {
        List<TrayMenuItem> snapshot;
        lock (_gate) snapshot = new List<TrayMenuItem>(_menu);
        if (snapshot.Count == 0) return;

        IntPtr menu = Win32.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            foreach (var item in snapshot)
            {
                if (item.IsSeparator)
                {
                    Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, IntPtr.Zero, string.Empty);
                }
                else
                {
                    uint flags = Win32.MF_STRING | (item.Grayed ? Win32.MF_GRAYED : 0);
                    Win32.AppendMenuW(menu, flags, new IntPtr(item.Id), item.Text);
                }
            }

            Win32.GetCursorPos(out Win32.POINT pt);
            Win32.SetForegroundWindow(hWnd);
            int cmd = Win32.TrackPopupMenu(menu,
                Win32.TPM_LEFTBUTTON | Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY,
                pt.X, pt.Y, 0, hWnd, IntPtr.Zero);

            if (cmd > 0)
            {
                try { MenuCommand?.Invoke(cmd); }
                catch (Exception ex) { Log.Write("托盘命令回调异常", ex); }
            }
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    private void AddIcon()
    {
        var data = new Win32.NOTIFYICONDATAW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
            uCallbackMessage = TrayCallback,
            hIcon = _hIcon,
            szTip = _tip,
        };
        if (!Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref data))
            Log.Write("添加托盘图标失败（不影响主功能）");
    }

    private void UpdateIcon()
    {
        if (_hwnd == IntPtr.Zero) return;
        var data = new Win32.NOTIFYICONDATAW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Win32.NIF_TIP | (Win32.NIF_ICON),
            hIcon = _hIcon,
            szTip = _tip,
        };
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    private void RemoveIcon()
    {
        var data = new Win32.NOTIFYICONDATAW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
        };
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
        if (_hIcon != IntPtr.Zero)
        {
            Win32.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    private static IntPtr LoadAppIcon()
    {
        try
        {
            string dir = AppContext.BaseDirectory;
            string ico = Path.Combine(dir, "Assets", "bluetooth.ico");
            if (!File.Exists(ico))
                ico = Path.Combine(dir, "bluetooth.ico");
            if (File.Exists(ico))
            {
                IntPtr h = Win32.LoadImageW(IntPtr.Zero, ico, Win32.IMAGE_ICON, 0, 0, Win32.LR_LOADFROMFILE);
                if (h != IntPtr.Zero) return h;
            }
        }
        catch (Exception ex)
        {
            Log.Write("加载托盘图标失败", ex);
        }
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                Win32.PostMessageW(_hwnd, Win32.WM_DESTROY, IntPtr.Zero, IntPtr.Zero);
                _thread?.Join(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex)
        {
            Log.Write("释放托盘资源失败", ex);
        }
    }
}
