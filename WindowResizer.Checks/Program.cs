using System.Windows.Forms;
using System.Diagnostics;
using System.Numerics;
using WindowResizer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"window-resizer-check-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(file);
            var first = store.Load(_ => { });
            Check(first.ApplyMode == ApplyMode.Window && first.Resolutions.Count == 7 && !first.DarkMode,
                "First launch defaults to light theme");
            first.Resolutions = new List<ResolutionInfo> { new(800, 600), new(600, 800), new(800, 600), new(1024, 768) };
            first.ApplyMode = ApplyMode.ContentArea;
            first.DarkMode = true;
            store.Save(first);
            var loaded = store.Load(_ => { });
            Check(loaded.Resolutions.SequenceEqual(new[] { new ResolutionInfo(600, 800), new(800, 600), new(1024, 768) }), "Resolution normalization");
            Check(loaded.ApplyMode == ApplyMode.ContentArea, "Mode persistence");
            Check(loaded.DarkMode, "Dark theme persistence");
            loaded.Resolutions.Clear();
            store.Save(loaded);
            Check(store.Load(_ => { }).Resolutions.Count == 0, "Saved empty list");
            loaded.DarkMode = false;
            store.Save(loaded);
            Check(!store.Load(_ => { }).DarkMode, "Light theme persistence");
            File.WriteAllText(file, "{\"Resolutions\":[],\"ApplyMode\":0}");
            Check(!store.Load(_ => { }).DarkMode, "Older settings default to light theme");
            var clicks = new DoubleClickTracker<ResolutionInfo>();
            long baseTick = Stopwatch.GetTimestamp();
            long step = Stopwatch.Frequency / 5;
            Check(!clicks.Register(new(800, 600), new Vector2(20, 20), baseTick, 500), "First row click selects only");
            Check(clicks.Register(new(800, 600), new Vector2(22, 21), baseTick + step, 500), "Second row click activates");
            Check(!clicks.Register(new(1024, 768), new Vector2(20, 20), baseTick + 2 * step, 500), "Different row does not activate");
            Check(BorderlessChrome.Classify(500, 20, 900, 700, false) == BorderlessChrome.HitResult.Draggable,
                "Custom title bar drags");
            Check(BorderlessChrome.Classify(840, 20, 900, 700, false) == BorderlessChrome.HitResult.Normal,
                "Control buttons receive clicks");
            Check(BorderlessChrome.Classify(2, 2, 900, 700, false) == BorderlessChrome.HitResult.ResizeTopLeft,
                "Borderless corner resizes");
            Check(BorderlessChrome.Classify(2, 2, 900, 700, true) == BorderlessChrome.HitResult.Draggable,
                "Maximized window has no resize grip");

            using var form = new Form { Text = "Window Resizer service check", Width = 520, Height = 420, StartPosition = FormStartPosition.Manual };
            form.Show();
            var service = new ExternalWindowService();
            var listed = service.GetWindows();
            Check(listed.All(w => w.Handle != form.Handle), "Own windows excluded");
            Check(listed.Select(w => w.Handle).Distinct().Count() == listed.Count, "No duplicate HWNDs");
            form.WindowState = FormWindowState.Maximized;
            Application.DoEvents();
            service.RestoreNormal(form.Handle);
            Check(form.WindowState == FormWindowState.Normal, "Maximized window restored");
            form.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            service.RestoreNormal(form.Handle);
            Check(form.WindowState == FormWindowState.Normal, "Minimized window restored");
            form.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            try { service.BringToFront(form.Handle); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("foreground activation", StringComparison.OrdinalIgnoreCase)) { }
            Check(form.WindowState != FormWindowState.Minimized, "Bring-to-front restores minimized window");
            service.ResizeOuterWindow(form.Handle, 640, 480);
            var outer = service.GetDimensions(form.Handle);
            Check(outer.OuterWidth == 640 && outer.OuterHeight == 480, "Outer size");
            service.ResizeClientArea(form.Handle, 800, 600);
            var client = service.GetDimensions(form.Handle);
            Check(client.ClientWidth == 800 && client.ClientHeight == 600, "Client size");
            form.Close();
            Console.WriteLine("All checks passed.");
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception($"Check failed: {label}");
        Console.WriteLine($"PASS {label}");
    }

}
