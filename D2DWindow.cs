using SharpDX;
using SharpDX.Direct2D1;
using System.Drawing;
using System.Runtime.InteropServices;
using static System.Runtime.CompilerServices.RuntimeHelpers;

namespace NativeWindow;

/// <summary>
/// 一个封装了纯 Win32 API 和 Direct2D 渲染环境的抽象基类。
/// 实现了高性能消息循环，适合游戏开发。
/// </summary>
public abstract class D2DWindow : IDisposable
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

    // 事件系统
    private readonly RawMouseEventArgs _cachedMouseArgs = new();
    private readonly RawKeyEventArgs _cachedKeyArgs = new();

    public event Action<RawMouseEventArgs>? MouseMove;
    public event Action<RawMouseEventArgs>? MouseDown;
    public event Action<RawMouseEventArgs>? MouseUp;
    public event Action<RawMouseEventArgs>? MouseWheel;
    public event Action<RawKeyEventArgs>? KeyDown;
    public event Action<RawKeyEventArgs>? KeyUp;

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

    protected D2DWindow(string title, int width, int height)
    {
        Title = title;
        Width = width;
        Height = height;
        _className = "Pixi2D_GameWindow_" + Guid.NewGuid().ToString("N");

        CreateInternal();
        InitializeDirect2D();
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
            throw new Exception("Failed to register window class.");

        var rect = new Win32Native.RECT { Left = 0, Top = 0, Right = Width, Bottom = Height };
        Win32Native.AdjustWindowRect(ref rect, Win32Native.WS_OVERLAPPEDWINDOW, false);

        _handle = Win32Native.CreateWindowEx(
            0, _className, Title,
            Win32Native.WS_OVERLAPPEDWINDOW | Win32Native.WS_VISIBLE,
            Win32Native.CW_USEDEFAULT, Win32Native.CW_USEDEFAULT,
            rect.Right - rect.Left, rect.Bottom - rect.Top,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_handle == IntPtr.Zero)
            throw new Exception("Failed to create window.");
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
                    Resize?.Invoke(w, h);
                }
                return IntPtr.Zero;

            // --- 输入事件处理 ---
            case Win32Native.WM_MOUSEMOVE:
                if (MouseMove is not null)
                {
                    UpdateMouseInfo(lParam, wParam, MouseButton.None);
                    MouseMove(_cachedMouseArgs);
                }
                return IntPtr.Zero;

            case Win32Native.WM_LBUTTONDOWN:
                if (MouseDown is not null) { UpdateMouseInfo(lParam, wParam, MouseButton.Left); MouseDown(_cachedMouseArgs); }
                return IntPtr.Zero;

            case Win32Native.WM_LBUTTONUP:
                if (MouseUp is not null) { UpdateMouseInfo(lParam, wParam, MouseButton.Left); MouseUp(_cachedMouseArgs); }
                return IntPtr.Zero;

            case Win32Native.WM_RBUTTONDOWN:
                if (MouseDown is not null) { UpdateMouseInfo(lParam, wParam, MouseButton.Right); MouseDown(_cachedMouseArgs); }
                return IntPtr.Zero;

            case Win32Native.WM_RBUTTONUP:
                if (MouseUp is not null) { UpdateMouseInfo(lParam, wParam, MouseButton.Right); MouseUp(_cachedMouseArgs); }
                return IntPtr.Zero;

            case Win32Native.WM_MOUSEWHEEL:
                if (MouseWheel is not null)
                {
                    UpdateMouseInfo(lParam, wParam, MouseButton.None);
                    // 滚轮 Delta 在 wParam 的高位
                    _cachedMouseArgs.WheelDelta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    MouseWheel(_cachedMouseArgs);
                    _cachedMouseArgs.WheelDelta = 0; // Reset
                }
                return IntPtr.Zero;

            case Win32Native.WM_KEYDOWN:
                if (KeyDown is not null)
                {
                    UpdateKeyInfo(wParam, true);
                    KeyDown(_cachedKeyArgs);
                }
                return IntPtr.Zero;

            case Win32Native.WM_KEYUP:
                if (KeyUp is not null)
                {
                    UpdateKeyInfo(wParam, false);
                    KeyUp(_cachedKeyArgs);
                }
                return IntPtr.Zero;
        }

        return Win32Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // --- 高效数据提取辅助方法 ---

    private void UpdateMouseInfo(IntPtr lParam, IntPtr wParam, MouseButton button)
    {
        // 提取低位和高位作为 X, Y (比 Marshal 效率高)
        long l = lParam.ToInt64();
        int x = (short)(l & 0xFFFF);
        int y = (short)((l >> 16) & 0xFFFF);

        MousePosition = new Point(x, y);

        _cachedMouseArgs.X = x;
        _cachedMouseArgs.Y = y;
        _cachedMouseArgs.Button = button;
        _cachedMouseArgs.WheelDelta = 0;
        _cachedMouseArgs.Handled = false;
    }

    private void UpdateKeyInfo(IntPtr wParam, bool isDown)
    {
        _cachedKeyArgs.Key = wParam.ToInt32(); // 虚拟键码直接映射
        _cachedKeyArgs.IsDown = isDown;
        _cachedKeyArgs.Handled = false;
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