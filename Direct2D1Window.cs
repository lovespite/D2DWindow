using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.Mathematics.Interop;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace D2DWindow;

/// <summary>
/// 一个封装了纯 Win32 API 和 Direct2D 渲染环境的抽象基类。
/// 实现了高性能消息循环，适合游戏开发。
/// </summary>
public abstract class Direct2D1Window : IDisposable, IWin32Owner
{
    private readonly Stopwatch _sw = new();
    private readonly Stopwatch _fpsTimer = new();
    private readonly BlockingCollection<Action> _pendingActions = new(new ConcurrentQueue<Action>());
    private readonly int _uiThreadId;
    private readonly nint _hInstance;
    private nint _handle;
    private Size _sz;
    private long _lastFrameTicks = 0;
    private int _fps;
    private int _frameCount = 0;
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
        _uiThreadId = Environment.CurrentManagedThreadId; // 记录创建线程 ID
        _hInstance = Win32Native.GetModuleHandle(null);
        UISynchronizationContext = new D2DSynchronizationContext(this); // 创建 UI 线程同步上下文
        SynchronizationContext.SetSynchronizationContext(UISynchronizationContext); // 安装同步上下文
        CreateInternal();
        InitializeDirect2D();
        SetTitle(title);
    }

    #region Core Initialization

    private void CreateInternal()
    {
        _wndProcDelegate = new Win32Native.WndProcDelegate(WndProc);

        var wndClass = new Win32Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Win32Native.WNDCLASSEX>(),
            style = Win32Native.CS_HREDRAW | Win32Native.CS_VREDRAW | Win32Native.CS_OWNDC,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = _hInstance,
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
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

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
    /// 启动消息循环。
    /// </summary>
    public void Run()
    {
        if (Environment.CurrentManagedThreadId != _uiThreadId)
            throw new InvalidOperationException("Run method must be called on the UI thread.");

        Win32Native.ShowWindow(_handle, 1);
        Win32Native.UpdateWindow(_handle);
        OnLoad();

        _sw.Start();
        var msg = new Win32Native.MSG();

        try
        {
            while (true)
            {
                if (Win32Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, Win32Native.PM_REMOVE))
                {
                    if (msg.message == Win32Native.WM_QUIT)
                        break;

                    Win32Native.TranslateMessage(ref msg);
                    Win32Native.DispatchMessage(ref msg);
                }
                else
                {
                    long frameStartTicks = _sw.ElapsedTicks;

                    // 渲染
                    RenderFrame();
                    if (_pendingActions.Count == 0) continue;
                    // 处理任务 (尽可能多做，直到达到某个安全阈值)
                    // 这里我们计算一下渲染用了多久
                    double renderTimeMs = (_sw.ElapsedTicks - frameStartTicks) * 1000.0 / Stopwatch.Frequency;

                    // 给任务留点时间，比如利用剩下的时间，但不要把时间全用光，留一点缓冲
                    double timeForActions = TargetFrameTime - renderTimeMs;
                    ProcessPendingActions(timeForActions);
                }
            }

            OnUnload();
        }
        finally
        {
            Dispose();
        }
    }

    private void ProcessPendingActions(double timeBudgetMs)
    {
        if (timeBudgetMs <= 0) return;

        long startTimestamp = Stopwatch.GetTimestamp();
        long budgetTicks = (long)(timeBudgetMs * Stopwatch.Frequency / 1000);

        // 关键点：TryTake 不传参数，立即返回，不阻塞
        while (_pendingActions.TryTake(out var action))
        {
            action();

            // 检查时间
            if (Stopwatch.GetTimestamp() - startTimestamp > budgetTicks)
                break;
        }
    }

    public void RunOnUIThread(Action action)
    {
        _pendingActions.TryAdd(action);
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

        ++_frameCount;
        if (_fpsTimer.ElapsedMilliseconds < 1000) return;
        _fps = _frameCount / (int)(_fpsTimer.ElapsedMilliseconds / 1000);
        _frameCount = 0;
        _fpsTimer.Restart();
    }

    private class UIThreadTaskScheduler(Direct2D1Window window) : TaskScheduler
    {
        protected override IEnumerable<Task>? GetScheduledTasks()
        {
            return null; // 不支持查询
        }
        protected override void QueueTask(Task task)
        {
            window.RunOnUIThread(() => TryExecuteTask(task));
        }
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            if (Environment.CurrentManagedThreadId != window._uiThreadId) return false;
            return TryExecuteTask(task);
        }
    }

    /// <summary>
    /// 自定义 SynchronizationContext，支持 Send（同步）和 Post（异步）。
    /// </summary>
    private class D2DSynchronizationContext(Direct2D1Window window) : SynchronizationContext
    {
        private readonly Direct2D1Window _window = window;

        /// <summary>
        /// 异步分发（对应 RunOnUIThread）。
        /// </summary>
        public override void Post(SendOrPostCallback d, object? state)
        {
            _window.RunOnUIThread(() => d(state));
        }

        /// <summary>
        /// 同步分发。
        /// </summary>
        public override void Send(SendOrPostCallback d, object? state)
        {
            // 如果已经在 UI 线程，直接执行以避免死锁
            if (Environment.CurrentManagedThreadId == _window._uiThreadId)
            {
                d(state);
            }
            else
            {
                // 如果在后台线程，需要阻塞直到 UI 线程执行完毕
                using var handle = new ManualResetEventSlim(false);
                Exception? error = null;

                _window.RunOnUIThread(() =>
                {
                    try
                    {
                        d(state);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        handle.Set();
                    }
                });

                handle.Wait();

                // 如果在 UI 线程执行时抛出了异常，我们需要在调用线程重新抛出它
                if (error != null)
                {
                    ExceptionDispatchInfo.Capture(error).Throw();
                }
            }
        }

        public override SynchronizationContext CreateCopy()
        {
            return new D2DSynchronizationContext(_window);
        }
    }

    #endregion

    #region Public Properties

    /// <summary>
    /// 帧时间，单位毫秒。
    /// 默认帧率 60 FPS -> 约 16.67 ms
    /// </summary>
    public double TargetFrameTime { get; set; } = 1000d / 60;

    /// <summary>
    /// 获取与 UI 线程关联的任务调度器。
    /// </summary>
    public SynchronizationContext UISynchronizationContext { get; }

    /// <summary>
    /// 获取窗口的原生句柄。
    /// </summary>
    /// <remarks>Use this property to access the unmanaged handle when interoperating with native APIs or
    /// performing low-level operations. The validity and lifetime of the handle depend on the state of the parent
    /// object; ensure the object is not disposed before using the handle.</remarks>
    public nint Handle => _handle;

    /// <summary>
    /// Gets the current frames per second (FPS) value.
    /// </summary>
    public int FPS => _fps;

    /// <summary>
    /// Gets the total elapsed time measured by the timer, in milliseconds.
    /// </summary>
    public long ElapsedMilliseconds => _sw.ElapsedMilliseconds;

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

    public void Close()
    {
        Win32Native.PostMessage(_handle, Win32Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
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

    /// <summary>
    /// Raises the closing event, allowing derived classes to cancel the window close operation.
    /// </summary>
    /// <remarks>Override this method in a derived class to implement custom logic that may conditionally
    /// cancel the window closing process. The value of <paramref name="cancel"/> should be set to <see
    /// langword="true"/> to cancel closing, or left unchanged to allow it.</remarks>
    /// <param name="cancel">A reference to a Boolean value that determines whether the window closing operation should be canceled. Set to
    /// <see langword="true"/> to prevent the window from closing; otherwise, <see langword="false"/>.</param>
    protected virtual void OnClosing(ref bool cancel)
    {
#if DEBUG
        Debug.WriteLine("{0}: Window closing.", nameof(OnClosing));
#endif
    }

    /// <summary>
    /// Invoked when the window has finished loading. Derived classes can override this method to perform additional
    /// initialization after the window is loaded.
    /// </summary>
    /// <remarks>Override this method to execute custom logic when the window load event occurs. The base
    /// implementation does not perform any actions other than diagnostic output.</remarks>
    protected virtual void OnLoad()
    {
#if DEBUG
        Debug.WriteLine("{0}: Window loaded.", nameof(OnLoad));
#endif
    }

    /// <summary>
    /// Raises the unload event for the window, allowing derived classes to perform cleanup or resource release
    /// operations when the window is being unloaded.
    /// </summary>
    /// <remarks>Override this method in a derived class to implement custom logic that should execute when
    /// the window is unloaded. This method is called as part of the window's lifecycle and is intended for resource
    /// management or other teardown activities.</remarks>
    protected virtual void OnUnload()
    {
#if DEBUG
        Debug.WriteLine("{0}: Window unloaded.", nameof(OnUnload));
#endif
    }

    #endregion

    #region Window Message Processing 

    protected virtual IntPtr HandleWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Native.WM_CLOSE:
                {
                    bool cancel = false;
                    OnClosing(ref cancel);
                    if (cancel) return IntPtr.Zero; // 取消关闭 
                }
                break;

            case Win32Native.WM_DESTROY:
                if (_handle == 0) return 0; // 已经销毁
                Win32Native.PostQuitMessage(0);
                Win32Native.DestroyWindow(_handle);
                _handle = 0;
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
                {
                    // 滚轮 Delta 在 wParam 的高位
                    var wheelData = _cachedMouseArgs.WheelDelta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    OnMouseWheel(wheelData);
                    _cachedMouseArgs.WheelDelta = 0; // Reset
                    return IntPtr.Zero;
                }

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

        if (_handle != 0)
        {
            // 窗口销毁通常在 WM_DESTROY 处理
            // 不过 WM_DESTROY 处理后 _handle 会被置 0
            // 如果 _handle != 0, 说明我们没有收到 WM_DESTROY 消息

            // 如果是因为异常退出循环，窗口此时还存在，必须销毁。
            // 如果是因为用户点击关闭，窗口已被系统销毁，此调用会失败但无害。
            // 注意：DestroyWindow 必须在创建窗口的线程（主线程）调用。
            Win32Native.DestroyWindow(_handle);
        }

        // 只有当该类没有关联的窗口时，注销才会成功，所以必须先 DestroyWindow 
        Win32Native.UnregisterClass(WindowClassName, _hInstance);

        _handle = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    #endregion
}