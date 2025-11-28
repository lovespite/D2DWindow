using SharpDX;
using SharpDX.Direct2D1;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace D2DWindow;

/// <summary>
/// 一个封装了纯 Win32 API 和 Direct2D 渲染环境的抽象基类。
/// 实现了高性能消息循环，适合游戏开发。
/// </summary>
public abstract class Direct2D1Window : IDisposable
{
    private IntPtr _handle;
    private Win32Native.WndProcDelegate? _wndProcDelegate;
    private string _className;
    private bool _isDeviceReady;

    // Direct2D 资源
    private Factory1? _factory;
    private WindowRenderTarget? _renderTarget;

    public IntPtr Handle => _handle;
    public string Title { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsDeviceReady => _isDeviceReady;

    // 鼠标当前状态（用于逻辑查询）
    public Point MousePosition { get; private set; }

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

    protected Direct2D1Window(string title, int width, int height)
    {
        Title = title;
        Width = width;
        Height = height;
        _className = "Pixi2D_GameWindow_" + Guid.NewGuid().ToString("N");

        CreateInternal();
        InitializeDirect2D();
    }

    protected virtual void OnLoad()
    {
    }

    private void CreateInternal()
    {
        IntPtr hInstance = Win32Native.GetModuleHandle(null);
        _wndProcDelegate = new Win32Native.WndProcDelegate(WndProc);

        var wndClass = new Win32Native.WNDCLASSEX();
        wndClass.cbSize = Marshal.SizeOf(typeof(Win32Native.WNDCLASSEX));
        wndClass.style = Win32Native.CS_HREDRAW | Win32Native.CS_VREDRAW | Win32Native.CS_OWNDC;
        wndClass.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        wndClass.hInstance = hInstance;
        wndClass.hCursor = Win32Native.LoadCursor(IntPtr.Zero, Win32Native.IDC_ARROW);
        wndClass.lpszClassName = _className;

        if (Win32Native.RegisterClassEx(ref wndClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            var exception = new System.ComponentModel.Win32Exception(error);
            throw exception;
        }

        var rect = new Win32Native.RECT { Left = 0, Top = 0, Right = Width, Bottom = Height };
        Win32Native.AdjustWindowRect(ref rect, Win32Native.WS_OVERLAPPEDWINDOW, false);

        _handle = Win32Native.CreateWindowEx(
            dwExStyle: 0,
            lpClassName: _className,
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

        _renderTarget = new WindowRenderTarget(_factory, renderTargetProperties, hwndRenderTargetProperties);
        _isDeviceReady = true;
        OnDeviceReady(_renderTarget);
    }

    /// <summary>
    /// 启动高性能消息循环。
    /// </summary>
    public void Run()
    {
        Win32Native.ShowWindow(_handle, 1);
        Win32Native.UpdateWindow(_handle);

        OnLoad();

        var msg = new Win32Native.MSG();

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

    /// <summary>
    /// 设置或取消窗口的置顶（TopMost）状态。
    /// </summary>
    /// <param name="topmost">如果为 true，窗口将置顶；否则取消置顶。</param>
    public void SetTopMost(bool topmost)
    {
        if (_handle == IntPtr.Zero) return;

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

    private void RenderFrame()
    {
        if (_renderTarget is null) return;

        _renderTarget.BeginDraw();
        // 我们不在这里强制 Clear，交给子类 OnPaint 决定是否 Clear 以及用什么颜色
        OnPaint(_renderTarget);

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

    /// <summary>
    /// 子类必须实现的绘制方法。
    /// 每一帧都会被调用。
    /// </summary>
    protected abstract void OnPaint(RenderTarget target);

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

    #region Window Message Processing

    protected virtual IntPtr HandleWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Native.WM_DESTROY:
                Win32Native.PostQuitMessage(0);
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
                    Width = w;
                    Height = h;
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
                if (KeyPress is not null)
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

    public virtual void Dispose()
    {
        _renderTarget?.Dispose();
        _factory?.Dispose();

        // 窗口销毁通常在 WM_DESTROY 处理，这里仅作为补充清理
        _handle = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }
}