using System.Net;
using System.Text;
using LocaUltrator.Core;

int passed = 0;
await Test("Beépített tokenprofil és visszaállítás", () =>
{
    var protection = new TokenProtectionService();
    TokenProfile profile = BuiltInTokenProfiles.CreateSafeDefault();
    MaskResult masked = protection.Mask("Save {name} <b>%s</b>\\n", profile);
    Assert(masked.Tokens.Count == 5, "Nem minden technikai token lett védve.");
    Assert(protection.Restore(masked.Text, masked.Tokens) == "Save {name} <b>%s</b>\\n", "A tokenek nem álltak vissza veszteségmentesen.");
    return Task.CompletedTask;
});

await Test("Gemini kérés: fejléc, strukturált JSON és tokenkapu", async () =>
{
    var handler = new FakeGeminiHandler();
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    using var service = new GeminiTranslationService(http);
    var project = new ProjectDocument
    {
        Units = [new TranslationUnit { Id = "row-1", SourceText = "Save {name}", Active = true, Order = 1 }]
    };
    TranslationRunResult result = await service.TranslateProjectAsync(project, new GeminiSettings(), "secret-test-key", false);
    Assert(result.Applied == 1 && result.Rejected == 0 && result.Failed == 0,
        "A validált fordítás nem lett alkalmazva: " + string.Join(" | ", result.Errors));
    Assert(project.Units[0].TranslationMasked == "Mentés ⟦VARIABLE_01⟧", "A fordítás vagy a tokenmaszk eltér.");
    Assert(handler.HeaderWasSafe, "Az API-kulcs nem kizárólag x-goog-api-key fejlécben szerepelt.");
    Assert(handler.StructuredSchemaRequested, "A strukturált JSON-séma hiányzott a kérésből.");
    Assert(handler.UnsupportedSamplingParametersOmitted,
        "A Gemini 3.7 kérés tiltott vagy elavult mintavételi paramétert tartalmaz.");
});

await Test("API-kulcs nincs a menthető beállításmodellben", () =>
{
    Assert(typeof(GeminiSettings).GetProperties().All(property => !property.Name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase)),
        "A GeminiSettings API-kulcsot tárolna.");
    return Task.CompletedTask;
});

await Test("Magas gondolkodási alapérték és aktív Free kulcsprofil", () =>
{
    var settings = new GeminiSettings();
    var key = new GeminiApiKeyEntry();
    Assert(settings.ThinkingLevel == "high", "A gondolkodási szint alapértéke nem high.");
    Assert(key.Enabled && key.IsActive && key.UsageTier == GeminiUsageTier.Free && key.RequestsPerMinute == 5,
        "Az új kulcs nem aktív, biztonságos Free profillal indul.");
    return Task.CompletedTask;
});

await Test("Kézzel szerkeszthető külön kulcsfájl és előfizetési státusz megőrzése", async () =>
{
    string folder = Path.Combine(Path.GetTempPath(), "LocaUltrator-keyfile-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        string path = Path.Combine(folder, "gemini-kulcsok.json");
        var store = new GeminiKeyFileStore(path);
        await store.SaveAsync([new GeminiApiKeyEntry
        {
            Name = "Kézi kulcs", Secret = "manual-secret", Enabled = true,
            UsageTier = GeminiUsageTier.Tier2, RequestsPerMinute = 30, QuotaGroup = "sajat-projekt"
        }]);
        string editableJson = await File.ReadAllTextAsync(path);
        Assert(editableJson.Contains("manual-secret", StringComparison.Ordinal) &&
               editableJson.Contains("\"usageTier\": \"Tier2\"", StringComparison.Ordinal),
            "A külön kulcsfájl nem kézzel szerkeszthető formában készült el.");

        editableJson = editableJson.Replace("\"usageTier\": \"Tier2\"", "\"usageTier\": \"Tier3\"", StringComparison.Ordinal)
            .Replace("\"requestsPerMinute\": 30", "\"requestsPerMinute\": 60", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, editableJson);
        IReadOnlyList<GeminiApiKeyEntry> loaded = await store.LoadAsync();
        Assert(loaded.Single().UsageTier == GeminiUsageTier.Tier3 && loaded.Single().RequestsPerMinute == 60,
            "A kézzel átírt előfizetési státusz vagy RPM nem töltődött vissza.");
        await store.SaveAsync(loaded);
        Assert(File.Exists(store.BackupPath), "A kulcsfájl előző példányáról nem készült .bak mentés.");
    }
    finally { Directory.Delete(folder, true); }
});

await Test("Projekt-specifikus prompt, AI-JSON ellenőrzés és aktiválás", () =>
{
    var protection = new TokenProtectionService();
    var project = new ProjectDocument
    {
        Name = "Tokenpróba",
        Units = [new TranslationUnit { Id = "row-token", SourceText = "Welcome {player_name}!", Active = true, Order = 1 }]
    };
    string prompt = protection.BuildFilledPrompt(project);
    Assert(prompt.Contains("{player_name}", StringComparison.Ordinal), "A tényleges projekttoken hiányzik a kitöltött promptból.");
    Assert(!prompt.Contains("{{TOKEN_CANDIDATES_JSON}}", StringComparison.Ordinal), "A prompt egyik helyőrzője kitöltetlen maradt.");

    const string response = """
        {"schemaVersion":1,"engine":"dotnet-regex","profileName":"Tokenpróba profil","rules":[{"id":"brace-variable","name":"Kapcsos változó","category":"placeholder","pattern":"\u005C{[A-Za-z_][A-Za-z0-9_.:-]{0,63}\u005C}","priority":200,"caseSensitive":true,"examples":["{player_name}"]}],"literals":[],"warnings":[]}
        """;
    TokenProfile profile = protection.ParseProfile(response);
    TokenProfileValidation validation = protection.ValidateProfile(profile, project.Units);
    Assert(validation.Success && validation.MatchCount == 1,
        "A valódi projekthez illeszkedő AI-tokenprofil ellenőrzése sikertelen: " + string.Join(" | ", validation.Errors));
    BuiltInTokenProfiles.Apply(project, profile, protection);
    Assert(project.Units[0].MaskedSource == "Welcome ⟦VARIABLE_01⟧!", "Az ellenőrzött projektprofil nem maszkolta a forrást.");
    Assert(ReferenceEquals(project.ActiveTokenProfile, profile), "Az ellenőrzött projektprofil nem lett aktív.");
    return Task.CompletedTask;
});

await Test("Többkulcsos API-váltás 429 után", async () =>
{
    var handler = new RotatingGeminiHandler();
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    using var service = new GeminiTranslationService(http, requestPacer: new GeminiRequestPacer(TimeSpan.Zero));
    var project = new ProjectDocument
    {
        Units = [new TranslationUnit { Id = "row-rotate", SourceText = "Exit", Active = true, Order = 1 }]
    };
    GeminiApiKeyEntry[] keys =
    [
        new() { Name = "Elsődleges", Secret = "first-key", Enabled = true, IsActive = true, QuotaGroup = "projekt-1", RequestsPerMinute = 10_000 },
        new() { Name = "Tartalék", Secret = "second-key", Enabled = true, IsActive = true, QuotaGroup = "projekt-2", RequestsPerMinute = 10_000 }
    ];
    TranslationRunResult result = await service.TranslateProjectAsync(project, new GeminiSettings(), keys, false);
    Assert(result.Applied == 1 && result.KeySwitches == 1 && result.LastKeyName == "Tartalék",
        "A program nem váltott át a tartalék kulcsra.");
    Assert(handler.FirstKeyCalls == 1 && handler.SecondKeyCalls == 1,
        "A kulcskísérletek sorrendje vagy száma hibás.");
    Assert(project.Units[0].TranslationOrigin.EndsWith("Tartalék", StringComparison.Ordinal),
        "A használt kulcs neve nem került a fordítás eredetébe.");
});

await Test("Minden engedélyezett kulcs soros körforgásban dolgozik", async () =>
{
    var handler = new RoundRobinGeminiHandler();
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    using var service = new GeminiTranslationService(http, requestPacer: new GeminiRequestPacer(TimeSpan.Zero));
    var project = new ProjectDocument
    {
        Units =
        [
            new TranslationUnit { Id = "row-a", SourceText = "One", Active = true, Order = 1 },
            new TranslationUnit { Id = "row-b", SourceText = "Two", Active = true, Order = 2 },
            new TranslationUnit { Id = "row-c", SourceText = "Three", Active = true, Order = 3 }
        ]
    };
    GeminiApiKeyEntry[] keys = Enumerable.Range(1, 3).Select(index => new GeminiApiKeyEntry
    {
        Name = "Kulcs " + index, Secret = "round-key-" + index, Enabled = true, IsActive = true,
        QuotaGroup = "projekt-" + index, RequestsPerMinute = 10_000
    }).ToArray();
    TranslationRunResult result = await service.TranslateProjectAsync(project, new GeminiSettings { BatchSize = 1 }, keys, false);
    Assert(result.Applied == 3, "Nem készült el mindhárom köteg.");
    Assert(handler.Keys.SequenceEqual(new[] { "round-key-1", "round-key-2", "round-key-3" }),
        "A kötegek nem az összes kulcson, sorrendben mentek ki.");
    Assert(result.KeyUsage.Values.Count(value => value == 1) == 3, "A kulcshasználati számláló nem körforgást mutat.");
});

await Test("Free és fizetős Tier csak a kért modelleket használja", () =>
{
    Assert(GeminiModelRouting.Candidates(GeminiModelRouting.Pro31, GeminiUsageTier.Free)
        .SequenceEqual(new[] { GeminiModelRouting.Flash37 }),
        "A Free Tier megpróbálná a fizetős 3.1 Pro modellt.");
    Assert(GeminiModelRouting.Candidates(GeminiModelRouting.Flash37, GeminiUsageTier.Tier1)
        .SequenceEqual(new[] { GeminiModelRouting.Flash37, GeminiModelRouting.Pro31 }),
        "A fizetős Tier dinamikus modellsorrendje hibás.");
    return Task.CompletedTask;
});

await Test("503 esetén fizetős Tieren dinamikus 3.7 Flash → 3.1 Pro váltás történik", async () =>
{
    var handler = new DynamicModelGeminiHandler();
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    using var service = new GeminiTranslationService(http, requestPacer: new GeminiRequestPacer(TimeSpan.Zero));
    var project = new ProjectDocument
    {
        Units = [new TranslationUnit { Id = "row-dynamic", SourceText = "Options", Active = true, Order = 1 }]
    };
    var key = new GeminiApiKeyEntry
    {
        Secret = "model-key", UsageTier = GeminiUsageTier.Tier1,
        RequestsPerMinute = 10_000, ProRequestsPerMinute = 10_000, QuotaGroup = "model-project"
    };
    TranslationRunResult result = await service.TranslateProjectAsync(project,
        new GeminiSettings { Model = GeminiModelRouting.Flash37, ThinkingLevel = "high" }, [key], false);
    Assert(result.Applied == 1 && result.LastModel == GeminiModelRouting.Pro31,
        "A 503 után nem készült el a fordítás a 3.1 Pro tartalékmodellel.");
    Assert(handler.Models.SequenceEqual(new[] { GeminiModelRouting.Flash37, GeminiModelRouting.Pro31 }),
        "A dinamikus modellváltás sorrendje hibás.");
    Assert(project.Units[0].TranslationOrigin.Contains(GeminiModelRouting.Pro31, StringComparison.Ordinal),
        "A ténylegesen használt tartalékmodell nem került a fordítás eredetébe.");
});

await Test("A kulcsteszt külön igazolja a kulcsot és 503 után működő tartalékmodellre vált", async () =>
{
    var handler = new DynamicModelGeminiHandler();
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    using var service = new GeminiTranslationService(http, requestPacer: new GeminiRequestPacer(TimeSpan.Zero));
    var key = new GeminiApiKeyEntry
    {
        Secret = "validated-key", UsageTier = GeminiUsageTier.Tier2,
        RequestsPerMinute = 10_000, ProRequestsPerMinute = 10_000, QuotaGroup = "validated-project"
    };
    GeminiConnectionTestResult result = await service.TestConnectionDetailedAsync(key,
        new GeminiSettings { Model = GeminiModelRouting.Flash37 });
    Assert(result.KeyValidated && result.GenerationSucceeded && result.UsedModel == GeminiModelRouting.Pro31,
        "A terhelt elsődleges modell miatt a működő kulcs hibás állapotba kerülne.");
});

await Test("Ismert Gemini kulcs- és szolgáltatáshibák osztályozása", () =>
{
    GeminiFailureInfo invalid = GeminiTranslationService.Diagnose(
        new HttpRequestException("API key not valid", null, HttpStatusCode.BadRequest));
    GeminiFailureInfo billing = GeminiTranslationService.Diagnose(
        new HttpRequestException("Your prepayment credits depleted", null, HttpStatusCode.BadRequest));
    GeminiFailureInfo busy = GeminiTranslationService.Diagnose(
        new HttpRequestException("This model is currently experiencing high demand", null, HttpStatusCode.ServiceUnavailable));
    GeminiFailureInfo model = GeminiTranslationService.Diagnose(
        new HttpRequestException("Model not found", null, HttpStatusCode.NotFound));
    Assert(invalid.State == GeminiKeyState.InvalidKey && invalid.DisableForRun, "A hibás kulcs nincs letiltva a futásra.");
    Assert(billing.State == GeminiKeyState.BillingRequired && billing.DisableForRun, "A számlázási hiba nincs felismerve.");
    Assert(busy.State == GeminiKeyState.ServerBusy && busy.Retryable, "Az 503 túlterhelés nincs átmeneti hibának jelölve.");
    Assert(model.State == GeminiKeyState.ModelUnavailable && !model.Retryable, "A modellhiba feleslegesen lenne újrapróbálva.");
    return Task.CompletedTask;
});

await Test("Azonos projektcsoport kérései között RPM-várakozás van", async () =>
{
    var pacer = new GeminiRequestPacer(TimeSpan.Zero);
    var first = new GeminiApiKeyEntry { QuotaGroup = "közös-projekt", RequestsPerMinute = 600 };
    var second = new GeminiApiKeyEntry { QuotaGroup = "közös-projekt", RequestsPerMinute = 600 };
    await pacer.WaitAsync(first);
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    await pacer.WaitAsync(second);
    Assert(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(70),
        "Az azonos projektcsoport második kérése nem várta ki az RPM-időközt.");
});

await Test("Modellenként külön RPM/TPM/RPD kvótavédelem működik", async () =>
{
    var pacer = new GeminiRequestPacer(TimeSpan.Zero);
    var key = new GeminiApiKeyEntry
    {
        UsageTier = GeminiUsageTier.Tier1, QuotaGroup = "modell-kvóta",
        RequestsPerMinute = 10_000, TokensPerMinute = 10, RequestsPerDay = 1,
        ProRequestsPerMinute = 10_000, ProTokensPerMinute = 10, ProRequestsPerDay = 1
    };
    await pacer.WaitAsync(key, GeminiModelRouting.Flash37, 5);
    await pacer.WaitAsync(key, GeminiModelRouting.Pro31, 5);
    bool dailyBlocked = false;
    try { await pacer.WaitAsync(key, GeminiModelRouting.Flash37, 1); }
    catch (GeminiLocalQuotaException) { dailyBlocked = true; }
    Assert(dailyBlocked, "A modellenkénti RPD-védelem nem állította meg a második Flash-kérést.");

    var oversized = new GeminiApiKeyEntry { QuotaGroup = "tpm-próba", TokensPerMinute = 5, RequestsPerDay = 10 };
    bool tpmBlocked = false;
    try { await pacer.WaitAsync(oversized, GeminiModelRouting.Flash37, 6); }
    catch (GeminiLocalQuotaException) { tpmBlocked = true; }
    Assert(tpmBlocked, "A becsült bemenetet meghaladó TPM-korlát nem lett felismerve.");
});

await Test("429 Retry-After lehűti a teljes projektcsoportot", async () =>
{
    var pacer = new GeminiRequestPacer(TimeSpan.Zero);
    var key = new GeminiApiKeyEntry { QuotaGroup = "kvóta-projekt", RequestsPerMinute = 10_000 };
    await pacer.WaitAsync(key);
    pacer.ReportResponse(key, HttpStatusCode.TooManyRequests, TimeSpan.FromMilliseconds(80));
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    await pacer.WaitAsync(key);
    Assert(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(55),
        "A 429 Retry-After nem állította meg a projektcsoportot.");
});

await Test("Vágólapcsomag és munkamód azonnal visszatölthető", async () =>
{
    string folder = Path.Combine(Path.GetTempPath(), "LocaUltrator-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        string path = Path.Combine(folder, "project.locaultrator.json");
        var protection = new TokenProtectionService();
        var clipboard = new ClipboardTranslationService(protection);
        var store = new ProjectStore();
        var project = new ProjectDocument
        {
            ProjectFilePath = path,
            TranslationTransport = TranslationTransport.Clipboard,
            Units = [new TranslationUnit { Id = "row-clip", SourceText = "Save {name}", Active = true, Order = 1 }]
        };
        BuiltInTokenProfiles.EnsureApplied(project, protection);
        PendingBatch batch = clipboard.CreateBatch(project);
        project.PendingBatches.Add(batch);
        await store.SaveAsync(project);

        ProjectDocument waiting = await store.LoadAsync(path);
        Assert(waiting.TranslationTransport == TranslationTransport.Clipboard && waiting.PendingBatches.Count == 1,
            "A vágólapos munkamód vagy a várakozó csomag nem maradt meg mentés után.");
        PendingBatch savedBatch = waiting.PendingBatches.Single();
        string response = string.Join(Environment.NewLine,
            ClipboardTranslationService.GetStartMarker(savedBatch),
            ClipboardTranslationService.GetRowId(savedBatch, 1) + "\tMentés ⟦VARIABLE_01⟧",
            ClipboardTranslationService.GetEndMarker(savedBatch));
        BatchApplyResult applied = clipboard.ApplyResponse(waiting, response);
        Assert(applied.Applied == 1 && waiting.PendingBatches.Count == 0,
            "A mentett vágólapcsomag válasza nem lett feldolgozva.");
        await store.SaveAsync(waiting);
        ProjectDocument completed = await store.LoadAsync(path);
        Assert(completed.Units.Single().Status == UnitStatus.Completed &&
               completed.Units.Single().TranslationMasked == "Mentés ⟦VARIABLE_01⟧",
            "A feldolgozott vágólapválasz nem maradt meg az újratöltés után.");
    }
    finally { Directory.Delete(folder, true); }
});

Console.WriteLine($"Minden teszt sikeres: {passed}/16");

async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("OK  " + name); }
    catch (Exception ex) { Console.Error.WriteLine("HIBA  " + name + ": " + ex.Message); Environment.ExitCode = 1; }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeGeminiHandler : HttpMessageHandler
{
    public bool HeaderWasSafe { get; private set; }
    public bool StructuredSchemaRequested { get; private set; }
    public bool UnsupportedSamplingParametersOmitted { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        HeaderWasSafe = request.Headers.TryGetValues("x-goog-api-key", out IEnumerable<string>? values) &&
            values.Single() == "secret-test-key" && !request.RequestUri!.Query.Contains("key", StringComparison.OrdinalIgnoreCase) &&
            !body.Contains("secret-test-key", StringComparison.Ordinal);
        StructuredSchemaRequested = body.Contains("responseJsonSchema", StringComparison.Ordinal) &&
            body.Contains("additionalProperties", StringComparison.Ordinal) && body.Contains("thinkingLevel", StringComparison.Ordinal);
        UnsupportedSamplingParametersOmitted = !body.Contains("temperature", StringComparison.OrdinalIgnoreCase) &&
            !body.Contains("topP", StringComparison.Ordinal) && !body.Contains("topK", StringComparison.Ordinal);
        const string response = """
            {"candidates":[{"content":{"parts":[{"text":"[{\"id\":\"row-1\",\"translation\":\"Mentés ⟦VARIABLE_01⟧\"}]"}]},"finishReason":"STOP"}]}
            """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        };
    }
}

sealed class RotatingGeminiHandler : HttpMessageHandler
{
    public int FirstKeyCalls { get; private set; }
    public int SecondKeyCalls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string key = request.Headers.GetValues("x-goog-api-key").Single();
        if (key == "first-key")
        {
            FirstKeyCalls++;
            const string error = "{\"error\":{\"code\":429,\"message\":\"Resource exhausted\",\"status\":\"RESOURCE_EXHAUSTED\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(error, Encoding.UTF8, "application/json")
            });
        }
        SecondKeyCalls++;
        const string response = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"[{\\\"id\\\":\\\"row-rotate\\\",\\\"translation\\\":\\\"Kilépés\\\"}]\"}]},\"finishReason\":\"STOP\"}]}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        });
    }
}

sealed class RoundRobinGeminiHandler : HttpMessageHandler
{
    public List<string> Keys { get; } = [];
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Keys.Add(request.Headers.GetValues("x-goog-api-key").Single());
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        Bodies.Add(body);
        using var envelope = System.Text.Json.JsonDocument.Parse(body);
        string rowsJson = envelope.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        using var rows = System.Text.Json.JsonDocument.Parse(rowsJson);
        string id = rows.RootElement[0].GetProperty("id").GetString()!;
        string payload = System.Text.Json.JsonSerializer.Serialize(new[] { new { id, translation = "HU-" + id } });
        string response = System.Text.Json.JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = payload } } }, finishReason = "STOP" } }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        };
    }
}

sealed class DynamicModelGeminiHandler : HttpMessageHandler
{
    public List<string> Models { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;
        string model = path.Contains(GeminiModelRouting.Pro31, StringComparison.Ordinal)
            ? GeminiModelRouting.Pro31 : GeminiModelRouting.Flash37;
        if (request.Method == HttpMethod.Get)
        {
            const string metadata = "{\"name\":\"models/gemini-3.7-flash\",\"supportedGenerationMethods\":[\"generateContent\"],\"inputTokenLimit\":1048576,\"outputTokenLimit\":65536}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(metadata, Encoding.UTF8, "application/json")
            };
        }
        Models.Add(model);
        if (model == GeminiModelRouting.Flash37)
        {
            const string unavailable = "{\"error\":{\"code\":503,\"message\":\"This model is currently experiencing high demand\",\"status\":\"UNAVAILABLE\"}}";
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(unavailable, Encoding.UTF8, "application/json")
            };
        }

        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var envelope = System.Text.Json.JsonDocument.Parse(body);
        string rowsJson = envelope.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        using var rows = System.Text.Json.JsonDocument.Parse(rowsJson);
        string id = rows.RootElement[0].GetProperty("id").GetString()!;
        string payload = System.Text.Json.JsonSerializer.Serialize(new[] { new { id, translation = "Beállítások" } });
        string response = System.Text.Json.JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = payload } } }, finishReason = "STOP" } }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        };
    }
}
