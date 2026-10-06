using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowResizer;

public sealed record WindowInfo(nint Handle, int ProcessId, string ProcessName, string Title, string ClassName)
{
    public string HandleText => $"0x{unchecked((ulong)Handle.ToInt64()):X8}";
    public string Display => string.IsNullOrWhiteSpace(Title)
        ? $"{ProcessName} - {ProcessId} - HWND {HandleText}"
        : $"{ProcessName} - {Title} - {ProcessId} - HWND {HandleText}";
}

public readonly record struct WindowDimensions(int OuterWidth, int OuterHeight, int ClientWidth, int ClientHeight);

public sealed class ExternalWindowService
{
    public IReadOnlyList<WindowInfo> GetWindows()
    {
        var windows = new List<WindowInfo>();
        var seen = new HashSet<nint>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || !seen.Add(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return true;
            try
            {
                using var process = Process.GetProcessById(checked((int)pid));
                if (process.HasExited) return true;
                int length = Math.Clamp(NativeMethods.GetWindowTextLength(hwnd), 0, 32767);
                var title = new StringBuilder(length + 1);
                NativeMethods.GetWindowText(hwnd, title, title.Capacity);
                var className = new StringBuilder(256);
                NativeMethods.GetClassName(hwnd, className, className.Capacity);
                windows.Add(new WindowInfo(hwnd, (int)pid, process.ProcessName, title.ToString().Trim(), className.ToString()));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
            {
                // The process may exit or become inaccessible during enumeration.
            }
            return true;
        }, 0);
        return windows.OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.ProcessId)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => unchecked((ulong)w.Handle.ToInt64())).ToArray();
    }

    public WindowDimensions GetDimensions(nint hwnd)
    {
        EnsureWindow(hwnd);
        if (!NativeMethods.GetWindowRect(hwnd, out var outer)) throw LastError("GetWindowRect");
        if (!NativeMethods.GetClientRect(hwnd, out var client)) throw LastError("GetClientRect");
        return new WindowDimensions(outer.Width, outer.Height, client.Width, client.Height);
    }

    public void EnsureSameWindow(WindowInfo info)
    {
        EnsureWindow(info.Handle);
        NativeMethods.GetWindowThreadProcessId(info.Handle, out uint pid);
        if (pid != info.ProcessId) throw new InvalidOperationException("The selected HWND now belongs to a different process. Refresh the window list.");
    }

    public void BringToFront(nint hwnd, Action<string>? log = null)
    {
        EnsureWindow(hwnd);
        log?.Invoke($"Bring-to-front: {hwnd:X}; minimized={NativeMethods.IsIconic(hwnd)}");
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            if (NativeMethods.IsIconic(hwnd)) throw new InvalidOperationException("The target window could not be restored from minimized state.");
        }
        bool raised = NativeMethods.BringWindowToTop(hwnd);
        bool foreground = NativeMethods.SetForegroundWindow(hwnd);
        log?.Invoke($"BringWindowToTop: {(raised ? "success" : "failed")}; SetForegroundWindow: {(foreground ? "success" : "denied")}");
        if (!foreground) throw new InvalidOperationException("Windows did not grant foreground activation. The target may require user interaction or different privileges.");
    }

    public void RestoreNormal(nint hwnd)
    {
        EnsureWindow(hwnd);
        for (int attempt = 0; attempt < 3 && (NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd)); attempt++)
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        if (NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd))
            throw new InvalidOperationException("The target window could not be restored to normal state.");
    }

    public void ResizeOuterWindow(nint hwnd, int width, int height, Action<string>? log = null)
    {
        ValidateSize(width, height);
        EnsureWindow(hwnd);
        Resize(hwnd, width, height, log);
        var actual = GetDimensions(hwnd);
        if (actual.OuterWidth != width || actual.OuterHeight != height)
            throw new InvalidOperationException($"Target restricted outer size. Requested {width}x{height}; actual {actual.OuterWidth}x{actual.OuterHeight}.");
    }

    public void ResizeClientArea(nint hwnd, int clientWidth, int clientHeight, Action<string>? log = null)
    {
        ValidateSize(clientWidth, clientHeight);
        EnsureWindow(hwnd);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var before = GetDimensions(hwnd);
            int outerWidth = checked(before.OuterWidth + clientWidth - before.ClientWidth);
            int outerHeight = checked(before.OuterHeight + clientHeight - before.ClientHeight);
            log?.Invoke($"Calculated outer: {outerWidth}x{outerHeight} (attempt {attempt + 1})");
            Resize(hwnd, outerWidth, outerHeight, log);
            var after = GetDimensions(hwnd);
            if (after.ClientWidth == clientWidth && after.ClientHeight == clientHeight) return;
        }
        var actual = GetDimensions(hwnd);
        throw new InvalidOperationException($"Target restricted client size. Requested {clientWidth}x{clientHeight}; actual {actual.ClientWidth}x{actual.ClientHeight}.");
    }

    public void MoveWindow(nint hwnd, int x, int y)
    {
        EnsureWindow(hwnd);
        var size = GetDimensions(hwnd);
        if (!NativeMethods.SetWindowPos(hwnd, 0, x, y, size.OuterWidth, size.OuterHeight,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE)) throw LastError("SetWindowPos");
    }

    private static void Resize(nint hwnd, int width, int height, Action<string>? log)
    {
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Calculated outer dimensions must be positive.");
        bool success = NativeMethods.SetWindowPos(hwnd, 0, 0, 0, width, height,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        log?.Invoke($"SetWindowPos: {(success ? "success" : "failed")}");
        if (!success) throw LastError("SetWindowPos");
    }

    private static void ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive integers.");
    }

    private static void EnsureWindow(nint hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) throw new InvalidOperationException("The target window is no longer available. Refresh the window list.");
    }

    private static Win32Exception LastError(string operation)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{operation} failed (Win32 {code}). The target may be running at a higher privilege level: {new Win32Exception(code).Message}");
    }
}
