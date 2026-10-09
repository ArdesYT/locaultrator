using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LocaUltrator.Core;

public static class LocaPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LocaUltrator");
    public static string Projects => Path.Combine(Root, "Projektek");
    public static string Logs => Path.Combine(Root, "Naplók");
    public static string Tools => Path.Combine(Root, "Eszközök");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Projects);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Tools);
    }

    public static string CreateProjectPath(string name)
    {
        EnsureCreated();
        string safe = SafeName(name);
        string folder = Path.Combine(Projects, safe);
        int suffix = 2;
        while (Directory.Exists(folder)) folder = Path.Combine(Projects, $"{safe} ({suffix++})");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "Forrás-pillanatképek"));
        Directory.CreateDirectory(Path.Combine(folder, "Export"));
        Directory.CreateDirectory(Path.Combine(folder, "Tokenprofilok"));
        return Path.Combine(folder, "project.locaultrator.json");
    }

    public static string SafeName(string value)
    {
        string safe = string.Concat((value ?? string.Empty).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? "Projekt" : safe;
    }
}

public sealed class ProjectStore
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(ProjectDocument project, string? path = null, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            string target = string.IsNullOrWhiteSpace(path) ? project.ProjectFilePath : path;
            if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException("A projektnek nincs mentési helye.");
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
            project.ModifiedUtc = DateTime.UtcNow;
            string temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
                {
                    await JsonSerializer.SerializeAsync(stream, project, Options, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                File.Move(temp, target, true);
                project.ProjectFilePath = Path.GetFullPath(target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { _saveGate.Release(); }
    }

    public async Task<ProjectDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        ProjectDocument project = await JsonSerializer.DeserializeAsync<ProjectDocument>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("A projektfájl üres vagy sérült.");
        project.Units ??= [];
        project.SourceFiles ??= [];
        project.SourceDocuments ??= [];
        project.PendingBatches ??= [];
        if (project.LegacyPendingBatch is not null)
        {
            if (project.PendingBatches.All(x => x.Id != project.LegacyPendingBatch.Id))
                project.PendingBatches.Add(project.LegacyPendingBatch);
            project.LegacyPendingBatch = null;
        }
        if (project.ClipboardMaximumItems is < 1 or > 5_000) project.ClipboardMaximumItems = ProjectDocument.DefaultClipboardMaximumItems;
        if (project.ClipboardMaximumCharacters is < 1_000 or > 1_000_000) project.ClipboardMaximumCharacters = ProjectDocument.DefaultClipboardMaximumCharacters;
        if (project.MaximumPendingBatches is < 1 or > 20) project.MaximumPendingBatches = ProjectDocument.DefaultMaximumPendingBatches;
        if (project.StatusHudDisplaySeconds is < 1 or > 60) project.StatusHudDisplaySeconds = 3;
        if (project.ClipboardHotkey is not ("Insert" or "F8" or "F9" or "Ctrl+Enter" or "Kikapcsolva")) project.ClipboardHotkey = "Insert";
        project.TokenPromptTemplate = DefaultPrompts.UpgradeTokenPrompt(project.TokenPromptTemplate);
        project.ProjectFilePath = Path.GetFullPath(path);
        foreach (TranslationUnit unit in project.Units) unit.RefreshDisplay();
        return project;
    }

    public async Task<string> ExportAsync(ProjectDocument project, TokenProtectionService protection, CancellationToken cancellationToken = default)
    {
        if (project.ActiveTokenProfile is null)
            throw new InvalidOperationException("Export előtt aktiválj tokenprofilt.");

        if (project.SourceDocuments.Count == 0)
        {
            string[] availableSources = project.SourceFiles.Where(File.Exists).ToArray();
            if (availableSources.Length != project.SourceFiles.Count || availableSources.Length == 0)
                throw new InvalidDataException("Ehhez a régi projekthez még nincs elmentett forráspéldány. Töltsd be egyszer a forrásfájlokat Projektfrissítésként, majd exportálj újra.");
            ImportedSource imported = await new SourceImporter().ImportAsync(availableSources, cancellationToken: cancellationToken);
            SourceImporter.AttachExportMetadata(project, imported);
        }

        string projectFolder = Path.GetDirectoryName(project.ProjectFilePath)
            ?? throw new InvalidOperationException("A projektnek nincs saját mappája.");
        string exportRoot = Path.Combine(projectFolder, "Export");
        Directory.CreateDirectory(exportRoot);
        string runFolder = CreateUniqueExportFolder(exportRoot);
        string stagingFolder = runFolder + ".ellenorzes-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stagingFolder);

        string? onlyOutputRelativePath = null;
        var pendingUnitUpdates = new List<PendingExportUpdate>();
        try
        {
            foreach (SourceDocumentInfo document in project.SourceDocuments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string snapshotPath = Path.GetFullPath(Path.Combine(projectFolder, document.SnapshotRelativePath));
                if (!File.Exists(snapshotPath))
                    throw new FileNotFoundException($"Hiányzik a projekt forráspéldánya: {document.OriginalFileName}. Töltsd be újra Projektfrissítésként.", snapshotPath);

                Encoding encoding = CreateStrictEncoding(document);
                string originalText = await ReadEncodedTextAsync(snapshotPath, encoding, document.HasBom, cancellationToken);
                List<PreservedLine> originalLines = SplitLines(originalText);
                List<PreservedLine> outputLines = originalLines
                    .Select(line => new PreservedLine(line.Content, line.Terminator)).ToList();
                TranslationUnit[] documentUnits = project.Units
                    .Where(unit => unit.Active && unit.SourceDocumentId == document.Id && unit.SourceRecordIndex >= 0)
                    .OrderBy(unit => unit.SourceRecordIndex).ToArray();
                int[] duplicateTargets = documentUnits
                    .GroupBy(unit => document.Format == SourceDocumentFormat.PlainText && unit.TextTargetRecordIndex >= 0
                        ? unit.TextTargetRecordIndex : unit.SourceRecordIndex)
                    .Where(group => group.Skip(1).Any()).Select(group => group.Key).Take(10).ToArray();
                if (duplicateTargets.Length > 0)
                    throw new InvalidDataException($"A(z) {document.OriginalFileName} több fordítást irányítana ugyanarra a célsorra ({string.Join(", ", duplicateTargets.Select(index => index + 1))}), ezért az export nem biztonságos.");

                foreach (TranslationUnit unit in documentUnits)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int sourceRecordIndex = unit.SourceRecordIndex;
                    int targetRecordIndex = document.Format == SourceDocumentFormat.PlainText && unit.TextTargetRecordIndex >= 0
                        ? unit.TextTargetRecordIndex : sourceRecordIndex;
                    if (sourceRecordIndex >= outputLines.Count || targetRecordIndex >= outputLines.Count)
                        throw StructureError(document, unit, "a tárolt fizikai sorszám nem található a forráspéldányban");
                    if (document.Format == SourceDocumentFormat.PlainText)
                    {
                        string originalSource = unit.TextSourceStart >= 0
                            ? SourceImporter.ReadRoutedTextField(originalLines[sourceRecordIndex].Content,
                                unit.TextSourceStart, unit.TextSourceLength, unit.TextSourceQuote)
                            : originalLines[sourceRecordIndex].Content;
                        if (!string.Equals(originalSource, unit.SourceText, StringComparison.Ordinal))
                            throw StructureError(document, unit, "a kijelölt forrásszöveg eltér az elmentett eredeti mezőtől");
                    }

                    ExportTranslation exportTranslation = RestoreExportText(project, unit, protection);
                    string translation = exportTranslation.Text;
                    if (document.Format == SourceDocumentFormat.PlainText)
                    {
                        outputLines[targetRecordIndex].Content = unit.TextTargetStart >= 0
                            ? SourceImporter.ReplaceRoutedTextField(originalLines[targetRecordIndex].Content,
                                unit.TextTargetStart, unit.TextTargetLength, unit.TextTargetQuote,
                                unit.TextDelimiter, translation)
                            : translation;
                    }
                    else
                    {
                        outputLines[targetRecordIndex].Content = ReplaceJsonTarget(
                            outputLines[targetRecordIndex].Content, unit.JsonTargetFieldName, translation);
                    }
                    ValidateExportedUnit(
                        project, document, unit, originalLines[targetRecordIndex],
                        outputLines[targetRecordIndex], translation, protection);
                    pendingUnitUpdates.Add(new PendingExportUpdate(
                        unit, exportTranslation.NormalizedStoredTranslation, SourceImporter.Hash(translation)));
                }

                ValidateLineTopology(document, originalLines, outputLines);
                string outputPath = ResolveSafeOutputPath(stagingFolder, document.OutputRelativePath, document.OriginalFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await WriteEncodedTextAtomicallyAsync(outputPath, JoinLines(outputLines), encoding, document.HasBom, cancellationToken);
                onlyOutputRelativePath = Path.GetRelativePath(stagingFolder, outputPath);
            }

            Directory.Move(stagingFolder, runFolder);
        }
        catch
        {
            try
            {
                if (Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, true);
            }
            catch
            {
                // Az eredeti exporthiba fontosabb; a következő indításkor a rejtett ellenőrzőmappa törölhető.
            }
            throw;
        }
        foreach (PendingExportUpdate update in pendingUnitUpdates)
        {
            if (update.NormalizedStoredTranslation is not null)
                update.Unit.TranslationMasked = update.NormalizedStoredTranslation;
            update.Unit.LastExportedHash = update.ExportedHash;
        }
        project.LastExportUtc = DateTime.UtcNow;
        return project.SourceDocuments.Count == 1
            ? Path.Combine(runFolder, onlyOutputRelativePath!)
            : runFolder;
    }

    private static ExportTranslation RestoreExportText(
        ProjectDocument project, TranslationUnit unit, TokenProtectionService protection)
    {
        MaskResult sourceMap = protection.Mask(unit.SourceText, project.ActiveTokenProfile);
        string candidateMasked;
        string? normalizedStoredTranslation;
        if (string.IsNullOrEmpty(unit.TranslationMasked))
        {
            string fallback = string.IsNullOrEmpty(unit.CurrentTargetTextRaw) ? unit.SourceText : unit.CurrentTargetTextRaw;
            candidateMasked = string.Equals(fallback, unit.SourceText, StringComparison.Ordinal)
                ? sourceMap.Text
                : protection.MaskExistingTranslation(fallback, project.ActiveTokenProfile, sourceMap.Tokens);
            normalizedStoredTranslation = null;
        }
        else
        {
            candidateMasked = unit.TranslationMasked;
            normalizedStoredTranslation = string.Empty;
        }

        string structurallyPreserved = TextStructureGuard.Preserve(sourceMap.Text, candidateMasked);
        if (!protection.ValidateMarkers(sourceMap.Text, structurallyPreserved, out string tokenError))
            throw new InvalidDataException($"{unit.Id}: {tokenError}");
        if (!TextStructureGuard.Validate(sourceMap.Text, structurallyPreserved, out string structureError))
            throw new InvalidDataException($"{unit.Id}: {structureError}");
        if (normalizedStoredTranslation is not null) normalizedStoredTranslation = structurallyPreserved;
        string maskedTranslation = project.ExportAccentMode == HungarianAccentMode.Circumflex
            ? ToCircumflexHungarian(structurallyPreserved)
            : structurallyPreserved;
        return new ExportTranslation(
            protection.Restore(maskedTranslation, sourceMap.Tokens), normalizedStoredTranslation);
    }

    private static void ValidateExportedUnit(
        ProjectDocument project, SourceDocumentInfo document, TranslationUnit unit,
        PreservedLine originalLine, PreservedLine outputLine, string expectedTranslation,
        TokenProtectionService protection)
    {
        if (!string.Equals(originalLine.Terminator, outputLine.Terminator, StringComparison.Ordinal))
            throw StructureError(document, unit, "megváltozott a sorvég típusa");

        string actualTranslation;
        if (document.Format == SourceDocumentFormat.PlainText)
        {
            if (outputLine.Content.IndexOfAny(['\r', '\n']) >= 0)
                throw StructureError(document, unit, "a fordítás új fizikai sort hozna létre");
            if (unit.TextTargetStart >= 0)
            {
                string expectedLine = SourceImporter.ReplaceRoutedTextField(originalLine.Content,
                    unit.TextTargetStart, unit.TextTargetLength, unit.TextTargetQuote,
                    unit.TextDelimiter, expectedTranslation);
                if (!string.Equals(expectedLine, outputLine.Content, StringComparison.Ordinal))
                    throw StructureError(document, unit, "a célmezőn kívüli TXT-szerkezet megváltozott");
                int exportedLength = outputLine.Content.Length - originalLine.Content.Length + unit.TextTargetLength;
                string exportedQuote = unit.TextTargetQuote;
                if (string.IsNullOrEmpty(exportedQuote) && exportedLength >= 2 &&
                    outputLine.Content[unit.TextTargetStart] == '"' &&
                    outputLine.Content[unit.TextTargetStart + exportedLength - 1] == '"') exportedQuote = "\"";
                actualTranslation = SourceImporter.ReadRoutedTextField(
                    outputLine.Content, unit.TextTargetStart, exportedLength, exportedQuote);
            }
            else actualTranslation = outputLine.Content;
            if (!string.Equals(actualTranslation, expectedTranslation, StringComparison.Ordinal))
                throw StructureError(document, unit, "a TXT célmező nem a teljes ellenőrzött fordítást tartalmazza");
        }
        else
        {
            actualTranslation = ReadJsonTarget(outputLine.Content, unit.JsonTargetFieldName);
            if (!string.Equals(actualTranslation, expectedTranslation, StringComparison.Ordinal))
                throw StructureError(document, unit, "a JSON/LUTEXT célmező nem a teljes ellenőrzött fordítást tartalmazza");
        }

        MaskResult sourceMap = protection.Mask(unit.SourceText, project.ActiveTokenProfile);
        string remaskedOutput = protection.MaskExistingTranslation(actualTranslation, project.ActiveTokenProfile, sourceMap.Tokens);
        if (!protection.ValidateMarkers(sourceMap.Text, remaskedOutput, out string tokenError))
            throw StructureError(document, unit, tokenError);
        if (!TextStructureGuard.Validate(sourceMap.Text, remaskedOutput, out string structureError))
            throw StructureError(document, unit, structureError);
    }

    private static void ValidateLineTopology(
        SourceDocumentInfo document, IReadOnlyList<PreservedLine> originalLines, IReadOnlyList<PreservedLine> outputLines)
    {
        if (originalLines.Count != outputLines.Count)
            throw new InvalidDataException($"A(z) {document.OriginalFileName} exportja megváltoztatná a fizikai sorok számát, ezért nem készült fájl.");
        for (int index = 0; index < originalLines.Count; index++)
        {
            if (outputLines[index].Content.IndexOfAny(['\r', '\n']) >= 0)
                throw new InvalidDataException($"A(z) {document.OriginalFileName} {index + 1}. sora új fizikai sort tartalmazna, ezért nem készült fájl.");
            if (!string.Equals(originalLines[index].Terminator, outputLines[index].Terminator, StringComparison.Ordinal))
                throw new InvalidDataException($"A(z) {document.OriginalFileName} {index + 1}. sorának sorvége megváltozna, ezért nem készült fájl.");
        }

        List<PreservedLine> finalLines = SplitLines(JoinLines(outputLines));
        if (finalLines.Count != originalLines.Count)
            throw new InvalidDataException($"A(z) {document.OriginalFileName} végleges exportja megváltoztatná a fizikai sorok számát, ezért nem készült fájl.");
        for (int index = 0; index < originalLines.Count; index++)
            if (!string.Equals(originalLines[index].Terminator, finalLines[index].Terminator, StringComparison.Ordinal))
                throw new InvalidDataException($"A(z) {document.OriginalFileName} {index + 1}. sorának végleges sorvége megváltozna, ezért nem készült fájl.");
    }

    private static InvalidDataException StructureError(SourceDocumentInfo document, TranslationUnit unit, string reason) =>
        new($"A(z) {document.OriginalFileName} {unit.SourceRecordIndex + 1}. sora ({unit.Id}) szerkezetileg hibás: {reason}. Az export nem készült el.");

    private static string ToCircumflexHungarian(string value) => value
        .Replace('ő', 'ô').Replace('ű', 'û').Replace('Ő', 'Ô').Replace('Ű', 'Û');

    private static string ReplaceJsonTarget(string line, string requestedFieldName, string translation)
    {
        JsonNode? node = JsonNode.Parse(line);
        if (node is not JsonObject json)
            throw new InvalidDataException("A JSONL/.lutext fordítási sor nem JSON objektum.");
        string desired = string.IsNullOrWhiteSpace(requestedFieldName) ? "currentTargetText" : requestedFieldName;
        string actual = json.Select(pair => pair.Key)
            .FirstOrDefault(name => name.Equals(desired, StringComparison.OrdinalIgnoreCase)) ?? desired;
        json[actual] = translation;
        return json.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string ReadJsonTarget(string line, string requestedFieldName)
    {
        JsonNode? node = JsonNode.Parse(line);
        if (node is not JsonObject json)
            throw new InvalidDataException("A JSONL/.lutext fordítási sor nem JSON objektum.");
        string desired = string.IsNullOrWhiteSpace(requestedFieldName) ? "currentTargetText" : requestedFieldName;
        string? actual = json.Select(pair => pair.Key)
            .FirstOrDefault(name => name.Equals(desired, StringComparison.OrdinalIgnoreCase));
        if (actual is null || json[actual] is not JsonValue value || !value.TryGetValue(out string? translation))
            throw new InvalidDataException($"A JSONL/.lutext fordítási sorból hiányzik a(z) {desired} szöveges célmező.");
        return translation ?? string.Empty;
    }

    private static string CreateUniqueExportFolder(string root)
    {
        string stem = $"magyar-{DateTime.Now:yyyyMMdd-HHmmss}";
        string candidate = Path.Combine(root, stem);
        int suffix = 2;
        while (Directory.Exists(candidate)) candidate = Path.Combine(root, $"{stem}-{suffix++}");
        return candidate;
    }

    private static string ResolveSafeOutputPath(string runFolder, string relativePath, string fallbackName)
    {
        string relative = string.IsNullOrWhiteSpace(relativePath) ? fallbackName : relativePath;
        string root = Path.GetFullPath(runFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(runFolder, relative));
        if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A forrásfájl exportútvonala nem biztonságos.");
        return result;
    }

    private static Encoding CreateStrictEncoding(SourceDocumentInfo document)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return document.CodePage switch
        {
            65001 => new UTF8Encoding(document.HasBom, true),
            1200 => new UnicodeEncoding(false, document.HasBom, true),
            1201 => new UnicodeEncoding(true, document.HasBom, true),
            _ => Encoding.GetEncoding(document.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        };
    }

    private static async Task<string> ReadEncodedTextAsync(string path, Encoding encoding, bool hasBom, CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[] preamble = hasBom ? encoding.GetPreamble() : [];
        int offset = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        return encoding.GetString(bytes, offset, bytes.Length - offset);
    }

    private static async Task WriteEncodedTextAtomicallyAsync(
        string path, string text, Encoding encoding, bool hasBom, CancellationToken cancellationToken)
    {
        byte[] body;
        try { body = encoding.GetBytes(text); }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidDataException($"A fordítás olyan karaktert tartalmaz, amely nem menthető az eredeti {encoding.EncodingName} kódolással. Válassz másik ő/ű exportmódot vagy javítsd a szöveget.", ex);
        }
        byte[] preamble = hasBom ? encoding.GetPreamble() : [];
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                if (preamble.Length > 0) await stream.WriteAsync(preamble, cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static List<PreservedLine> SplitLines(string text)
    {
        var lines = new List<PreservedLine>();
        int position = 0;
        while (position < text.Length)
        {
            int end = text.IndexOfAny(['\r', '\n'], position);
            if (end < 0)
            {
                lines.Add(new PreservedLine(text[position..], string.Empty));
                break;
            }
            string terminator = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? "\r\n" : text[end].ToString();
            lines.Add(new PreservedLine(text[position..end], terminator));
            position = end + terminator.Length;
        }
        return lines;
    }

    private static string JoinLines(IEnumerable<PreservedLine> lines)
    {
        var text = new StringBuilder();
        foreach (PreservedLine line in lines) text.Append(line.Content).Append(line.Terminator);
        return text.ToString();
    }

    private sealed class PreservedLine(string content, string terminator)
    {
        public string Content { get; set; } = content;
        public string Terminator { get; } = terminator;
    }

    private sealed record ExportTranslation(string Text, string? NormalizedStoredTranslation);
    private sealed record PendingExportUpdate(
        TranslationUnit Unit, string? NormalizedStoredTranslation, string ExportedHash);
}

public static class DefaultPrompts
{
    public const string TokenProfilePrompt = """
Te egy lokalizációs tokenvédelmi szakértő vagy. Készíts a LocaUltrator 0.8.7 számára közvetlenül betölthető tokenprofilt a mellékelt, tényleges projektből gyűjtött tokenjelöltek alapján.
LOCAULTRATOR_TOKEN_PROMPT_VERSION: 3

PROJEKT: {{PROJECT_NAME}}
TXT-SZERKEZET, TOKENJELÖLTEK ÉS FORRÁSPÉLDÁK:
{{TOKEN_CANDIDATES_JSON}}

FONTOS TXT-ÚTVÁLASZTÁSI SZABÁLY: a textLayouts és layoutSafety adatok azt írják le, hogy a program hol találta meg az angol forrást és hová írja a magyar fordítást. A forrás-/céloszlop, mezőcímke, elválasztó, idézőjel, technikai rekord-ID és üres célhely már program által védett fájlszerkezet; ezekre ne készíts tokenszabályt. Csak a candidates tömb value elemeiből indulj ki.

Csak olyan technikai elemet védj, amely a candidates tömbben ténylegesen szerepel és fordításkor változatlanul megőrzendő. Normál szót, nevet, mondatrészt, központozást vagy önmagában álló számot ne védj. Minden regex pontosan egy teljes tokenre illeszkedjen, és a projektben semmilyen candidates-listán kívüli szövegrészre ne illeszkedjen. Tilos a .*, .+, általános \\w+, általános \\d+, lookbehind, visszahivatkozás, feltételes vagy katasztrofális visszalépést okozó minta. Használj .NET 8 kompatibilis, szűk karakterosztályokat és ésszerű korlátokat. Ha valami bizonytalan, ne készíts rá szabályt, hanem tedd a warnings tömbbe.

Engedélyezett kategóriák: placeholder, format_tag, control_code, escape_sequence, resource_id, script_fragment, other.

JSON-BIZTONSÁGI SZABÁLYOK:
- A válasz kizárólag egyetlen érvényes UTF-8 JSON objektum legyen, Markdown, kódkeret és magyarázat nélkül.
- A pattern és value mezőkben a regex fordított perjelét mindig JSON-biztos Unicode alakban írd: \u005C.
- Példa: a [TAG] regex JSON-forrása pontosan "\u005C[TAG\u005C]" legyen.
- Tilos a pattern mezőben közvetlenül \[, \], \{, \}, \+, \$, \|, \d, \s vagy \w alakot írni; a fordított perjel helyén mindig \u005C legyen.
- A teljes válasz elkészítése után belsőleg ellenőrizd, hogy szabványos JSON-parszolóval hiba nélkül beolvasható.

Pontosan ezt a szerkezetet add vissza:
{{OUTPUT_SCHEMA}}

Önellenőrzés: minden id egyedi; minden példa szó szerint szerepel a bemenetben; nincs túl általános regex; minden regex-perjel \u005C alakú; nincs szöveg a JSON-on kívül.
""";

    public const string OutputSchema = """
{"schemaVersion":1,"engine":"dotnet-regex","profileName":"Projekt tokenprofil","rules":[{"id":"egyedi-kebab-case-id","name":"Magyar név","category":"placeholder","pattern":"\u005C{[A-Za-z_][A-Za-z0-9_.:-]{0,63}\u005C}","priority":100,"caseSensitive":true,"examples":["csak tényleges példa"]}],"literals":[{"value":"csak tényleges ritka token","category":"control_code","caseSensitive":true}],"warnings":[]}
""";

    public static string UpgradeTokenPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return TokenProfilePrompt;
        bool oldBuiltIn = prompt.Contains("A válasz kizárólag egyetlen érvényes UTF-8 JSON objektum legyen", StringComparison.Ordinal)
            && (!prompt.Contains("JSON-BIZTONSÁGI SZABÁLYOK", StringComparison.Ordinal) ||
                prompt.Contains("Te egy lokalizációs tokenvédelmi szakértő vagy", StringComparison.Ordinal) &&
                !prompt.Contains("LOCAULTRATOR_TOKEN_PROMPT_VERSION: 3", StringComparison.Ordinal));
        return oldBuiltIn ? TokenProfilePrompt : prompt;
    }
}
