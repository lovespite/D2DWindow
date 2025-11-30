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
