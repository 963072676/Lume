using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lume.Core;

namespace Lume.Desktop;

internal sealed record AiSettings(string BaseUrl = "https://api.openai.com/v1", string Model = "", string EncryptedKey = "", bool IncludePaths = false, string Preference = "");
internal sealed class AiSettingsStore(string directory)
{
    private string FilePath => Path.Combine(directory, "ai-settings.json");
    public AiSettings Load() => File.Exists(FilePath) ? JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("AI 配置为空。") : new();
    public string ReadKey(AiSettings settings) => settings.EncryptedKey.Length == 0 ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(settings.EncryptedKey), null, DataProtectionScope.CurrentUser));
    public void Save(AiConnection connection, bool includePaths, string preference)
    {
        _ = AiAnalysisClient.Endpoint(connection.BaseUrl, "models");
        var encrypted = connection.ApiKey.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(connection.ApiKey), null, DataProtectionScope.CurrentUser));
        var settings = new AiSettings(connection.BaseUrl.Trim(), connection.Model.Trim(), encrypted, includePaths, preference);
        Directory.CreateDirectory(directory); var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, FilePath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
