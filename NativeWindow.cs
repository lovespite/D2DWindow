using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace D2DWindow;

public abstract class NativeWindow : IWin32Owner, IDisposable
{
    public abstract string WindowClassName { get; }

    private bool _visible = true;
    private nint _handle;
    private Size _sz = new(100, 100);
    private Win32Native.WndProcDelegate? _wndProcDelegate;

    // 全屏/窗口状态存储
    private bool _isFullScreen = false;
    private Win32Native.RECT _savedWindowRect;
    private uint _savedWindowStyle;

    #region Events

    /// <summary>
    /// 当窗口大小改变时触发。
    /// </summary>
    public event Action<int, int>? Resize;

    #endregion

    protected NativeWindow(bool visible = true)
    {
        _visible = visible;
        CreateWindowInstanceInternal();
    }

    #region Core Initialization

    private void CreateWindowInstanceInternal()
    {
        nint hInstance = Win32Native.GetModuleHandle(null);
        _wndProcDelegate = new Win32Native.WndProcDelegate(WndProc);

        var wndClass = new Win32Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Win32Native.WNDCLASSEX>(),
            style = Win32Native.CS_HREDRAW | Win32Native.CS_VREDRAW | Win32Native.CS_OWNDC,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            hCursor = Win32Native.LoadCursor(IntPtr.Zero, Win32Native.IDC_ARROW),
            lpszClassName = WindowClassName,
        };

        if (Win32Native.RegisterClassEx(ref wndClass) == 0)
        {
            var error = Marshal.GetLastSystemError();
            var exception = new System.ComponentModel.Win32Exception(error);
            throw exception;
        }

        var rect = new Win32Native.RECT { Left = 0, Top = 0, Right = _sz.Width, Bottom = _sz.Height };
        Win32Native.AdjustWindowRect(ref rect, Win32Native.WS_OVERLAPPEDWINDOW, false);

        uint dwStyle = _visible ? (Win32Native.WS_OVERLAPPEDWINDOW | Win32Native.WS_VISIBLE) :
            Win32Native.WS_OVERLAPPEDWINDOW;

        _handle = Win32Native.CreateWindowEx(
            dwExStyle: 0,
            lpClassName: wndClass.lpszClassName, lpWindowName: string.Empty,
            dwStyle,
            x: Win32Native.CW_USEDEFAULT, y: Win32Native.CW_USEDEFAULT,
            nWidth: rect.Right - rect.Left, nHeight: rect.Bottom - rect.Top,
            hWndParent: 0, hMenu: 0, hInstance, lpParam: 0);

        if (_handle == 0)
        {
            var error = Marshal.GetLastSystemError();
            var exception = new System.ComponentModel.Win32Exception(error);
            throw exception;
        }
    }

    #endregion

    #region Main Message Loop

    /// <summary>
    /// 启动高性能消息循环。
    /// </summary>
    public void Run()
    {
        Win32Native.ShowWindow(_handle, _visible ? Win32Native.SW_SHOWNORMAL : Win32Native.SW_HIDE);
        Win32Native.UpdateWindow(_handle);

        OnLoad();

        var msg = new Win32Native.MSG();

        try
        {
            while (true)
            {
                if (Win32Native.GetMessage(out msg, IntPtr.Zero, 0, 0))
                {
                    Debug.WriteLine("Message: " + msg.message);
                    if (msg.message == Win32Native.WM_QUIT)
                        break;

                    Win32Native.TranslateMessage(ref msg);
                    Win32Native.DispatchMessage(ref msg);
                }
                else
                {
                    Debug.WriteLine("Idle");
                    // OnIdle();
                }
            }
            Debug.WriteLine("Message loop exited.");
        }
        finally
        {
            Dispose();
        }
    }

    #endregion

    #region Public Properties

    /// <summary>
    /// 获取窗口的原生句柄。
    /// </summary>
    /// <remarks>Use this property to access the unmanaged handle when interoperating with native APIs or
    /// performing low-level operations. The validity and lifetime of the handle depend on the state of the parent
    /// object; ensure the object is not disposed before using the handle.</remarks>
    public nint Handle => _handle;

    public bool Visible
    {
        get => IsVisible();
        set
        {
            if (value)
                Show();
            else
                Hide();
        }
    }

    /// <summary>
    /// 获取或设置窗口标题。 
    /// </summary>
    public string Title
    {
        get => GetTitle();
        set => SetTitle(value);
    }

    /// <summary>
    /// 设置或获取窗口宽度。
    /// </summary> 
    /// <remarks>
    /// 返回的是缓存值，并不总是实时查询窗口高度，如果需要准确的值请使用 GetSize() 方法。<br />
    /// 注意：无法通过此属性将高度设置为 0，如果有意设置为 0，请使用 Bounds 属性 或 SetBounds() 方法。
    /// </remarks>
    public int Width
    {
        get => _sz.Width;
        set => SetSize(width: value, height: 0);
    }

    /// <summary>
    /// 设置或获取窗口高度。
    /// </summary>
    /// <remarks>
    /// 返回的是缓存值，并不总是实时查询窗口高度，如果需要准确的值请使用 GetSize() 方法。<br />
    /// 注意：无法通过此属性将高度设置为 0，如果有意设置为 0，请使用 Bounds 属性 或 SetBounds() 方法。
    /// </remarks>
    public int Height
    {
        get => _sz.Height;
        set => SetSize(width: 0, height: value);
    }

    /// <summary>
    /// 获取或设置窗口大小。 
    /// </summary>
    /// <remarks>
    /// 返回的 Size 可能是缓存值，并不总是实时查询窗口大小。<br />
    /// 要获取实时大小，请使用 Bounds 属性 或 GetSize() 或 GetBounds() 方法。 <br />
    /// 将参数设置为 0 可保留当前对应维度的大小。 如果有意设置为 0，请使用 Bounds 属性 或 SetBounds() 方法。
    /// </remarks>
    public Size Size
    {
        get => _sz;
        set => SetSize(value.Width, value.Height);
    }

    /// <summary>
    /// 鼠标当前状态（用于逻辑查询）
    /// </summary>
    public Point MousePosition { get; private set; }

    /// <summary>
    /// 获取或设置窗口位置。
    /// </summary>
    public Point Location
    {
        get => GetLocation();
        set => SetLocation(value.X, value.Y);
    }

    /// <summary>
    /// 获取或设置窗口边界。
    /// </summary>
    public Rectangle Bounds
    {
        get => GetBounds();
        set => SetBounds(value.X, value.Y, value.Width, value.Height);
    }

    /// <summary>
    /// 获取或设置是否显示最小化按钮。
    /// </summary>
    public bool MinimizeBox
    {
        get => HasStyle(Win32Native.WS_MINIMIZEBOX);
        set => SetStyle(Win32Native.WS_MINIMIZEBOX, value);
    }

    /// <summary>
    /// 获取或设置是否显示最大化按钮。
    /// </summary>
    public bool MaximizeBox
    {
        get => HasStyle(Win32Native.WS_MAXIMIZEBOX);
        set => SetStyle(Win32Native.WS_MAXIMIZEBOX, value);
    }

    #endregion

    #region Public Methods

    public void Close()
    {
        var ok = Win32Native.PostMessage(_handle, Win32Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (!ok)
        {
            Debug.WriteLine("NativeWindow Close: return false");
        }
    }

    /// <summary>
    /// 设置或取消窗口的置顶（TopMost）状态。
    /// </summary>
    /// <param name="topmost">如果为 true，窗口将置顶；否则取消置顶。</param>
    public void SetTopMost(bool topmost)
    {
        if (_handle == 0) return;

        IntPtr hWndInsertAfter = topmost
            ? Win32Native.HWND_TOPMOST // 置顶
            : Win32Native.HWND_NOTOPMOST; // 取消置顶

        // 使用 SWP_NOMOVE 和 SWP_NOSIZE 标志来保持窗口当前的位置和大小不变，
        // 只改变 Z 轴顺序 (Z-order)。
        const uint flags = Win32Native.SWP_NOMOVE | Win32Native.SWP_NOSIZE;

        Win32Native.SetWindowPos(
            _handle,
            hWndInsertAfter,
            0, 0, 0, 0, // X, Y, cx, cy 在设置 SWP_NOMOVE|SWP_NOSIZE 时被忽略
            flags);
    }

    public void SetTitle(string title)
    {
        if (_handle == 0) return;
        Win32Native.SetWindowText(_handle, title);
    }

    public string GetTitle() => GetTitleInternal(out string title) ? title : string.Empty;

    private unsafe bool GetTitleInternal(out string title)
    {
        title = string.Empty;
        if (_handle == 0) return false;

        const int nChar = 256;
        // 直接分配 char，无需 * 2
        Span<char> span = stackalloc char[nChar];

        fixed (char* ptr = span)
        {
            int len = Win32Native.GetWindowTextPtr(_handle, ptr, nChar);
            if (len <= 0) return false;
            title = new string(span[..len]);
            return true;
        }
    }

    /// <summary>
    /// 设置窗口大小。
    /// </summary>
    /// <param name="width">
    /// 窗口的宽度，设置为 0 则保留当前宽度。该值被限制为最小值 1。
    /// </param>
    /// <param name="height">
    /// 窗口的高度，设置为 0 则保留当前高度。该值被限制为最小值 1。 
    /// </param>
    /// <remarks>
    /// 将参数设置为 0 可保留当前对应维度的大小。 如果有意设置为 0，请使用 Bounds 属性 或 SetBounds() 方法。
    /// </remarks>
    public void SetSize(int width, int height)
    {
        if (_handle == 0) return;
        if (width == 0 || height == 0)
        {
            var sz = GetSize();
            if (width == 0) width = sz.Width;
            if (height == 0) height = sz.Height;
        }

        width = Math.Clamp(width, 1, int.MaxValue);
        height = Math.Clamp(height, 1, int.MaxValue);
        Win32Native.SetWindowPos(
            _handle,
            0,
            0, 0,
            width,
            height,
            Win32Native.SWP_NOMOVE | Win32Native.SWP_NOZORDER);
    }
    /// <summary> 
    /// 获取实时窗口大小。
    /// </summary>
    /// <remarks>
    /// 每次调用都查询当前窗口大小并更新缓存值。
    /// </remarks>
    /// <returns></returns>
    public Size GetSize()
    {
        if (_handle == IntPtr.Zero) return Size.Empty;
        Win32Native.GetWindowRect(_handle, out Win32Native.RECT rect);
        return _sz = new Size(rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public void Hide()
    {
        _visible = false;
        Win32Native.ShowWindow(_handle, Win32Native.SW_HIDE);
    }

    public void Show()
    {
        _visible = true;
        Win32Native.ShowWindow(_handle, Win32Native.SW_SHOWNORMAL);
    }

    public bool IsVisible()
    {
        if (_handle == IntPtr.Zero) return false;
        return _visible = Win32Native.IsWindowVisible(_handle);
    }

    public void SetLocation(int x, int y)
    {
        if (_handle != IntPtr.Zero)
        {
            Win32Native.SetWindowPos(
                _handle,
                IntPtr.Zero,
                x,
                y,
                0,
                0,
                Win32Native.SWP_NOSIZE | Win32Native.SWP_NOZORDER);
        }
    }
    public Point GetLocation()
    {
        if (_handle == IntPtr.Zero) return Point.Empty;
        Win32Native.GetWindowRect(_handle, out Win32Native.RECT rect);
        return new Point(rect.Left, rect.Top);
    }

    public void SetBounds(int x, int y, int width, int height)
    {
        if (_handle == 0) return;
        width = Math.Clamp(width, 1, int.MaxValue);
        height = Math.Clamp(height, 1, int.MaxValue);
        Win32Native.SetWindowPos(
            _handle,
            0,
            x,
            y,
            width,
            height,
            Win32Native.SWP_NOZORDER);
    }
    /// <summary>
    /// 获取实时窗口边界。
    /// </summary>
    /// <remarks>
    /// 不会使用缓存值，而是每次调用都查询当前窗口位置和大小。
    /// </remarks>
    /// <returns></returns>
    public Rectangle GetBounds()
    {
        if (_handle == 0) return Rectangle.Empty;
        Win32Native.GetWindowRect(_handle, out Win32Native.RECT rect);
        _sz = new Size(rect.Right - rect.Left, rect.Bottom - rect.Top);
        return new Rectangle(new Point(rect.Left, rect.Top), _sz);
    }

    /// <summary>
    /// 获取窗口边框样式。 
    /// </summary>
    public WindowBorderStyle GetBorderStyle()
    {
        if (HasStyle(Win32Native.WS_POPUP)) return WindowBorderStyle.None;
        if (HasStyle(Win32Native.WS_THICKFRAME)) return WindowBorderStyle.Sizable;
        return WindowBorderStyle.FixedSingle;
    }

    /// <summary>
    /// 设置窗口边框样式。
    /// </summary>
    public void SetBorderStyle(WindowBorderStyle value)
    {
        // 重置相关样式
        uint style = (uint)Win32Native.GetWindowLong(_handle, Win32Native.GWL_STYLE).ToInt64();
        style &= ~(Win32Native.WS_POPUP | Win32Native.WS_THICKFRAME | Win32Native.WS_CAPTION | Win32Native.WS_SYSMENU);

        switch (value)
        {
            case WindowBorderStyle.None:
                style |= Win32Native.WS_POPUP | Win32Native.WS_VISIBLE;
                break;
            case WindowBorderStyle.Sizable:
                style |= Win32Native.WS_OVERLAPPEDWINDOW | Win32Native.WS_VISIBLE;
                break;
            case WindowBorderStyle.FixedSingle:
                style |= (Win32Native.WS_OVERLAPPED | Win32Native.WS_CAPTION | Win32Native.WS_SYSMENU | Win32Native.WS_MINIMIZEBOX | Win32Native.WS_VISIBLE);
                break;
        }

        Win32Native.SetWindowLong(_handle, Win32Native.GWL_STYLE, (IntPtr)style);
        // 触发布局刷新
        Win32Native.SetWindowPos(_handle, IntPtr.Zero, 0, 0, 0, 0,
            Win32Native.SWP_NOMOVE | Win32Native.SWP_NOSIZE | Win32Native.SWP_NOZORDER | Win32Native.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// 获取或设置窗口状态（最大化、最小化、正常）。
    /// </summary>
    public WindowState GetWindowState()
    {
        if (Win32Native.IsIconic(_handle)) return WindowState.Minimized;
        if (Win32Native.IsZoomed(_handle)) return WindowState.Maximized;
        return WindowState.Normal;
    }

    /// <summary>
    /// 设置窗口状态（最大化、最小化、正常）。 
    /// </summary>
    /// <param name="value"></param>
    public void SetWindowState(WindowState value)
    {
        switch (value)
        {
            case WindowState.Normal:
                Win32Native.ShowWindow(_handle, Win32Native.SW_RESTORE);
                break;
            case WindowState.Minimized:
                Win32Native.ShowWindow(_handle, Win32Native.SW_SHOWMINIMIZED);
                break;
            case WindowState.Maximized:
                Win32Native.ShowWindow(_handle, Win32Native.SW_SHOWMAXIMIZED);
                break;
        }
    }

    /// <summary>
    /// 切换全屏模式。
    /// 在全屏模式下，窗口将变为无边框并覆盖整个主屏幕。
    /// 再次调用将还原到之前的窗口大小和样式。
    /// </summary>
    public void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            // 进入全屏：保存当前状态
            _savedWindowStyle = (uint)Win32Native.GetWindowLong(_handle, Win32Native.GWL_STYLE).ToInt64();
            Win32Native.GetWindowRect(_handle, out _savedWindowRect);

            // 设置为无边框
            uint style = _savedWindowStyle;
            style &= ~Win32Native.WS_OVERLAPPEDWINDOW; // 移除标题栏、边框等
            style |= Win32Native.WS_POPUP;
            Win32Native.SetWindowLong(_handle, Win32Native.GWL_STYLE, (IntPtr)style);

            // 获取屏幕尺寸
            int screenWidth = Win32Native.GetSystemMetrics(Win32Native.SM_CXSCREEN);
            int screenHeight = Win32Native.GetSystemMetrics(Win32Native.SM_CYSCREEN);

            // 调整窗口位置和大小覆盖全屏
            Win32Native.SetWindowPos(_handle, Win32Native.HWND_TOPMOST, 0, 0, screenWidth, screenHeight, Win32Native.SWP_FRAMECHANGED | Win32Native.SWP_SHOWWINDOW);

            _isFullScreen = true;
        }
        else
        {
            // 退出全屏：还原样式
            Win32Native.SetWindowLong(_handle, Win32Native.GWL_STYLE, (IntPtr)_savedWindowStyle);

            // 还原位置和大小
            int w = _savedWindowRect.Right - _savedWindowRect.Left;
            int h = _savedWindowRect.Bottom - _savedWindowRect.Top;

            Win32Native.SetWindowPos(_handle, Win32Native.HWND_NOTOPMOST,
                _savedWindowRect.Left, _savedWindowRect.Top, w, h,
                Win32Native.SWP_FRAMECHANGED | Win32Native.SWP_SHOWWINDOW);

            _isFullScreen = false;
        }
    }

    #endregion

    #region Style Helpers

    private bool HasStyle(uint styleToCheck)
    {
        uint style = (uint)Win32Native.GetWindowLong(_handle, Win32Native.GWL_STYLE).ToInt64();
        return (style & styleToCheck) == styleToCheck;
    }

    private void SetStyle(uint styleFlag, bool enable)
    {
        uint style = (uint)Win32Native.GetWindowLong(_handle, Win32Native.GWL_STYLE).ToInt64();
        if (enable)
            style |= styleFlag;
        else
            style &= ~styleFlag;

        Win32Native.SetWindowLong(_handle, Win32Native.GWL_STYLE, (IntPtr)style);
        Win32Native.SetWindowPos(_handle, IntPtr.Zero, 0, 0, 0, 0,
            Win32Native.SWP_NOMOVE | Win32Native.SWP_NOSIZE | Win32Native.SWP_NOZORDER | Win32Native.SWP_FRAMECHANGED);
    }

    #endregion

    #region Virtual Methods

    protected virtual void OnIdle() { }

    /// <summary>
    /// Raises the resize event to notify subscribers that the dimensions have changed.
    /// </summary>
    /// <remarks>Override this method in a derived class to provide custom handling when the size changes.
    /// This method invokes the Resize event if any handlers are attached.</remarks>
    /// <param name="width">The new width, in pixels, after the resize operation. Must be non-negative.</param>
    /// <param name="height">The new height, in pixels, after the resize operation. Must be non-negative.</param>
    protected virtual void OnResize(int width, int height)
    {
        Resize?.Invoke(width, height);
    }

    protected virtual void OnLoad()
    {
    }

    #endregion

    #region Window Message Processing 

    protected virtual nint HandleWndProc(nint hWnd, uint msg, nint wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Native.WM_DESTROY:
                {
                    Win32Native.PostQuitMessage(0);
                    Win32Native.DestroyWindow(_handle);
                    return nint.Zero;
                }

            case Win32Native.WM_ERASEBKGND:
                {
                    return (nint)1;
                }

            case Win32Native.WM_SIZE:
                {
                    int w = (int)(lParam.ToInt64() & 0xFFFF);
                    int h = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                    _sz.Width = w;
                    _sz.Height = h;
                    OnResize(w, h);
                    return nint.Zero;
                }
        }

        return Win32Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        return HandleWndProc(hWnd, msg, wParam, lParam);
    }

    #endregion

    #region IDisposable Support

    public virtual void Dispose()
    {
        Debug.WriteLine("Disposing...");
        if (_handle != IntPtr.Zero)
        {
            // 1. 尝试销毁窗口
            // 如果是因为异常退出循环，窗口此时还存在，必须销毁。
            // 如果是因为用户点击关闭，窗口已被系统销毁，此调用会失败但无害。
            // 注意：DestroyWindow 必须在创建窗口的线程（主线程）调用。
            Win32Native.DestroyWindow(_handle);

            // 2. 注销窗口类
            // 只有当该类没有关联的窗口时，注销才会成功，所以必须先 DestroyWindow
            IntPtr hInstance = Win32Native.GetModuleHandle(null);
            Win32Native.UnregisterClass(WindowClassName, hInstance);

            _handle = IntPtr.Zero;
        }

        // 窗口销毁通常在 WM_DESTROY 处理，这里仅作为补充清理
        _handle = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    #endregion
}
