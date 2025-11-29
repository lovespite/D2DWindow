using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.Mathematics.Interop;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace D2DWindow;

/// <summary>
/// 窗口边框样式。
/// </summary>
public enum WindowBorderStyle
{
    Sizable,     // 可调整大小 (WS_THICKFRAME)
    FixedSingle, // 固定边框，不可调整大小
    None         // 无边框 (WS_POPUP)
}

/// <summary>
/// 窗口状态。
/// </summary>
public enum WindowState
{
    Normal,
    Minimized,
    Maximized
}

/// <summary>
/// 一个封装了纯 Win32 API 和 Direct2D 渲染环境的抽象基类。
/// 实现了高性能消息循环，适合游戏开发。
/// </summary>
public abstract class Direct2D1Window : IDisposable, IWin32Owner
{
    private readonly Stopwatch _sw = new();
    private nint _handle;
    private Size _sz;
    private long _lastFrameTicks = 0;
    private Win32Native.WndProcDelegate? _wndProcDelegate;
    public abstract string WindowClassName { get; }
    private bool _isDeviceReady;
    private RawColor4 _backgroundColor = new(0, 0, 0, 1); // 默认黑色背景

    // 全屏/窗口状态存储
    private bool _isFullScreen = false;
    private Win32Native.RECT _savedWindowRect;
    private uint _savedWindowStyle;

    // Direct2D 资源
    private Factory1? _factory;
    private WindowRenderTarget? _renderTarget;

    // HID输入事件系统
    #region HID Event System

    private readonly RawMouseEventArgs _cachedMouseArgs = new();
    private readonly RawKeyEventArgs _cachedKeyArgs = new();
    private readonly RawKeyPressEventArgs _cachedKeyPressArgs = new();

    public event Action<RawMouseEventArgs>? MouseMove;
    public event Action<RawMouseEventArgs>? MouseDown;
    public event Action<RawMouseEventArgs>? MouseUp;
    public event Action<RawMouseEventArgs>? MouseWheel;
    public event Action<RawKeyEventArgs>? KeyDown;
    public event Action<RawKeyEventArgs>? KeyUp;
    public event Action<RawKeyPressEventArgs>? KeyPress;

    protected virtual void OnMouseDown(Point p, MouseButton button)
    {
        MouseDown?.Invoke(_cachedMouseArgs);
    }

    protected virtual void OnMouseUp(Point p, MouseButton button)
    {
        MouseUp?.Invoke(_cachedMouseArgs);
    }

    protected virtual void OnMouseMove(Point p)
    {
        MouseMove?.Invoke(_cachedMouseArgs);
    }

    protected virtual void OnMouseWheel(int delta)
    {
        MouseWheel?.Invoke(_cachedMouseArgs);
    }

    protected virtual void OnKeyUp(int key, KeyModifiers modifiers)
    {
        KeyUp?.Invoke(_cachedKeyArgs);
    }

    protected virtual void OnKeyDown(int key, KeyModifiers modifiers)
    {
        KeyDown?.Invoke(_cachedKeyArgs);
    }

    protected virtual void OnKeyPress(char keyChar, KeyModifiers modifiers)
    {
        KeyPress?.Invoke(_cachedKeyPressArgs);
    }

    #endregion

    // 提供给子类访问 RenderTarget
    protected WindowRenderTarget RenderTarget => _renderTarget ?? throw new InvalidOperationException("RenderTarget is not initialized yet.");
    protected Factory1 Factory => _factory ?? throw new InvalidOperationException("Factory is not initialized yet.");

    #region Events

    /// <summary>
    /// 当窗口大小改变时触发。
    /// </summary>
    public event Action<int, int>? Resize;

    /// <summary>
    /// Occurs when the rendering device has been initialized and is ready for use.
    /// </summary>
    /// <remarks>Subscribers can use this event to perform setup or resource allocation that depends on the
    /// device being available. The event provides a <see cref="RenderTarget"/> instance representing the active
    /// rendering target.</remarks>
    public event Action<RenderTarget>? DeviceReady;

    #endregion

    protected Direct2D1Window(string title, int width, int height)
    {
        _sz = new Size(width, height);
        CreateInternal();
        InitializeDirect2D();
        SetTitle(title);
    }

    #region Core Initialization

    private void CreateInternal()
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
            lpszClassName = WindowClassName
        };

        if (Win32Native.RegisterClassEx(ref wndClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            var exception = new System.ComponentModel.Win32Exception(error);
            throw exception;
        }

        var rect = new Win32Native.RECT { Left = 0, Top = 0, Right = _sz.Width, Bottom = _sz.Height };
        Win32Native.AdjustWindowRect(ref rect, Win32Native.WS_OVERLAPPEDWINDOW, false);

        _handle = Win32Native.CreateWindowEx(
            dwExStyle: 0,
            lpClassName: wndClass.lpszClassName,
            lpWindowName: Title,
            dwStyle: Win32Native.WS_OVERLAPPEDWINDOW | Win32Native.WS_VISIBLE,
            Win32Native.CW_USEDEFAULT, Win32Native.CW_USEDEFAULT,
            rect.Right - rect.Left, rect.Bottom - rect.Top,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            var exception = new System.ComponentModel.Win32Exception(error);
            throw exception;
        }
    }

    private void InitializeDirect2D()
    {
        _factory = new Factory1();

        var pixelFormat = new PixelFormat(SharpDX.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);
        var renderTargetProperties = new RenderTargetProperties(RenderTargetType.Default, pixelFormat, 96, 96, RenderTargetUsage.None, FeatureLevel.Level_DEFAULT);
        var hwndRenderTargetProperties = new HwndRenderTargetProperties
        {
            Hwnd = _handle,
            PixelSize = new Size2(Width, Height),
            PresentOptions = PresentOptions.None, // 垂直同步可在这里控制 
        };

        _renderTarget = new WindowRenderTarget(_factory, renderTargetProperties, hwndRenderTargetProperties)
        {
            AntialiasMode = AntialiasMode.PerPrimitive
        };
        _isDeviceReady = true;
        OnDeviceReady(_renderTarget);
    }

    #endregion

    #region Main Message Loop & Rendering

    /// <summary>
    /// 启动高性能消息循环。
    /// </summary>
    public void Run()
    {
        Win32Native.ShowWindow(_handle, 1);
        Win32Native.UpdateWindow(_handle);

        OnLoad();

        _sw.Start();
        _lastFrameTicks = _sw.ElapsedTicks;
        var msg = new Win32Native.MSG();

        try
        {
            while (true)
            {
                // 使用 PeekMessage 检查消息
                if (Win32Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, Win32Native.PM_REMOVE))
                {
                    if (msg.message == Win32Native.WM_QUIT)
                        break;

                    Win32Native.TranslateMessage(ref msg);
                    Win32Native.DispatchMessage(ref msg);
                }
                else
                {
                    // 空闲时进行渲染
                    RenderFrame();
                }
            }

        }
        finally
        {
            Dispose();
        }
    }

    private void RenderFrame()
    {
        if (_renderTarget is null) return;

        long currentTicks = _sw.ElapsedTicks;
        float deltaTimeInSeconds = (currentTicks - _lastFrameTicks) / (float)Stopwatch.Frequency;
        _lastFrameTicks = currentTicks;

        _renderTarget.BeginDraw();
        _renderTarget.Clear(_backgroundColor);

        OnPaint(_renderTarget, deltaTimeInSeconds);

        try
        {
            _renderTarget.EndDraw();
        }
        catch (SharpDXException ex) when ((uint)ex.ResultCode.Code == 0x8899000C) // D2DERR_RECREATE_TARGET
        {
            // 设备丢失，需要重建资源 (这里简化处理，仅仅为了防止崩溃)
            // 实际上应该 Dispose RenderTarget 并重新 InitializeDirect2D
            _renderTarget.Dispose();
            _renderTarget = null;
            InitializeDirect2D();
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

    /// <summary>
    /// 获取一个值，指示 Direct2D 渲染设备是否已就绪。
    /// </summary>
    public bool IsDeviceReady => _isDeviceReady;

    /// <summary>
    /// 获取或设置窗口背景颜色。
    /// </summary>
    public RawColor4 BackgroundColor
    {
        get => _backgroundColor;
        set => _backgroundColor = value;
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

    #region 

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
    /// <summary>
    /// 子类必须实现的绘制方法。
    /// 每一帧都会被调用。
    /// </summary>
    protected abstract void OnPaint(RenderTarget target, float deltaTimeInSeconds);

    /// <summary>
    /// Raises the device ready event to signal that the specified render target is prepared for use.
    /// </summary>
    /// <remarks>Derived classes can override this method to perform additional actions when a render target
    /// becomes ready. This method invokes the DeviceReady event if it is subscribed.</remarks>
    /// <param name="target">The render target that has become ready. This parameter provides context about the device or resource that is
    /// now available.</param>
    protected virtual void OnDeviceReady(RenderTarget target)
    {
        // 子类可以重写以响应设备就绪事件
        DeviceReady?.Invoke(target);
    }

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
    protected virtual IntPtr HandleWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Native.WM_DESTROY:
                Win32Native.PostQuitMessage(0);
                Win32Native.DestroyWindow(_handle);
                return IntPtr.Zero;

            case Win32Native.WM_ERASEBKGND:
                return (IntPtr)1;

            case Win32Native.WM_SIZE:
                int w = (int)(lParam.ToInt64() & 0xFFFF);
                int h = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                // 最小化时 w/h 可能为 0，需处理
                if (w > 0 && h > 0)
                {
                    _renderTarget?.Resize(new Size2(w, h));
                    _sz.Width = w;
                    _sz.Height = h;
                    OnResize(w, h);
                }
                return IntPtr.Zero;

            // --- 输入事件处理 ---
            case Win32Native.WM_MOUSEMOVE:
                {
                    UpdateMouseInfo(lParam, wParam, MouseButton.None, out var p);
                    OnMouseMove(p);
                    return IntPtr.Zero;
                }

            case Win32Native.WM_LBUTTONDOWN:
                {
                    UpdateMouseInfo(lParam, wParam, MouseButton.Left, out var p);
                    OnMouseDown(p, MouseButton.Left);
                    return IntPtr.Zero;
                }

            case Win32Native.WM_LBUTTONUP:
                {
                    UpdateMouseInfo(lParam, wParam, MouseButton.Left, out var p);
                    OnMouseUp(p, MouseButton.Left);
                    return IntPtr.Zero;
                }

            case Win32Native.WM_RBUTTONDOWN:
                {
                    UpdateMouseInfo(lParam, wParam, MouseButton.Right, out var p);
                    OnMouseDown(p, MouseButton.Right);
                    return IntPtr.Zero;
                }

            case Win32Native.WM_RBUTTONUP:
                {
                    UpdateMouseInfo(lParam, wParam, MouseButton.Right, out var p);
                    OnMouseUp(p, MouseButton.Right);
                    return IntPtr.Zero;
                }

            case Win32Native.WM_MOUSEWHEEL:
                if (MouseWheel is not null)
                {
                    UpdateMouseInfo(lParam, wParam, MouseButton.None, out _);
                    // 滚轮 Delta 在 wParam 的高位
                    var wheelData = _cachedMouseArgs.WheelDelta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    OnMouseWheel(wheelData);
                    _cachedMouseArgs.WheelDelta = 0; // Reset
                }
                return IntPtr.Zero;

            case Win32Native.WM_KEYDOWN:
                {
                    UpdateKeyInfo(wParam, true, out var key, out var modifiers);
                    OnKeyDown(key, modifiers);
                    return IntPtr.Zero;
                }

            case Win32Native.WM_KEYUP:
                {
                    UpdateKeyInfo(wParam, false, out var key, out var modifiers);
                    OnKeyUp(key, modifiers);
                    return IntPtr.Zero;
                }

            case Win32Native.WM_CHAR:
                {
                    UpdateKeyPressInfo(wParam, out var c, out var modifiers);
                    OnKeyPress(c, modifiers);
                }
                return IntPtr.Zero;
        }

        return Win32Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // --- 高效数据提取辅助方法 ---
    private KeyModifiers GetCurrentModifiers()
    {
        KeyModifiers modifiers = KeyModifiers.None;

        // 检查高位是否为1 (0x8000)，表示当前键被按下
        if ((Win32Native.GetKeyState(Win32Native.VK_MENU) & 0x8000) != 0)
            modifiers |= KeyModifiers.Alt;
        if ((Win32Native.GetKeyState(Win32Native.VK_CONTROL) & 0x8000) != 0)
            modifiers |= KeyModifiers.Control;
        if ((Win32Native.GetKeyState(Win32Native.VK_SHIFT) & 0x8000) != 0)
            modifiers |= KeyModifiers.Shift;

        return modifiers;
    }

    private void UpdateMouseInfo(IntPtr lParam, IntPtr wParam, MouseButton button, out Point p)
    {
        // 提取低位和高位作为 X, Y (比 Marshal 效率高)
        long l = lParam.ToInt64();
        int x = (short)(l & 0xFFFF);
        int y = (short)((l >> 16) & 0xFFFF);

        MousePosition = p = new Point(x, y);

        _cachedMouseArgs.X = x;
        _cachedMouseArgs.Y = y;
        _cachedMouseArgs.Button = button;
        _cachedMouseArgs.WheelDelta = 0;
        _cachedMouseArgs.Handled = false;
    }

    private void UpdateKeyInfo(IntPtr wParam, bool isDown, out int key, out KeyModifiers modifiers)
    {
        key = wParam.ToInt32();
        modifiers = GetCurrentModifiers();
        _cachedKeyArgs.Key = key; // 虚拟键码直接映射
        _cachedKeyArgs.Modifiers = modifiers;
        _cachedKeyArgs.IsDown = isDown;
        _cachedKeyArgs.Handled = false;
    }

    private void UpdateKeyPressInfo(IntPtr wParam, out char c, out KeyModifiers modifiers)
    {
        modifiers = GetCurrentModifiers();
        _cachedKeyPressArgs.KeyChar = c = (char)wParam.ToInt64();
        _cachedKeyPressArgs.Modifiers = modifiers;
        _cachedKeyPressArgs.Handled = false;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        return HandleWndProc(hWnd, msg, wParam, lParam);
    }

    #endregion

    #region IDisposable Support
    public virtual void Dispose()
    {
        _renderTarget?.Dispose();
        _factory?.Dispose();

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