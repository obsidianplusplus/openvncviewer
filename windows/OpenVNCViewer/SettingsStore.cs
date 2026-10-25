// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace OpenVNCViewer;

public sealed record ConnectionProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 5900;
    public string Username { get; init; } = "";
    public string PasswordToken { get; init; } = "";
    public ViewerOptions Options { get; init; } = new();
    [JsonIgnore] public string Address => $"{Host}:{Port}";
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;
}
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public ViewerOptions Defaults { get; set; } = new();
    public List<ConnectionProfile> Profiles { get; set; } = [];
    public string SelectedProfileId { get; set; } = "";
    public double WindowWidth { get; set; } = 1380;
    public double WindowHeight { get; set; } = 860;
    public bool Maximized { get; set; }
    public bool SidebarVisible { get; set; } = true;
}
public sealed class SettingsStore
{
    public string Path { get; }
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 16 };
    public SettingsStore(string? path = null) => Path = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenVNCViewer", "settings.json");
    public AppSettings Load()
    {
        try
        {
            var file = new FileInfo(Path);
            if (!file.Exists || file.Length > 2 * 1024 * 1024) return new();
            var state = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json) ?? new();
            state.Defaults = (state.Defaults ?? new()).Normalize();
            state.Profiles = (state.Profiles ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Host))
                .Take(50).Select(p => p with
                {
                    Id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N") : p.Id,
                    Name = p.Name ?? "", Host = p.Host.Trim(), Username = p.Username ?? "",
                    PasswordToken = p.PasswordToken ?? "", Port = Math.Clamp(p.Port, 1, 65535),
                    Options = (p.Options ?? new()).Normalize()
                }).DistinctBy(p => p.Id).ToList();
            state.WindowWidth = double.IsFinite(state.WindowWidth) ? Math.Clamp(state.WindowWidth, 960, 3840) : 1380;
            state.WindowHeight = double.IsFinite(state.WindowHeight) ? Math.Clamp(state.WindowHeight, 620, 2160) : 860;
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { return new(); }
    }
    public void Save(AppSettings state)
    {
        string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
