namespace D2DWindow;

public enum MouseButton
{
    None, Left, Right, Middle, X1, X2
}

[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4
}

public static class KeyModifiersExtensions
{
    public static bool Control(this KeyModifiers modifiers)
    {
        return modifiers.HasFlag(KeyModifiers.Control);
    }

    public static bool Alt(this KeyModifiers modifiers)
    {
        return modifiers.HasFlag(KeyModifiers.Alt);
    }

    public static bool Shift(this KeyModifiers modifiers)
    {
        return modifiers.HasFlag(KeyModifiers.Shift);
    }
}

/// <summary>
/// 高性能鼠标事件参数
/// </summary>
/// <remarks>
/// 不要将此类的实例存储在事件处理程序之外，因为它们会被重用。
/// </remarks>
public class RawMouseEventArgs
{
    public int X { get; internal set; }
    public int Y { get; internal set; }
    public MouseButton Button { get; internal set; }
    public int WheelDelta { get; internal set; }
    public bool Handled { get; set; }
}

public abstract class KeyEventArgsBase
{
    public KeyModifiers Modifiers { get; internal set; }
    public bool Shift => (Modifiers & KeyModifiers.Shift) != 0;
    public bool Control => (Modifiers & KeyModifiers.Control) != 0;
    public bool Alt => (Modifiers & KeyModifiers.Alt) != 0;
}

/// <summary>
/// 高性能键盘事件参数
/// </summary>
/// <remarks>
/// 不要将此类的实例存储在事件处理程序之外，因为它们会被重用。
/// </remarks>
public class RawKeyEventArgs : KeyEventArgsBase
{
    public int Key { get; internal set; }
    public bool IsDown { get; internal set; }
    public bool Handled { get; set; }
}

/// <summary>
/// 字符输入事件参数
/// 用于处理文本输入，包含了经过系统翻译后的字符（考虑了 Shift、Caps Lock 等）。
/// </summary>
/// <remarks>
/// 不要将此类的实例存储在事件处理程序之外，因为它们会被重用。
/// </remarks>
public class RawKeyPressEventArgs : KeyEventArgsBase
{
    public char KeyChar { get; internal set; }
    public bool Handled { get; set; }
}