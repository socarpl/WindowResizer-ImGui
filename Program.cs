using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

namespace WindowResizer;

internal enum WindowChromeAction { None, Minimize, ToggleMaximize, Close }

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (Sdl2Native.SDL_Init(SDLInitFlags.Video) != 0)
            throw new InvalidOperationException("SDL video initialization failed.");
        // Veldrid's dedicated SDL event thread polls continuously while idle.
        // The resize hook below handles redraws inside the native drag loop.
        var window = new Sdl2Window("Window Resizer", 80, 60, 1180, 860,
            SDL_WindowFlags.OpenGL | SDL_WindowFlags.Resizable | SDL_WindowFlags.Shown | SDL_WindowFlags.Borderless,
            threadedProcessing: false);
        using var windowIcon = new WindowIcon(window.Handle);
        var chrome = new BorderlessChrome(window);
        using GraphicsDevice graphics = VeldridStartup.CreateGraphicsDevice(window,
            new GraphicsDeviceOptions(false, null, true, ResourceBindingModel.Improved, true, true),
            GraphicsBackend.Direct3D11);
        using (var commands = graphics.ResourceFactory.CreateCommandList())
        using (var renderer = new ImGuiRenderer(graphics, graphics.MainSwapchain.Framebuffer.OutputDescription, window.Width, window.Height))
        {
            nint mainHwnd = window.Handle;
            ImFontPtr regularFont = default;
            ImFontPtr largeFont = default;
            string fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "segoeui.ttf");
            if (File.Exists(fontPath))
            {
                var fonts = ImGui.GetIO().Fonts;
                regularFont = fonts.AddFontFromFileTTF(fontPath, 18);
                largeFont = fonts.AddFontFromFileTTF(fontPath, 38);
                renderer.RecreateFontDeviceTexture();
            }
            var app = new ResizerApp(mainHwnd, regularFont, largeFont);
            app.ConfigureStyle();
            int renderedWidth = window.Width;
            int renderedHeight = window.Height;
            var watch = Stopwatch.StartNew();
            // The UI has no animation, so avoid redrawing at the monitor's full refresh rate.
            TimeSpan frameInterval = TimeSpan.FromSeconds(1d / 30d);
            bool closeRequested = false;
            bool rendering = false;
            void RenderFrame(InputSnapshot input, int width, int height)
            {
                if (rendering) return;
                rendering = true;
                try
                {
                    float dt = Math.Max(1f / 1000f, (float)watch.Elapsed.TotalSeconds);
                    watch.Restart();
                    if (width != renderedWidth || height != renderedHeight)
                    {
                        graphics.MainSwapchain.Resize((uint)width, (uint)height);
                        renderer.WindowResized(width, height);
                        renderedWidth = width;
                        renderedHeight = height;
                    }
                    renderer.Update(dt, input);
                    app.Draw(width, height);
                    commands.Begin();
                    commands.SetFramebuffer(graphics.MainSwapchain.Framebuffer);
                    commands.ClearColorTarget(0, app.ClearColor);
                    renderer.Render(graphics, commands);
                    commands.End();
                    graphics.SubmitCommands(commands);
                    graphics.SwapBuffers(graphics.MainSwapchain);
                }
                finally { rendering = false; }
            }

            using var resizeRedraw = new ResizeRedrawHook(mainHwnd, () =>
            {
                if (NativeMethods.GetClientRect(mainHwnd, out var client) && client.Width > 0 && client.Height > 0)
                    RenderFrame(NoInputSnapshot.Instance, client.Width, client.Height);
            });
            while (window.Exists && !closeRequested)
            {
                var input = window.PumpEvents();
                if (!window.Exists) break;
                // SDL's cached size can lag until the Windows sizing loop exits.
                // The Win32 client rectangle reflects the live dragged size.
                if (!NativeMethods.GetClientRect(mainHwnd, out var client)) break;
                int width = client.Width;
                int height = client.Height;
                if (width <= 0 || height <= 0)
                {
                    Thread.Sleep(16);
                    continue;
                }
                RenderFrame(input, width, height);
                switch (app.TakeChromeAction())
                {
                    case WindowChromeAction.Minimize:
                        NativeMethods.ShowWindow(mainHwnd, NativeMethods.SW_MINIMIZE);
                        break;
                    case WindowChromeAction.ToggleMaximize:
                        NativeMethods.ShowWindow(mainHwnd, NativeMethods.IsZoomed(mainHwnd)
                            ? NativeMethods.SW_RESTORE : NativeMethods.SW_MAXIMIZE);
                        break;
                    case WindowChromeAction.Close:
                        closeRequested = true;
                        break;
                }
                TimeSpan remaining = frameInterval - watch.Elapsed;
                if (!closeRequested && remaining > TimeSpan.Zero) Thread.Sleep(remaining);
            }
            graphics.WaitForIdle();
        }
        if (window.Exists)
        {
            window.Close();
            var closeWatch = Stopwatch.StartNew();
            while (window.Exists && closeWatch.Elapsed < TimeSpan.FromSeconds(2)) Thread.Sleep(1);
        }
        GC.KeepAlive(chrome);
    }

    private sealed class NoInputSnapshot : InputSnapshot
    {
        public static readonly NoInputSnapshot Instance = new();
        public IReadOnlyList<KeyEvent> KeyEvents => Array.Empty<KeyEvent>();
        public IReadOnlyList<MouseEvent> MouseEvents => Array.Empty<MouseEvent>();
        public IReadOnlyList<char> KeyCharPresses => Array.Empty<char>();
        public Vector2 MousePosition => new(-1, -1);
        public float WheelDelta => 0;
        public bool IsMouseDown(MouseButton button) => false;
    }
}

internal sealed class ResizerApp
{
    private readonly nint _mainHwnd;
    private readonly ImFontPtr _regularFont;
    private readonly ImFontPtr _largeFont;
    private readonly ExternalWindowService _windows = new();
    private readonly SettingsStore _store = new();
    private readonly DoubleClickTracker<nint> _windowClicks = new();
    private readonly DoubleClickTracker<ResolutionInfo> _resolutionClicks = new();
    private readonly List<string> _log = new();
    private ApplicationSettings _settings;
    private IReadOnlyList<WindowInfo> _allWindows = Array.Empty<WindowInfo>();
    private nint _selectedHwnd;
    private ResolutionInfo? _selectedResolution;
    private string _filter = string.Empty;
    private string _status = "Ready";
    private string _dialogWidth = "800";
    private string _dialogHeight = "600";
    private string _dialogError = string.Empty;
    private ResolutionInfo? _editing;
    private bool _dialogPending;
    private bool _dialogOpen;
    private WindowChromeAction _pendingChromeAction;

    public ResizerApp(nint mainHwnd, ImFontPtr regularFont, ImFontPtr largeFont)
    {
        _mainHwnd = mainHwnd;
        _regularFont = regularFont;
        _largeFont = largeFont;
        _settings = _store.Load(Log);
        SaveSettings();
        RefreshWindows();
    }

    public void ConfigureStyle()
    {
        if (_settings.DarkMode) ImGui.StyleColorsDark();
        else ImGui.StyleColorsLight();
        var style = ImGui.GetStyle();
        style.WindowRounding = 0;
        style.ChildRounding = 5;
        style.FrameRounding = 5;
        style.FramePadding = new Vector2(10, 7);
        style.ItemSpacing = new Vector2(9, 9);
        style.WindowPadding = new Vector2(18, 16);
        if (_settings.DarkMode)
        {
            style.Colors[(int)ImGuiCol.Text] = new Vector4(.91f, .92f, .97f, 1);
            style.Colors[(int)ImGuiCol.TextDisabled] = new Vector4(.55f, .56f, .65f, 1);
            style.Colors[(int)ImGuiCol.WindowBg] = new Vector4(.17f, .17f, .23f, 1);
            style.Colors[(int)ImGuiCol.ChildBg] = new Vector4(.055f, .06f, .08f, 1);
            style.Colors[(int)ImGuiCol.PopupBg] = new Vector4(.11f, .12f, .17f, 1);
            style.Colors[(int)ImGuiCol.Border] = new Vector4(.34f, .35f, .45f, 1);
            style.Colors[(int)ImGuiCol.TitleBg] = new Vector4(.25f, .25f, .36f, 1);
            style.Colors[(int)ImGuiCol.TitleBgActive] = new Vector4(.32f, .32f, .47f, 1);
            style.Colors[(int)ImGuiCol.FrameBg] = new Vector4(.19f, .20f, .27f, 1);
            style.Colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(.26f, .27f, .38f, 1);
            style.Colors[(int)ImGuiCol.FrameBgActive] = new Vector4(.31f, .32f, .45f, 1);
            style.Colors[(int)ImGuiCol.Button] = new Vector4(.28f, .29f, .43f, 1);
            style.Colors[(int)ImGuiCol.ButtonHovered] = new Vector4(.38f, .39f, .58f, 1);
            style.Colors[(int)ImGuiCol.ButtonActive] = new Vector4(.45f, .45f, .67f, 1);
            style.Colors[(int)ImGuiCol.Header] = new Vector4(.38f, .37f, .68f, 1);
            style.Colors[(int)ImGuiCol.HeaderHovered] = new Vector4(.34f, .35f, .53f, 1);
            style.Colors[(int)ImGuiCol.HeaderActive] = new Vector4(.46f, .45f, .73f, 1);
            style.Colors[(int)ImGuiCol.Separator] = new Vector4(.34f, .35f, .46f, 1);
            style.Colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(.11f, .12f, .17f, 1);
            style.Colors[(int)ImGuiCol.ScrollbarGrab] = new Vector4(.31f, .32f, .49f, 1);
            style.Colors[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(.39f, .40f, .61f, 1);
            style.Colors[(int)ImGuiCol.ScrollbarGrabActive] = new Vector4(.46f, .47f, .69f, 1);
            style.Colors[(int)ImGuiCol.CheckMark] = new Vector4(.72f, .70f, 1, 1);
        }
        else
        {
            style.Colors[(int)ImGuiCol.WindowBg] = new Vector4(.94f, .96f, .98f, 1);
            style.Colors[(int)ImGuiCol.ChildBg] = new Vector4(1, 1, 1, 1);
            style.Colors[(int)ImGuiCol.Button] = new Vector4(.81f, .88f, .97f, 1);
            style.Colors[(int)ImGuiCol.ButtonHovered] = new Vector4(.68f, .80f, .96f, 1);
            style.Colors[(int)ImGuiCol.ButtonActive] = new Vector4(.54f, .71f, .94f, 1);
        }
    }

    public RgbaFloat ClearColor => _settings.DarkMode
        ? new RgbaFloat(.17f, .17f, .23f, 1)
        : new RgbaFloat(.93f, .95f, .97f, 1);

    public WindowChromeAction TakeChromeAction()
    {
        var action = _pendingChromeAction;
        _pendingChromeAction = WindowChromeAction.None;
        return action;
    }

    public unsafe void Draw(int width, int height)
    {
        if (_regularFont.NativePtr != null) ImGui.PushFont(_regularFont);
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(width, height));
        ImGui.Begin("##main", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings);
        DrawTitleBar();
        ImGui.Separator();
        float availableHeight = ImGui.GetContentRegionAvail().Y;
        float diagnosticsHeight = Math.Clamp(availableHeight * .27f, 155, 270);
        float mainHeight = Math.Max(180, availableHeight - diagnosticsHeight - 60);
        float availableWidth = ImGui.GetContentRegionAvail().X;
        float leftWidth = Math.Max(250, availableWidth * .52f);
        DrawWindowsPanel(new Vector2(leftWidth, mainHeight));
        ImGui.SameLine();
        DrawResolutionsPanel(new Vector2(Math.Max(240, availableWidth - leftWidth - ImGui.GetStyle().ItemSpacing.X), mainHeight));
        DrawDiagnostics(new Vector2(availableWidth, diagnosticsHeight));
        ImGui.Separator();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(_status);
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X - 32);
        DrawThemeButton();
        ImGui.End();
        DrawDialog();
        if (_regularFont.NativePtr != null) ImGui.PopFont();
    }

    private void DrawTitleBar()
    {
        ImGui.TextUnformatted("Window Resizer");
        ImGui.SameLine();
        float right = ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X;
        ImGui.SetCursorPosX(right - 128);
        if (ChromeButton("minimize", ChromeIcon.Minimize))
            _pendingChromeAction = WindowChromeAction.Minimize;
        ImGui.SameLine(0, 4);
        if (ChromeButton("maximize", NativeMethods.IsZoomed(_mainHwnd) ? ChromeIcon.Restore : ChromeIcon.Maximize))
            _pendingChromeAction = WindowChromeAction.ToggleMaximize;
        ImGui.SameLine(0, 4);
        if (ChromeButton("close", ChromeIcon.Close)) _pendingChromeAction = WindowChromeAction.Close;
    }

    private enum ChromeIcon { Minimize, Maximize, Restore, Close }

    private void DrawThemeButton()
    {
        bool clicked = ImGui.Button("##theme", new Vector2(30, 27));
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        if (clicked)
        {
            _settings.DarkMode = !_settings.DarkMode;
            ConfigureStyle();
            SaveSettings();
            Log($"Theme: {(_settings.DarkMode ? "Dark" : "Light")}");
        }

        Vector2 center = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
        var draw = ImGui.GetWindowDrawList();
        if (_settings.DarkMode)
        {
            uint sun = ImGui.ColorConvertFloat4ToU32(new Vector4(1, .84f, .46f, 1));
            draw.AddCircleFilled(center, 4.5f, sun, 24);
            for (int i = 0; i < 8; i++)
            {
                float angle = i * MathF.PI / 4;
                Vector2 direction = new(MathF.Cos(angle), MathF.Sin(angle));
                draw.AddLine(center + direction * 7, center + direction * 11, sun, 2);
            }
            if (hovered) ImGui.SetTooltip("Switch to light mode");
        }
        else
        {
            uint moon = ImGui.ColorConvertFloat4ToU32(new Vector4(.19f, .30f, .48f, 1));
            uint button = ImGui.GetColorU32(active ? ImGuiCol.ButtonActive
                : hovered ? ImGuiCol.ButtonHovered : ImGuiCol.Button);
            draw.AddCircleFilled(center, 8.5f, moon, 32);
            draw.AddCircleFilled(center + new Vector2(4, -3), 7.5f, button, 32);
            if (hovered) ImGui.SetTooltip("Switch to dark mode");
        }
    }

    private bool ChromeButton(string id, ChromeIcon icon)
    {
        bool close = icon == ChromeIcon.Close;
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, close
            ? new Vector4(.88f, .24f, .24f, 1) : _settings.DarkMode
                ? new Vector4(.39f, .40f, .58f, 1) : new Vector4(.79f, .85f, .92f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, close
            ? new Vector4(.72f, .15f, .15f, 1) : _settings.DarkMode
                ? new Vector4(.47f, .48f, .68f, 1) : new Vector4(.68f, .78f, .89f, 1));
        bool clicked = ImGui.Button($"##chrome-{id}", new Vector2(40, 28));
        ImGui.PopStyleColor(3);
        Vector2 center = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
        uint color = ImGui.GetColorU32(ImGuiCol.Text);
        var draw = ImGui.GetWindowDrawList();
        switch (icon)
        {
            case ChromeIcon.Minimize:
                draw.AddLine(center + new Vector2(-6, 5), center + new Vector2(6, 5), color, 1.5f);
                break;
            case ChromeIcon.Maximize:
                draw.AddRect(center + new Vector2(-6, -6), center + new Vector2(6, 6), color, 0, ImDrawFlags.None, 1.5f);
                break;
            case ChromeIcon.Restore:
                draw.AddRect(center + new Vector2(-4, -7), center + new Vector2(7, 4), color, 0, ImDrawFlags.None, 1.5f);
                draw.AddRectFilled(center + new Vector2(-7, -3), center + new Vector2(3, 7),
                    ImGui.GetColorU32(ImGuiCol.WindowBg));
                draw.AddRect(center + new Vector2(-7, -3), center + new Vector2(3, 7), color, 0, ImDrawFlags.None, 1.5f);
                break;
            case ChromeIcon.Close:
                draw.AddLine(center + new Vector2(-6, -6), center + new Vector2(6, 6), color, 1.5f);
                draw.AddLine(center + new Vector2(6, -6), center + new Vector2(-6, 6), color, 1.5f);
                break;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(icon switch
        {
            ChromeIcon.Minimize => "Minimize",
            ChromeIcon.Maximize => "Maximize",
            ChromeIcon.Restore => "Restore",
            _ => "Close"
        });
        return clicked;
    }

    private void DrawWindowsPanel(Vector2 size)
    {
        ImGui.BeginChild("Windows list", size, true);
        ImGui.Text("Windows list");
        ImGui.Separator();
        float buttonsWidth = 2 * 34 + 2 * ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(Math.Max(80, ImGui.GetContentRegionAvail().X - buttonsWidth));
        if (ImGui.InputTextWithHint("##filter", "Filter", ref _filter, 256))
            ClearHiddenSelection();
        ImGui.SameLine();
        if (IconButton("clear", Icon.Brush, "Clear filter")) { _filter = string.Empty; ClearHiddenSelection(); }
        ImGui.SameLine();
        if (IconButton("refresh", Icon.Refresh, "Refresh window list")) RefreshWindows();
        ImGui.BeginChild("window rows", new Vector2(0, 0), true);
        foreach (var entry in VisibleWindows())
        {
            bool selected = _selectedHwnd == entry.Handle;
            if (ImGui.Selectable($"{entry.Display}##{entry.HandleText}", selected, ImGuiSelectableFlags.AllowDoubleClick))
            {
                _selectedHwnd = entry.Handle;
                _status = $"Selected {entry.ProcessName} ({entry.HandleText})";
            }
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _selectedHwnd = entry.Handle;
                if (_windowClicks.Register(entry.Handle, ImGui.GetMousePos(), Stopwatch.GetTimestamp(), NativeMethods.GetDoubleClickTime()))
                    BringSelectedToFront();
            }
        }
        ImGui.EndChild();
        ImGui.EndChild();
    }

    private void DrawResolutionsPanel(Vector2 size)
    {
        ImGui.BeginChild("Resolutions", size, true);
        ImGui.Text("Resolutions");
        ImGui.Separator();
        if (IconButton("add", Icon.Plus, "Add resolution")) OpenDialog(null);
        ImGui.SameLine();
        ImGui.BeginDisabled(_selectedResolution is null);
        if (IconButton("edit", Icon.Pen, "Edit resolution")) OpenDialog(_selectedResolution);
        ImGui.SameLine();
        if (IconButton("delete", Icon.Trash, "Delete resolution")) DeleteSelectedResolution();
        ImGui.EndDisabled();
        float listHeight = Math.Max(80, ImGui.GetContentRegionAvail().Y - 160);
        ImGui.BeginChild("resolution rows", new Vector2(0, listHeight), true);
        foreach (var resolution in _settings.Resolutions)
        {
            bool selected = _selectedResolution == resolution;
            if (ImGui.Selectable(resolution.ToString(), selected, ImGuiSelectableFlags.AllowDoubleClick))
            {
                _selectedResolution = resolution;
            }
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _selectedResolution = resolution;
                if (_resolutionClicks.Register(resolution, ImGui.GetMousePos(), Stopwatch.GetTimestamp(), NativeMethods.GetDoubleClickTime()))
                {
                    Log($"Resolution double-click: {resolution}");
                    ApplySelectedResolution();
                }
            }
        }
        ImGui.EndChild();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Apply resolution to:");
        ImGui.SameLine(0, 16);
        if (ModeOption("Window", _settings.ApplyMode == ApplyMode.Window)) SetMode(ApplyMode.Window);
        ImGui.SameLine(0, 16);
        if (ModeOption("Content Area", _settings.ApplyMode == ApplyMode.ContentArea)) SetMode(ApplyMode.ContentArea);
        bool canApply = SelectedWindow() is not null && _selectedResolution is not null;
        ImGui.BeginDisabled(!canApply);
        if (ApplyButton(ImGui.GetContentRegionAvail().X)) ApplySelectedResolution();
        ImGui.EndDisabled();
        ImGui.EndChild();
    }

    private bool ModeOption(string label, bool selected)
    {
        const float diameter = 20;
        const float labelGap = 7;
        float height = ImGui.GetFrameHeight();
        float width = diameter + labelGap + ImGui.CalcTextSize(label).X;
        bool clicked = ImGui.InvisibleButton($"##mode-{label}", new Vector2(width, height));
        Vector2 min = ImGui.GetItemRectMin();
        Vector2 center = new(min.X + diameter / 2, min.Y + height / 2);
        var draw = ImGui.GetWindowDrawList();
        uint border = ImGui.ColorConvertFloat4ToU32(_settings.DarkMode
            ? new Vector4(.66f, .67f, .78f, 1) : new Vector4(.24f, .36f, .49f, 1));
        uint fill = _settings.DarkMode ? ImGui.GetColorU32(ImGuiCol.FrameBg)
            : ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1));
        uint dot = ImGui.ColorConvertFloat4ToU32(_settings.DarkMode
            ? new Vector4(.72f, .70f, 1, 1) : new Vector4(.16f, .46f, .88f, 1));
        draw.AddCircleFilled(center, diameter / 2, fill);
        draw.AddCircle(center, diameter / 2, border, 32, 2);
        if (selected) draw.AddCircleFilled(center, 5.5f, dot, 24);
        Vector2 labelSize = ImGui.CalcTextSize(label);
        draw.AddText(new Vector2(min.X + diameter + labelGap, min.Y + (height - labelSize.Y) / 2),
            ImGui.GetColorU32(ImGuiCol.Text), label);
        return clicked;
    }

    private void DrawDiagnostics(Vector2 size)
    {
        ImGui.BeginChild("Diagnostics", size, true);
        ImGui.Text("Diagnostics");
        ImGui.Separator();
        ImGui.BeginChild("log", new Vector2(0, 0), true);
        foreach (var line in _log) ImGui.TextUnformatted(line);
        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 12) ImGui.SetScrollHereY(1);
        ImGui.EndChild();
        ImGui.EndChild();
    }

    private void DrawDialog()
    {
        if (_dialogPending)
        {
            ImGui.OpenPopup("Add/Edit Resolution");
            _dialogPending = false;
            _dialogOpen = true;
        }
        ImGui.SetNextWindowSize(new Vector2(500, 280), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("Add/Edit Resolution", ref _dialogOpen, ImGuiWindowFlags.NoResize)) return;
        ImGui.BeginChild("Resolution", new Vector2(0, 135), true);
        ImGui.Text("Resolution");
        ImGui.Separator();
        NumericInput("Width", ref _dialogWidth);
        ImGui.SameLine();
        ImGui.Text("  x  ");
        ImGui.SameLine();
        NumericInput("Height", ref _dialogHeight);
        ImGui.EndChild();
        if (!string.IsNullOrEmpty(_dialogError))
        {
            ImGui.TextColored(new Vector4(.8f, .15f, .15f, 1), _dialogError);
        }
        else ImGui.Dummy(new Vector2(0, 22));
        float remaining = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, remaining - 206));
        if (ImGui.Button("Cancel", new Vector2(98, 36))) { _dialogOpen = false; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Save", new Vector2(98, 36))) SaveDialog();
        ImGui.EndPopup();
    }

    private unsafe void NumericInput(string label, ref string value)
    {
        ImGui.PushID(label);
        ImGui.SetNextItemWidth(145);
        if (_largeFont.NativePtr != null) ImGui.PushFont(_largeFont);
        ImGui.InputText("##number", ref value, 12, ImGuiInputTextFlags.CharsDecimal);
        if (_largeFont.NativePtr != null) ImGui.PopFont();
        ImGui.SameLine(0, 2);
        ImGui.BeginGroup();
        if (ImGui.SmallButton("+"))
            value = int.TryParse(value, out int up) && up < int.MaxValue ? (up + 1).ToString() : "1";
        if (ImGui.SmallButton("-"))
            value = int.TryParse(value, out int down) && down > 1 ? (down - 1).ToString() : "1";
        ImGui.EndGroup();
        ImGui.PopID();
    }

    private void OpenDialog(ResolutionInfo? resolution)
    {
        _editing = resolution;
        _dialogWidth = (resolution?.Width ?? 800).ToString();
        _dialogHeight = (resolution?.Height ?? 600).ToString();
        _dialogError = string.Empty;
        _dialogPending = true;
    }

    private void SaveDialog()
    {
        if (!int.TryParse(_dialogWidth, out int width) || width <= 0 ||
            !int.TryParse(_dialogHeight, out int height) || height <= 0)
        {
            _dialogError = "Width and height must be positive whole numbers.";
            return;
        }
        var resolution = new ResolutionInfo(width, height);
        if (_settings.Resolutions.Any(r => r == resolution && r != _editing))
        {
            _dialogError = $"{resolution} already exists.";
            return;
        }
        if (_editing is { } old) _settings.Resolutions.Remove(old);
        _settings.Resolutions.Add(resolution);
        _settings.Resolutions = SettingsStore.Normalize(_settings.Resolutions);
        _selectedResolution = resolution;
        SaveSettings();
        Log($"{(_editing is null ? "Added" : "Updated")} resolution: {resolution}");
        _dialogOpen = false;
        ImGui.CloseCurrentPopup();
    }

    private void DeleteSelectedResolution()
    {
        if (_selectedResolution is not { } resolution) return;
        _settings.Resolutions.Remove(resolution);
        _selectedResolution = null;
        SaveSettings();
        Log($"Deleted resolution: {resolution}");
    }

    private void SetMode(ApplyMode mode)
    {
        _settings.ApplyMode = mode;
        SaveSettings();
        Log($"Apply resolution to: {(mode == ApplyMode.Window ? "Window" : "Content Area")}");
    }

    private void SaveSettings()
    {
        try { _store.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"Could not save settings: {ex.Message}"); }
    }

    private void RefreshWindows()
    {
        _allWindows = _windows.GetWindows();
        ClearHiddenSelection();
        Log($"Refreshed window list: {_allWindows.Count} visible external windows.");
    }

    private IEnumerable<WindowInfo> VisibleWindows() => _allWindows.Where(w =>
        string.IsNullOrEmpty(_filter) || w.ProcessName.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        w.Title.Contains(_filter, StringComparison.OrdinalIgnoreCase));

    private void ClearHiddenSelection()
    {
        if (_selectedHwnd != 0 && !VisibleWindows().Any(w => w.Handle == _selectedHwnd)) _selectedHwnd = 0;
    }

    private WindowInfo? SelectedWindow() => VisibleWindows().FirstOrDefault(w => w.Handle == _selectedHwnd);

    private void BringSelectedToFront()
    {
        var target = SelectedWindow();
        if (target is null) return;
        try
        {
            _windows.EnsureSameWindow(target);
            Log($"Target HWND: {target.HandleText}; PID: {target.ProcessId}; process: {target.ProcessName}");
            _windows.BringToFront(target.Handle, Log);
            _status = $"Brought {target.ProcessName} to front";
        }
        catch (Exception ex) { Log($"Bring-to-front failed: {ex.Message}"); }
    }

    private void ApplySelectedResolution()
    {
        var target = SelectedWindow();
        if (target is null || _selectedResolution is not { } resolution)
        {
            Log("Select a visible target window and a resolution before applying.");
            _status = "Select a target window and a resolution";
            return;
        }
        try
        {
            _windows.EnsureSameWindow(target);
            _windows.RestoreNormal(target.Handle);
            var before = _windows.GetDimensions(target.Handle);
            Log($"Target HWND: {target.HandleText}; PID: {target.ProcessId}; process: {target.ProcessName}");
            Log($"Current outer: {before.OuterWidth}x{before.OuterHeight}; client: {before.ClientWidth}x{before.ClientHeight}");
            Log($"Selected resolution: {resolution}; Apply resolution to: {(_settings.ApplyMode == ApplyMode.Window ? "Window" : "Content Area")}");
            if (_settings.ApplyMode == ApplyMode.Window)
                _windows.ResizeOuterWindow(target.Handle, resolution.Width, resolution.Height, Log);
            else
                _windows.ResizeClientArea(target.Handle, resolution.Width, resolution.Height, Log);
            _status = $"Applied {resolution} to {target.ProcessName}";
        }
        catch (Exception ex)
        {
            Log($"Resize failed: {ex.Message}");
            _status = "Resize failed — see Diagnostics";
        }
        finally
        {
            try
            {
                if (NativeMethods.IsWindow(target.Handle))
                {
                    var final = _windows.GetDimensions(target.Handle);
                    Log($"Final outer: {final.OuterWidth}x{final.OuterHeight}; client: {final.ClientWidth}x{final.ClientHeight}");
                }
            }
            catch (Exception ex) { Log($"Final measurement failed: {ex.Message}"); }
        }
    }

    private void Log(string message)
    {
        _log.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        if (_log.Count > 300) _log.RemoveAt(0);
    }

    private enum Icon { Plus, Pen, Trash, Brush, Refresh }

    private static bool ApplyButton(float width)
    {
        bool clicked = ImGui.Button("##apply resolution", new Vector2(width, 64));
        var min = ImGui.GetItemRectMin();
        var draw = ImGui.GetWindowDrawList();
        uint color = ImGui.GetColorU32(ImGuiCol.Text);
        Vector2 gear = new(min.X + width / 2, min.Y + 19);
        draw.AddCircle(gear, 8, color, 20, 2);
        draw.AddCircleFilled(gear, 2.5f, color);
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            draw.AddLine(gear + direction * 8, gear + direction * 12, color, 2);
        }
        string label = "Set Resolution";
        float labelWidth = ImGui.CalcTextSize(label).X;
        draw.AddText(new Vector2(min.X + (width - labelWidth) / 2, min.Y + 38), color, label);
        return clicked;
    }

    private static bool IconButton(string id, Icon icon, string tooltip)
    {
        Vector2 size = new(34, 31);
        bool clicked = ImGui.Button($"##{id}", size);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var center = (min + max) / 2;
        var draw = ImGui.GetWindowDrawList();
        uint color = ImGui.GetColorU32(ImGuiCol.Text);
        switch (icon)
        {
            case Icon.Plus:
                draw.AddLine(center + new Vector2(-7, 0), center + new Vector2(7, 0), color, 2);
                draw.AddLine(center + new Vector2(0, -7), center + new Vector2(0, 7), color, 2);
                break;
            case Icon.Pen:
                draw.AddLine(center + new Vector2(-7, 6), center + new Vector2(6, -7), color, 3);
                draw.AddTriangleFilled(center + new Vector2(-9, 9), center + new Vector2(-7, 3), center + new Vector2(-3, 7), color);
                break;
            case Icon.Trash:
                draw.AddRect(center + new Vector2(-6, -4), center + new Vector2(6, 8), color, 1, ImDrawFlags.None, 2);
                draw.AddLine(center + new Vector2(-9, -7), center + new Vector2(9, -7), color, 2);
                break;
            case Icon.Brush:
                draw.AddLine(center + new Vector2(-5, 7), center + new Vector2(6, -7), color, 4);
                draw.AddTriangleFilled(center + new Vector2(-8, 8), center + new Vector2(-2, 8), center + new Vector2(-6, 2), color);
                break;
            case Icon.Refresh:
                DrawRecycleArrow(draw, center, -2.8f, -1.0f, color);
                DrawRecycleArrow(draw, center, -.7f, 1.1f, color);
                DrawRecycleArrow(draw, center, 1.4f, 3.2f, color);
                break;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    private static void DrawRecycleArrow(ImDrawListPtr draw, Vector2 center, float start, float end, uint color)
    {
        const float radius = 8;
        const int segments = 10;
        Vector2 previous = center + radius * new Vector2(MathF.Cos(start), MathF.Sin(start));
        for (int i = 1; i <= segments; i++)
        {
            float angle = start + (end - start) * i / segments;
            Vector2 next = center + radius * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            draw.AddLine(previous, next, color, 2.2f);
            previous = next;
        }
        Vector2 tangent = new(-MathF.Sin(end), MathF.Cos(end));
        Vector2 radial = new(MathF.Cos(end), MathF.Sin(end));
        Vector2 baseCenter = previous - tangent * 4;
        draw.AddTriangleFilled(previous + tangent * 2, baseCenter + radial * 2.8f,
            baseCenter - radial * 2.8f, color);
    }
}
