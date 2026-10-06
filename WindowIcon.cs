using System.Runtime.InteropServices;

namespace WindowResizer;

internal sealed class WindowIcon : IDisposable
{
    private const uint WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    private nint _large;
    private nint _small;

    public WindowIcon(nint hwnd)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the application executable icon.");
        if (ExtractIconEx(executable, 0, out _large, out _small, 1) == 0)
            throw new InvalidOperationException("Cannot load the application icon from the executable.");

        if (_large != 0) SendMessage(hwnd, WM_SETICON, ICON_BIG, _large);
        if (_small != 0) SendMessage(hwnd, WM_SETICON, ICON_SMALL, _small);
    }

    public void Dispose()
    {
        if (_large != 0) DestroyIcon(_large);
        if (_small != 0) DestroyIcon(_small);
        _large = 0;
        _small = 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconEx(string file, int iconIndex, out nint large, out nint small, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
