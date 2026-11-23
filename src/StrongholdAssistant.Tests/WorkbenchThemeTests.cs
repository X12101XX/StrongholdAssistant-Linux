using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using StrongholdAssistant.Host;

namespace StrongholdAssistant.Tests;

public sealed class WorkbenchThemeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "StrongholdAssistant.Tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Theory]
    [InlineData("glass")]
    [InlineData("round")]
    public async Task ThemeSchemeAndAccentSurviveSettingsAndBackup(string skin)
    {
        var api = await HostApi.CreateAsync(Path.Combine(AppContext.BaseDirectory, "data"), _directory);
        api.SaveSettings(api.Settings with { UiSkin = skin, UiScheme = "light", UiAccent = "#22C55E" });
        var loaded = api.SettingsStore.LoadSettings();
        Assert.Equal(skin, loaded.UiSkin);
        Assert.Equal("light", loaded.UiScheme);
        Assert.Equal("#22c55e", loaded.UiAccent);
        Assert.Equal("#22c55e", api.Settings.UiAccent);
        var path = Path.Combine(_directory, "backup.json");
        await api.ExportBackupAsync(path);
        api.SaveSettings(api.Settings with { UiScheme = "dark", UiAccent = "" });
        var backup = await api.ReadBackupAsync(path);
        await api.ImportDataAsync(backup.Token, "replace");
        Assert.Equal("light", api.Settings.UiScheme);
        Assert.Equal("#22c55e", api.Settings.UiAccent);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        root["settings"]!.AsObject().Remove("uiScheme");
        root["settings"]!.AsObject().Remove("uiAccent");
        await File.WriteAllTextAsync(path, root.ToJsonString());
        backup = await api.ReadBackupAsync(path);
        await api.ImportDataAsync(backup.Token, "replace");
        Assert.Equal("system", api.Settings.UiScheme);
        Assert.Equal("", api.Settings.UiAccent);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"uiScheme\":\"invalid\",\"uiAccent\":\"#xyz123\"}")]
    public void MissingOrInvalidSchemeAndAccentUseDefaults(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);
        var settings = new HostSettingsStore(SettingsPath).LoadSettings();
        Assert.Equal("system", settings.UiScheme);
        Assert.Equal("", settings.UiAccent);
    }

    [Fact]
    public void InvalidSchemeAndAccentAreRejected()
    {
        var store = new HostSettingsStore(SettingsPath);
        var settings = store.LoadSettings();
        Assert.Equal("界面配色无效。", Assert.Throws<ArgumentException>(() =>
            store.SaveSettings(settings with { UiScheme = "invalid" })).Message);
        Assert.Equal("主题色无效。", Assert.Throws<ArgumentException>(() =>
            store.SaveSettings(settings with { UiAccent = "#123" })).Message);
    }

    [Theory]
    [InlineData(1920, 1080, 1200, 800)]
    [InlineData(1000, 700, 1000, 700)]
    public void MinimumSizeScalesAndFitsWorkArea(double width, double height, double minWidth, double minHeight)
    {
        var work = new Rect(0, 0, width, height);
        Assert.Equal(new Size(minWidth, minHeight), WorkbenchGeometry.MinimumSize(work, 1.25));
        var bounds = WorkbenchGeometry.Calculate(work, 1.25, new(0, 0, 960, 640));
        Assert.Equal(minWidth, bounds.Width);
        Assert.Equal(minHeight, bounds.Height);
    }

    [Fact]
    public void WorkbenchMaximizedSurvivesReloadAndOldBoundsDefaultToNormal()
    {
        var store = new HostSettingsStore(SettingsPath);
        var dock = new DockSettings(WorkbenchBounds: new(100, 50, 1280, 800, true));
        store.SaveDock(dock);
        Assert.Equal(dock, new HostSettingsStore(SettingsPath).LoadDock());
        File.WriteAllText(SettingsPath, """{"workbenchBounds":{"left":100,"top":50,"width":1280,"height":800}}""");
        Assert.Equal(new FreeWindowBounds(100, 50, 1280, 800), store.LoadDock().WorkbenchBounds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
