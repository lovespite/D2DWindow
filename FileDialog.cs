using SharpDX.Direct2D1;
using System.IO;
using System.Runtime.InteropServices;

namespace D2DWindow;

/// <summary>
/// 封装 Win32 原生文件对话框 (comdlg32.dll)。
/// </summary>
public class FileDialog
{
    public const string FilterAllFiles = "All Files (*.*)\0*.*\0";

    /// <summary>
    /// 对话框标题。
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 文件过滤器。
    /// 格式示例："Text Files (*.txt)\0*.txt\0All Files (*.*)\0*.*\0"
    /// 注意：必须以 \0 分隔描述和模式，并以 \0 结尾。
    /// </summary>
    public string Filter { get; set; } = FilterAllFiles;

    /// <summary>
    /// 默认文件扩展名（不带点），例如 "txt"。
    /// </summary>
    public string DefaultExt { get; set; } = string.Empty;

    /// <summary>
    /// 初始目录。
    /// </summary>
    public string InitialDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 选择的文件名（包含完整路径）。
    /// 如果是多选，此属性包含第一个选择的文件。
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 所有选择的文件名（包含完整路径）。
    /// 支持多选时使用。
    /// </summary>
    public string[] FileNames { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// 是否允许选择多个文件。
    /// </summary>
    public bool MultiSelect { get; set; } = false;

    /// <summary>
    /// 显示“打开文件”对话框。
    /// </summary>
    /// <param name="owner">父窗口句柄。</param>
    /// <returns>如果用户点击确定，返回 true；否则返回 false。</returns>
    public bool ShowOpen(IWin32Owner owner)
    {
        return ShowDialog(owner, true);
    }

    /// <summary>
    /// 显示“保存文件”对话框。
    /// </summary>
    /// <param name="owner">父窗口句柄。</param>
    /// <returns>如果用户点击确定，返回 true；否则返回 false。</returns>
    public bool ShowSave(IWin32Owner owner)
    {
        return ShowDialog(owner, false);
    }

    #region Static Helper Methods

    /// <summary>
    /// 显示“保存文件”对话框并返回选择的路径。
    /// </summary>
    /// <param name="owner">父窗口句柄。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="dir">初始目录。</param>
    /// <param name="defaultFileName">默认文件名。</param>
    /// <returns>用户选择的文件路径，如果取消则返回 null。</returns>
    public static string? GetSaveFileName(IWin32Owner owner, string title = "Save", string? dir = null, string? defaultFileName = null, string filter = FilterAllFiles)
    {
        var dialog = new FileDialog
        {
            Title = title,
            InitialDirectory = dir ?? string.Empty,
            FileName = defaultFileName ?? string.Empty,
            Filter = filter, // 允许使用 '|' 作为分隔符，方便调用
        };

        if (dialog.ShowSave(owner))
        {
            return dialog.FileName;
        }
        return null;
    }

    /// <summary>
    /// 显示“打开文件”对话框并返回选择的文件路径数组。
    /// 默认启用多选。
    /// </summary>
    /// <param name="owner">父窗口句柄。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="dir">初始目录。</param>
    /// <returns>用户选择的文件路径数组，如果取消则返回 null。</returns>
    public static string[]? GetOpenFileName(IWin32Owner owner, string title = "Open", string? dir = null, string filter = FilterAllFiles, bool multiSelect = false)
    {
        var dialog = new FileDialog
        {
            Title = title,
            InitialDirectory = dir ?? string.Empty,
            MultiSelect = multiSelect,
            Filter = filter, // 允许使用 '|' 作为分隔符，方便调用
        };

        if (dialog.ShowOpen(owner))
        {
            return dialog.FileNames;
        }
        return null;
    }

    #endregion

    private bool ShowDialog(IWin32Owner owner, bool isOpen)
    {
        var filter = Filter.Replace('|', '\0'); // 允许使用 '|' 作为分隔符，方便调用
        if (!filter.EndsWith('\0'))
        {
            filter += "\0";
        }

        var ofn = new Win32Native.OPENFILENAME();
        ofn.lStructSize = Marshal.SizeOf(ofn);
        ofn.hwndOwner = owner.Handle;
        ofn.hInstance = IntPtr.Zero;
        ofn.lpstrFilter = filter; // 允许使用 '|' 作为分隔符，方便调用
        ofn.nFilterIndex = 1;
        ofn.lpstrFileTitle = null!;
        ofn.nMaxFileTitle = 0;
        ofn.lpstrInitialDir = string.IsNullOrEmpty(InitialDirectory) ? null! : InitialDirectory;
        ofn.lpstrTitle = string.IsNullOrEmpty(Title) ? null! : Title;
        ofn.lpstrDefExt = string.IsNullOrEmpty(DefaultExt) ? null! : DefaultExt;

        // 常用 Flags
        ofn.Flags = Win32Native.OFN_PATHMUSTEXIST | Win32Native.OFN_FILEMUSTEXIST | Win32Native.OFN_NOCHANGEDIR | Win32Native.OFN_EXPLORER;

        if (!isOpen)
        {
            ofn.Flags |= Win32Native.OFN_OVERWRITEPROMPT;
        }
        else if (MultiSelect)
        {
            ofn.Flags |= Win32Native.OFN_ALLOWMULTISELECT;
        }

        // 关键：手动分配缓冲区以接收文件名
        // 对于多选，Win32 API 推荐使用较大的缓冲区（通常 32KB 足够）
        const int bufferSize = 32768;
        ofn.nMaxFile = bufferSize;
        ofn.lpstrFile = Marshal.AllocHGlobal(bufferSize * 2); // Unicode 字符占 2 字节

        // 初始化缓冲区
        if (!string.IsNullOrEmpty(FileName))
        {
            // 简单预设文件名逻辑
            var chars = FileName.ToCharArray();
            int len = Math.Min(chars.Length, bufferSize - 2);
            Marshal.Copy(chars, 0, ofn.lpstrFile, len);
            Marshal.WriteInt16(ofn.lpstrFile, len * 2, 0);
        }
        else
        {
            Marshal.WriteInt16(ofn.lpstrFile, 0);
        }

        try
        {
            bool result = isOpen
                ? Win32Native.GetOpenFileName(ref ofn)
                : Win32Native.GetSaveFileName(ref ofn);

            if (result)
            {
                ParseResults(ofn.lpstrFile);
                return true;
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ofn.lpstrFile);
        }
    }

    private void ParseResults(IntPtr buffer)
    {
        // Win32 GetOpenFileName 多选时的返回格式：
        // 1. 如果只选一个文件： "C:\Path\To\File.txt\0\0"
        // 2. 如果选多个文件： "C:\Path\To\Directory\0File1.txt\0File2.txt\0...\0\0"

        string? firstStr = Marshal.PtrToStringUni(buffer);
        if (string.IsNullOrEmpty(firstStr))
        {
            FileNames = [];
            FileName = string.Empty;
            return;
        }

        // 移动指针到第一个字符串之后
        IntPtr currentPtr = IntPtr.Add(buffer, (firstStr.Length + 1) * 2);
        string? nextStr = Marshal.PtrToStringUni(currentPtr);

        if (string.IsNullOrEmpty(nextStr))
        {
            // 单选情况：第一个字符串就是完整路径
            FileName = firstStr;
            FileNames = [firstStr];
        }
        else
        {
            // 多选情况：第一个字符串是目录，后续是文件名
            string directory = firstStr;
            var list = new List<string>();

            while (!string.IsNullOrEmpty(nextStr))
            {
                list.Add(Path.Combine(directory, nextStr));

                // 移动到下一个字符串
                currentPtr = IntPtr.Add(currentPtr, (nextStr.Length + 1) * 2);
                nextStr = Marshal.PtrToStringUni(currentPtr);
            }

            FileNames = [.. list];
            FileName = FileNames.FirstOrDefault() ?? string.Empty;
        }
    }
}