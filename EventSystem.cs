namespace NativeWindow;

public enum MouseButton
{
    None, Left, Right, Middle, X1, X2
}

/// <summary>
/// 高性能鼠标事件参数（可复用）
/// </summary>
public class RawMouseEventArgs
{
    public int X { get; internal set; }
    public int Y { get; internal set; }
    public MouseButton Button { get; internal set; }
    public int WheelDelta { get; internal set; }
    public bool Handled { get; set; }
}

/// <summary>
/// 高性能键盘事件参数（可复用）
/// </summary>
public class RawKeyEventArgs
{
    public int Key { get; internal set; }
    public bool IsDown { get; internal set; }
    public bool Handled { get; set; }
}