using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocaUltrator.Core;

public sealed class GeminiKeyFileDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Figyelmeztetes { get; set; } =
        "Ez a fájl titkos Gemini API-kulcsokat tartalmaz. Ne oszd meg és ne töltsd fel nyilvános helyre.";
    public string KeziSzerkesztes { get; set; } =
        "A lenti példát másold a keys tömb szögletes zárójelei közé, majd írd át a secret értékét.";
    public GeminiKeyFileExample KezilegMasolhatoPelda { get; set; } = new();
    public List<GeminiApiKeyEntry> Keys { get; set; } = [];
}

public sealed class GeminiKeyFileExample
{
    public string Name { get; set; } = "Gemini kulcs 1";
    public string Secret { get; set; } = "IDE_ILLASZD_A_GEMINI_API_KULCSOT";
    public bool Enabled { get; set; } = true;
    public GeminiUsageTier UsageTier { get; set; } = GeminiUsageTier.Free;
    public int RequestsPerMinute { get; set; } = 5;
    public int TokensPerMinute { get; set; } = 25_000;
    public int RequestsPerDay { get; set; } = 20;
    public int ProRequestsPerMinute { get; set; } = 1;
    public int ProTokensPerMinute { get; set; } = 1;
    public int ProRequestsPerDay { get; set; } = 1;
    public string QuotaGroup { get; set; } = "Alapértelmezett projekt";
}

/// <summary>
/// Kézzel is szerkeszthető, atomi mentésű elsődleges Gemini-kulcstároló.
/// </summary>
public sealed class GeminiKeyFileStore
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public GeminiKeyFileStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(LocaPaths.Root, "gemini-kulcsok.json")
            : Path.GetFullPath(filePath);
    }

    public string FilePath { get; }
    public string BackupPath => FilePath + ".bak";
    public bool Exists => File.Exists(FilePath);

    public async Task<IReadOnlyList<GeminiApiKeyEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            await using FileStream stream = new(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            List<GeminiApiKeyEntry>? keys = json.RootElement.ValueKind switch
            {
                JsonValueKind.Array => json.RootElement.Deserialize<List<GeminiApiKeyEntry>>(Options),
                JsonValueKind.Object => json.RootElement.Deserialize<GeminiKeyFileDocument>(Options)?.Keys,
                _ => throw new JsonException("A gyökérelem csak objektum vagy kulcslista lehet.")
            };
            return Normalize(keys ?? []);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"A Gemini-kulcsfájl JSON-formátuma hibás, ezért a program változatlanul hagyta: {FilePath}", ex);
        }
    }

    public async Task SaveAsync(IEnumerable<GeminiApiKeyEntry> entries, CancellationToken cancellationToken = default)
    {
        List<GeminiApiKeyEntry> keys = Normalize(entries).Select(key => key.Clone()).ToList();
        var document = new GeminiKeyFileDocument { Keys = keys };
        string? directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("A kulcsfájl mentési mappája nem állapítható meg.");
        Directory.CreateDirectory(directory);

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporary = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, true))
            {
                await JsonSerializer.SerializeAsync(stream, document, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(FilePath)) File.Copy(FilePath, BackupPath, true);
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _saveGate.Release();
        }
    }

    private static IReadOnlyList<GeminiApiKeyEntry> Normalize(IEnumerable<GeminiApiKeyEntry> entries)
    {
        var result = new List<GeminiApiKeyEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (GeminiApiKeyEntry source in entries)
        {
            if (result.Count >= 20) throw new InvalidDataException("A kulcsfájl legfeljebb 20 Gemini API-kulcsot tartalmazhat.");
            if (string.IsNullOrWhiteSpace(source.Secret)) continue;
            string sourceId = source.Id ?? string.Empty;
            string id = IsSafeId(sourceId) && ids.Add(sourceId) ? sourceId : Guid.NewGuid().ToString("N");
            ids.Add(id);
            string name = string.IsNullOrWhiteSpace(source.Name) ? $"Gemini kulcs {result.Count + 1}" : source.Name.Trim();
            string group = string.IsNullOrWhiteSpace(source.QuotaGroup) ? "Alapértelmezett projekt" : source.QuotaGroup.Trim();
            result.Add(new GeminiApiKeyEntry
            {
                Id = id,
                Name = name[..Math.Min(name.Length, 80)],
                Secret = source.Secret.Trim(),
                Enabled = source.Enabled,
                IsActive = source.Enabled,
                UsageTier = source.UsageTier,
                RequestsPerMinute = Math.Clamp(source.RequestsPerMinute <= 0
                    ? GeminiUsageProfiles.SuggestedRequestsPerMinute(source.UsageTier)
                    : source.RequestsPerMinute, 1, 10_000),
                TokensPerMinute = Math.Clamp(source.TokensPerMinute <= 0
                    ? GeminiUsageProfiles.SuggestedTokensPerMinute(source.UsageTier)
                    : source.TokensPerMinute, 1, 2_000_000_000),
                RequestsPerDay = Math.Clamp(source.RequestsPerDay <= 0
                    ? GeminiUsageProfiles.SuggestedRequestsPerDay(source.UsageTier)
                    : source.RequestsPerDay, 1, 10_000_000),
                ProRequestsPerMinute = Math.Clamp(source.ProRequestsPerMinute <= 0
                    ? GeminiUsageProfiles.SuggestedProRequestsPerMinute(source.UsageTier)
                    : source.ProRequestsPerMinute, 1, 10_000),
                ProTokensPerMinute = Math.Clamp(source.ProTokensPerMinute <= 0 ||
                    (source.ProTokensPerMinute <= 1 && GeminiUsageProfiles.AllowsGemini31Pro(source.UsageTier))
                    ? GeminiUsageProfiles.SuggestedProTokensPerMinute(source.UsageTier)
                    : source.ProTokensPerMinute, 1, 2_000_000_000),
                ProRequestsPerDay = Math.Clamp(source.ProRequestsPerDay <= 0 ||
                    (source.ProRequestsPerDay <= 1 && GeminiUsageProfiles.AllowsGemini31Pro(source.UsageTier))
                    ? GeminiUsageProfiles.SuggestedProRequestsPerDay(source.UsageTier)
                    : source.ProRequestsPerDay, 1, 10_000_000),
                QuotaGroup = group[..Math.Min(group.Length, 80)]
            });
        }
        return result;
    }

    private static bool IsSafeId(string value) => value.Length == 32 && value.All(Uri.IsHexDigit);
}
