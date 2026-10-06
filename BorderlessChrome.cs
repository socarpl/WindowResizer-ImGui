using System.Runtime.InteropServices;
using Veldrid.Sdl2;

namespace WindowResizer;

internal sealed class BorderlessChrome
{
    private const int Edge = 7;
    private const int TitleHeight = 46;
    private const int ControlAreaWidth = 164;

    private readonly HitTestCallback _callback;
    private readonly nint _hwnd;

    public BorderlessChrome(Sdl2Window window)
    {
        _hwnd = window.Handle;
        _callback = HitTest;
        if (SDL_SetWindowHitTest(window.SdlWindowHandle, _callback, 0) != 0)
            throw new InvalidOperationException("SDL could not enable borderless window dragging and resizing.");
        SDL_SetWindowMinimumSize(window.SdlWindowHandle, 760, 560);
    }

    private HitResult HitTest(nint sdlWindow, in SdlPoint point, nint userData)
    {
        SDL_GetWindowSize(sdlWindow, out int width, out int height);
        bool maximized = NativeMethods.IsZoomed(_hwnd);
        return Classify(point.X, point.Y, width, height, maximized);
    }

    internal static HitResult Classify(int x, int y, int width, int height, bool maximized)
    {
        if (width <= 0 || height <= 0) return HitResult.Normal;
        if (!maximized)
        {
            bool left = x < Edge;
            bool right = x >= width - Edge;
            bool top = y < Edge;
            bool bottom = y >= height - Edge;
            if (top && left) return HitResult.ResizeTopLeft;
            if (top && right) return HitResult.ResizeTopRight;
            if (bottom && left) return HitResult.ResizeBottomLeft;
            if (bottom && right) return HitResult.ResizeBottomRight;
            if (top) return HitResult.ResizeTop;
            if (bottom) return HitResult.ResizeBottom;
            if (left) return HitResult.ResizeLeft;
            if (right) return HitResult.ResizeRight;
        }
        return y < TitleHeight && x < width - ControlAreaWidth
            ? HitResult.Draggable : HitResult.Normal;
    }

    internal enum HitResult
    {
        Normal, Draggable, ResizeTopLeft, ResizeTop, ResizeTopRight,
        ResizeRight, ResizeBottomRight, ResizeBottom, ResizeBottomLeft, ResizeLeft
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SdlPoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate HitResult HitTestCallback(nint window, in SdlPoint point, nint userData);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_SetWindowHitTest(nint window, HitTestCallback callback, nint userData);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_GetWindowSize(nint window, out int width, out int height);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_SetWindowMinimumSize(nint window, int width, int height);
}
