using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocaUltrator.Core;

public sealed class ClipboardTranslationService
{
    public const int CurrentProtocolVersion = 3;
    private const int BatchKeyLength = 10;
    private const int RowNumberLength = 4;
    private readonly TokenProtectionService _protection;

    public ClipboardTranslationService(TokenProtectionService protection) => _protection = protection;

    public PendingBatch CreateBatch(ProjectDocument project, int? maximumItems = null, int? maximumCharacters = null)
    {
        if (project.ActiveTokenProfile is null)
            throw new InvalidOperationException("Fordítás előtt aktiválj tokenprofilt. Az üres, ellenőrzött profil is aktiválható.");
        if (project.PendingBatches.Count >= Math.Clamp(project.MaximumPendingBatches, 1, 20))
            throw new InvalidOperationException($"Már {project.PendingBatches.Count} csomag vár válaszra. Dolgozz fel vagy állíts vissza egyet, vagy emeld meg a Beállításokban a várakozó csomagok számát.");

        int itemLimit = project.ClipboardLimitMode == ClipboardLimitMode.Rows
            ? Math.Clamp(maximumItems ?? project.ClipboardMaximumItems, 1, 5_000)
            : 5_000;
        int characterLimit = project.ClipboardLimitMode == ClipboardLimitMode.Characters
            ? Math.Clamp(maximumCharacters ?? project.ClipboardMaximumCharacters, 1_000, 1_000_000)
            : 1_000_000;
        TranslationUnit[] candidates = project.Units.Where(x => x.Active && x.Status == UnitStatus.Pending)
            .OrderBy(x => x.Order).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("Nincs elküldhető, le nem fordított szöveg.");

        string id = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var batch = new PendingBatch { Id = id, ProtocolVersion = CurrentProtocolVersion };
        var clipboard = new StringBuilder();
        clipboard.AppendLine($"Fordítsd {project.TargetLanguage} nyelvre.");
        clipboard.AppendLine($"Válasz: első sor {GetStartMarker(batch)}, utolsó sor {GetEndMarker(batch)}.");
        clipboard.AppendLine("Közte minden sor: változatlan LU-azonosító, tabulátor, fordítás. A ⟦...⟧ tokeneket ne módosítsd. Más szöveget ne adj.");
        if (!string.IsNullOrWhiteSpace(project.TranslationInstruction))
            clipboard.AppendLine(project.TranslationInstruction.Trim());
        clipboard.AppendLine(GetStartMarker(batch));

        var selected = new List<TranslationUnit>();
        foreach (TranslationUnit unit in candidates)
        {
            if (selected.Count >= itemLimit) break;
            string masked = string.IsNullOrEmpty(unit.MaskedSource)
                ? _protection.Mask(unit.SourceText, project.ActiveTokenProfile).Text
                : unit.MaskedSource;
            int lineNumber = selected.Count + 1;
            string promptLine = GetRowId(batch, lineNumber) + "\t" + ToSingleLine(masked);
            int predictedLength = clipboard.Length + promptLine.Length + Environment.NewLine.Length +
                GetEndMarker(batch).Length + Environment.NewLine.Length;
            if (predictedLength > characterLimit)
            {
                if (selected.Count == 0)
                    throw new InvalidOperationException($"A következő szöveg önmagában sem fér el a {characterLimit:N0} karakteres vágólapkorlátban. Emeld meg a Beállításokban a maximális karakterszámot.");
                break;
            }
            selected.Add(unit);
            clipboard.AppendLine(promptLine);
        }
        clipboard.AppendLine(GetEndMarker(batch));

        for (int i = 0; i < selected.Count; i++)
        {
            int line = i + 1;
            batch.UnitIds[line] = selected[i].Id;
            selected[i].Status = UnitStatus.Waiting;
        }
        batch.ClipboardText = clipboard.ToString();
        batch.CharacterCount = batch.ClipboardText.Length;
        return batch;
    }

    public static string GetRowId(PendingBatch batch, int lineNumber)
    {
        string normalized = new string((batch.Id ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length < BatchKeyLength)
            normalized = normalized.PadRight(BatchKeyLength, '0');
        string key = normalized[..BatchKeyLength];
        return batch.ProtocolVersion >= CurrentProtocolVersion
            ? $"LU-{key}-SOR{lineNumber.ToString($"D{RowNumberLength}")}"
            : "LU" + key + lineNumber.ToString($"D{RowNumberLength}");
    }

    public static string GetStartMarker(PendingBatch batch) => $"<<LU-KEZDES:{GetBatchKey(batch)}>>";
    public static string GetEndMarker(PendingBatch batch) => $"<<LU-ZARAS:{GetBatchKey(batch)}>>";

    public bool ContainsResponseForAnyBatch(ProjectDocument project, string response) =>
        FindMatchingBatches(project, response).Count > 0;

    public BatchApplyResult ApplyResponse(ProjectDocument project, string response)
    {
        PendingBatch batch = FindMatchingBatches(project, response).FirstOrDefault()
            ?? throw new InvalidOperationException("A vágólapon nincs egyik várakozó csomaghoz sem tartozó AI-válasz.");
        if (string.Equals(response.Trim(), batch.ClipboardText.Trim(), StringComparison.Ordinal))
            throw new InvalidDataException("A vágólapon még a kiküldött csomag van, nem az AI válasza.");
        return IsJsonEnvelopeLegacy(batch) ? ApplyLegacyResponse(project, batch, response) : ApplyIdentifiedLines(project, batch, response);
    }

    public IReadOnlyList<BatchApplyResult> ApplyResponses(ProjectDocument project, string response)
    {
        PendingBatch[] matching = FindMatchingBatches(project, response).ToArray();
        if (matching.Length == 0)
            throw new InvalidOperationException("A vágólapon nincs egyik várakozó csomaghoz sem tartozó AI-válasz.");

        var results = new List<BatchApplyResult>();
        foreach (PendingBatch batch in matching)
            results.Add(IsJsonEnvelopeLegacy(batch) ? ApplyLegacyResponse(project, batch, response) : ApplyIdentifiedLines(project, batch, response));
        return results;
    }

    public void CancelBatch(ProjectDocument project, string? batchId = null)
    {
        PendingBatch? batch = string.IsNullOrWhiteSpace(batchId)
            ? project.PendingBatches.OrderByDescending(x => x.CreatedUtc).FirstOrDefault()
            : project.PendingBatches.FirstOrDefault(x => x.Id == batchId);
        if (batch is null) return;
        foreach (string unitId in batch.UnitIds.Values)
        {
            TranslationUnit? unit = project.Units.FirstOrDefault(x => x.Id == unitId && x.Status == UnitStatus.Waiting);
            if (unit is not null) unit.Status = UnitStatus.Pending;
        }
        project.PendingBatches.Remove(batch);
    }

    private IReadOnlyList<PendingBatch> FindMatchingBatches(ProjectDocument project, string response)
    {
        HashSet<string> responseIds = ParseIdentifiedLines(response, out _).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return project.PendingBatches.Where(batch =>
            IsJsonEnvelopeLegacy(batch)
                ? response.Contains($"[[LU-RESPONSE:{batch.Id}]]", StringComparison.Ordinal)
                : batch.UnitIds.Keys.Any(line => responseIds.Contains(GetRowId(batch, line))))
            .ToArray();
    }

    private BatchApplyResult ApplyIdentifiedLines(ProjectDocument project, PendingBatch batch, string response)
    {
        string payload = ExtractIdentifiedPayload(batch, response);
        Dictionary<string, string> received = ParseIdentifiedLines(payload, out HashSet<string> duplicates);
        var errors = new List<string>();
        int applied = 0, rejected = 0, missing = 0;
        foreach ((int line, string unitId) in batch.UnitIds)
        {
            string rowId = GetRowId(batch, line);
            TranslationUnit? unit = project.Units.FirstOrDefault(x => x.Id == unitId);
            if (unit is null) continue;
            if (duplicates.Contains(rowId))
            {
                unit.Status = UnitStatus.Pending;
                errors.Add($"{rowId}: az azonosító többször szerepel.");
                rejected++;
                continue;
            }
            if (!received.TryGetValue(rowId, out string? translation))
            {
                unit.Status = UnitStatus.Pending;
                missing++;
                continue;
            }
            translation = RestoreEscapedCharacters(translation, unit.SourceDisplay);
            translation = TextStructureGuard.Preserve(unit.SourceDisplay, translation);
            if (!_protection.ValidateMarkers(unit.SourceDisplay, translation, out string tokenError))
            {
                unit.Status = UnitStatus.Pending;
                errors.Add($"{rowId}: {tokenError}");
                rejected++;
                continue;
            }
            if (!TextStructureGuard.Validate(unit.SourceDisplay, translation, out string structureError))
            {
                unit.Status = UnitStatus.Pending;
                errors.Add($"{rowId}: {structureError}");
                rejected++;
                continue;
            }
            unit.TranslationMasked = translation;
            unit.LastAiTranslationMasked = translation;
            unit.TranslationOrigin = "clipboard-ai";
            unit.Status = UnitStatus.Completed;
            applied++;
        }
        project.PendingBatches.Remove(batch);
        project.ModifiedUtc = DateTime.UtcNow;
        return new BatchApplyResult(applied, rejected, missing, errors) { BatchId = batch.Id };
    }

    private BatchApplyResult ApplyLegacyResponse(ProjectDocument project, PendingBatch batch, string response)
    {
        string startMarker = $"[[LU-RESPONSE:{batch.Id}]]";
        string endMarker = $"[[LU-END:{batch.Id}]]";
        int start = response.IndexOf(startMarker, StringComparison.Ordinal);
        int end = response.IndexOf(endMarker, StringComparison.Ordinal);
        if (start < 0 || end <= start)
            throw new InvalidDataException("A régi csomag válaszából hiányzik a zárójelölő.");
        string body = response[(start + startMarker.Length)..end];
        var received = new Dictionary<int, string>();
        var errors = new List<string>();
        foreach (string rawLine in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal)) continue;
            try
            {
                using JsonDocument json = JsonDocument.Parse(line);
                int number = json.RootElement.GetProperty("line").GetInt32();
                string translation = json.RootElement.GetProperty("translation").GetString() ?? string.Empty;
                if (!batch.UnitIds.ContainsKey(number)) { errors.Add($"Idegen sorszám: {number}."); continue; }
                if (!received.TryAdd(number, translation)) errors.Add($"Ismétlődő sorszám: {number}.");
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                errors.Add("Nem feldolgozható régi válaszsor: " + line);
            }
        }

        int applied = 0, rejected = 0, missing = 0;
        foreach ((int line, string unitId) in batch.UnitIds)
        {
            TranslationUnit? unit = project.Units.FirstOrDefault(x => x.Id == unitId);
            if (unit is null) continue;
            if (!received.TryGetValue(line, out string? translation))
            {
                unit.Status = UnitStatus.Pending;
                missing++;
                continue;
            }
            translation = RestoreEscapedCharacters(translation, unit.SourceDisplay);
            translation = TextStructureGuard.Preserve(unit.SourceDisplay, translation);
            if (!_protection.ValidateMarkers(unit.SourceDisplay, translation, out string tokenError))
            {
                unit.Status = UnitStatus.Pending;
                errors.Add($"{line}. sor: {tokenError}");
                rejected++;
                continue;
            }
            if (!TextStructureGuard.Validate(unit.SourceDisplay, translation, out string structureError))
            {
                unit.Status = UnitStatus.Pending;
                errors.Add($"{line}. sor: {structureError}");
                rejected++;
                continue;
            }
            unit.TranslationMasked = translation;
            unit.LastAiTranslationMasked = translation;
            unit.TranslationOrigin = "clipboard-ai";
            unit.Status = UnitStatus.Completed;
            applied++;
        }
        project.PendingBatches.Remove(batch);
        project.ModifiedUtc = DateTime.UtcNow;
        return new BatchApplyResult(applied, rejected, missing, errors) { BatchId = batch.Id };
    }

    private static Dictionary<string, string> ParseIdentifiedLines(string response, out HashSet<string> duplicates)
    {
        var received = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(response ?? string.Empty);
        while (reader.ReadLine() is { } rawLine)
        {
            if (!TryParseIdentifiedLine(rawLine, out string rowId, out string translation)) continue;
            if (!received.TryAdd(rowId, translation))
            {
                duplicates.Add(rowId);
                received[rowId] = translation;
            }
        }
        return received;
    }

    private static bool TryParseIdentifiedLine(string rawLine, out string rowId, out string translation)
    {
        rowId = string.Empty;
        translation = string.Empty;
        string line = rawLine.TrimEnd();
        if (line.StartsWith("```", StringComparison.Ordinal)) return false;
        line = line.TrimStart(' ', '\t', '-', '*', '•', '>', '|');
        Match idMatch = Regex.Match(line,
            @"^(?<id>LU(?:-[0-9A-Fa-f]{10}-SOR[0-9]{4}|[0-9A-Fa-f]{10}[0-9]{4}))",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!idMatch.Success) return false;
        string candidate = idMatch.Groups["id"].Value;
        int rowIdLength = candidate.Length;
        if (line.Length > rowIdLength && !IsSeparator(line[rowIdLength])) return false;

        rowId = candidate.ToUpperInvariant();
        string rest = line[rowIdLength..].TrimStart(' ', '\t');
        if (rest.StartsWith("- ", StringComparison.Ordinal)) rest = rest[2..].TrimStart();
        else if (rest.Length > 0 && rest[0] is '|' or ':' or '=' or '→') rest = rest[1..].TrimStart(' ', '\t');
        if (rawLine.TrimStart().StartsWith('|') && rest.EndsWith('|')) rest = rest[..^1].TrimEnd();
        translation = rest;
        return true;
    }

    private static bool IsSeparator(char value) => char.IsWhiteSpace(value) || value is '|' or ':' or '=' or '→' or '-';
    private static bool IsJsonEnvelopeLegacy(PendingBatch batch) => batch.ProtocolVersion <= 1 ||
        batch.ClipboardText.Contains("[[LU-RESPONSE:", StringComparison.Ordinal);
    private static string ExtractIdentifiedPayload(PendingBatch batch, string response)
    {
        if (batch.ProtocolVersion < CurrentProtocolVersion) return response;
        string startMarker = GetStartMarker(batch);
        string endMarker = GetEndMarker(batch);
        int start = response.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        int end = start < 0 ? -1 : response.IndexOf(endMarker, start + startMarker.Length, StringComparison.OrdinalIgnoreCase);
        if (start < 0) throw new InvalidDataException($"Hiányzik a csomag kezdő sora: {startMarker}");
        if (end < 0) throw new InvalidDataException($"Hiányzik a csomag záró sora: {endMarker}");
        return response[(start + startMarker.Length)..end];
    }
    private static string GetBatchKey(PendingBatch batch)
    {
        string normalized = new string((batch.Id ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length < BatchKeyLength) normalized = normalized.PadRight(BatchKeyLength, '0');
        return normalized[..BatchKeyLength];
    }
    private static string ToSingleLine(string value) => (value ?? string.Empty)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
    private static string RestoreLineBreaks(string value, string source)
    {
        if (!source.Contains('\r') && !source.Contains('\n')) return value;
        string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : source.Contains('\r') ? "\r" : "\n";
        return value.Replace("\\n", newline, StringComparison.Ordinal);
    }
    private static string RestoreEscapedCharacters(string value, string source)
    {
        string restored = Regex.Replace(value, @"\\u([0-9A-Fa-f]{4})", match =>
            ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString(), RegexOptions.CultureInvariant);
        if (source.Contains('\t')) restored = restored.Replace("\\t", "\t", StringComparison.Ordinal);
        return RestoreLineBreaks(restored, source);
    }
}
