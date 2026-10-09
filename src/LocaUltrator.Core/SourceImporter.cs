using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocaUltrator.Core;

public sealed class SourceImporter
{
    static SourceImporter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public async Task<ImportedSource> ImportAsync(
        IEnumerable<string> filePaths,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        string sourceLanguage = "angol",
        string targetLanguage = "magyar")
    {
        string[] paths = filePaths.Where(File.Exists).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) throw new InvalidOperationException("Nem választottál ki olvasható forrásfájlt.");

        var units = new List<ImportedUnit>();
        var documents = new List<SourceDocumentInfo>();
        Dictionary<string, string> outputRelativePaths = BuildOutputRelativePaths(paths);
        string version = string.Empty;
        int order = 0;
        for (int documentIndex = 0; documentIndex < paths.Length; documentIndex++)
        {
            string path = paths[documentIndex];
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Beolvasás: {Path.GetFileName(path)}");
            string extension = Path.GetExtension(path).ToLowerInvariant();
            Encoding encoding = DetectEncoding(path);
            string fileName = Path.GetFileName(path) ?? string.Empty;
            string fileContentHash = HashFile(path);
            string outputRelativePath = outputRelativePaths[path];
            var document = new SourceDocumentInfo
            {
                Id = Hash(path.ToUpperInvariant() + "|" + fileContentHash)[..20],
                ContentHash = fileContentHash,
                OriginalPath = path,
                OriginalFileName = fileName,
                OutputRelativePath = outputRelativePath,
                Format = extension switch
                {
                    ".jsonl" => SourceDocumentFormat.JsonLines,
                    ".lutext" => SourceDocumentFormat.LuTextJsonLines,
                    _ => SourceDocumentFormat.PlainText
                },
                CodePage = encoding.CodePage,
                HasBom = HasEncodingPreamble(path, encoding)
            };
            documents.Add(document);
            if (extension is ".jsonl" or ".lutext")
                version = await ReadJsonLinesAsync(path, document, encoding, units, version, order, cancellationToken);
            else
                await ReadTextAsync(path, document, encoding, units, order, sourceLanguage, targetLanguage, cancellationToken);
            order = units.Count;
        }
        return new ImportedSource(version, units, paths) { SourceDocuments = documents };
    }

    private static async Task ReadTextAsync(
        string path, SourceDocumentInfo document, Encoding encoding, List<ImportedUnit> output,
        int baseOrder, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        using (var reader = new StreamReader(path, encoding, true, 1 << 16))
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
                lines.Add(line);
        }

        HashSet<string> sourceAliases = BuildLanguageAliases(sourceLanguage, source: true);
        HashSet<string> targetAliases = BuildLanguageAliases(targetLanguage, source: false);
        if (TryReadDelimitedText(path, document, lines, output, baseOrder, sourceAliases, targetAliases)) return;
        if (TryReadLabeledPairs(path, document, lines, output, baseOrder, sourceAliases, targetAliases)) return;
        if (HasAmbiguousSeparateTargetSignals(lines, sourceAliases, targetAliases))
            throw new InvalidDataException($"A(z) {document.OriginalFileName} külön forrás- és célmezőre utal, de a párosítás nem egyértelmű. A program biztonsági okból nem írja felül a teljes angol sort. Adj egyértelmű English/Hungarian (vagy source/target) fejlécet, illetve mezőcímkéket.");

        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int lineNumber = 0; lineNumber < lines.Count; lineNumber++)
        {
            string line = lines[lineNumber];
            string hash = Hash(line);
            int occurrence = occurrences.GetValueOrDefault(hash) + 1;
            occurrences[hash] = occurrence;
            string fileKey = Path.GetFileName(path).ToLowerInvariant();
            string id = $"txt:{fileKey}:{hash[..16]}:{occurrence}";
            output.Add(new ImportedUnit(id, false, Path.GetFileName(path), line, hash, string.Empty, string.Empty, "null", baseOrder + lineNumber)
            {
                SourceDocumentId = document.Id,
                SourceDocumentKey = NormalizeDocumentKey(document.OutputRelativePath, document.OriginalFileName),
                SourceRecordIndex = lineNumber
            });
        }
        document.TextLayoutKind = PlainTextLayoutKind.WholeLine;
        document.TextLayoutDescription = "Soronként egy teljes fordítási egység; a fordítás ugyanarra a sorra kerül.";
    }

    private static bool TryReadDelimitedText(
        string path, SourceDocumentInfo document, IReadOnlyList<string> lines, List<ImportedUnit> output,
        int baseOrder, HashSet<string> sourceAliases, HashSet<string> targetAliases)
    {
        DelimitedLayout? layout = DetectHeaderedDelimitedLayout(lines, sourceAliases, targetAliases)
            ?? DetectHeaderlessDelimitedLayout(lines);
        if (layout is null) return false;

        var detectedRows = new List<DetectedTextRow>();
        int firstLine = layout.HeaderLine >= 0 ? layout.HeaderLine + 1 : 0;
        for (int lineIndex = firstLine; lineIndex < lines.Count; lineIndex++)
        {
            string line = lines[lineIndex];
            if (string.IsNullOrWhiteSpace(line) || IsCommentLine(line) || IsTableDivider(line)) continue;
            if (!TryParseDelimitedLine(line, layout.Delimiter, out List<TextFieldSpan>? fields) ||
                fields.Count != layout.ColumnCount)
                throw new InvalidDataException($"A(z) {document.OriginalFileName} {lineIndex + 1}. sora nem követi a felismert {layout.ColumnCount} oszlopos TXT-szerkezetet. A program nem hagy ki csendben bizonytalan célhelyű sort.");
            TextFieldSpan sourceField = fields[layout.SourceColumn];
            TextFieldSpan targetField = fields[layout.TargetColumn];
            if (string.IsNullOrWhiteSpace(sourceField.Value)) continue;
            string externalId = layout.IdColumn >= 0 && layout.IdColumn < fields.Count
                ? fields[layout.IdColumn].Value.Trim() : string.Empty;
            string context = layout.ContextColumn >= 0 && layout.ContextColumn < fields.Count
                ? fields[layout.ContextColumn].Value : string.Empty;
            detectedRows.Add(new DetectedTextRow(
                lineIndex, sourceField, lineIndex, targetField, externalId, context));
        }
        if (detectedRows.Count == 0) return false;

        AddDetectedTextRows(path, document, output, baseOrder, detectedRows,
            PlainTextLayoutKind.DelimitedColumns, layout.Delimiter.ToString());
        document.TextLayoutKind = PlainTextLayoutKind.DelimitedColumns;
        document.TextLayoutDescription = layout.Description + $" Felismert fordítási egységek: {detectedRows.Count}.";
        return true;
    }

    private static DelimitedLayout? DetectHeaderedDelimitedLayout(
        IReadOnlyList<string> lines, HashSet<string> sourceAliases, HashSet<string> targetAliases)
    {
        DelimitedLayout? best = null;
        int bestRows = -1;
        int inspected = 0;
        for (int lineIndex = 0; lineIndex < lines.Count && inspected < 50; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex]) || IsCommentLine(lines[lineIndex])) continue;
            inspected++;
            foreach (char delimiter in new[] { '\t', ';', '|' })
            {
                if (!TryParseDelimitedLine(lines[lineIndex], delimiter, out List<TextFieldSpan>? fields) || fields.Count < 2) continue;
                int[] sources = FieldIndexes(fields, sourceAliases);
                int[] targets = FieldIndexes(fields, targetAliases);
                if (sources.Length != 1 || targets.Length != 1 || sources[0] == targets[0]) continue;
                int idColumn = FindHeaderColumn(fields, IdentifierAliases);
                int contextColumn = FindHeaderColumn(fields, ContextAliases);
                int required = Math.Max(sources[0], targets[0]);
                int rows = 0;
                for (int row = lineIndex + 1; row < lines.Count; row++)
                    if (TryParseDelimitedLine(lines[row], delimiter, out List<TextFieldSpan>? data) &&
                        data.Count > required && !string.IsNullOrWhiteSpace(data[sources[0]].Value) &&
                        !IsTableDivider(lines[row])) rows++;
                if (rows <= bestRows) continue;
                string separatorName = delimiter == '\t' ? "TAB" : delimiter.ToString();
                bestRows = rows;
                best = new DelimitedLayout(delimiter, fields.Count, lineIndex, sources[0], targets[0], idColumn, contextColumn,
                    $"Fejléc alapján felismert {separatorName} elválasztású forrás- és céloszlop; csak a céloszlop változik.");
            }
        }
        return bestRows > 0 ? best : null;
    }

    private static DelimitedLayout? DetectHeaderlessDelimitedLayout(IReadOnlyList<string> lines)
    {
        var candidates = new List<(DelimitedLayout Layout, double Score)>();
        foreach (char delimiter in new[] { '\t', ';', '|' })
        {
            List<List<TextFieldSpan>> sample = lines.Where(line => !string.IsNullOrWhiteSpace(line) && !IsCommentLine(line))
                .Take(250)
                .Select(line => TryParseDelimitedLine(line, delimiter, out List<TextFieldSpan>? fields) ? fields : [])
                .Where(fields => fields.Count >= 3).ToList();
            if (sample.Count < 3) continue;
            int columnCount = sample.GroupBy(fields => fields.Count).OrderByDescending(group => group.Count())
                .ThenByDescending(group => group.Key).First().Key;
            List<List<TextFieldSpan>> rows = sample.Where(fields => fields.Count == columnCount).ToList();
            if (rows.Count < 3) continue;

            for (int sourceColumn = 0; sourceColumn + 1 < columnCount; sourceColumn++)
            {
                int targetColumn = sourceColumn + 1;
                double sourceRatio = rows.Count(fields => LooksLikeTranslatableText(fields[sourceColumn].Value)) / (double)rows.Count;
                double emptyTargetRatio = rows.Count(fields => string.IsNullOrWhiteSpace(fields[targetColumn].Value)) / (double)rows.Count;
                if (sourceRatio < 0.80 || emptyTargetRatio < 0.80) continue;
                int idColumn = Enumerable.Range(0, columnCount)
                    .Where(column => column != sourceColumn && column != targetColumn)
                    .OrderBy(column => column)
                    .FirstOrDefault(column =>
                        rows.Count(fields => LooksLikeStableIdentifier(fields[column].Value)) / (double)rows.Count >= 0.80,
                        -1);
                if (idColumn < 0) continue;
                double uniqueIdRatio = rows.Select(fields => fields[idColumn].Value.Trim())
                    .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Count() / (double)rows.Count;
                if (uniqueIdRatio < 0.80) continue;
                string separatorName = delimiter == '\t' ? "TAB" : delimiter.ToString();
                candidates.Add((new DelimitedLayout(delimiter, columnCount, -1, sourceColumn, targetColumn, idColumn, -1,
                    $"Fejléc nélküli, konzisztens {separatorName} táblázat: technikai ID, forrásszöveg és üres céloszlop; csak a céloszlop változik."),
                    sourceRatio + emptyTargetRatio + uniqueIdRatio));
            }
        }
        if (candidates.Count == 0) return null;
        (DelimitedLayout layout, double score) = candidates.OrderByDescending(candidate => candidate.Score).First();
        if (candidates.Count > 1 && Math.Abs(score - candidates.OrderByDescending(candidate => candidate.Score).Skip(1).First().Score) < 0.05)
            return null;
        return layout;
    }

    private static bool TryReadLabeledPairs(
        string path, SourceDocumentInfo document, IReadOnlyList<string> lines, List<ImportedUnit> output,
        int baseOrder, HashSet<string> sourceAliases, HashSet<string> targetAliases)
    {
        List<LabeledField> sources = FindLabeledFields(lines, sourceAliases);
        List<LabeledField> targets = FindLabeledFields(lines, targetAliases);
        if (sources.Count == 0 || targets.Count == 0) return false;

        List<LabeledPair> sourceFirst = BuildLabeledPairs(sources, targets, lines.Count);
        List<LabeledPair> targetFirst = BuildLabeledPairs(targets, sources, lines.Count)
            .Select(pair => new LabeledPair(pair.Target, pair.Source, pair.Start, pair.End)).ToList();
        List<LabeledPair> pairs = sourceFirst.Count >= targetFirst.Count ? sourceFirst : targetFirst;
        if (pairs.Count != sources.Count || pairs.Count != targets.Count) return false;

        var rows = new List<DetectedTextRow>(pairs.Count);
        foreach (LabeledPair pair in pairs.OrderBy(pair => pair.Source.LineIndex))
        {
            string id = FindAuxiliaryField(lines, pair.Start, pair.End, IdentifierAliases);
            string context = FindAuxiliaryField(lines, pair.Start, pair.End, ContextAliases);
            rows.Add(new DetectedTextRow(pair.Source.LineIndex, pair.Source.Field,
                pair.Target.LineIndex, pair.Target.Field, id, context));
        }
        AddDetectedTextRows(path, document, output, baseOrder, rows, PlainTextLayoutKind.LabeledPairs, string.Empty);
        document.TextLayoutKind = PlainTextLayoutKind.LabeledPairs;
        document.TextLayoutDescription = $"Párosított nyelvi mezők külön forrás- és célsoron; csak a {targetAliases.FirstOrDefault() ?? "cél"} mező értéke változik. Felismert fordítási egységek: {rows.Count}.";
        return true;
    }

    private static List<LabeledPair> BuildLabeledPairs(
        IReadOnlyList<LabeledField> anchors, IReadOnlyList<LabeledField> partners, int lineCount)
    {
        var result = new List<LabeledPair>();
        for (int index = 0; index < anchors.Count; index++)
        {
            int searchStart = anchors[index].LineIndex;
            int end = index + 1 < anchors.Count ? anchors[index + 1].LineIndex - 1 : lineCount - 1;
            int recordStart = index == 0 ? 0 : anchors[index - 1].LineIndex + 1;
            LabeledField[] matches = partners.Where(field => field.LineIndex >= searchStart && field.LineIndex <= end).ToArray();
            if (matches.Length == 1)
                result.Add(new LabeledPair(anchors[index], matches[0], recordStart, end));
        }
        return result;
    }

    private static void AddDetectedTextRows(
        string path, SourceDocumentInfo document, List<ImportedUnit> output, int baseOrder,
        IReadOnlyList<DetectedTextRow> rows, PlainTextLayoutKind layoutKind, string delimiter)
    {
        string fileKey = Path.GetFileName(path).ToLowerInvariant();
        Dictionary<string, int> externalIdCounts = rows.Where(row => !string.IsNullOrWhiteSpace(row.ExternalId))
            .GroupBy(row => row.ExternalId.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < rows.Count; index++)
        {
            DetectedTextRow row = rows[index];
            string source = row.SourceField.Value;
            string hash = Hash(source);
            int occurrence = occurrences.GetValueOrDefault(hash) + 1;
            occurrences[hash] = occurrence;
            string externalId = row.ExternalId.Trim();
            bool stable = externalId.Length > 0 && externalIdCounts.GetValueOrDefault(externalId) == 1;
            string id = stable ? externalId : $"txt:{fileKey}:{hash[..16]}:{occurrence}";
            output.Add(new ImportedUnit(id, stable, Path.GetFileName(path), source, hash,
                row.TargetField.Value, row.Context, "null", baseOrder + index)
            {
                SourceDocumentId = document.Id,
                SourceDocumentKey = NormalizeDocumentKey(document.OutputRelativePath, document.OriginalFileName),
                SourceRecordIndex = row.SourceLineIndex,
                TextLayoutKind = layoutKind,
                TextTargetRecordIndex = row.TargetLineIndex,
                TextSourceStart = row.SourceField.Start,
                TextSourceLength = row.SourceField.Length,
                TextSourceQuote = row.SourceField.Quote,
                TextTargetStart = row.TargetField.Start,
                TextTargetLength = row.TargetField.Length,
                TextTargetQuote = row.TargetField.Quote,
                TextDelimiter = delimiter
            });
        }
    }

    internal static string ReadRoutedTextField(string line, int start, int length, string quote)
    {
        if (start < 0 || length < 0 || start > line.Length || length > line.Length - start)
            throw new InvalidDataException("A TXT-mező tárolt helye kívül esik a forrássoron.");
        string raw = line.Substring(start, length);
        if (string.IsNullOrEmpty(quote)) return raw;
        char quoteCharacter = quote[^1];
        bool backslashEscaped = quote.Length == 2 && quote[0] == '\\';
        if (quote.Length is < 1 or > 2 || raw.Length < 2 || raw[0] != quoteCharacter || raw[^1] != quoteCharacter)
            throw new InvalidDataException("A TXT-mező idézőjeles szerkezete megváltozott.");
        string inner = raw[1..^1];
        if (!backslashEscaped)
            return inner.Replace(new string(quoteCharacter, 2), quoteCharacter.ToString(), StringComparison.Ordinal);
        var decoded = new StringBuilder(inner.Length);
        for (int index = 0; index < inner.Length; index++)
        {
            if (inner[index] == '\\' && index + 1 < inner.Length &&
                (inner[index + 1] is '\\' || inner[index + 1] == quoteCharacter))
            {
                decoded.Append(inner[++index]);
                continue;
            }
            decoded.Append(inner[index]);
        }
        return decoded.ToString();
    }

    internal static string ReplaceRoutedTextField(
        string line, int start, int length, string quote, string delimiter, string value)
    {
        _ = ReadRoutedTextField(line, start, length, quote);
        string encoded;
        if (!string.IsNullOrEmpty(quote))
        {
            char quoteCharacter = quote[^1];
            bool backslashEscaped = quote.Length == 2 && quote[0] == '\\';
            string escaped = backslashEscaped
                ? value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace(quoteCharacter.ToString(), "\\" + quoteCharacter, StringComparison.Ordinal)
                : value.Replace(quoteCharacter.ToString(), new string(quoteCharacter, 2), StringComparison.Ordinal);
            encoded = quoteCharacter + escaped + quoteCharacter;
        }
        else
        {
            bool needsQuotes = !string.IsNullOrEmpty(delimiter) &&
                (value.Contains(delimiter, StringComparison.Ordinal) || value.Contains('"') ||
                 value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));
            encoded = needsQuotes ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
        }
        return line[..start] + encoded + line[(start + length)..];
    }

    private static HashSet<string> BuildLanguageAliases(string language, bool source)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        void Add(params string[] values)
        {
            foreach (string value in values) aliases.Add(NormalizeLabel(value));
        }
        Add(language);
        string normalized = NormalizeLabel(language);
        if (normalized is "angol" or "english" or "en" or "enus" or "engb") Add("angol", "english", "en", "en-US", "en-GB");
        if (normalized is "magyar" or "hungarian" or "hu" or "huhu") Add("magyar", "hungarian", "hu", "hu-HU");
        if (source) Add("source", "sourceText", "source_text", "original", "originalText", "forrás", "forrásszöveg", "msgid", "old");
        else Add("target", "targetText", "target_text", "translation", "translatedText", "localizedText", "localisedText", "fordítás", "célszöveg", "msgstr", "new");
        return aliases;
    }

    private static readonly HashSet<string> IdentifierAliases =
        ["id", "key", "identifier", "recordid", "stringid", "azonosito"];
    private static readonly HashSet<string> ContextAliases =
        ["context", "notes", "note", "comment", "megjegyzes", "kontextus"];

    private static int[] FieldIndexes(IReadOnlyList<TextFieldSpan> fields, HashSet<string> aliases) =>
        fields.Select((field, index) => (Label: NormalizeLabel(field.Value), Index: index))
            .Where(item => aliases.Contains(item.Label)).Select(item => item.Index).ToArray();

    private static int FindHeaderColumn(IReadOnlyList<TextFieldSpan> fields, HashSet<string> aliases) =>
        fields.Select((field, index) => (Label: NormalizeLabel(field.Value), Index: index))
            .Where(item => aliases.Contains(item.Label)).Select(item => item.Index).DefaultIfEmpty(-1).First();

    private static List<LabeledField> FindLabeledFields(IReadOnlyList<string> lines, HashSet<string> aliases)
    {
        var result = new List<LabeledField>();
        for (int index = 0; index < lines.Count; index++)
            if (TryParseLabeledField(lines[index], aliases, out TextFieldSpan? field))
                result.Add(new LabeledField(index, field));
        return result;
    }

    private static bool TryParseLabeledField(string line, HashSet<string> aliases, out TextFieldSpan field)
    {
        field = default!;
        int equals = line.IndexOf('=');
        int colon = line.IndexOf(':');
        int separator = equals < 0 ? colon : colon < 0 ? equals : Math.Min(equals, colon);
        int valueStart;
        if (separator > 0 && aliases.Contains(NormalizeLabel(line[..separator]))) valueStart = separator + 1;
        else
        {
            int labelStart = 0;
            while (labelStart < line.Length && IsHorizontalWhitespace(line[labelStart])) labelStart++;
            int labelEnd = labelStart;
            while (labelEnd < line.Length && !IsHorizontalWhitespace(line[labelEnd])) labelEnd++;
            if (labelEnd == labelStart || !aliases.Contains(NormalizeLabel(line[labelStart..labelEnd]))) return false;
            valueStart = labelEnd;
        }
        field = ParseValueField(line, valueStart, backslashEscapedQuotes: true);
        return true;
    }

    private static string FindAuxiliaryField(
        IReadOnlyList<string> lines, int start, int end, HashSet<string> aliases)
    {
        for (int index = Math.Max(0, start); index <= Math.Min(end, lines.Count - 1); index++)
            if (TryParseLabeledField(lines[index], aliases, out TextFieldSpan? field)) return field.Value;
        return string.Empty;
    }

    private static TextFieldSpan ParseValueField(string line, int valueStart, bool backslashEscapedQuotes)
    {
        int start = valueStart;
        while (start < line.Length && IsHorizontalWhitespace(line[start])) start++;
        int end = line.Length;
        while (end > start && IsHorizontalWhitespace(line[end - 1])) end--;
        string quote = end - start >= 2 && line[start] is '"' or '\'' && line[end - 1] == line[start]
            ? (backslashEscapedQuotes ? "\\" : string.Empty) + line[start] : string.Empty;
        int length = end - start;
        string value = ReadRoutedTextField(line, start, length, quote);
        return new TextFieldSpan(start, length, value, quote);
    }

    private static bool TryParseDelimitedLine(string line, char delimiter, out List<TextFieldSpan> fields)
    {
        fields = [];
        int fieldStart = 0;
        bool insideQuotes = false;
        for (int index = 0; index <= line.Length; index++)
        {
            if (index < line.Length && line[index] == '"')
            {
                if (insideQuotes && index + 1 < line.Length && line[index + 1] == '"') { index++; continue; }
                insideQuotes = !insideQuotes;
                continue;
            }
            if (index < line.Length && (line[index] != delimiter || insideQuotes)) continue;
            int start = fieldStart;
            while (start < index && IsHorizontalWhitespace(line[start])) start++;
            int end = index;
            while (end > start && IsHorizontalWhitespace(line[end - 1])) end--;
            string quote = end - start >= 2 && line[start] == '"' && line[end - 1] == '"' ? "\"" : string.Empty;
            try
            {
                fields.Add(new TextFieldSpan(start, end - start,
                    ReadRoutedTextField(line, start, end - start, quote), quote));
            }
            catch (InvalidDataException) { fields = []; return false; }
            fieldStart = index + 1;
        }
        if (insideQuotes) { fields = []; return false; }
        return fields.Count > 1;
    }

    private static bool HasAmbiguousSeparateTargetSignals(
        IReadOnlyList<string> lines, HashSet<string> sourceAliases, HashSet<string> targetAliases)
    {
        bool labeledSource = lines.Any(line => TryParseLabeledField(line, sourceAliases, out _));
        bool labeledTarget = lines.Any(line => TryParseLabeledField(line, targetAliases, out _));
        if (labeledSource && labeledTarget) return true;
        foreach (string line in lines.Take(100))
        foreach (char delimiter in new[] { '\t', ';', '|' })
            if (TryParseDelimitedLine(line, delimiter, out List<TextFieldSpan>? fields) &&
                FieldIndexes(fields, sourceAliases).Length > 0 && FieldIndexes(fields, targetAliases).Length > 0)
                return true;
        return false;
    }

    private static string NormalizeLabel(string value)
    {
        string decomposed = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormD);
        var output = new StringBuilder(decomposed.Length);
        foreach (char character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
                output.Append(char.ToLowerInvariant(character));
        return output.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool LooksLikeStableIdentifier(string value)
    {
        string candidate = (value ?? string.Empty).Trim();
        bool technicalShape = candidate.StartsWith('[') || candidate.Any(char.IsDigit) ||
            candidate.IndexOfAny(['_', '.', ':', '/', '\\', '-']) >= 0;
        return technicalShape && candidate.Length is >= 2 and <= 160 &&
            Regex.IsMatch(candidate, @"^\[?[A-Za-z_][A-Za-z0-9_.:\-/]{1,158}\]?$", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeTranslatableText(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Any(char.IsLetter) && !LooksLikeStableIdentifier(value.Trim());

    private static bool IsCommentLine(string line) =>
        line.TrimStart().StartsWith("#", StringComparison.Ordinal) ||
        line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    private static bool IsTableDivider(string line)
    {
        string compact = new(line.Where(character => !char.IsWhiteSpace(character)).ToArray());
        return compact.Length > 0 && compact.All(character => character is '-' or ':' or '|');
    }

    private static bool IsHorizontalWhitespace(char value) =>
        value is ' ' or '\t' or '\u00A0' or '\u1680' or '\u202F' or '\u205F' or '\u3000' ||
        value is >= '\u2000' and <= '\u200A';

    private sealed record TextFieldSpan(int Start, int Length, string Value, string Quote);
    private sealed record DetectedTextRow(
        int SourceLineIndex, TextFieldSpan SourceField, int TargetLineIndex, TextFieldSpan TargetField,
        string ExternalId, string Context);
    private sealed record DelimitedLayout(
        char Delimiter, int ColumnCount, int HeaderLine, int SourceColumn, int TargetColumn, int IdColumn, int ContextColumn,
        string Description);
    private sealed record LabeledField(int LineIndex, TextFieldSpan Field);
    private sealed record LabeledPair(LabeledField Source, LabeledField Target, int Start, int End);

    private static async Task<string> ReadJsonLinesAsync(
        string path, SourceDocumentInfo sourceDocument, Encoding encoding, List<ImportedUnit> output,
        string version, int baseOrder, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(path, encoding, true, 1 << 16);
        int lineNumber = 0;
        int recordOrder = baseOrder;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            int sourceRecordIndex = lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string type = String(root, "type");
            if (type.Equals("manifest", StringComparison.OrdinalIgnoreCase))
            {
                string incomingVersion = String(root, "sourceVersion");
                if (!string.IsNullOrWhiteSpace(incomingVersion)) version = incomingVersion;
                continue;
            }

            string source = String(root, "sourceText", "source", "text");
            if (source.Length == 0 && !Has(root, "sourceText") && !Has(root, "source") && !Has(root, "text")) continue;
            string id = String(root, "id", "key");
            bool stable = !string.IsNullOrWhiteSpace(id);
            if (!stable) id = $"jsonl:{Path.GetFileName(path).ToLowerInvariant()}:{Hash(source)[..20]}:{lineNumber}";
            string targetFieldName = PropertyName(root, "currentTargetText", "targetText", "translation");
            string target = string.IsNullOrEmpty(targetFieldName) ? string.Empty : String(root, targetFieldName);
            string context = String(root, "context", "notes");
            string routing = Try(root, "routing", out JsonElement routingElement) ? routingElement.GetRawText() : "null";
            output.Add(new ImportedUnit(id, stable, Path.GetFileName(path), source, Hash(source), target, context, routing, recordOrder++)
            {
                SourceDocumentId = sourceDocument.Id,
                SourceDocumentKey = NormalizeDocumentKey(sourceDocument.OutputRelativePath, sourceDocument.OriginalFileName),
                SourceRecordIndex = sourceRecordIndex,
                JsonTargetFieldName = string.IsNullOrEmpty(targetFieldName) ? "currentTargetText" : targetFieldName
            });
        }
        return version;
    }

    public static UpdateSummary Merge(
        ProjectDocument project, ImportedSource source, bool update, bool installSourceDocuments = true)
    {
        List<TranslationUnit> history = project.Units.Select(CloneUnit).ToList();
        var originallyActive = new HashSet<TranslationUnit>(history.Where(unit => unit.Active));
        var consumed = new HashSet<TranslationUnit>();
        foreach (TranslationUnit unit in history)
        {
            if (unit.Status == UnitStatus.Waiting) unit.Status = UnitStatus.Pending;
            if (unit.StatusBeforeArchive == UnitStatus.Waiting)
                unit.StatusBeforeArchive = UnitStatus.Pending;
        }

        Dictionary<string, string> oldDocumentKeys = BuildDocumentKeys(project.SourceDocuments);
        Dictionary<string, string> newDocumentKeys = BuildIncomingDocumentKeys(
            project.SourceDocuments, source.SourceDocuments);
        var stablePools = new Dictionary<StableMatchKey, List<TranslationUnit>>(StableMatchKeyComparer.Instance);
        var legacyStableAliasPools = new Dictionary<StableMatchKey, List<TranslationUnit>>(StableMatchKeyComparer.Instance);
        var stableGlobalPools = new Dictionary<string, List<TranslationUnit>>(StringComparer.Ordinal);
        foreach (TranslationUnit unit in history.Where(unit => unit.HasStableExternalId))
        {
            string externalId = string.IsNullOrWhiteSpace(unit.ExternalId) ? unit.Id : unit.ExternalId;
            string documentKey = ExistingDocumentKey(unit, oldDocumentKeys);
            var key = new StableMatchKey(documentKey, externalId);
            if (!stablePools.TryGetValue(key, out List<TranslationUnit>? pool)) stablePools[key] = pool = [];
            pool.Add(unit);
            if (!stableGlobalPools.TryGetValue(externalId, out List<TranslationUnit>? globalPool))
                stableGlobalPools[externalId] = globalPool = [];
            globalPool.Add(unit);
            if (string.IsNullOrWhiteSpace(unit.ExternalId) &&
                TryLegacyExternalIdAlias(unit.Id, out string legacyExternalId))
            {
                var aliasKey = new StableMatchKey(documentKey, legacyExternalId);
                if (!legacyStableAliasPools.TryGetValue(aliasKey, out List<TranslationUnit>? aliasPool))
                    legacyStableAliasPools[aliasKey] = aliasPool = [];
                aliasPool.Add(unit);
                if (!stableGlobalPools.TryGetValue(legacyExternalId, out List<TranslationUnit>? aliasGlobalPool))
                    stableGlobalPools[legacyExternalId] = aliasGlobalPool = [];
                aliasGlobalPool.Add(unit);
            }
        }
        StableMatchKey[] duplicateIncomingStableIds = source.Units.Where(unit => unit.HasStableExternalId)
            .Select(unit => new StableMatchKey(IncomingDocumentKey(unit, newDocumentKeys), unit.Id))
            .GroupBy(key => key, StableMatchKeyComparer.Instance)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).Take(10).ToArray();
        if (duplicateIncomingStableIds.Length > 0)
            throw new InvalidDataException("Ugyanabban a forrásfájlban ugyanaz a tartós ID többször szerepel: " +
                string.Join(", ", duplicateIncomingStableIds.Select(key => key.ExternalId)));
        Dictionary<ImportedUnit, TranslationUnit> stableAssignments = BuildStableAssignments(
            stablePools, legacyStableAliasPools, stableGlobalPools,
            source.Units.Where(unit => unit.HasStableExternalId),
            source.Units, oldDocumentKeys, newDocumentKeys);
        var semanticPools = new Dictionary<SemanticMatchKey, List<TranslationUnit>>(SemanticMatchKeyComparer.Instance);
        foreach (TranslationUnit unit in history.Where(unit => !unit.HasStableExternalId))
        {
            string documentKey = ExistingDocumentKey(unit, oldDocumentKeys);
            var key = new SemanticMatchKey(documentKey, TranslationMatchText(unit.SourceText));
            if (!semanticPools.TryGetValue(key, out List<TranslationUnit>? pool)) semanticPools[key] = pool = [];
            pool.Add(unit);
        }
        (Dictionary<ImportedUnit, TranslationUnit> semanticAssignments,
            HashSet<ImportedUnit> ambiguousSemanticMatches,
            Dictionary<SemanticMatchKey, SemanticReusePlan> semanticReusePlans) = BuildSemanticAssignments(
                semanticPools, source.Units.Where(unit => !unit.HasStableExternalId), newDocumentKeys);

        var mergedActive = new List<TranslationUnit>(source.Units.Count);
        var usedIds = history.Select(unit => unit.Id).ToHashSet(StringComparer.Ordinal);
        var protection = new TokenProtectionService();
        int unchanged = 0, moved = 0, added = 0, changed = 0, conflicts = 0;

        foreach (ImportedUnit incoming in source.Units.OrderBy(unit => unit.Order))
        {
            string incomingDocumentKey = IncomingDocumentKey(incoming, newDocumentKeys);
            TranslationUnit? matched = null;
            bool semanticMatch = !incoming.HasStableExternalId;

            if (incoming.HasStableExternalId)
                stableAssignments.TryGetValue(incoming, out matched);
            else
            {
                var key = new SemanticMatchKey(incomingDocumentKey, TranslationMatchText(incoming.SourceText));
                if (semanticPools.ContainsKey(key))
                    semanticAssignments.TryGetValue(incoming, out matched);
            }

            if (matched is null)
            {
                TranslationUnit created = NewUnit(incoming, UniqueId(incoming.Id, usedIds), incomingDocumentKey);
                bool createdTranslationIsRaw = !string.IsNullOrEmpty(created.TranslationMasked);
                if (semanticMatch)
                {
                    var semanticKey = new SemanticMatchKey(incomingDocumentKey,
                        TranslationMatchText(incoming.SourceText));
                    if (semanticReusePlans.TryGetValue(semanticKey, out SemanticReusePlan? reuse))
                    {
                        (string rebased, bool safe) = RebaseFliPrefix(
                            reuse.Donor.SourceText, incoming.SourceText, reuse.Donor.TranslationMasked);
                        created.TranslationMasked = rebased;
                        created.Status = safe ? reuse.Status : UnitStatus.NeedsReview;
                        createdTranslationIsRaw = false;
                        if (reuse.IsConflict || !safe) conflicts++;
                    }
                }
                PrepareForActiveProfile(created, project.ActiveTokenProfile, protection, createdTranslationIsRaw);
                mergedActive.Add(created);
                added++;
                continue;
            }

            consumed.Add(matched);
            bool wasActive = originallyActive.Contains(matched);
            bool exactSource = string.Equals(matched.SourceHash, incoming.SourceHash, StringComparison.Ordinal);
            bool samePosition = matched.SourceRecordIndex == incoming.SourceRecordIndex &&
                matched.TextTargetRecordIndex == incoming.TextTargetRecordIndex &&
                matched.TextSourceStart == incoming.TextSourceStart &&
                matched.TextTargetStart == incoming.TextTargetStart;
            string previousSource = matched.SourceText;
            string previousTarget = matched.CurrentTargetTextRaw;
            string previousTranslation = matched.TranslationMasked;

            matched.HasStableExternalId = incoming.HasStableExternalId;
            matched.ExternalId = incoming.HasStableExternalId ? incoming.Id : string.Empty;
            matched.SourceFile = incoming.SourceFile;
            matched.SourceDocumentId = incoming.SourceDocumentId;
            matched.SourceDocumentKey = incomingDocumentKey;
            matched.SourceRecordIndex = incoming.SourceRecordIndex;
            matched.JsonTargetFieldName = incoming.JsonTargetFieldName;
            matched.TextLayoutKind = incoming.TextLayoutKind;
            matched.TextTargetRecordIndex = incoming.TextTargetRecordIndex;
            matched.TextSourceStart = incoming.TextSourceStart;
            matched.TextSourceLength = incoming.TextSourceLength;
            matched.TextSourceQuote = incoming.TextSourceQuote;
            matched.TextTargetStart = incoming.TextTargetStart;
            matched.TextTargetLength = incoming.TextTargetLength;
            matched.TextTargetQuote = incoming.TextTargetQuote;
            matched.TextDelimiter = incoming.TextDelimiter;
            matched.SourceText = incoming.SourceText;
            matched.SourceHash = incoming.SourceHash;
            matched.Context = incoming.Context;
            matched.RoutingJson = incoming.RoutingJson;
            matched.Order = incoming.Order;
            matched.Active = true;

            bool translationIsRaw = false;
            if (incoming.HasStableExternalId && !exactSource)
            {
                matched.PreviousTranslationMasked = previousTranslation;
                matched.TranslationMasked = string.Empty;
                matched.Status = string.IsNullOrWhiteSpace(incoming.SourceText) ? UnitStatus.Skipped : UnitStatus.Pending;
                changed++;
            }
            else
            {
                if (matched.Status == UnitStatus.Archived)
                {
                    matched.Status = matched.StatusBeforeArchive ??
                        (string.IsNullOrEmpty(previousTranslation) ? UnitStatus.Pending : UnitStatus.Completed);
                    matched.StatusBeforeArchive = null;
                }
                if (wasActive && exactSource && samePosition) unchanged++;
                else moved++;
            }

            if (!string.IsNullOrWhiteSpace(incoming.CurrentTargetText) &&
                (string.IsNullOrWhiteSpace(matched.TranslationMasked) ||
                 !incoming.CurrentTargetText.Equals(previousTarget, StringComparison.Ordinal)))
            {
                if (!string.IsNullOrWhiteSpace(matched.TranslationMasked))
                {
                    matched.ConflictTargetTextRaw = incoming.CurrentTargetText;
                    matched.Status = UnitStatus.NeedsReview;
                    conflicts++;
                }
                else
                {
                    matched.TranslationMasked = incoming.CurrentTargetText;
                    matched.Status = UnitStatus.NeedsReview;
                    translationIsRaw = true;
                }
            }
            if (semanticMatch && !string.IsNullOrEmpty(matched.TranslationMasked))
            {
                (string rebased, bool safe) = RebaseFliPrefix(previousSource, incoming.SourceText, matched.TranslationMasked);
                matched.TranslationMasked = rebased;
                if (!safe && matched.Status == UnitStatus.Completed)
                {
                    matched.Status = UnitStatus.NeedsReview;
                    conflicts++;
                }
            }
            matched.CurrentTargetTextRaw = incoming.CurrentTargetText;
            PrepareForActiveProfile(matched, project.ActiveTokenProfile, protection, translationIsRaw);
            if (ambiguousSemanticMatches.Contains(incoming) &&
                !string.IsNullOrEmpty(matched.TranslationMasked) && matched.Status != UnitStatus.NeedsReview)
            {
                matched.Status = UnitStatus.NeedsReview;
                conflicts++;
            }
            mergedActive.Add(matched);
        }

        int removed = 0;
        var archived = new List<TranslationUnit>(Math.Max(0, history.Count - consumed.Count));
        foreach (TranslationUnit unit in history)
        {
            if (consumed.Contains(unit)) continue;
            if (originallyActive.Contains(unit))
            {
                unit.Active = false;
                unit.StatusBeforeArchive = unit.Status;
                unit.Status = UnitStatus.Archived;
                removed++;
            }
            archived.Add(unit);
        }

        List<SourceDocumentInfo>? installedDocuments = installSourceDocuments && source.SourceDocuments.Count > 0
            ? InstallSourceDocuments(project, ReconcileSourceDocuments(source.SourceDocuments, newDocumentKeys))
            : null;
        project.Units = mergedActive.Concat(archived.OrderBy(unit => unit.Order)).ToList();
        project.PendingBatches.Clear();
        project.SourceFiles = source.Files.ToList();
        if (installedDocuments is not null) project.SourceDocuments = installedDocuments;
        if (!string.IsNullOrWhiteSpace(source.SourceVersion)) project.SourceVersion = source.SourceVersion;
        project.ModifiedUtc = DateTime.UtcNow;
        return new UpdateSummary(unchanged, moved, added, changed, removed, conflicts);
    }

    private static TranslationUnit NewUnit(
        ImportedUnit incoming, string? id = null, string? sourceDocumentKey = null) => new()
    {
        Id = id ?? incoming.Id,
        HasStableExternalId = incoming.HasStableExternalId,
        ExternalId = incoming.HasStableExternalId ? incoming.Id : string.Empty,
        SourceFile = incoming.SourceFile,
        SourceDocumentId = incoming.SourceDocumentId,
        SourceDocumentKey = sourceDocumentKey ?? incoming.SourceDocumentKey,
        SourceRecordIndex = incoming.SourceRecordIndex,
        JsonTargetFieldName = incoming.JsonTargetFieldName,
        TextLayoutKind = incoming.TextLayoutKind,
        TextTargetRecordIndex = incoming.TextTargetRecordIndex,
        TextSourceStart = incoming.TextSourceStart,
        TextSourceLength = incoming.TextSourceLength,
        TextSourceQuote = incoming.TextSourceQuote,
        TextTargetStart = incoming.TextTargetStart,
        TextTargetLength = incoming.TextTargetLength,
        TextTargetQuote = incoming.TextTargetQuote,
        TextDelimiter = incoming.TextDelimiter,
        SourceText = incoming.SourceText,
        SourceHash = incoming.SourceHash,
        Context = incoming.Context,
        RoutingJson = incoming.RoutingJson,
        CurrentTargetTextRaw = incoming.CurrentTargetText,
        TranslationMasked = incoming.CurrentTargetText,
        Order = incoming.Order,
        Active = true,
        Status = string.IsNullOrWhiteSpace(incoming.SourceText) ? UnitStatus.Skipped :
            string.IsNullOrWhiteSpace(incoming.CurrentTargetText) ? UnitStatus.Pending : UnitStatus.NeedsReview
    };

    public static string TranslationMatchText(string sourceText)
    {
        sourceText ??= string.Empty;
        if (sourceText.Length < 15 || sourceText[0] != '[' || sourceText[1] != 'F' ||
            sourceText[2] != 'L' || sourceText[3] != 'I' || sourceText[4] != '_' ||
            sourceText[13] != ']' || sourceText[14] != '\t') return sourceText;
        for (int index = 5; index <= 12; index++)
            if (sourceText[index] is < '0' or > '9') return sourceText;
        return sourceText[15..];
    }

    private static (string Translation, bool Safe) RebaseFliPrefix(
        string previousSource, string currentSource, string translation)
    {
        if (!TryGetFliPrefix(currentSource, out string currentPrefix))
            return (translation, true);
        if (translation.StartsWith(currentPrefix, StringComparison.Ordinal) ||
            translation.StartsWith("⟦CONTROL_", StringComparison.Ordinal))
            return (translation, true);
        if (TryGetFliPrefix(previousSource, out string previousPrefix) &&
            translation.StartsWith(previousPrefix, StringComparison.Ordinal))
            return (currentPrefix + translation[previousPrefix.Length..], true);
        if (!string.IsNullOrEmpty(previousPrefix))
        {
            string previousId = previousPrefix[..^1];
            if (translation.StartsWith(previousId, StringComparison.Ordinal))
                return (currentPrefix + translation[previousId.Length..].TrimStart('\t'), true);
        }
        if (TryGetFliPrefix(translation, out string translatedPrefix))
            return (currentPrefix + translation[translatedPrefix.Length..], true);
        return (currentPrefix + translation.TrimStart('\t'), true);
    }

    private static bool TryGetFliPrefix(string value, out string prefix)
    {
        string body = TranslationMatchText(value);
        if (body.Length == value.Length)
        {
            prefix = string.Empty;
            return false;
        }
        prefix = value[..15];
        return true;
    }

    private static Dictionary<ImportedUnit, TranslationUnit> BuildStableAssignments(
        IReadOnlyDictionary<StableMatchKey, List<TranslationUnit>> scopedPools,
        IReadOnlyDictionary<StableMatchKey, List<TranslationUnit>> legacyAliasPools,
        IReadOnlyDictionary<string, List<TranslationUnit>> globalPools,
        IEnumerable<ImportedUnit> incomingStableUnits,
        IEnumerable<ImportedUnit> allIncomingUnits,
        IReadOnlyDictionary<string, string> oldDocumentKeys,
        IReadOnlyDictionary<string, string> newDocumentKeys)
    {
        ImportedUnit[] incoming = incomingStableUnits.OrderBy(unit => unit.Order).ToArray();
        var assignments = new Dictionary<ImportedUnit, TranslationUnit>();
        var reserved = new HashSet<TranslationUnit>();

        // Először minden megmaradt dokumentum saját (dokumentum, külső ID) párját foglaljuk le.
        // Így egy korábban sorra kerülő új fájl nem veheti el egy későbbi régi fájl fordítását.
        foreach (ImportedUnit unit in incoming)
        {
            var key = new StableMatchKey(IncomingDocumentKey(unit, newDocumentKeys), unit.Id);
            if (!scopedPools.TryGetValue(key, out List<TranslationUnit>? candidates)) continue;
            TranslationUnit? selected = SelectStableCandidate(candidates, reserved, unit.SourceHash);
            if (selected is null) continue;
            assignments[unit] = selected;
            reserved.Add(selected);
        }

        // Az ExternalId mező előtti projektek az azonos külső ID második példányát
        // belső :v2/:v3 utótaggal tárolták. Csak az exact kör után használjuk ezt az aliast.
        foreach (ImportedUnit unit in incoming.Where(unit => !assignments.ContainsKey(unit)))
        {
            var key = new StableMatchKey(IncomingDocumentKey(unit, newDocumentKeys), unit.Id);
            if (!legacyAliasPools.TryGetValue(key, out List<TranslationUnit>? candidates)) continue;
            TranslationUnit? selected = SelectStableCandidate(candidates, reserved, unit.SourceHash);
            if (selected is null) continue;
            assignments[unit] = selected;
            reserved.Add(selected);
        }

        var incomingDocumentKeys = allIncomingUnits
            .Select(unit => IncomingDocumentKey(unit, newDocumentKeys))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, ImportedUnit> group in incoming
                     .Where(unit => !assignments.ContainsKey(unit))
                     .GroupBy(unit => unit.Id, StringComparer.Ordinal))
        {
            ImportedUnit[] unmatchedIncoming = group.ToArray();
            if (unmatchedIncoming.Length != 1 ||
                !globalPools.TryGetValue(group.Key, out List<TranslationUnit>? globalCandidates)) continue;
            TranslationUnit[] donors = globalCandidates.Where(candidate => !reserved.Contains(candidate) &&
                    !incomingDocumentKeys.Contains(ExistingDocumentKey(candidate, oldDocumentKeys)))
                .ToArray();
            if (donors.Length != 1) continue;
            assignments[unmatchedIncoming[0]] = donors[0];
            reserved.Add(donors[0]);
        }
        return assignments;
    }

    private static bool TryLegacyExternalIdAlias(string internalId, out string externalId)
    {
        int marker = internalId.LastIndexOf(":v", StringComparison.Ordinal);
        if (marker > 0 && int.TryParse(internalId.AsSpan(marker + 2), out int suffix) && suffix >= 2)
        {
            externalId = internalId[..marker];
            return true;
        }
        externalId = string.Empty;
        return false;
    }

    private static TranslationUnit? SelectStableCandidate(
        IEnumerable<TranslationUnit> candidates, HashSet<TranslationUnit> consumed, string incomingSourceHash) =>
        candidates.Where(candidate => !consumed.Contains(candidate))
            .OrderByDescending(candidate => string.Equals(
                candidate.SourceHash, incomingSourceHash, StringComparison.Ordinal))
            .ThenByDescending(candidate => !string.IsNullOrEmpty(candidate.TranslationMasked))
            .ThenByDescending(StableTranslationQuality)
            .ThenByDescending(candidate => candidate.Active)
            .ThenByDescending(candidate => candidate.UpdatedUtc)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    private static int StableTranslationQuality(TranslationUnit unit) => EffectiveStatus(unit) switch
    {
        UnitStatus.Completed => 5,
        UnitStatus.Skipped => 4,
        UnitStatus.NeedsReview => 3,
        UnitStatus.Pending => 2,
        UnitStatus.Archived => 1,
        _ => 0
    };

    private static (Dictionary<ImportedUnit, TranslationUnit> Assignments,
        HashSet<ImportedUnit> Ambiguous,
        Dictionary<SemanticMatchKey, SemanticReusePlan> ReusePlans)
        BuildSemanticAssignments(
            IReadOnlyDictionary<SemanticMatchKey, List<TranslationUnit>> oldPools,
            IEnumerable<ImportedUnit> incomingUnits,
            IReadOnlyDictionary<string, string> newDocumentKeys)
    {
        var incomingGroups = new Dictionary<SemanticMatchKey, List<ImportedUnit>>(SemanticMatchKeyComparer.Instance);
        foreach (ImportedUnit incoming in incomingUnits)
        {
            var key = new SemanticMatchKey(IncomingDocumentKey(incoming, newDocumentKeys),
                TranslationMatchText(incoming.SourceText));
            if (!incomingGroups.TryGetValue(key, out List<ImportedUnit>? group)) incomingGroups[key] = group = [];
            group.Add(incoming);
        }

        var assignments = new Dictionary<ImportedUnit, TranslationUnit>();
        var ambiguous = new HashSet<ImportedUnit>();
        var reusePlans = new Dictionary<SemanticMatchKey, SemanticReusePlan>(SemanticMatchKeyComparer.Instance);
        foreach ((SemanticMatchKey key, List<ImportedUnit> incomingGroup) in incomingGroups)
        {
            if (!oldPools.TryGetValue(key, out List<TranslationUnit>? oldGroup) || oldGroup.Count == 0) continue;
            ImportedUnit[] orderedIncoming = incomingGroup.OrderBy(unit => unit.SourceRecordIndex)
                .ThenBy(unit => unit.Order).ToArray();
            var remainingIncoming = orderedIncoming.ToHashSet();
            var remainingOld = oldGroup.ToHashSet();
            bool anyReusableTranslation = oldGroup.Any(unit => !string.IsNullOrEmpty(unit.TranslationMasked));
            bool groupHasDifferentTranslations = HasDifferentTranslations(oldGroup);
            bool multiplicityConflict = groupHasDifferentTranslations && oldGroup.Count != orderedIncoming.Length;

            // A teljesen azonos nyers sor ugyanazon a fizikai helyen biztos egyezés,
            // még akkor is, ha más, azonos angol törzsű sor eltérő fordítást kapott.
            var exactPositionPools = oldGroup.GroupBy(unit => (unit.SourceHash, unit.SourceRecordIndex))
                .ToDictionary(group => group.Key, group => OrderedCandidates(group).ToList());
            foreach (ImportedUnit incoming in orderedIncoming)
            {
                if (!exactPositionPools.TryGetValue((incoming.SourceHash, incoming.SourceRecordIndex),
                        out List<TranslationUnit>? candidates)) continue;
                TranslationUnit? selected = candidates.FirstOrDefault(candidate =>
                    remainingOld.Contains(candidate) &&
                    (!anyReusableTranslation || !string.IsNullOrEmpty(candidate.TranslationMasked)));
                if (selected is null) continue;
                assignments[incoming] = selected;
                remainingIncoming.Remove(incoming);
                remainingOld.Remove(selected);
                if (multiplicityConflict) ambiguous.Add(incoming);
            }

            // A megmaradt, teljesen azonos nyers sorokat sorrendben párosítjuk.
            // Ha eltérő magyar változatok mozoghattak el, kézi ellenőrzést kérünk.
            Dictionary<string, TranslationUnit[]> remainingOldByHash = remainingOld
                .GroupBy(unit => unit.SourceHash, StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => group.OrderBy(unit => unit.SourceRecordIndex)
                        .ThenBy(unit => unit.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            foreach (IGrouping<string, ImportedUnit> sourceGroup in remainingIncoming
                         .GroupBy(unit => unit.SourceHash, StringComparer.Ordinal).ToArray())
            {
                ImportedUnit[] incomingBySource = sourceGroup.OrderBy(unit => unit.SourceRecordIndex).ToArray();
                if (!remainingOldByHash.TryGetValue(sourceGroup.Key, out TranslationUnit[]? sourceCandidates))
                    continue;
                TranslationUnit[] oldBySource = sourceCandidates.Where(candidate =>
                        remainingOld.Contains(candidate) &&
                        (!anyReusableTranslation || !string.IsNullOrEmpty(candidate.TranslationMasked)))
                    .Take(incomingBySource.Length).ToArray();
                int pairCount = Math.Min(incomingBySource.Length, oldBySource.Length);
                bool uncertain = multiplicityConflict || groupHasDifferentTranslations && Enumerable.Range(0, pairCount)
                    .Any(index => incomingBySource[index].SourceRecordIndex != oldBySource[index].SourceRecordIndex);
                for (int index = 0; index < pairCount; index++)
                {
                    assignments[incomingBySource[index]] = oldBySource[index];
                    remainingIncoming.Remove(incomingBySource[index]);
                    remainingOld.Remove(oldBySource[index]);
                    if (uncertain) ambiguous.Add(incomingBySource[index]);
                }
            }

            // Az FLI-prefix újraszámozásával megmaradt törzseket monoton sorrendben kötjük vissza.
            TranslationUnit[] semanticOld = OrderedCandidates(remainingOld)
                .Take(Math.Min(remainingOld.Count, remainingIncoming.Count))
                .OrderBy(unit => unit.SourceRecordIndex).ThenBy(unit => unit.Id, StringComparer.Ordinal).ToArray();
            ImportedUnit[] semanticIncoming = remainingIncoming.OrderBy(unit => unit.SourceRecordIndex)
                .ThenBy(unit => unit.Order).Take(semanticOld.Length).ToArray();
            bool semanticUncertain = multiplicityConflict || HasDifferentTranslations(semanticOld);
            for (int index = 0; index < semanticOld.Length; index++)
            {
                assignments[semanticIncoming[index]] = semanticOld[index];
                if (semanticUncertain) ambiguous.Add(semanticIncoming[index]);
            }

            SemanticReusePlan? reusePlan = BuildReusePlan(oldGroup);
            if (reusePlan is not null) reusePlans[key] = reusePlan;
        }
        return (assignments, ambiguous, reusePlans);
    }

    private static IOrderedEnumerable<TranslationUnit> OrderedCandidates(IEnumerable<TranslationUnit> units) =>
        units.OrderByDescending(TranslationQuality)
            .ThenByDescending(unit => unit.Active)
            .ThenBy(unit => unit.SourceRecordIndex)
            .ThenBy(unit => unit.Id, StringComparer.Ordinal);

    private static bool HasDifferentTranslations(IEnumerable<TranslationUnit> units) =>
        units.Where(unit => !string.IsNullOrEmpty(unit.TranslationMasked))
            .Select(unit => unit.TranslationMasked).Distinct(StringComparer.Ordinal).Skip(1).Any();

    private static SemanticReusePlan? BuildReusePlan(IEnumerable<TranslationUnit> candidates)
    {
        TranslationUnit[] translated = OrderedCandidates(candidates)
            .Where(unit => !string.IsNullOrEmpty(unit.TranslationMasked)).ToArray();
        if (translated.Length == 0) return null;
        TranslationUnit[] safe = translated.Where(unit => EffectiveStatus(unit) is UnitStatus.Completed or UnitStatus.Skipped)
            .ToArray();
        TranslationUnit[] donors = safe.Length > 0 ? safe : translated;
        string[] alternatives = donors.Select(unit => unit.TranslationMasked)
            .Distinct(StringComparer.Ordinal).Take(2).ToArray();
        TranslationUnit donor = donors[0];
        bool conflict = safe.Length == 0 || alternatives.Length > 1;
        UnitStatus status = conflict ? UnitStatus.NeedsReview : EffectiveStatus(donor);
        return new SemanticReusePlan(donor, status, conflict);
    }

    private static UnitStatus EffectiveStatus(TranslationUnit unit) =>
        unit.Status == UnitStatus.Archived ? unit.StatusBeforeArchive ?? UnitStatus.Archived : unit.Status;

    private static int TranslationQuality(TranslationUnit unit)
    {
        if (string.IsNullOrEmpty(unit.TranslationMasked)) return 0;
        UnitStatus effectiveStatus = EffectiveStatus(unit);
        int quality = effectiveStatus switch
        {
            UnitStatus.Completed => 60,
            UnitStatus.Archived => 55,
            UnitStatus.NeedsReview => 45,
            UnitStatus.Skipped => 35,
            _ => 30
        };
        return unit.Active ? quality + 100 : quality;
    }

    private static long Distance(int left, int right) => Math.Abs((long)left - right);

    private static string UniqueId(string requested, HashSet<string> used)
    {
        string stem = string.IsNullOrWhiteSpace(requested) ? "txt:" + Guid.NewGuid().ToString("N") : requested;
        if (used.Add(stem)) return stem;
        int suffix = 2;
        string candidate;
        do candidate = $"{stem}:v{suffix++}"; while (!used.Add(candidate));
        return candidate;
    }

    private static void PrepareForActiveProfile(
        TranslationUnit unit, TokenProfile? profile, TokenProtectionService protection, bool translationIsRaw)
    {
        if (profile is null)
        {
            unit.MaskedSource = string.Empty;
            unit.MaskedContext = string.Empty;
            unit.TokenSignature = string.Empty;
            return;
        }

        MaskResult sourceMap = protection.Mask(unit.SourceText, profile);
        unit.MaskedSource = sourceMap.Text;
        unit.MaskedContext = string.IsNullOrEmpty(unit.Context)
            ? string.Empty : protection.Mask(unit.Context, profile).Text;
        unit.TokenSignature = sourceMap.Signature;
        if (string.IsNullOrEmpty(unit.TranslationMasked)) return;

        string candidate = translationIsRaw
            ? protection.MaskExistingTranslation(unit.TranslationMasked, profile, sourceMap.Tokens)
            : unit.TranslationMasked;
        candidate = TextStructureGuard.Preserve(sourceMap.Text, candidate);
        unit.TranslationMasked = candidate;
        if ((!protection.ValidateMarkers(sourceMap.Text, candidate, out _) ||
             !TextStructureGuard.Validate(sourceMap.Text, candidate, out _)) &&
            unit.Status == UnitStatus.Completed)
            unit.Status = UnitStatus.NeedsReview;
    }

    private static Dictionary<string, string> BuildDocumentKeys(IEnumerable<SourceDocumentInfo> documents) =>
        documents.GroupBy(document => document.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => NormalizeDocumentKey(group.First().OutputRelativePath, group.First().OriginalFileName),
                StringComparer.Ordinal);

    private static Dictionary<string, string> BuildIncomingDocumentKeys(
        IEnumerable<SourceDocumentInfo> existingDocuments,
        IEnumerable<SourceDocumentInfo> incomingDocuments)
    {
        Dictionary<string, string> oldByPath = existingDocuments
            .Where(document => !string.IsNullOrWhiteSpace(document.OriginalPath))
            .GroupBy(document => Path.GetFullPath(document.OriginalPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => NormalizeDocumentKey(group.First().OutputRelativePath, group.First().OriginalFileName),
                StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SourceDocumentInfo document in incomingDocuments)
        {
            string path = string.IsNullOrWhiteSpace(document.OriginalPath)
                ? string.Empty : Path.GetFullPath(document.OriginalPath);
            result[document.Id] = path.Length > 0 && oldByPath.TryGetValue(path, out string? oldKey)
                ? oldKey : NormalizeDocumentKey(document.OutputRelativePath, document.OriginalFileName);
        }
        return result;
    }

    private static IReadOnlyList<SourceDocumentInfo> ReconcileSourceDocuments(
        IEnumerable<SourceDocumentInfo> documents,
        IReadOnlyDictionary<string, string> documentKeys) =>
        documents.Select(document => new SourceDocumentInfo
        {
            Id = document.Id,
            ContentHash = document.ContentHash,
            OriginalPath = document.OriginalPath,
            OriginalFileName = document.OriginalFileName,
            OutputRelativePath = documentKeys.GetValueOrDefault(document.Id,
                NormalizeDocumentKey(document.OutputRelativePath, document.OriginalFileName)),
            SnapshotRelativePath = document.SnapshotRelativePath,
            Format = document.Format,
            TextLayoutKind = document.TextLayoutKind,
            TextLayoutDescription = document.TextLayoutDescription,
            CodePage = document.CodePage,
            HasBom = document.HasBom
        }).ToArray();

    private static string ExistingDocumentKey(TranslationUnit unit, IReadOnlyDictionary<string, string> documentKeys)
    {
        if (!string.IsNullOrWhiteSpace(unit.SourceDocumentKey)) return unit.SourceDocumentKey;
        if (!string.IsNullOrWhiteSpace(unit.SourceDocumentId) &&
            documentKeys.TryGetValue(unit.SourceDocumentId, out string? key)) return key;
        return NormalizeDocumentKey(unit.SourceFile, unit.SourceFile);
    }

    private static string IncomingDocumentKey(ImportedUnit unit, IReadOnlyDictionary<string, string> documentKeys)
    {
        if (!string.IsNullOrWhiteSpace(unit.SourceDocumentId) &&
            documentKeys.TryGetValue(unit.SourceDocumentId, out string? key)) return key;
        if (!string.IsNullOrWhiteSpace(unit.SourceDocumentKey)) return unit.SourceDocumentKey;
        return NormalizeDocumentKey(unit.SourceFile, unit.SourceFile);
    }

    private static string NormalizeDocumentKey(string? preferred, string? fallback)
    {
        string value = string.IsNullOrWhiteSpace(preferred) ? fallback ?? string.Empty : preferred;
        return value.Replace('\\', '/').TrimStart('/');
    }

    private static TranslationUnit CloneUnit(TranslationUnit source)
    {
        var clone = new TranslationUnit
        {
            Id = source.Id,
            HasStableExternalId = source.HasStableExternalId,
            ExternalId = source.ExternalId,
            SourceFile = source.SourceFile,
            SourceDocumentId = source.SourceDocumentId,
            SourceDocumentKey = source.SourceDocumentKey,
            SourceRecordIndex = source.SourceRecordIndex,
            JsonTargetFieldName = source.JsonTargetFieldName,
            TextLayoutKind = source.TextLayoutKind,
            TextTargetRecordIndex = source.TextTargetRecordIndex,
            TextSourceStart = source.TextSourceStart,
            TextSourceLength = source.TextSourceLength,
            TextSourceQuote = source.TextSourceQuote,
            TextTargetStart = source.TextTargetStart,
            TextTargetLength = source.TextTargetLength,
            TextTargetQuote = source.TextTargetQuote,
            TextDelimiter = source.TextDelimiter,
            SourceText = source.SourceText,
            MaskedSource = source.MaskedSource,
            SourceHash = source.SourceHash,
            TokenSignature = source.TokenSignature,
            Context = source.Context,
            MaskedContext = source.MaskedContext,
            RoutingJson = source.RoutingJson,
            CurrentTargetTextRaw = source.CurrentTargetTextRaw,
            ConflictTargetTextRaw = source.ConflictTargetTextRaw,
            PreviousTranslationMasked = source.PreviousTranslationMasked,
            LastAiTranslationMasked = source.LastAiTranslationMasked,
            ReviewNotes = source.ReviewNotes,
            TranslationOrigin = source.TranslationOrigin,
            LastExportedHash = source.LastExportedHash,
            Order = source.Order,
            Active = source.Active,
            StatusBeforeArchive = source.StatusBeforeArchive,
            TranslationMasked = source.TranslationMasked,
            Status = source.Status
        };
        clone.UpdatedUtc = source.UpdatedUtc;
        return clone;
    }

    private readonly record struct SemanticMatchKey(string DocumentKey, string Text);
    private readonly record struct StableMatchKey(string DocumentKey, string ExternalId);
    private sealed record SemanticReusePlan(
        TranslationUnit Donor, UnitStatus Status, bool IsConflict);

    private sealed class SemanticMatchKeyComparer : IEqualityComparer<SemanticMatchKey>
    {
        public static readonly SemanticMatchKeyComparer Instance = new();
        public bool Equals(SemanticMatchKey left, SemanticMatchKey right) =>
            string.Equals(left.DocumentKey, right.DocumentKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.Text, right.Text, StringComparison.Ordinal);
        public int GetHashCode(SemanticMatchKey value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.DocumentKey),
                StringComparer.Ordinal.GetHashCode(value.Text));
    }

    private sealed class StableMatchKeyComparer : IEqualityComparer<StableMatchKey>
    {
        public static readonly StableMatchKeyComparer Instance = new();
        public bool Equals(StableMatchKey left, StableMatchKey right) =>
            string.Equals(left.DocumentKey, right.DocumentKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
        public int GetHashCode(StableMatchKey value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.DocumentKey),
                StringComparer.Ordinal.GetHashCode(value.ExternalId));
    }

    public static void AttachExportMetadata(ProjectDocument project, ImportedSource source)
    {
        if (source.SourceDocuments.Count == 0)
            throw new InvalidDataException("A beolvasott forrás nem tartalmaz exportálási fájladatokat.");

        TranslationUnit[] active = project.Units.Where(unit => unit.Active).ToArray();
        Dictionary<string, List<TranslationUnit>> byHash = active
            .GroupBy(unit => unit.SourceHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var consumed = new HashSet<TranslationUnit>();
        Dictionary<string, string> existingDocumentKeys = BuildDocumentKeys(project.SourceDocuments);
        Dictionary<string, string> documentKeys = BuildIncomingDocumentKeys(
            project.SourceDocuments, source.SourceDocuments);
        var staged = new List<(TranslationUnit Unit, ImportedUnit Incoming, string DocumentKey)>(active.Length);
        foreach (ImportedUnit incoming in source.Units)
        {
            if (!byHash.TryGetValue(incoming.SourceHash, out List<TranslationUnit>? hashCandidates))
                throw new InvalidDataException($"A(z) {incoming.Id} forrásrekord nem található a régi projektben. Töltsd be Projektfrissítésként.");
            string incomingDocumentKey = IncomingDocumentKey(incoming, documentKeys);
            TranslationUnit[] candidates = hashCandidates.Where(unit => !consumed.Contains(unit) &&
                    (!incoming.HasStableExternalId || StableIdMatches(unit, incoming.Id)))
                .ToArray();
            TranslationUnit[] documentMatches = candidates.Where(unit =>
                    string.Equals(ExistingDocumentKey(unit, existingDocumentKeys),
                        incomingDocumentKey, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (documentMatches.Length > 0) candidates = documentMatches;
            if (candidates.Length != 1)
                throw new InvalidDataException($"A(z) {incoming.Id} forrásrekord régi exportadata nem párosítható egyértelműen. Töltsd be Projektfrissítésként.");
            TranslationUnit unit = candidates[0];
            consumed.Add(unit);
            staged.Add((unit, incoming, incomingDocumentKey));
        }
        if (consumed.Count != active.Length || staged.Count != source.Units.Count)
            throw new InvalidDataException("A régi projekt forrásfájljai időközben megváltoztak. Töltsd be őket Projektfrissítésként, majd próbáld újra az exportot.");

        List<SourceDocumentInfo> installedDocuments = InstallSourceDocuments(project,
            ReconcileSourceDocuments(source.SourceDocuments, documentKeys));
        foreach ((TranslationUnit unit, ImportedUnit incoming, string incomingDocumentKey) in staged)
        {
            unit.HasStableExternalId = incoming.HasStableExternalId;
            unit.ExternalId = incoming.HasStableExternalId ? incoming.Id : string.Empty;
            unit.SourceFile = incoming.SourceFile;
            unit.SourceDocumentId = incoming.SourceDocumentId;
            unit.SourceDocumentKey = incomingDocumentKey;
            unit.SourceRecordIndex = incoming.SourceRecordIndex;
            unit.JsonTargetFieldName = incoming.JsonTargetFieldName;
            unit.TextLayoutKind = incoming.TextLayoutKind;
            unit.TextTargetRecordIndex = incoming.TextTargetRecordIndex;
            unit.TextSourceStart = incoming.TextSourceStart;
            unit.TextSourceLength = incoming.TextSourceLength;
            unit.TextSourceQuote = incoming.TextSourceQuote;
            unit.TextTargetStart = incoming.TextTargetStart;
            unit.TextTargetLength = incoming.TextTargetLength;
            unit.TextTargetQuote = incoming.TextTargetQuote;
            unit.TextDelimiter = incoming.TextDelimiter;
        }
        project.SourceDocuments = installedDocuments;
        project.SourceFiles = source.Files.ToList();
    }

    private static bool StableIdMatches(TranslationUnit unit, string incomingId)
    {
        if (!unit.HasStableExternalId) return false;
        if (!string.IsNullOrWhiteSpace(unit.ExternalId))
            return string.Equals(unit.ExternalId, incomingId, StringComparison.Ordinal);
        if (string.Equals(unit.Id, incomingId, StringComparison.Ordinal)) return true;
        if (!unit.Id.StartsWith(incomingId + ":v", StringComparison.Ordinal)) return false;
        return int.TryParse(unit.Id.AsSpan(incomingId.Length + 2), out _);
    }

    private static List<SourceDocumentInfo> InstallSourceDocuments(ProjectDocument project, IReadOnlyList<SourceDocumentInfo> documents)
    {
        string projectFolder = Path.GetDirectoryName(project.ProjectFilePath)
            ?? throw new InvalidOperationException("A projektnek nincs saját mappája a forráspéldányok mentéséhez.");
        string snapshotFolder = Path.Combine(projectFolder, "Forrás-pillanatképek");
        Directory.CreateDirectory(snapshotFolder);
        var installed = new List<SourceDocumentInfo>(documents.Count);
        foreach (SourceDocumentInfo document in documents)
        {
            string extension = Path.GetExtension(document.OriginalFileName);
            string snapshotPath = Path.Combine(snapshotFolder, document.Id + extension);
            string expectedHash = string.IsNullOrWhiteSpace(document.ContentHash)
                ? HashFile(document.OriginalPath) : document.ContentHash;
            if (!string.Equals(HashFile(document.OriginalPath), expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException($"A(z) {document.OriginalFileName} forrásfájl megváltozott a beolvasás közben; a projekt változatlan maradt.");
            if (!Path.GetFullPath(document.OriginalPath).Equals(Path.GetFullPath(snapshotPath), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(snapshotPath))
                {
                    if (!string.Equals(expectedHash, HashFile(snapshotPath), StringComparison.Ordinal))
                        throw new InvalidDataException($"A(z) {document.OriginalFileName} forráspéldány-azonosítója ütközik egy eltérő fájllal; a projekt változatlan maradt.");
                }
                else
                {
                    File.Copy(document.OriginalPath, snapshotPath, false);
                    if (!string.Equals(expectedHash, HashFile(snapshotPath), StringComparison.Ordinal))
                    {
                        try { File.Delete(snapshotPath); } catch { }
                        throw new InvalidDataException($"A(z) {document.OriginalFileName} forráspéldányának ellenőrzése sikertelen; a projekt változatlan maradt.");
                    }
                }
            }
            installed.Add(new SourceDocumentInfo
            {
                Id = document.Id,
                ContentHash = expectedHash,
                OriginalPath = document.OriginalPath,
                OriginalFileName = document.OriginalFileName,
                OutputRelativePath = document.OutputRelativePath,
                SnapshotRelativePath = Path.GetRelativePath(projectFolder, snapshotPath),
                Format = document.Format,
                TextLayoutKind = document.TextLayoutKind,
                TextLayoutDescription = document.TextLayoutDescription,
                CodePage = document.CodePage,
                HasBom = document.HasBom
            });
        }
        return installed;
    }

    private static Dictionary<string, string> BuildOutputRelativePaths(string[] paths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, string> group in paths.GroupBy(
                     path => Path.GetFileName(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            string[] groupPaths = group.ToArray();
            if (groupPaths.Length == 1)
            {
                result[groupPaths[0]] = group.Key;
                continue;
            }

            Dictionary<string, string[]> components = groupPaths.ToDictionary(
                path => path,
                path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in groupPaths)
            {
                string[] parts = components[path];
                string? chosen = null;
                for (int depth = 2; depth <= parts.Length; depth++)
                {
                    string candidate = Path.Combine(parts[^depth..]);
                    int matches = components.Values.Count(other => other.Length >= depth &&
                        string.Equals(Path.Combine(other[^depth..]), candidate, StringComparison.OrdinalIgnoreCase));
                    if (matches == 1)
                    {
                        chosen = candidate;
                        break;
                    }
                }
                result[path] = chosen ?? Path.Combine("Forrás-" + Hash(Path.GetDirectoryName(path) ?? path)[..8], group.Key);
            }
        }
        return result;
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Encoding DetectEncoding(string path)
    {
        byte[] sample = new byte[Math.Min(64 * 1024, (int)Math.Min(new FileInfo(path).Length, int.MaxValue))];
        using (FileStream stream = File.OpenRead(path)) _ = stream.Read(sample, 0, sample.Length);
        if (sample.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return new UTF8Encoding(true, true);
        if (sample.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode;
        if (sample.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode;
        try { _ = new UTF8Encoding(false, true).GetString(sample); return new UTF8Encoding(false, true); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1250); }
    }

    private static bool HasEncodingPreamble(string path, Encoding encoding)
    {
        byte[] preamble = encoding.GetPreamble();
        if (preamble.Length == 0) return false;
        byte[] beginning = new byte[preamble.Length];
        using FileStream stream = File.OpenRead(path);
        return stream.Read(beginning, 0, beginning.Length) == beginning.Length && beginning.AsSpan().SequenceEqual(preamble);
    }

    private static bool Has(JsonElement element, string name) => Try(element, name, out _);
    private static string PropertyName(JsonElement element, params string[] names)
    {
        foreach (string name in names)
            foreach (JsonProperty property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Name;
        return string.Empty;
    }
    private static string String(JsonElement element, params string[] names)
    {
        foreach (string name in names)
            if (Try(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        return string.Empty;
    }
    private static bool Try(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }
}
