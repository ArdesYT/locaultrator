using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocaUltrator.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeminiUsageTier { Free, Tier1, Tier2, Tier3, Custom }

public enum GeminiKeyState
{
    Unknown, Testing, Ready, RateLimited, InvalidKey, LeakedKey, PermissionDenied,
    BillingRequired, ModelUnavailable, ServerBusy, ServerError, Timeout, NetworkError, Error
}

public static class GeminiUsageProfiles
{
    // A Google nem ad univerzális, Tierből levezethető RPM-et. Ezek csak óvatos
    // kezdőértékek; a projekt tényleges aktív limitjét az AI Studio mutatja.
    public static int SuggestedRequestsPerMinute(GeminiUsageTier tier) => 5;
    public static int SuggestedProRequestsPerMinute(GeminiUsageTier tier) => tier == GeminiUsageTier.Free ? 1 : 2;
    public static int SuggestedTokensPerMinute(GeminiUsageTier tier) => tier == GeminiUsageTier.Free ? 25_000 : 100_000;
    public static int SuggestedRequestsPerDay(GeminiUsageTier tier) => tier == GeminiUsageTier.Free ? 20 : 100;
    public static int SuggestedProTokensPerMinute(GeminiUsageTier tier) => tier == GeminiUsageTier.Free ? 1 : 25_000;
    public static int SuggestedProRequestsPerDay(GeminiUsageTier tier) => tier == GeminiUsageTier.Free ? 1 : 50;

    public static string DisplayName(GeminiUsageTier tier) => tier switch
    {
        GeminiUsageTier.Free => "Free",
        GeminiUsageTier.Tier1 => "Tier 1 · fizetős · $250 havi cap",
        GeminiUsageTier.Tier2 => "Tier 2 · fizetős · $2 000 havi cap",
        GeminiUsageTier.Tier3 => "Tier 3 · fizetős · $20 000+ havi cap",
        _ => "Egyéni"
    };

    public static bool AllowsGemini31Pro(GeminiUsageTier tier) => tier != GeminiUsageTier.Free;
}

public static class GeminiModelRouting
{
    public const string Flash37 = "gemini-3.7-flash";
    public const string Pro31 = "gemini-3.1-pro-preview";

    public static IReadOnlyList<string> Candidates(string? requestedModel, GeminiUsageTier tier)
    {
        string requested = string.Equals(requestedModel, Pro31, StringComparison.OrdinalIgnoreCase)
            ? Pro31 : Flash37;
        if (!GeminiUsageProfiles.AllowsGemini31Pro(tier)) return [Flash37];
        return requested == Pro31 ? [Pro31, Flash37] : [Flash37, Pro31];
    }

    public static int RequestsPerMinute(GeminiApiKeyEntry key, string model) =>
        string.Equals(model, Pro31, StringComparison.OrdinalIgnoreCase)
            ? key.ProRequestsPerMinute : key.RequestsPerMinute;
    public static int TokensPerMinute(GeminiApiKeyEntry key, string model) =>
        string.Equals(model, Pro31, StringComparison.OrdinalIgnoreCase)
            ? (key.ProTokensPerMinute <= 1 && GeminiUsageProfiles.AllowsGemini31Pro(key.UsageTier)
                ? GeminiUsageProfiles.SuggestedProTokensPerMinute(key.UsageTier) : key.ProTokensPerMinute)
            : key.TokensPerMinute;
    public static int RequestsPerDay(GeminiApiKeyEntry key, string model) =>
        string.Equals(model, Pro31, StringComparison.OrdinalIgnoreCase)
            ? (key.ProRequestsPerDay <= 1 && GeminiUsageProfiles.AllowsGemini31Pro(key.UsageTier)
                ? GeminiUsageProfiles.SuggestedProRequestsPerDay(key.UsageTier) : key.ProRequestsPerDay)
            : key.RequestsPerDay;
}

public sealed class GeminiSettings
{
    public string Model { get; set; } = GeminiModelRouting.Flash37;
    public int BatchSize { get; set; } = 30;
    public double Temperature { get; set; } = 0.10;
    public string ThinkingLevel { get; set; } = "high";
    public string StyleGuide { get; set; } = "Természetes, pontos, gördülékeny magyar játékfordítás. Ne adj magyarázatot.";
    public string Glossary { get; set; } = string.Empty;
    public bool RememberApiKey { get; set; }
}

public sealed class GeminiApiKeyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Gemini kulcs";
    public string Secret { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public GeminiUsageTier UsageTier { get; set; } = GeminiUsageTier.Free;
    public int RequestsPerMinute { get; set; } = GeminiUsageProfiles.SuggestedRequestsPerMinute(GeminiUsageTier.Free);
    public int TokensPerMinute { get; set; } = GeminiUsageProfiles.SuggestedTokensPerMinute(GeminiUsageTier.Free);
    public int RequestsPerDay { get; set; } = GeminiUsageProfiles.SuggestedRequestsPerDay(GeminiUsageTier.Free);
    public int ProRequestsPerMinute { get; set; } = GeminiUsageProfiles.SuggestedProRequestsPerMinute(GeminiUsageTier.Free);
    public int ProTokensPerMinute { get; set; } = GeminiUsageProfiles.SuggestedProTokensPerMinute(GeminiUsageTier.Free);
    public int ProRequestsPerDay { get; set; } = GeminiUsageProfiles.SuggestedProRequestsPerDay(GeminiUsageTier.Free);
    public string QuotaGroup { get; set; } = "Alapértelmezett projekt";
    [JsonIgnore] public GeminiKeyState RuntimeState { get; set; } = GeminiKeyState.Unknown;
    [JsonIgnore] public string RuntimeMessage { get; set; } = "Még nincs tesztelve.";
    [JsonIgnore] public DateTime? LastCheckedUtc { get; set; }
    [JsonIgnore] public string StatusLabel => !Enabled ? "KIKAPCSOLVA" : RuntimeState switch
    {
        GeminiKeyState.Testing => "TESZTELÉS",
        GeminiKeyState.Ready => "MŰKÖDIK",
        GeminiKeyState.RateLimited => "KVÓTA",
        GeminiKeyState.InvalidKey => "HIBÁS KULCS",
        GeminiKeyState.LeakedKey => "KISZIVÁRGOTT",
        GeminiKeyState.PermissionDenied => "NINCS JOG",
        GeminiKeyState.BillingRequired => "SZÁMLÁZÁS",
        GeminiKeyState.ModelUnavailable => "MODELLHIBA",
        GeminiKeyState.ServerBusy => "TERHELT",
        GeminiKeyState.ServerError => "SZERVERHIBA",
        GeminiKeyState.Timeout => "IDŐTÚLLÉPÉS",
        GeminiKeyState.NetworkError => "HÁLÓZAT",
        GeminiKeyState.Error => "HIBA",
        _ => "ISMERETLEN"
    };
    [JsonIgnore] public string StatusColor => !Enabled ? "#64748B" : RuntimeState switch
    {
        GeminiKeyState.Ready => "#5BE7C4",
        GeminiKeyState.Testing => "#45D8F2",
        GeminiKeyState.RateLimited => "#F6C85F",
        GeminiKeyState.ServerBusy or GeminiKeyState.Timeout or GeminiKeyState.NetworkError => "#F59E0B",
        GeminiKeyState.Unknown => "#64748B",
        _ => "#FB7185"
    };
    [JsonIgnore] public string UsageSummary => GeminiUsageProfiles.AllowsGemini31Pro(UsageTier)
        ? $"{GeminiUsageProfiles.DisplayName(UsageTier)} · 3.7: {RequestsPerMinute} RPM · Pro: {ProRequestsPerMinute} RPM · {QuotaGroup}"
        : $"Free · 3.7: {RequestsPerMinute} RPM · Pro nem elérhető · {QuotaGroup}";

    public GeminiApiKeyEntry Clone() => new()
    {
        Id = Id,
        Name = Name,
        Secret = Secret,
        Enabled = Enabled,
        IsActive = IsActive,
        UsageTier = UsageTier,
        RequestsPerMinute = RequestsPerMinute,
        TokensPerMinute = TokensPerMinute,
        RequestsPerDay = RequestsPerDay,
        ProRequestsPerMinute = ProRequestsPerMinute,
        ProTokensPerMinute = ProTokensPerMinute,
        ProRequestsPerDay = ProRequestsPerDay,
        QuotaGroup = QuotaGroup,
        RuntimeState = RuntimeState,
        RuntimeMessage = RuntimeMessage,
        LastCheckedUtc = LastCheckedUtc
    };
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string FilePath { get; } = Path.Combine(LocaPaths.Root, "gemini.settings.json");

    public async Task<GeminiSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return new GeminiSettings();
        try
        {
            await using FileStream stream = File.OpenRead(FilePath);
            GeminiSettings? settings = await JsonSerializer.DeserializeAsync<GeminiSettings>(stream, JsonOptions, cancellationToken);
            return Normalize(settings ?? new GeminiSettings());
        }
        catch (JsonException) { return new GeminiSettings(); }
    }

    public async Task SaveAsync(GeminiSettings settings, CancellationToken cancellationToken = default)
    {
        LocaPaths.EnsureCreated();
        settings = Normalize(settings);
        string temporary = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        File.Move(temporary, FilePath, true);
    }

    private static GeminiSettings Normalize(GeminiSettings settings)
    {
        settings.Model = string.Equals(settings.Model, GeminiModelRouting.Pro31, StringComparison.OrdinalIgnoreCase)
            ? GeminiModelRouting.Pro31 : GeminiModelRouting.Flash37;
        settings.BatchSize = Math.Clamp(settings.BatchSize, 1, 100);
        settings.Temperature = Math.Clamp(settings.Temperature, 0, 1);
        settings.ThinkingLevel = settings.ThinkingLevel.ToLowerInvariant() is "low" or "medium" or "high"
            ? settings.ThinkingLevel.ToLowerInvariant() : "high";
        return settings;
    }
}

public static class GeminiCredentialStore
{
    private const string LegacyTargetName = "LocaUltrator/GeminiApiKey";
    private const string LegacyKeyRingTargetName = "LocaUltrator/GeminiApiKeys.v2";
    private const string KeyRingManifestTargetName = "LocaUltrator/GeminiApiKeys.v3";
    private const string KeyTargetPrefix = "LocaUltrator/GeminiApiKey.v3/";
    private const uint GenericCredential = 1;
    private const uint LocalMachinePersistence = 2;

    public static IReadOnlyList<GeminiApiKeyEntry> ReadAll()
    {
        string manifest = ReadSecret(KeyRingManifestTargetName);
        if (!string.IsNullOrWhiteSpace(manifest))
        {
            try
            {
                string[]? ids = JsonSerializer.Deserialize<string[]>(manifest);
                if (ids is not null)
                {
                    var keys = new List<GeminiApiKeyEntry>();
                    foreach (string id in ids.Take(20))
                    {
                        if (!IsSafeId(id)) continue;
                        string serializedKey = ReadSecret(KeyTargetPrefix + id);
                        if (string.IsNullOrWhiteSpace(serializedKey)) continue;
                        GeminiApiKeyEntry? key = JsonSerializer.Deserialize<GeminiApiKeyEntry>(serializedKey);
                        if (key is not null) keys.Add(key);
                    }
                    if (keys.Count > 0) return Normalize(keys);
                }
            }
            catch (JsonException) { }
        }

        string legacyKeyRing = ReadSecret(LegacyKeyRingTargetName);
        if (!string.IsNullOrWhiteSpace(legacyKeyRing))
        {
            try
            {
                List<GeminiApiKeyEntry>? keys = JsonSerializer.Deserialize<List<GeminiApiKeyEntry>>(legacyKeyRing);
                if (keys is not null) return Normalize(keys);
            }
            catch (JsonException) { }
        }

        string legacy = ReadSecret(LegacyTargetName);
        return string.IsNullOrWhiteSpace(legacy)
            ? []
            : [new GeminiApiKeyEntry { Name = "Elsődleges kulcs", Secret = legacy, Enabled = true, IsActive = true }];
    }

    public static void WriteAll(IEnumerable<GeminiApiKeyEntry> entries)
    {
        List<GeminiApiKeyEntry> keys = Normalize(entries).ToList();
        if (keys.Count == 0) { DeleteAll(); return; }
        if (keys.Count > 20) throw new InvalidOperationException("Legfeljebb 20 Gemini API-kulcs tárolható.");

        string[] oldIds = ReadManifestIds();
        foreach (GeminiApiKeyEntry key in keys)
        {
            WriteSecret(KeyTargetPrefix + key.Id, JsonSerializer.Serialize(key));
        }
        WriteSecret(KeyRingManifestTargetName, JsonSerializer.Serialize(keys.Select(key => key.Id).ToArray()));
        foreach (string obsoleteId in oldIds.Except(keys.Select(key => key.Id), StringComparer.Ordinal))
            if (IsSafeId(obsoleteId)) DeleteSecret(KeyTargetPrefix + obsoleteId);
        DeleteSecret(LegacyKeyRingTargetName);
        DeleteSecret(LegacyTargetName);
    }

    public static string Read()
    {
        IReadOnlyList<GeminiApiKeyEntry> keys = ReadAll();
        return keys.FirstOrDefault(key => key.Enabled && key.IsActive)?.Secret
            ?? keys.FirstOrDefault(key => key.Enabled)?.Secret
            ?? string.Empty;
    }

    public static void Write(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) { DeleteAll(); return; }
        WriteAll([new GeminiApiKeyEntry { Name = "Elsődleges kulcs", Secret = apiKey.Trim(), Enabled = true, IsActive = true }]);
    }

    public static void Delete() => DeleteAll();

    public static void DeleteAll()
    {
        foreach (string id in ReadManifestIds())
            if (IsSafeId(id)) DeleteSecret(KeyTargetPrefix + id);
        DeleteSecret(KeyRingManifestTargetName);
        DeleteSecret(LegacyKeyRingTargetName);
        DeleteSecret(LegacyTargetName);
    }

    private static string[] ReadManifestIds()
    {
        string manifest = ReadSecret(KeyRingManifestTargetName);
        if (string.IsNullOrWhiteSpace(manifest)) return [];
        try { return JsonSerializer.Deserialize<string[]>(manifest) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string ReadSecret(string targetName)
    {
        if (!CredRead(targetName, GenericCredential, 0, out IntPtr pointer)) return string.Empty;
        try
        {
            CREDENTIAL credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return string.Empty;
            byte[] value = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, value, 0, value.Length);
            return Encoding.Unicode.GetString(value).TrimEnd('\0');
        }
        finally { CredFree(pointer); }
    }

    private static void WriteSecret(string targetName, string value)
    {
        byte[] secret = Encoding.Unicode.GetBytes(value);
        if (secret.Length > 2500)
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new InvalidOperationException("Egy API-kulcs rekordja túl nagy a Windows Hitelesítőadat-kezelőhöz.");
        }
        IntPtr blob = Marshal.AllocCoTaskMem(secret.Length);
        try
        {
            Marshal.Copy(secret, 0, blob, secret.Length);
            var credential = new CREDENTIAL
            {
                Type = GenericCredential,
                TargetName = targetName,
                CredentialBlobSize = (uint)secret.Length,
                CredentialBlob = blob,
                Persist = LocalMachinePersistence,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException("A Gemini API-kulcsok nem menthetők a Windows Hitelesítőadat-kezelőbe.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    private static void DeleteSecret(string targetName) => _ = CredDelete(targetName, GenericCredential, 0);

    private static IReadOnlyList<GeminiApiKeyEntry> Normalize(IEnumerable<GeminiApiKeyEntry> entries)
    {
        var result = new List<GeminiApiKeyEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (GeminiApiKeyEntry source in entries)
        {
            if (string.IsNullOrWhiteSpace(source.Secret)) continue;
            string id = !IsSafeId(source.Id) || !ids.Add(source.Id)
                ? Guid.NewGuid().ToString("N")
                : source.Id;
            ids.Add(id);
            result.Add(new GeminiApiKeyEntry
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(source.Name) ? $"Gemini kulcs {result.Count + 1}" : source.Name.Trim()[..Math.Min(source.Name.Trim().Length, 80)],
                Secret = source.Secret.Trim(),
                Enabled = source.Enabled,
                IsActive = source.Enabled,
                UsageTier = source.UsageTier,
                RequestsPerMinute = Math.Clamp(source.RequestsPerMinute <= 0
                    ? GeminiUsageProfiles.SuggestedRequestsPerMinute(source.UsageTier) : source.RequestsPerMinute, 1, 10_000),
                TokensPerMinute = Math.Clamp(source.TokensPerMinute <= 0
                    ? GeminiUsageProfiles.SuggestedTokensPerMinute(source.UsageTier) : source.TokensPerMinute, 1, 2_000_000_000),
                RequestsPerDay = Math.Clamp(source.RequestsPerDay <= 0
                    ? GeminiUsageProfiles.SuggestedRequestsPerDay(source.UsageTier) : source.RequestsPerDay, 1, 10_000_000),
                ProRequestsPerMinute = Math.Clamp(source.ProRequestsPerMinute <= 0
                    ? GeminiUsageProfiles.SuggestedProRequestsPerMinute(source.UsageTier) : source.ProRequestsPerMinute, 1, 10_000),
                ProTokensPerMinute = Math.Clamp(source.ProTokensPerMinute <= 0 ||
                    (source.ProTokensPerMinute <= 1 && GeminiUsageProfiles.AllowsGemini31Pro(source.UsageTier))
                    ? GeminiUsageProfiles.SuggestedProTokensPerMinute(source.UsageTier) : source.ProTokensPerMinute, 1, 2_000_000_000),
                ProRequestsPerDay = Math.Clamp(source.ProRequestsPerDay <= 0 ||
                    (source.ProRequestsPerDay <= 1 && GeminiUsageProfiles.AllowsGemini31Pro(source.UsageTier))
                    ? GeminiUsageProfiles.SuggestedProRequestsPerDay(source.UsageTier) : source.ProRequestsPerDay, 1, 10_000_000),
                QuotaGroup = string.IsNullOrWhiteSpace(source.QuotaGroup) ? "Alapértelmezett projekt" : source.QuotaGroup.Trim()[..Math.Min(source.QuotaGroup.Trim().Length, 80)]
            });
        }
        foreach (GeminiApiKeyEntry key in result) key.IsActive = key.Enabled;
        return result;
    }

    private static bool IsSafeId(string? value) => value is { Length: 32 } && value.All(Uri.IsHexDigit);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credential);
}
