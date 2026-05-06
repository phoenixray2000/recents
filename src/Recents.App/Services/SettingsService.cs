using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Recents.App.Models;
using Recents.App.Services.Sources;
using Serilog;

namespace Recents.App.Services;

// PRD §6.18 / §8.3 配置服务：读写 %APPDATA%\Recents\settings.json
// 损坏时备份 .bak.<ts> 并重建默认值（§11 配置损坏处理）。
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented          = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _settingsPath;
    private readonly string _settingsDir;

    public AppSettings Current { get; private set; } = new();
    public string SettingsPath => _settingsPath;
    public string SettingsDirectory => _settingsDir;

    public SettingsService()
    {
        _settingsDir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Recents");
        _settingsPath = Path.Combine(_settingsDir, "settings.json");
    }

    // 从磁盘加载设置；损坏时备份并重建默认值。
    public void Load()
    {
        if (!File.Exists(_settingsPath))
        {
            Log.Information("SettingsService: 首次运行，创建默认 settings.json");
            Current = CreateDefault();
            EnsureSystemSources(Current);
            EnsureOpenWithSettings(Current);
            Save();
            return;
        }

        try
        {
            var json    = File.ReadAllText(_settingsPath);
            var loaded  = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            Current     = loaded ?? CreateDefault();
            EnsureSystemSources(Current);
            EnsureOpenWithSettings(Current);
            Log.Information("SettingsService: 配置加载成功，Sources={Count}", Current.Sources.Count);
        }
        catch (Exception ex)
        {
            BackupCorruptedFile();
            Log.Error(ex, "SettingsService: settings.json 损坏，已备份并重建默认值");
            Current = CreateDefault();
            EnsureSystemSources(Current);
            EnsureOpenWithSettings(Current);
            Save();
        }
    }

    // 将 Current 写回磁盘（原子写：先写临时文件再替换）
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_settingsDir);
            var json    = JsonSerializer.Serialize(Current, JsonOptions);
            var tmpPath = _settingsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _settingsPath, overwrite: true);
            Log.Debug("SettingsService: 配置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SettingsService: 保存配置失败");
        }
    }

    // 备份损坏的 settings.json 为 settings.json.bak.<yyyyMMddHHmmss>
    private void BackupCorruptedFile()
    {
        try
        {
            var ts      = DateTime.Now.ToString("yyyyMMddHHmmss");
            var bakPath = _settingsPath + $".bak.{ts}";
            File.Copy(_settingsPath, bakPath, overwrite: true);
            Log.Warning("SettingsService: 损坏文件已备份到 {BakPath}", LogPrivacy.Format(bakPath));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SettingsService: 备份损坏文件失败");
        }
    }

    // 创建默认设置，包含 6 个已知文件夹 Source（禁用 Path，Path 由 KnownFolderWatchSource 在运行期解析）
    private static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            Sources = new List<SourceConfig>
            {
                MakeKnownFolderSource("{374DE290-123F-4565-9164-39C4925E467B}", "Downloads"),
                MakeKnownFolderSource("{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", "Desktop"),
                MakeKnownFolderSource("{FDD39AD0-238F-46AF-ADB4-6C85480369C7}", "Documents"),
                MakeKnownFolderSource("{33E28130-4E1E-4676-835A-98395C3BC3BB}", "Pictures"),
                MakeKnownFolderSource("{18989B1D-99B5-455B-841C-AB7C74E4DDFC}", "Videos"),
                MakeKnownFolderSource("{4BD8D571-6D19-48D3-BE97-422220080E43}", "Music"),
            }
        };
    }

    private static void EnsureSystemSources(AppSettings settings)
    {
        const int removedJumpListAuto = 1 << 4;
        const int removedJumpListCustom = 1 << 5;

        settings.SystemSources.RemoveAll(s =>
            (int)s.Kind is removedJumpListAuto or removedJumpListCustom);

        var required = new[]
        {
            SourceKinds.RecentLnk,
            SourceKinds.OfficeMru,
            SourceKinds.OpenSavePidlMru,
        };

        foreach (var kind in required)
        {
            if (settings.SystemSources.All(s => s.Kind != kind))
                settings.SystemSources.Add(new SystemSourceConfig { Kind = kind, Enabled = true });
        }
    }

    private static void EnsureOpenWithSettings(AppSettings settings)
    {
        settings.OpenWithMaxAppsPerType = Math.Clamp(settings.OpenWithMaxAppsPerType, 1, 10);
        settings.OpenWithHistory = settings.OpenWithHistory is null
            ? new Dictionary<string, List<OpenWithAppConfig>>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, List<OpenWithAppConfig>>(settings.OpenWithHistory, StringComparer.OrdinalIgnoreCase);

        foreach (var key in settings.OpenWithHistory.Keys.ToList())
        {
            var items = settings.OpenWithHistory[key]
                .Where(a => !string.IsNullOrWhiteSpace(a.ExecutablePath))
                .OrderByDescending(a => a.LastUsedUtc)
                .Take(settings.OpenWithMaxAppsPerType)
                .ToList();

            if (items.Count == 0)
                settings.OpenWithHistory.Remove(key);
            else
                settings.OpenWithHistory[key] = items;
        }
    }

    private static SourceConfig MakeKnownFolderSource(string folderGuid, string displayName) =>
        new()
        {
            Id               = Guid.NewGuid().ToString(),
            Kind             = SourceKinds.KnownFolderWatch,
            KnownFolderGuid  = folderGuid,
            DisplayName      = displayName,
            Enabled          = true,
            RecentLookbackDays = 30,
        };
}
