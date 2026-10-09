using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LocaUltrator.Core;

public sealed record TranslationProgress(int Completed, int Total, string Message);
public sealed record GeminiFailureInfo(GeminiKeyState State, string UserMessage, bool Retryable, bool DisableForRun);
public sealed record GeminiConnectionTestResult(
    bool KeyValidated, bool GenerationSucceeded, string RequestedModel, string UsedModel, string Message);
public sealed record TranslationRunResult(int Applied, int Rejected, int Failed, IReadOnlyList<string> Errors)
{
    public bool ApiUnavailable { get; init; }
    public string LastKeyName { get; init; } = string.Empty;
    public int KeySwitches { get; init; }
    public string LastModel { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, int> KeyUsage { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, GeminiKeyState> KeyStates { get; init; } = new Dictionary<string, GeminiKeyState>();
    public IReadOnlyDictionary<string, string> KeyMessages { get; init; } = new Dictionary<string, string>();
}

public sealed class GeminiTranslationService : IDisposable
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly TokenProtectionService _protection;
    private readonly GeminiRequestPacer _pacer;
    private readonly bool _ownsClient;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public GeminiTranslationService(HttpClient? httpClient = null, TokenProtectionService? protection = null,
        GeminiRequestPacer? requestPacer = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(190) };
        _protection = protection ?? new TokenProtectionService();
        _pacer = requestPacer ?? new GeminiRequestPacer();
    }

    public async Task TestConnectionAsync(string apiKey, GeminiSettings settings, CancellationToken cancellationToken = default)
    {
        var key = new GeminiApiKeyEntry { Name = "Gemini kulcs", Secret = apiKey, Enabled = true, IsActive = true,
            RequestsPerMinute = 60, QuotaGroup = "Kapcsolatpróba" };
        _ = await TestConnectionDetailedAsync(key, settings, cancellationToken);
    }

    public async Task TestConnectionAsync(GeminiApiKeyEntry key, GeminiSettings settings,
        CancellationToken cancellationToken = default)
        => _ = await TestConnectionDetailedAsync(key, settings, cancellationToken);

    public async Task<GeminiConnectionTestResult> TestConnectionDetailedAsync(GeminiApiKeyEntry key, GeminiSettings settings,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(key.Secret, settings);
        IReadOnlyList<string> candidates = GeminiModelRouting.Candidates(settings.Model, key.UsageTier);
        string validatedModel = await ValidateKeyAndModelAsync(key, candidates[0], cancellationToken);
        var row = new RequestRow("connection-test", "Translate this word: Save", "Button label");
        try
        {
            (IReadOnlyDictionary<string, string> result, string usedModel) = await TranslateForKeyWithModelFallbackAsync(
                [row], "angol", "magyar", "Pontosan, röviden fordíts.", settings, key,
                false, null, 0, 1, cancellationToken);
            if (!result.TryGetValue(row.Id, out string? value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("A Gemini válasza nem tartalmazott fordítást.");
            string modelNote = string.Equals(usedModel, settings.Model, StringComparison.OrdinalIgnoreCase)
                ? usedModel : $"{usedModel} (automatikus váltás {settings.Model} modellről)";
            return new(true, true, settings.Model, usedModel,
                $"A kulcs érvényes, a fordítás működik. Használt modell: {modelNote}.");
        }
        catch (Exception ex) when (Diagnose(ex).State is GeminiKeyState.ServerBusy or GeminiKeyState.ServerError or GeminiKeyState.Timeout)
        {
            return new(true, false, settings.Model, validatedModel,
                "A kulcs és a modell-hozzáférés érvényes. A Google generálási szolgáltatása pillanatnyilag nem válaszolt; ez nem kulcshiba. Fordításkor a program tovább vár és dinamikusan modellt vált.");
        }
    }

    private async Task<string> ValidateKeyAndModelAsync(GeminiApiKeyEntry key, string model,
        CancellationToken cancellationToken)
    {
        string escaped = Uri.EscapeDataString(model);
        using HttpResponseMessage response = await SendGetWithRetryAsync(
            $"https://generativelanguage.googleapis.com/v1beta/models/{escaped}", key, model, cancellationToken);
        string responseText = await ReadLimitedAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, responseText);
        using JsonDocument document = JsonDocument.Parse(responseText);
        if (!document.RootElement.TryGetProperty("supportedGenerationMethods", out JsonElement methods) ||
            methods.ValueKind != JsonValueKind.Array ||
            !methods.EnumerateArray().Any(method => string.Equals(method.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"A(z) {model} modell nem támogatja a generateContent fordítási végpontot.");
        return model;
    }

    public Task<TranslationRunResult> TranslateProjectAsync(
        ProjectDocument project,
        GeminiSettings settings,
        string apiKey,
        bool overwriteCompleted,
        IProgress<TranslationProgress>? progress = null,
        CancellationToken cancellationToken = default) => TranslateProjectAsync(project, settings,
            [new GeminiApiKeyEntry { Name = "Gemini kulcs", Secret = apiKey, Enabled = true, IsActive = true }],
            overwriteCompleted, progress, cancellationToken);

    public async Task<TranslationRunResult> TranslateProjectAsync(
        ProjectDocument project,
        GeminiSettings settings,
        IReadOnlyList<GeminiApiKeyEntry> apiKeys,
        bool overwriteCompleted,
        IProgress<TranslationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        GeminiApiKeyEntry[] keys = apiKeys.Where(key => key.Enabled && !string.IsNullOrWhiteSpace(key.Secret))
            .Select(key => key.Clone()).ToArray();
        if (keys.Length == 0) throw new InvalidOperationException("Nincs engedélyezett Gemini API-kulcs.");
        foreach (IGrouping<string, GeminiApiKeyEntry> group in keys.GroupBy(key => key.QuotaGroup, StringComparer.OrdinalIgnoreCase))
        {
            int safestFlashRpm = group.Min(key => Math.Clamp(key.RequestsPerMinute, 1, 10_000));
            int safestProRpm = group.Min(key => Math.Clamp(key.ProRequestsPerMinute, 1, 10_000));
            int safestFlashTpm = group.Min(key => Math.Clamp(key.TokensPerMinute, 1, 2_000_000_000));
            int safestFlashRpd = group.Min(key => Math.Clamp(key.RequestsPerDay, 1, 10_000_000));
            int safestProTpm = group.Min(key => Math.Clamp(key.ProTokensPerMinute, 1, 2_000_000_000));
            int safestProRpd = group.Min(key => Math.Clamp(key.ProRequestsPerDay, 1, 10_000_000));
            foreach (GeminiApiKeyEntry key in group)
            {
                key.RequestsPerMinute = safestFlashRpm;
                key.TokensPerMinute = safestFlashTpm;
                key.RequestsPerDay = safestFlashRpd;
                key.ProRequestsPerMinute = safestProRpm;
                key.ProTokensPerMinute = safestProTpm;
                key.ProRequestsPerDay = safestProRpd;
            }
        }
        ValidateConfiguration(keys[0].Secret, settings);
        BuiltInTokenProfiles.EnsureApplied(project, _protection);
        TranslationUnit[] targets = project.Units.Where(unit => unit.Active && unit.Status != UnitStatus.Skipped &&
            (overwriteCompleted || string.IsNullOrWhiteSpace(unit.TranslationMasked) || unit.Status is UnitStatus.Pending or UnitStatus.NeedsReview))
            .OrderBy(unit => unit.Order).ToArray();
        if (targets.Length == 0) return new TranslationRunResult(0, 0, 0, []);

        int applied = 0, rejected = 0, failed = 0, completed = 0, nextKeyIndex = 0, keySwitches = 0;
        bool apiUnavailable = false;
        string lastKeyName = keys[0].Name;
        string lastModel = settings.Model;
        var errors = new List<string>();
        var keyUsage = keys.ToDictionary(key => key.Id, _ => 0, StringComparer.Ordinal);
        var keyMessages = new Dictionary<string, string>(StringComparer.Ordinal);
        var disabledForRun = new HashSet<string>(StringComparer.Ordinal);
        int batchSize = Math.Clamp(settings.BatchSize, 1, 100);
        foreach (TranslationUnit[] batch in targets.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (TranslationUnit unit in batch) unit.Status = UnitStatus.Waiting;
            progress?.Report(new TranslationProgress(completed, targets.Length,
                $"Gemini fordítás · {keys[nextKeyIndex].Name}… {completed:N0}/{targets.Length:N0}"));
            try
            {
                RequestRow[] rows = batch.Select(unit => new RequestRow(unit.Id, unit.SourceDisplay, unit.ContextDisplay)).ToArray();
                string instruction = string.Join("\n", new[]
                {
                    project.TranslationInstruction,
                    settings.StyleGuide,
                    string.IsNullOrWhiteSpace(settings.Glossary) ? string.Empty : "Kötelező terminológia:\n" + settings.Glossary.Trim()
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                (IReadOnlyDictionary<string, string> translations, int usedKeyIndex, string usedModel) = await TranslateWithKeyFailoverAsync(
                    rows, project.SourceLanguage, project.TargetLanguage, instruction, settings, keys, nextKeyIndex,
                    disabledForRun, keyMessages, progress, completed, targets.Length, cancellationToken);
                if (usedKeyIndex != nextKeyIndex) keySwitches++;
                GeminiApiKeyEntry usedKey = keys[usedKeyIndex];
                usedKey.RuntimeState = GeminiKeyState.Ready;
                usedKey.RuntimeMessage = "A legutóbbi fordítási kérés sikeres volt.";
                usedKey.LastCheckedUtc = DateTime.UtcNow;
                keyUsage[usedKey.Id]++;
                lastKeyName = usedKey.Name;
                lastModel = usedModel;
                nextKeyIndex = (usedKeyIndex + 1) % keys.Length;

                foreach (TranslationUnit unit in batch)
                {
                    if (!translations.TryGetValue(unit.Id, out string? translated) || string.IsNullOrWhiteSpace(translated))
                    {
                        unit.Status = UnitStatus.NeedsReview;
                        failed++;
                        errors.Add($"{unit.Id}: hiányzó Gemini-válasz.");
                        continue;
                    }
                    translated = TextStructureGuard.Preserve(unit.SourceDisplay, translated.Trim());
                    bool markersValid = _protection.ValidateMarkers(unit.SourceDisplay, translated, out string markerError);
                    bool structureValid = TextStructureGuard.Validate(unit.SourceDisplay, translated, out string structureError);
                    if (!markersValid || !structureValid)
                    {
                        unit.Status = UnitStatus.NeedsReview;
                        rejected++;
                        errors.Add($"{unit.Id}: {(!markersValid ? markerError : structureError)}");
                        continue;
                    }
                    unit.PreviousTranslationMasked = unit.TranslationMasked;
                    unit.LastAiTranslationMasked = translated;
                    unit.TranslationMasked = translated;
                    unit.TranslationOrigin = $"Gemini API · {usedModel} · {lastKeyName}";
                    unit.Status = UnitStatus.Completed;
                    applied++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                foreach (TranslationUnit unit in batch.Where(unit => unit.Status == UnitStatus.Waiting)) unit.Status = UnitStatus.Pending;
                throw;
            }
            catch (ApiKeyRingExhaustedException ex)
            {
                foreach (TranslationUnit unit in batch.Where(unit => unit.Status == UnitStatus.Waiting)) unit.Status = UnitStatus.Pending;
                apiUnavailable = true;
                errors.Add(ex.Message);
                break;
            }
            catch (GeminiApiException ex)
            {
                foreach (TranslationUnit unit in batch.Where(unit => unit.Status == UnitStatus.Waiting)) unit.Status = UnitStatus.Pending;
                apiUnavailable = true;
                errors.Add(Diagnose(ex).UserMessage);
                break;
            }
            catch (Exception ex)
            {
                foreach (TranslationUnit unit in batch)
                {
                    unit.Status = UnitStatus.NeedsReview;
                    failed++;
                }
                errors.Add($"Köteg {completed / batchSize + 1}: {ex.Message}");
            }
            completed += batch.Length;
            progress?.Report(new TranslationProgress(completed, targets.Length,
                $"Gemini fordítás… {completed:N0}/{targets.Length:N0}"));
        }
        return new TranslationRunResult(applied, rejected, failed, errors)
        {
            ApiUnavailable = apiUnavailable,
            LastKeyName = lastKeyName,
            LastModel = lastModel,
            KeySwitches = keySwitches,
            KeyUsage = keyUsage,
            KeyStates = keys.ToDictionary(key => key.Id, key => key.RuntimeState, StringComparer.Ordinal),
            KeyMessages = keys.ToDictionary(key => key.Id,
                key => keyMessages.GetValueOrDefault(key.Id, key.RuntimeMessage), StringComparer.Ordinal)
        };
    }

    private async Task<(IReadOnlyDictionary<string, string> Translations, int KeyIndex, string UsedModel)> TranslateWithKeyFailoverAsync(
        IReadOnlyList<RequestRow> rows, string sourceLanguage, string targetLanguage, string instruction,
        GeminiSettings settings, IReadOnlyList<GeminiApiKeyEntry> keys, int startIndex,
        ISet<string> disabledForRun, IDictionary<string, string> keyMessages,
        IProgress<TranslationProgress>? progress, int completed, int total, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        for (int offset = 0; offset < keys.Count; offset++)
        {
            int index = (startIndex + offset) % keys.Count;
            GeminiApiKeyEntry key = keys[index];
            if (disabledForRun.Contains(key.Id)) continue;
            if (offset > 0)
                progress?.Report(new TranslationProgress(completed, total,
                    $"API-kulcsváltás · {key.Name}… {completed:N0}/{total:N0}"));
            try
            {
                (IReadOnlyDictionary<string, string> result, string usedModel) = await TranslateForKeyWithModelFallbackAsync(
                    rows, sourceLanguage, targetLanguage, instruction, settings, key, keys.Count > 1,
                    progress, completed, total, cancellationToken);
                return (result, index, usedModel);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ShouldTryAnotherKey(ex))
            {
                GeminiFailureInfo diagnosis = Diagnose(ex);
                string safe = Redact(diagnosis.UserMessage, keys);
                bool serviceOrQuotaFailure = diagnosis.State is GeminiKeyState.RateLimited or GeminiKeyState.ServerBusy or
                    GeminiKeyState.ServerError or GeminiKeyState.Timeout;
                key.RuntimeState = serviceOrQuotaFailure ? GeminiKeyState.Ready : diagnosis.State;
                key.RuntimeMessage = safe;
                key.LastCheckedUtc = DateTime.UtcNow;
                keyMessages[key.Id] = safe;
                if (diagnosis.DisableForRun) disabledForRun.Add(key.Id);
                failures.Add($"{key.Name}: {safe}");
            }
        }
        throw new ApiKeyRingExhaustedException("Minden engedélyezett Gemini API-kulcs sikertelen. " + string.Join(" | ", failures));
    }

    private async Task<(IReadOnlyDictionary<string, string> Translations, string UsedModel)> TranslateForKeyWithModelFallbackAsync(
        IReadOnlyList<RequestRow> rows, string sourceLanguage, string targetLanguage, string instruction,
        GeminiSettings settings, GeminiApiKeyEntry key, bool quickKeyFailover,
        IProgress<TranslationProgress>? progress, int completed, int total, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> candidates = GeminiModelRouting.Candidates(settings.Model, key.UsageTier);
        Exception? lastFailure = null;
        for (int modelIndex = 0; modelIndex < candidates.Count; modelIndex++)
        {
            string model = candidates[modelIndex];
            GeminiSettings routed = CopySettingsWithModel(settings, model);
            try
            {
                IReadOnlyDictionary<string, string> translations = await TranslateWithMissingRetryAsync(rows,
                    sourceLanguage, targetLanguage, instruction, routed, key, quickKeyFailover,
                    modelIndex + 1 < candidates.Count, progress, completed, total, cancellationToken);
                return (translations, model);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (modelIndex + 1 < candidates.Count && ShouldTryAnotherModel(ex))
            {
                lastFailure = ex;
                progress?.Report(new TranslationProgress(completed, total,
                    $"Dinamikus modellváltás · {model} → {candidates[modelIndex + 1]} · {completed:N0}/{total:N0}"));
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                break;
            }
        }
        throw lastFailure ?? new InvalidOperationException("Nincs használható Gemini fordítási modell.");
    }

    private static GeminiSettings CopySettingsWithModel(GeminiSettings settings, string model) => new()
    {
        Model = model,
        BatchSize = settings.BatchSize,
        Temperature = settings.Temperature,
        ThinkingLevel = settings.ThinkingLevel,
        StyleGuide = settings.StyleGuide,
        Glossary = settings.Glossary,
        RememberApiKey = settings.RememberApiKey
    };

    private async Task<IReadOnlyDictionary<string, string>> TranslateWithMissingRetryAsync(
        IReadOnlyList<RequestRow> rows, string sourceLanguage, string targetLanguage, string instruction,
        GeminiSettings settings, GeminiApiKeyEntry key, bool quickKeyFailover, bool quickModelFailover,
        IProgress<TranslationProgress>? progress, int completed, int total, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(await TranslateBatchAsync(rows, sourceLanguage, targetLanguage,
            instruction, settings, key, quickKeyFailover, quickModelFailover, progress, completed, total, cancellationToken), StringComparer.Ordinal);
        foreach (RequestRow row in rows.Where(row => !result.ContainsKey(row.Id)))
        {
            IReadOnlyDictionary<string, string> retry = await TranslateBatchAsync([row], sourceLanguage, targetLanguage,
                instruction, settings, key, quickKeyFailover, quickModelFailover, progress, completed, total, cancellationToken);
            if (retry.TryGetValue(row.Id, out string? translation)) result[row.Id] = translation;
        }
        return result;
    }

    private async Task<IReadOnlyDictionary<string, string>> TranslateBatchAsync(
        IReadOnlyList<RequestRow> rows, string sourceLanguage, string targetLanguage, string instruction,
        GeminiSettings settings, GeminiApiKeyEntry key, bool quickKeyFailover, bool quickModelFailover,
        IProgress<TranslationProgress>? progress, int completed, int total, CancellationToken cancellationToken)
    {
        string[] ids = rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).ToArray();
        object thinkingConfig = new { thinkingLevel = settings.ThinkingLevel };
        object requestBody = new
        {
            systemInstruction = new { parts = new[] { new { text = SystemInstruction(sourceLanguage, targetLanguage, instruction) } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = JsonSerializer.Serialize(rows, _json) } } } },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseJsonSchema = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            id = new { type = "string", @enum = ids },
                            translation = new { type = "string" }
                        },
                        required = new[] { "id", "translation" },
                        additionalProperties = false
                    }
                },
                thinkingConfig
            }
        };
        string model = Uri.EscapeDataString(settings.Model.Trim());
        using HttpResponseMessage response = await SendWithRetryAsync(
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent",
            requestBody, key, settings.Model, quickKeyFailover, quickModelFailover,
            progress, completed, total, cancellationToken);
        string responseText = await ReadLimitedAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, responseText);
        using JsonDocument document = JsonDocument.Parse(responseText, new JsonDocumentOptions { MaxDepth = 64 });
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("promptFeedback", out JsonElement feedback) &&
            feedback.TryGetProperty("blockReason", out JsonElement block) && block.ValueKind == JsonValueKind.String)
            throw new InvalidDataException("A Gemini biztonsági szűrője blokkolta a kérést: " + block.GetString());
        if (!root.TryGetProperty("candidates", out JsonElement candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() != 1)
            throw new InvalidDataException("A Gemini-válasz nem pontosan egy jelöltet tartalmaz.");
        JsonElement candidate = candidates[0];
        string finishReason = candidate.TryGetProperty("finishReason", out JsonElement finish) ? finish.GetString() ?? string.Empty : string.Empty;
        if (!string.Equals(finishReason, "STOP", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A Gemini nem fejezte be szabályosan a választ: " + (finishReason.Length == 0 ? "ismeretlen ok" : finishReason));
        if (!candidate.TryGetProperty("content", out JsonElement content) || !content.TryGetProperty("parts", out JsonElement parts))
            throw new InvalidDataException("A Gemini-válasz tartalma hiányzik.");
        string payload = string.Concat(parts.EnumerateArray()
            .Where(part => !part.TryGetProperty("thought", out JsonElement thought) || thought.ValueKind != JsonValueKind.True)
            .Select(part => part.TryGetProperty("text", out JsonElement text) ? text.GetString() : null));
        TranslationItem[] items = JsonSerializer.Deserialize<TranslationItem[]>(payload, _json)
            ?? throw new InvalidDataException("A Gemini üres JSON-választ adott.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowed = ids.ToHashSet(StringComparer.Ordinal);
        foreach (TranslationItem item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || !allowed.Contains(item.Id))
                throw new InvalidDataException("A Gemini ismeretlen sorazonosítót adott vissza.");
            if (!result.TryAdd(item.Id, item.Translation ?? string.Empty))
                throw new InvalidDataException("A Gemini ugyanazt a sorazonosítót többször adta vissza.");
        }
        return result;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, object body, GeminiApiKeyEntry key,
        string model, bool quickKeyFailover, bool quickModelFailover,
        IProgress<TranslationProgress>? progress, int completed, int total,
        CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, _json);
        int estimatedInputTokens = Math.Max(1, (int)Math.Ceiling(json.Length / 4d));
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await _pacer.WaitAsync(key, model, estimatedInputTokens, (delay, group) => progress?.Report(new TranslationProgress(completed, total,
                    $"Kvótavédelem · {key.Name} / {group} · várakozás {Math.Ceiling(delay.TotalSeconds):N0} mp…")), cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("x-goog-api-key", key.Secret.Trim());
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new ByteArrayContent(json);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                _pacer.ReportResponse(key, model, response.StatusCode, GetRetryAfter(response));
                if (attempt >= 4 || !IsTransient(response.StatusCode) ||
                    (quickKeyFailover && response.StatusCode == HttpStatusCode.TooManyRequests) ||
                    (quickModelFailover && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)))
                    return response;
                TimeSpan delay = RetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (attempt < 4 && ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(JitterDelay(attempt), cancellationToken);
            }
        }
    }

    private async Task<HttpResponseMessage> SendGetWithRetryAsync(string url, GeminiApiKeyEntry key, string model,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await _pacer.WaitAsync(key, model, cancellationToken: cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("x-goog-api-key", key.Secret.Trim());
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                _pacer.ReportResponse(key, model, response.StatusCode, GetRetryAfter(response));
                if (attempt >= 4 || !IsTransient(response.StatusCode)) return response;
                TimeSpan delay = RetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (attempt < 4 && ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(JitterDelay(attempt), cancellationToken);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero) return delta;
        if (response.Headers.RetryAfter?.Date is DateTimeOffset date && date > DateTimeOffset.UtcNow)
            return date - DateTimeOffset.UtcNow;
        return null;
    }
    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (GetRetryAfter(response) is TimeSpan specified)
            return specified > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : specified;
        return JitterDelay(attempt);
    }
    private static TimeSpan JitterDelay(int attempt)
    {
        int ceiling = Math.Min(60_000, 1000 * (1 << Math.Min(attempt, 6)));
        return TimeSpan.FromMilliseconds(Random.Shared.Next(Math.Max(1, ceiling / 2), ceiling + 1));
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("A Gemini-válasz túllépte a 16 MiB biztonsági korlátot.");
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[81920];
        int total = 0, read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaximumResponseBytes) throw new InvalidDataException("A Gemini-válasz túllépte a 16 MiB biztonsági korlátot.");
            memory.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
    }

    private static Exception CreateApiException(HttpStatusCode status, string response)
    {
        string message = "Gemini API-hiba";
        string apiStatus = string.Empty;
        string reason = string.Empty;
        try
        {
            using JsonDocument json = JsonDocument.Parse(response);
            if (json.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.TryGetProperty("message", out JsonElement detail)) message = detail.GetString() ?? message;
                if (error.TryGetProperty("status", out JsonElement statusElement)) apiStatus = statusElement.GetString() ?? string.Empty;
                if (error.TryGetProperty("details", out JsonElement details) && details.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement item in details.EnumerateArray())
                        if (item.TryGetProperty("reason", out JsonElement reasonElement))
                        { reason = reasonElement.GetString() ?? string.Empty; break; }
            }
        }
        catch (JsonException) { }
        return new GeminiApiException($"{message} (HTTP {(int)status}).", status, apiStatus, reason);
    }

    public static GeminiFailureInfo Diagnose(Exception exception)
    {
        string raw = exception.Message;
        string lower = raw.ToLowerInvariant();
        HttpStatusCode? status = exception is HttpRequestException http ? http.StatusCode : null;
        string reason = exception is GeminiApiException api ? api.Reason : string.Empty;
        if (exception is GeminiLocalQuotaException)
            return new(GeminiKeyState.RateLimited,
                raw + " A limitet az AI Studio aktív Rate limits értékével egyezően állítsd be.", true, false);
        if (lower.Contains("reported as leaked") || lower.Contains("api key was reported as leaked") ||
            lower.Contains("kiszivárg") || lower.Contains("leaked key"))
            return new(GeminiKeyState.LeakedKey,
                "A Google kiszivárgottként blokkolta a kulcsot. Töröld, és készíts újat az AI Studioban. " + raw, false, true);
        if (reason.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase) || lower.Contains("api key not valid") ||
            status == HttpStatusCode.Unauthorized)
            return new(GeminiKeyState.InvalidKey,
                "A kulcs érvénytelen vagy hiányos. Ellenőrizd, majd hozz létre új kulcsot, ha szükséges. " + raw, false, true);
        if (lower.Contains("prepayment credits depleted") || lower.Contains("no credits") ||
            lower.Contains("billing account") || lower.Contains("enable billing") ||
            lower.Contains("free tier is not available") || lower.Contains("failed_precondition"))
            return new(GeminiKeyState.BillingRequired,
                "A projekthez számlázás vagy pozitív Prepay-egyenleg szükséges. Ellenőrizd az AI Studio Billing oldalt. " + raw, false, true);
        if (status == HttpStatusCode.Forbidden)
            return new(GeminiKeyState.PermissionDenied,
                "A kulcsnak nincs joga ehhez a modellhez vagy projekthez. Ellenőrizd a kulcskorlátozást és a projekt-hozzáférést. " + raw, false, true);
        if (status == HttpStatusCode.NotFound)
            return new(GeminiKeyState.ModelUnavailable,
                "A kiválasztott modell nem érhető el ezen a végponton vagy projektben. Válassz másik modellt. " + raw, false, false);
        if (status == HttpStatusCode.TooManyRequests)
            return new(GeminiKeyState.RateLimited,
                "RPM, TPM, RPD vagy költési kvóta fogyott el. Ez nem kulcshiba: az érintett projekt+modell várakozik, fizetős Tieren pedig a program dinamikusan a másik engedélyezett modellre vált. " + raw, true, false);
        if (status == HttpStatusCode.ServiceUnavailable || lower.Contains("high demand"))
            return new(GeminiKeyState.ServerBusy,
                "A Google modellkapacitása átmenetileg nem elérhető; a kulcs ettől még érvényes. Exponenciális visszavárás, majd fizetős Tieren 3.7 Flash ↔ 3.1 Pro váltás következik. " + raw, true, false);
        if (status == HttpStatusCode.InternalServerError || (int)(status ?? 0) is >= 500 and < 503)
            return new(GeminiKeyState.ServerError,
                "Átmeneti Gemini szerverhiba. A program késleltetve újrapróbálja. " + raw, true, false);
        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout || exception is TaskCanceledException)
            return new(GeminiKeyState.Timeout,
                "A kérés időtúllépéssel állt le. A program késleltetve másik kulcsot is próbálhat. " + raw, true, false);
        if (exception is HttpRequestException)
            return new(GeminiKeyState.NetworkError,
                "Hálózati vagy TLS-kapcsolati hiba. Ellenőrizd az internetkapcsolatot, proxyt és tűzfalat. " + raw, true, false);
        return new(GeminiKeyState.Error, raw, false, false);
    }

    private static bool ShouldTryAnotherKey(Exception exception)
    {
        GeminiFailureInfo diagnosis = Diagnose(exception);
        if (diagnosis.Retryable || diagnosis.DisableForRun) return true;
        if (exception is GeminiApiException api)
        {
            return api.StatusCode == HttpStatusCode.BadRequest &&
                (api.Reason.Contains("API_KEY", StringComparison.OrdinalIgnoreCase) ||
                 api.Message.Contains("API key", StringComparison.OrdinalIgnoreCase) ||
                 api.Message.Contains("leaked", StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    private static bool ShouldTryAnotherModel(Exception exception)
    {
        if (exception is GeminiLocalQuotaException) return true;
        HttpStatusCode? status = exception is HttpRequestException http ? http.StatusCode : null;
        if (status is HttpStatusCode.NotFound or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout) return true;
        GeminiFailureInfo diagnosis = Diagnose(exception);
        return diagnosis.State is GeminiKeyState.ModelUnavailable or GeminiKeyState.ServerBusy or
            GeminiKeyState.ServerError or GeminiKeyState.Timeout;
    }

    private static string Redact(string message, IReadOnlyList<GeminiApiKeyEntry> keys)
    {
        string safe = message;
        foreach (string secret in keys.Select(key => key.Secret).Where(secret => !string.IsNullOrWhiteSpace(secret)))
            safe = safe.Replace(secret, "[API-KULCS]", StringComparison.Ordinal);
        return safe;
    }

    private static string SystemInstruction(string sourceLanguage, string targetLanguage, string instruction) => $"""
        Professzionális játéklokalizációs fordító vagy. Fordíts {sourceLanguage} nyelvről {targetLanguage} nyelvre.
        {instruction}
        A bemenet JSON-tömb. Minden elemhez pontosan ugyanazt az id értéket add vissza.
        Csak a translation mezőben fordíts. A ⟦NAGYBETŰS_00⟧ tokenmaszkokat, sortöréseket és technikai szerkezetet karakterpontosan őrizd meg.
        Ne adj magyarázatot, megjegyzést vagy Markdownot.
        """;

    private static void ValidateConfiguration(string apiKey, GeminiSettings settings)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Add meg a Gemini API-kulcsot a beállításokban.");
        if (string.IsNullOrWhiteSpace(settings.Model)) throw new InvalidOperationException("A Gemini modell neve nem lehet üres.");
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
    private sealed class ApiKeyRingExhaustedException(string message) : Exception(message);
    private sealed class GeminiApiException(string message, HttpStatusCode statusCode, string apiStatus, string reason)
        : HttpRequestException(message, null, statusCode)
    {
        public string ApiStatus { get; } = apiStatus;
        public string Reason { get; } = reason;
    }
    private sealed record RequestRow(string Id, string Source, string Context);
    private sealed class TranslationItem { public string Id { get; set; } = string.Empty; public string Translation { get; set; } = string.Empty; }
}
