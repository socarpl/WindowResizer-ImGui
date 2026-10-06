using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowResizer;

// Windows holds SDL_PollEvent inside its modal border-resize loop. WM_SIZE still
// reaches the window procedure, so redraw from that callback while dragging.
internal sealed class ResizeRedrawHook : IDisposable
{
    private const uint WM_SIZE = 0x0005;
    private const uint WM_ENTERSIZEMOVE = 0x0231;
    private const uint WM_EXITSIZEMOVE = 0x0232;
    private const nuint SubclassId = 0x5752;

    private readonly nint _hwnd;
    private readonly Action _redraw;
    private readonly SubclassProc _subclassProc;
    private bool _inSizeMove;
    private bool _redrawing;
    private bool _disposed;
    private long _lastRedraw;

    public ResizeRedrawHook(nint hwnd, Action redraw)
    {
        _hwnd = hwnd;
        _redraw = redraw;
        _subclassProc = WindowProc;
        if (!SetWindowSubclass(hwnd, _subclassProc, SubclassId, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not observe live window resizing.");
    }

    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WM_ENTERSIZEMOVE) _inSizeMove = true;

        nint result = DefSubclassProc(hwnd, message, wParam, lParam);

        if (message == WM_SIZE && _inSizeMove)
            Redraw(force: false);
        else if (message == WM_EXITSIZEMOVE)
        {
            _inSizeMove = false;
            Redraw(force: true);
        }
        return result;
    }

    private void Redraw(bool force)
    {
        if (_redrawing) return;
        long now = Stopwatch.GetTimestamp();
        if (!force && Stopwatch.GetElapsedTime(_lastRedraw, now) < TimeSpan.FromSeconds(1d / 30d)) return;
        _redrawing = true;
        try
        {
            _redraw();
            _lastRedraw = Stopwatch.GetTimestamp();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Live resize redraw failed: {ex}");
            _inSizeMove = false;
        }
        finally { _redrawing = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
        GC.KeepAlive(_subclassProc);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
}
