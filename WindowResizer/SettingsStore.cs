using System.Text.Json;

namespace WindowResizer;

public readonly record struct ResolutionInfo(int Width, int Height)
{
    public override string ToString() => $"{Width} x {Height}";
}

public enum ApplyMode { Window, ContentArea }

public sealed class ApplicationSettings
{
    public List<ResolutionInfo> Resolutions { get; set; } = new();
    public ApplyMode ApplyMode { get; set; } = ApplyMode.Window;
    public bool DarkMode { get; set; }
}

public sealed class SettingsStore
{
    public string Path { get; }
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public SettingsStore(string? path = null) => Path = path ?? System.IO.Path.Combine(AppContext.BaseDirectory, "application_settings.json");

    public ApplicationSettings Load(Action<string> log)
    {
        if (!File.Exists(Path))
        {
            log("First launch: loaded default resolutions and Window mode.");
            return new ApplicationSettings
            {
                Resolutions = new List<ResolutionInfo>
                {
                    new(800, 600), new(1024, 768), new(1280, 720), new(1920, 1080),
                    new(1920, 1200), new(2560, 1440), new(3840, 2160)
                }
            };
        }
        try
        {
            var settings = JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(Path), _json)
                ?? throw new JsonException("Settings are empty.");
            var savedResolutions = settings.Resolutions ?? new();
            settings.Resolutions = Normalize(savedResolutions);
            if (!Enum.IsDefined(settings.ApplyMode)) settings.ApplyMode = ApplyMode.Window;
            if (!savedResolutions.SequenceEqual(settings.Resolutions))
            {
                try { Save(settings); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { log($"Could not normalize saved settings: {ex.Message}"); }
            }
            log($"Loaded {settings.Resolutions.Count} saved resolutions.");
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log($"Settings could not be loaded: {ex.Message}");
            return new ApplicationSettings();
        }
    }

    public void Save(ApplicationSettings settings)
    {
        settings.Resolutions = Normalize(settings.Resolutions);
        string temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, _json));
        File.Move(temp, Path, true);
    }

    public static List<ResolutionInfo> Normalize(IEnumerable<ResolutionInfo> resolutions) => resolutions
        .Where(r => r.Width > 0 && r.Height > 0)
        .Distinct()
        .OrderBy(r => r.Width).ThenBy(r => r.Height).ToList();
}
