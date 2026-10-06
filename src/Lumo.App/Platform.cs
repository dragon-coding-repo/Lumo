using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Lumo.App.Win;

/// <summary>全局快捷键。用 <c>RegisterHotKey</c> 而不是低层键盘钩子 —— 零常驻开销。</summary>
internal sealed partial class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Dictionary<int, Action> _actions = new();
    /// <summary>句柄还没创建时先攒着，等 <see cref="Attach"/> 时统一注册。</summary>
    private readonly List<(string Accel, Action Action)> _pending = new();
    private IntPtr _hWnd;
    private int _nextId = 1;
    private bool _disposed;

    /// <summary>
    /// 句柄就绪后调用，把之前攒下的快捷键真正注册上去。
    /// </summary>
    /// <remarks>
    /// 刻意不在构造函数里接收 <c>Handle</c>：访问 <c>Control.Handle</c> 会
    /// 强制创建窗口句柄，进而触发 <c>WndProc</c> —— 那时本对象还没赋值完，
    /// 构造函数里的 <c>_hotkeys = new HotkeyManager(Handle)</c> 就会 NRE。
    /// </remarks>
    public void Attach(IntPtr hWnd)
    {
        _hWnd = hWnd;
        if (_disposed) return;
        var queued = _pending.ToList();
        _pending.Clear();
        foreach (var (accel, action) in queued) RegisterNow(accel, action);
    }

    /// <summary>注册一个快捷键。<paramref name="accel"/> 形如 <c>Alt+D</c>。</summary>
    /// <returns>句柄就绪时表示注册是否成功；句柄未就绪会先攒下，返回 true。</returns>
    public bool Register(string accel, Action action)
    {
        if (_disposed || string.IsNullOrWhiteSpace(accel) || action is null) return false;
        if (!TryParse(accel, out _, out _)) return false;
        if (_hWnd == IntPtr.Zero)
        {
            _pending.Add((accel, action));
            return true;
        }
        return RegisterNow(accel, action);
    }

    private bool RegisterNow(string accel, Action action)
    {
        if (!TryParse(accel, out var mods, out var vk)) return false;
        var id = _nextId++;
        if (!RegisterHotKey(_hWnd, id, mods, vk)) return false;
        _actions[id] = action;
        return true;
    }

    public void Clear()
    {
        _pending.Clear();
        if (_hWnd == IntPtr.Zero) return;
        foreach (var id in _actions.Keys) UnregisterHotKey(_hWnd, id);
        _actions.Clear();
    }

    /// <summary>由窗口消息循环转发进来。</summary>
    public void OnMessage(ref Message m)
    {
        if (m.Msg != WmHotkey) return;
        if (_actions.TryGetValue(m.WParam.ToInt32(), out var action)) action();
    }

    /// <summary>解析 <c>Alt+Shift+D</c> 这类写法。解析不了就返回 false，不猜。</summary>
    internal static bool TryParse(string accel, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        var parts = accel.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        // 最后一个是主键，其余是修饰键。
        // 每个分支显式写成 uint：字面量默认是 int，混进 uint 的 |= 会触发符号扩展，
        // 位运算结果可能悄悄带上高位的 1。
        foreach (var mod in parts[..^1])
        {
            var bit = mod.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => 0x0002u,
                "ALT" => 0x0001u,
                "SHIFT" => 0x0004u,
                "WIN" => 0x0008u,
                _ => 0u,
            };
            mods |= bit;
        }

        var key = parts[^1].ToUpperInvariant();
        // 单字符走虚拟键码表
        if (key.Length == 1 && char.IsAsciiLetter(key[0]))
        {
            vk = (uint)key[0];
        }
        else
        {
            vk = key switch
            {
                "SPACE" => 0x20,
                "ENTER" or "RETURN" => 0x0D,
                "ESC" or "ESCAPE" => 0x1B,
                "TAB" => 0x09,
                "UP" => 0x26,
                "DOWN" => 0x28,
                "LEFT" => 0x25,
                "RIGHT" => 0x27,
                "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
                "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
                "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
                _ => 0,
            };
            if (vk == 0) return false;
        }

        if (mods == 0) return false;  // 裸键会抢走正常输入，必须带修饰键
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 划词取词。
/// </summary>
/// <remarks>
/// 走 .NET 自带的 UIAutomation（<c>System.Windows.Automation</c>），不手写 COM。
/// 取词顺序：TextPattern 的选区 → TextPattern 全文 → ValuePattern 全文。
/// 拿不到时返回 <c>null</c> 而不是报错 —— 「没选中东西」是正常操作，不该弹红字。
/// </remarks>
internal static class Selection
{
    /// <summary>取当前前台窗口里选中的文字。</summary>
    public static string? GetSelectedText()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null) return null;

            // 1) 真正的选区（双击/拖选出来的部分）
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var p))
            {
                var tp = (TextPattern)p;
                try
                {
                    var selection = tp.GetSelection();
                    if (selection.Length > 0)
                    {
                        var text = selection[0].GetText(-1);
                        if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                    }
                }
                catch (COMException) { /* 目标程序没实现完整接口，往下退化 */ }

                // 2) 整个文档范围（有些程序不报选区但认全文）
                try
                {
                    var text = tp.DocumentRange.GetText(-1);
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
                catch (COMException) { /* 同上 */ }
            }

            // 3) ValuePattern —— 普通输入框很多只实现这个
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var v))
            {
                var text = ((ValuePattern)v).Current.Value;
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch (COMException)
        {
            // 目标程序以更高权限运行，或正在退出。静默跳过。
        }
        catch (InvalidOperationException)
        {
            // 桌面正在切换（锁屏、UAC），拿到的元素已失效。
        }
        return null;
    }

    /// <summary>取词并把焦点还给原窗口。</summary>
    public static string? Grab()
    {
        var fg = Native.GetForegroundWindow();
        var text = GetSelectedText();
        if (text is not null && fg != IntPtr.Zero) Native.SetForegroundWindow(fg);
        return text;
    }
}

/// <summary>模拟按键 + 剪贴板 + 屏幕捕获。</summary>
internal static unsafe partial class Native
{
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(
        uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentProcessId();
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessageCore(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>给控件发窗口消息。用于 ScrollToCaret 失效时兜底。</summary>
    public static void SendMessage(Control c, uint msg, nint wParam, nint lParam) =>
        SendMessageCore(c.Handle, msg, wParam, lParam);


    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUT_UNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    private const uint KeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkA = 0x41;
    private const ushort VkV = 0x56;

    /// <summary>
    /// 把译文粘贴回当前焦点窗口。
    /// </summary>
    /// <remarks>
    /// 流程：备份剪贴板 → 写译文 → 抢回前台 → 可选 Ctrl+A → Ctrl+V → 还原剪贴板。
    /// 还原是必须的，否则用户刚复制的东西会被我们悄悄顶掉。
    /// </remarks>
    public static void SendText(string text, bool replaceAll)
    {
        var backup = ReadClipboardText();
        try
        {
            WriteClipboardText(text);

            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                // 不 Attach 的话 SetForegroundWindow 经常被系统忽略，
                // 后续 SendInput 就会打到别的程序去。
                GetWindowThreadProcessId(fg, out _);
                AttachThreadInput(GetCurrentThreadId(), GetWindowThreadProcessId(fg, out _), true);
                SetForegroundWindow(fg);
            }

            if (replaceAll) Press(VkControl, VkA);
            Press(VkControl, VkV);
            Thread.Sleep(60);
        }
        finally
        {
            if (backup is not null) WriteClipboardText(backup);
        }
    }

    private static void Press(params ushort[] keys)
    {
        var inputs = new List<INPUT>(keys.Length * 2);
        foreach (var k in keys)
            inputs.Add(new INPUT { type = 1, u = new INPUT_UNION { ki = new KEYBDINPUT { wVk = k } } });
        foreach (var k in keys)
            inputs.Add(new INPUT
            {
                type = 1,
                u = new INPUT_UNION { ki = new KEYBDINPUT { wVk = k, dwFlags = KeyUp } },
            });
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    // ── 剪贴板 ──

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr GetClipboardData(uint uFormat);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalAlloc(uint uFlags, nint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalFree(IntPtr hMem);

    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public static void WriteClipboardText(string text)
    {
        // 剪贴板要求 NUL 结尾的 UTF-16
        var buf = Encoding.Unicode.GetBytes(text + "\0");
        var size = buf.Length;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                // 被别的进程占用是常态，退避重试
                Thread.Sleep(30 * (attempt + 1));
                continue;
            }
            try
            {
                EmptyClipboard();
                var mem = GlobalAlloc(GmemMoveable, size);
                if (mem == IntPtr.Zero) return;
                var ptr = GlobalLock(mem);
                if (ptr == IntPtr.Zero) { GlobalFree(mem); return; }
                Marshal.Copy(buf, 0, ptr, size);
                GlobalUnlock(mem);
                // 成功之后所有权转移给系统
                if (SetClipboardData(CfUnicodeText, mem) == IntPtr.Zero) GlobalFree(mem);
                return;
            }
            finally
            {
                CloseClipboard();
            }
        }
    }

    public static string? ReadClipboardText()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(20 * (attempt + 1));
                continue;
            }
            try
            {
                var handle = GetClipboardData(CfUnicodeText);
                if (handle == IntPtr.Zero) return null;
                var ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero) return null;
                try
                {
                    var text = Marshal.PtrToStringUni(ptr);
                    return string.IsNullOrEmpty(text) ? null : text;
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
        return null;
    }

    // ── 屏幕捕获 ──

    /// <summary>
    /// 抓取屏幕区域，返回 BGRA 像素。
    /// </summary>
    /// <remarks>
    /// 用 <c>CopyFromScreen</c> 而不是 GDI BitBlt —— .NET 把句柄管理封装好了，
    /// 少一层泄漏风险。DPI 感知由 <c>ApplicationHighDpiMode</c> 负责，
    /// 在 <c>ApplicationConfiguration.Initialize()</c> 里设好。
    /// </remarks>
    public static byte[] CaptureRegion(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("选区为空");

        using var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height),
            System.Drawing.CopyPixelOperation.SourceCopy);

        var rect = new System.Drawing.Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
