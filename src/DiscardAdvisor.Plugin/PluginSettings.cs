using System;
using System.IO;
using Newtonsoft.Json;

namespace DiscardAdvisor.Plugin;

public enum PluginPresentationMode
{
    Shadow,
    Experimental
}

public sealed class PluginSettings
{
    private PluginSettings(PluginPresentationMode presentationMode)
    {
        PresentationMode = presentationMode;
    }

    public PluginPresentationMode PresentationMode { get; }

    public bool ShowOverlay => PresentationMode == PluginPresentationMode.Experimental;

    public string ModeName => PresentationMode.ToString().ToLowerInvariant();

    public static PluginSettings Experimental { get; } = new(PluginPresentationMode.Experimental);

    public static PluginSettings Shadow { get; } = new(PluginPresentationMode.Shadow);

    public static PluginSettings Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A settings path is required.", nameof(path));
        if (!File.Exists(path))
            return Shadow;
        try
        {
            var document = JsonConvert.DeserializeObject<SettingsDocument>(File.ReadAllText(path));
            if (string.Equals(document?.Mode, "experimental", StringComparison.OrdinalIgnoreCase))
                return Experimental;
            if (string.Equals(document?.Mode, "shadow", StringComparison.OrdinalIgnoreCase))
                return Shadow;
            return Shadow;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Shadow;
        }
    }

    private sealed class SettingsDocument
    {
        public string? Mode { get; set; }
    }
}
