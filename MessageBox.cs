namespace D2DWindow;

/// <summary>
/// 指定消息框显示的按钮。
/// </summary>
public enum MessageBoxButtons
{
    OK = 0x00000000,
    OKCancel = 0x00000001,
    AbortRetryIgnore = 0x00000002,
    YesNoCancel = 0x00000003,
    YesNo = 0x00000004,
    RetryCancel = 0x00000005
}

/// <summary>
/// 指定消息框显示的图标。
/// </summary>
public enum MessageBoxIcon
{
    None = 0,
    Error = 0x00000010,
    Hand = 0x00000010,
    Stop = 0x00000010,
    Question = 0x00000020,
    Warning = 0x00000030,
    Exclamation = 0x00000030,
    Information = 0x00000040,
    Asterisk = 0x00000040
}

/// <summary>
/// 标识消息框的返回值。
/// </summary>
public enum MessageBoxResult
{
    None = 0,
    OK = 1,
    Cancel = 2,
    Abort = 3,
    Retry = 4,
    Ignore = 5,
    Yes = 6,
    No = 7
}

/// <summary>
/// 封装 Win32 MessageBox 的静态工具类。
/// </summary>
public static class MessageBox
{
    public const uint MB_TOPMOST = 0x00040000;

    /// <summary>
    /// 显示一个具有指定文本、标题、按钮和图标的消息框。
    /// </summary>
    /// <param name="owner">父窗口句柄，可以为 IntPtr.Zero。</param>
    /// <param name="text">要显示的文本。</param>
    /// <param name="caption">标题栏文本。</param>
    /// <param name="buttons">要在消息框中显示的按钮。</param>
    /// <param name="icon">要在消息框中显示的图标。</param>
    /// <returns>用户点击的按钮结果。</returns>
    public static MessageBoxResult Show(IntPtr owner, string text, string caption = "Message", MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        uint type = (uint)buttons | (uint)icon;
        // 默认让消息框置顶，防止被全屏游戏窗口遮挡 
        type |= MB_TOPMOST;

        int result = Win32Native.MessageBox(owner, text, caption, type);
        return (MessageBoxResult)result;
    }

    /// <summary>
    /// 显示一个消息框（使用桌面作为父窗口）。
    /// </summary>
    public static MessageBoxResult Show(string text, string caption = "Message", MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        return Show(IntPtr.Zero, text, caption, buttons, icon);
    }

    // --- 常用快捷方法 ---

    /// <summary>
    /// 显示一个信息提示框。
    /// </summary>
    public static void ShowInfo(string text, string caption = "提示")
    {
        Show(IntPtr.Zero, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// 显示一个信息提示框。
    /// </summary>
    public static void ShowInfo(IWin32Owner owner, string text, string caption = "提示")
    {
        Show(owner.Handle, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// 显示一个警告框。
    /// </summary>
    public static void ShowWarning(string text, string caption = "警告")
    {
        Show(IntPtr.Zero, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>
    /// 显示一个警告框。
    /// </summary>
    public static void ShowWarning(IWin32Owner owner, string text, string caption = "警告")
    {
        Show(owner.Handle, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>
    /// 显示一个错误框。
    /// </summary>
    public static void ShowError(string text, string caption = "错误")
    {
        Show(IntPtr.Zero, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// 显示一个错误框。
    /// </summary>
    public static void ShowError(IWin32Owner owner, string text, string caption = "错误")
    {
        Show(owner.Handle, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// 询问用户（是/否），返回 bool 值。
    /// </summary>
    /// <param name="text">询问内容。</param>
    /// <param name="caption">标题。</param>
    /// <returns>如果用户点击“是”返回 true，否则返回 false。</returns>
    public static bool Ask(IWin32Owner owner, string text, string caption = "确认")
    {
        var result = Show(owner.Handle, text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        return result == MessageBoxResult.Yes;
    }
}