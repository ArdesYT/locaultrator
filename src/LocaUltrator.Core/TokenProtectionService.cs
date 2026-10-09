using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace LocaUltrator.Core;

public sealed class TokenProtectionService
{
    private static readonly HashSet<string> Categories =
    ["placeholder", "format_tag", "control_code", "escape_sequence", "resource_id", "script_fragment", "other"];
    private static readonly Regex MarkerRegex = new("⟦[A-Z_]+_[0-9]{2,4}⟧", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex[] CandidateDetectors =
    [
        new(@"\{\{[^{}\r\n]{1,100}\}\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)),
        new(@"\{[^{}\r\n]{1,100}\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)),
        new(@"</?[A-Za-z][^<>\r\n]{0,150}>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)),
        new(@"%(?:[0-9]{1,3}\$)?[-+0-9.#]{0,20}(?:hh|h|ll|l|L|z|j|t)?[A-Za-z%]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)),
        new(@"\\(?:[""'\\nrt0]|x[0-9A-Fa-f]{2,8}|u[0-9A-Fa-f]{4})", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)),
        new(@"\$\{[^{}\r\n]{1,100}\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)),
        new(@"\[\$?[A-Za-z_][A-Za-z0-9_.$:-]{0,80}\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)),
        new(@"@[A-Z_][A-Z0-9_.:-]{1,80}@", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
    ];
    private static readonly ConditionalWeakTable<TokenProfile, PreparedProfile> PreparedProfiles = new();

    public TokenProfile ParseProfile(string json)
    {
        string clean = StripCodeFence(json);
        bool repaired = false;
        TokenProfile? profile;
        try
        {
            profile = DeserializeProfile(clean);
        }
        catch (JsonException firstError)
        {
            string repairedJson = RepairInvalidJsonEscapes(clean);
            if (repairedJson == clean)
                throw new InvalidDataException("A tokenprofil nem érvényes JSON. Másold ki újra kizárólag a teljes JSON-objektumot.", firstError);
            try
            {
                profile = DeserializeProfile(repairedJson);
                repaired = true;
            }
            catch (JsonException)
            {
                throw new InvalidDataException("A tokenprofil JSON-szerkezete hibás, és nem javítható automatikusan. Másold ki újra kizárólag a teljes JSON-objektumot.", firstError);
            }
        }
        if (profile is null) throw new InvalidDataException("Az AI válasza nem tartalmaz tokenprofilt.");
        profile.Rules ??= [];
        profile.Literals ??= [];
        profile.Warnings ??= [];
        NormalizeProfileEscapes(profile);
        if (repaired) profile.Warnings.Insert(0, "A LocaUltrator automatikusan kijavította az AI hibás JSON-regex perjeleit.");
        return profile;
    }

    public TokenProfileValidation ValidateProfile(
        TokenProfile profile,
        IEnumerable<TranslationUnit> units,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        if (profile.SchemaVersion != 1) errors.Add("Csak az 1-es tokenprofil-séma támogatott.");
        if (!profile.Engine.Equals("dotnet-regex", StringComparison.OrdinalIgnoreCase)) errors.Add("A regexmotor csak dotnet-regex lehet.");
        if (profile.Rules.Count > 200) errors.Add("Legfeljebb 200 regexszabály engedélyezett.");
        if (profile.Literals.Count > 500) errors.Add("Legfeljebb 500 literális token engedélyezett.");
        if (profile.Rules.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Count() > 1))
            errors.Add("A szabályazonosítók nem üresek és egyediek legyenek.");

        foreach (TokenRule rule in profile.Rules)
        {
            if (!Categories.Contains(rule.Category)) errors.Add($"Ismeretlen kategória: {rule.Category} ({rule.Id}).");
            if (rule.Pattern.Length is 0 or > 500) errors.Add($"Érvénytelen regexhossz: {rule.Id}.");
            if (IsDangerouslyBroad(rule.Pattern)) errors.Add($"Túl általános vagy veszélyes regex: {rule.Id}.");
            try { _ = CompileUncached(rule); }
            catch (ArgumentException ex) { errors.Add($"Hibás regex ({rule.Id}): {ex.Message}"); }
        }
        foreach (LiteralToken literal in profile.Literals)
        {
            if (!Categories.Contains(literal.Category)) errors.Add($"Ismeretlen literális kategória: {literal.Category}.");
            if (string.IsNullOrWhiteSpace(literal.Value) || literal.Value.Length > 300) errors.Add("Üres vagy túl hosszú literális token.");
        }
        if (errors.Count > 0) return new TokenProfileValidation(false, errors, 0, 0);

        int total = units.Count(x => x.Active);
        int processed = 0, matches = 0, overlaps = 0;
        var foundRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unsafeRuleMatches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unsafeLiteralMatches = new HashSet<string>(StringComparer.Ordinal);
        var candidateSafetyCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var missingExamples = profile.Rules.ToDictionary(
            x => x.Id,
            x => new HashSet<string>(x.Examples, x.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var foundLiterals = new bool[profile.Literals.Count];
        var sensitiveLiterals = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var insensitiveLiterals = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < profile.Literals.Count; index++)
        {
            LiteralToken literal = profile.Literals[index];
            Dictionary<string, List<int>> lookup = literal.CaseSensitive ? sensitiveLiterals : insensitiveLiterals;
            if (!lookup.TryGetValue(literal.Value, out List<int>? indexes)) lookup[literal.Value] = indexes = [];
            indexes.Add(index);
        }

        progress?.Report($"Tokenprofil ellenőrzése… 0/{total:N0}");
        foreach (TranslationUnit unit in units.Where(x => x.Active))
        {
            if ((processed & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            MaskResult result = Mask(unit.SourceText, profile);
            matches += result.Tokens.Count;
            overlaps += result.OverlapCount;
            foreach (TokenMapItem token in result.Tokens)
            {
                if (!candidateSafetyCache.TryGetValue(token.Raw, out bool recognizedCandidate))
                {
                    recognizedCandidate = IsRecognizedCandidate(token.Raw);
                    candidateSafetyCache[token.Raw] = recognizedCandidate;
                }
                if (!recognizedCandidate)
                {
                    if (token.RuleId == "literal") unsafeLiteralMatches.Add(token.Raw);
                    else unsafeRuleMatches.TryAdd(token.RuleId, token.Raw);
                }
                if (token.RuleId == "literal")
                {
                    if (sensitiveLiterals.TryGetValue(token.Raw, out List<int>? sensitive))
                        foreach (int index in sensitive) foundLiterals[index] = true;
                    if (insensitiveLiterals.TryGetValue(token.Raw, out List<int>? insensitive))
                        foreach (int index in insensitive) foundLiterals[index] = true;
                }
                else
                {
                    foundRules.Add(token.RuleId);
                    if (missingExamples.TryGetValue(token.RuleId, out HashSet<string>? examples)) examples.Remove(token.Raw);
                }
            }
            processed++;
            if ((processed & 8191) == 0) progress?.Report($"Tokenprofil ellenőrzése… {processed:N0}/{total:N0}");
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report($"Tokenprofil ellenőrzése… {processed:N0}/{total:N0}");
        foreach (TokenRule rule in profile.Rules)
        {
            if (!foundRules.Contains(rule.Id))
                errors.Add($"A(z) {rule.Id} szabály nem talál valódi tokent a projektben.");
            foreach (string example in missingExamples[rule.Id])
                errors.Add($"A(z) {rule.Id} AI-példája nem valódi projekttalálat: {example}");
            if (unsafeRuleMatches.TryGetValue(rule.Id, out string? unsafeMatch))
                errors.Add($"A(z) {rule.Id} szabály normál szöveget vagy a tokenjelölt-listán kívüli részt is védene: {unsafeMatch}");
        }
        for (int index = 0; index < profile.Literals.Count; index++)
            if (!foundLiterals[index]) errors.Add($"A literális token nem található a projektben: {profile.Literals[index].Value}");
        foreach (string unsafeLiteral in unsafeLiteralMatches)
            errors.Add($"A literális érték nem igazolt technikai tokenjelölt: {unsafeLiteral}");
        if (profile.Rules.Count + profile.Literals.Count > 0 && matches == 0)
            errors.Add("A profil egyetlen valódi projekt-tokenre sem illeszkedik.");
        if (overlaps > 0) errors.Add($"A szabályok {overlaps} átfedő találatot okoznak. Szűkítsd a profil mintáit.");
        return new TokenProfileValidation(errors.Count == 0, errors, matches, overlaps);
    }

    public MaskResult Mask(string text, TokenProfile? profile)
    {
        if (profile is null || string.IsNullOrEmpty(text)) return new MaskResult(text ?? string.Empty, [], string.Empty, 0);
        PreparedProfile prepared = Prepare(profile);
        var candidates = new List<Candidate>();
        foreach (PreparedRule preparedRule in prepared.Rules)
        {
            TokenRule rule = preparedRule.Rule;
            foreach (Match match in preparedRule.Regex.Matches(text))
                if (match.Success && match.Length > 0)
                    candidates.Add(new Candidate(match.Index, match.Length, match.Value, rule.Category, rule.Id, rule.Priority));
        }
        foreach (LiteralToken literal in prepared.Literals)
        {
            StringComparison comparison = literal.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int start = 0;
            while (start < text.Length)
            {
                int index = text.IndexOf(literal.Value, start, comparison);
                if (index < 0) break;
                candidates.Add(new Candidate(index, literal.Value.Length, text.Substring(index, literal.Value.Length), literal.Category, "literal", 1000));
                start = index + literal.Value.Length;
            }
        }

        Candidate[] sorted = candidates.OrderBy(x => x.Index).ThenByDescending(x => x.Priority).ThenByDescending(x => x.Length).ToArray();
        var selected = new List<Candidate>();
        int overlap = 0, occupiedUntil = -1;
        foreach (Candidate candidate in sorted)
        {
            if (candidate.Index < occupiedUntil) { overlap++; continue; }
            selected.Add(candidate);
            occupiedUntil = candidate.Index + candidate.Length;
        }
        if (selected.Count == 0) return new MaskResult(text, [], string.Empty, overlap);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var map = new List<TokenMapItem>(selected.Count);
        var output = new StringBuilder(text.Length + selected.Count * 8);
        int position = 0;
        foreach (Candidate candidate in selected)
        {
            output.Append(text, position, candidate.Index - position);
            string prefix = Prefix(candidate.Category);
            int number = counts.GetValueOrDefault(prefix) + 1;
            counts[prefix] = number;
            string marker = $"⟦{prefix}_{number:00}⟧";
            output.Append(marker);
            map.Add(new TokenMapItem(marker, candidate.Raw, candidate.Category, candidate.RuleId, candidate.Index));
            position = candidate.Index + candidate.Length;
        }
        output.Append(text, position, text.Length - position);
        string signature = SourceImporter.Hash(string.Join('\u001F', map.Select(x => x.Category + ":" + x.Raw)));
        return new MaskResult(output.ToString(), map, signature, overlap);
    }

    public string MaskExistingTranslation(string rawTranslation, TokenProfile? profile, IReadOnlyList<TokenMapItem> sourceTokens)
    {
        if (string.IsNullOrEmpty(rawTranslation) || sourceTokens.Count == 0) return rawTranslation ?? string.Empty;
        MaskResult target = Mask(rawTranslation, profile);
        var queues = sourceTokens.GroupBy(x => (x.Category, x.Raw), new TokenKeyComparer())
            .ToDictionary(x => x.Key, x => new Queue<string>(x.Select(y => y.Marker)), new TokenKeyComparer());
        string result = target.Text;
        var temporary = new List<(string Temporary, string Final)>();
        int temporaryIndex = 0;
        foreach (TokenMapItem targetToken in target.Tokens)
        {
            if (!queues.TryGetValue((targetToken.Category, targetToken.Raw), out Queue<string>? queue) || queue.Count == 0)
                continue;
            string holder = $"\uE000{temporaryIndex++:D4}\uE001";
            result = result.Replace(targetToken.Marker, holder, StringComparison.Ordinal);
            temporary.Add((holder, queue.Dequeue()));
        }
        foreach ((string holder, string final) in temporary) result = result.Replace(holder, final, StringComparison.Ordinal);
        return result;
    }

    public string Restore(string maskedText, IReadOnlyList<TokenMapItem> tokens)
    {
        string restored = maskedText ?? string.Empty;
        foreach (TokenMapItem token in tokens) restored = restored.Replace(token.Marker, token.Raw, StringComparison.Ordinal);
        return restored;
    }

    public bool ValidateMarkers(string sourceMasked, string translatedMasked, out string error)
    {
        string[] expected = MarkerRegex.Matches(sourceMasked ?? string.Empty).Select(x => x.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        string[] actual = MarkerRegex.Matches(translatedMasked ?? string.Empty).Select(x => x.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            error = "A válaszban hiányzó, módosított vagy idegen tokenmaszk található.";
            return false;
        }
        int sourceOpen = (sourceMasked ?? string.Empty).Count(character => character == '⟦');
        int sourceClose = (sourceMasked ?? string.Empty).Count(character => character == '⟧');
        int translatedOpen = (translatedMasked ?? string.Empty).Count(character => character == '⟦');
        int translatedClose = (translatedMasked ?? string.Empty).Count(character => character == '⟧');
        if (sourceOpen != translatedOpen || sourceClose != translatedClose)
        {
            error = "A válaszban sérült vagy idegen tokenzárójel található.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    public string BuildCandidateReport(ProjectDocument project)
    {
        var found = new Dictionary<string, CandidateInfo>(StringComparer.Ordinal);
        foreach (TranslationUnit unit in project.Units.Where(x => x.Active))
        {
            for (int detectorIndex = 0; detectorIndex < CandidateDetectors.Length; detectorIndex++)
            {
                Regex detector = CandidateDetectors[detectorIndex];
                foreach (Match match in detector.Matches(unit.SourceText))
                {
                    if (!found.TryGetValue(match.Value, out CandidateInfo? info))
                        found[match.Value] = info = new CandidateInfo { DetectorIndex = detectorIndex };
                    info.Count++;
                    if (info.Examples.Count < 2)
                    {
                        string example = unit.SourceText.Length <= 180 ? unit.SourceText : unit.SourceText[..177] + "…";
                        if (!info.Examples.Contains(example, StringComparer.Ordinal)) info.Examples.Add(example);
                    }
                }
            }
        }
        KeyValuePair<string, CandidateInfo>[] reported = found
            .GroupBy(candidate => candidate.Value.DetectorIndex)
            .SelectMany(group => group.OrderByDescending(candidate => candidate.Value.Count)
                .ThenBy(candidate => candidate.Key, StringComparer.Ordinal).Take(24))
            .OrderByDescending(candidate => candidate.Value.Count)
            .ThenBy(candidate => candidate.Key, StringComparer.Ordinal)
            .Take(160).ToArray();
        return JsonSerializer.Serialize(new
        {
            project = project.Name,
            activeUnits = project.Units.Count(x => x.Active),
            textLayouts = project.SourceDocuments
                .Where(document => document.Format == SourceDocumentFormat.PlainText)
                .Select(document => new
                {
                    file = document.OriginalFileName,
                    kind = document.TextLayoutKind.ToString(),
                    targetIsSeparate = document.TextLayoutKind != PlainTextLayoutKind.WholeLine,
                    description = document.TextLayoutDescription
                }),
            layoutSafety = new[]
            {
                "A forrás-/céloszlop, mezőcímke, elválasztó, idézőjel és technikai ID fájlszerkezet; a program őrzi, nem tokenprofil-szabály.",
                "Token csak a candidates tömbben szereplő, a SourceText belsejében ténylegesen előforduló technikai elem lehet."
            },
            totalDistinctCandidates = found.Count,
            reportedCandidates = reported.Length,
            omittedCandidates = found.Count - reported.Length,
            candidateReportNote = found.Count > reported.Length
                ? "A jelentés méretvédelmi mintavételt használ. A kihagyott értékekre csak a bemutatott alakokból levezetett, szűk regex illeszkedhet; normál szövegre nem."
                : "Minden felismert tokenjelölt szerepel a jelentésben.",
            candidates = reported.Select(x => new { value = x.Key, count = x.Value.Count, examples = x.Value.Examples })
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public string BuildFilledPrompt(ProjectDocument project)
    {
        string template = string.IsNullOrWhiteSpace(project.TokenPromptTemplate) ? DefaultPrompts.TokenProfilePrompt : project.TokenPromptTemplate;
        return template.Replace("{{PROJECT_NAME}}", project.Name, StringComparison.Ordinal)
            .Replace("{{TOKEN_CANDIDATES_JSON}}", BuildCandidateReport(project), StringComparison.Ordinal)
            .Replace("{{OUTPUT_SCHEMA}}", DefaultPrompts.OutputSchema, StringComparison.Ordinal);
    }

    private static PreparedProfile Prepare(TokenProfile profile) => PreparedProfiles.GetValue(profile, static value => new PreparedProfile(
        value.Rules.OrderByDescending(x => x.Priority).Select(x => new PreparedRule(x, CompileUncached(x))).ToArray(),
        value.Literals.ToArray()));

    private static Regex CompileUncached(TokenRule rule)
    {
        RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
        if (!rule.CaseSensitive) options |= RegexOptions.IgnoreCase;
        return new Regex(rule.Pattern, options, TimeSpan.FromMilliseconds(100));
    }

    private static bool IsDangerouslyBroad(string pattern)
    {
        string compact = pattern.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Contains(".*", StringComparison.Ordinal) || compact.Contains(".+", StringComparison.Ordinal) ||
               compact is "\\w+" or "\\d+" or ".*" or ".+" ||
               compact.Contains("(?<=", StringComparison.Ordinal) || compact.Contains("(?<!", StringComparison.Ordinal) ||
               Regex.IsMatch(compact, @"\\[1-9]") || compact.Contains("(?(", StringComparison.Ordinal);
    }

    private static bool IsRecognizedCandidate(string value)
    {
        value ??= string.Empty;
        foreach (Regex detector in CandidateDetectors)
        {
            Match match = detector.Match(value);
            if (match.Success && match.Index == 0 && match.Length == value.Length) return true;
        }
        return false;
    }

    private static string Prefix(string category) => category switch
    {
        "placeholder" => "VARIABLE",
        "format_tag" => "TAG",
        "control_code" => "CONTROL",
        "escape_sequence" => "ESCAPE",
        "resource_id" => "RESOURCE",
        "script_fragment" => "SCRIPT",
        _ => "TOKEN"
    };

    private static string StripCodeFence(string value)
    {
        string clean = (value ?? string.Empty).Trim();
        if (!clean.StartsWith("```", StringComparison.Ordinal)) return clean;
        int firstBreak = clean.IndexOf('\n');
        int lastFence = clean.LastIndexOf("```", StringComparison.Ordinal);
        return firstBreak >= 0 && lastFence > firstBreak ? clean[(firstBreak + 1)..lastFence].Trim() : clean;
    }

    private static TokenProfile? DeserializeProfile(string json) => JsonSerializer.Deserialize<TokenProfile>(json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    internal static string RepairInvalidJsonEscapes(string json)
    {
        if (string.IsNullOrEmpty(json)) return json ?? string.Empty;
        var output = new StringBuilder(json.Length + 32);
        bool insideString = false;
        for (int index = 0; index < json.Length; index++)
        {
            char current = json[index];
            if (!insideString)
            {
                output.Append(current);
                if (current == '"') insideString = true;
                continue;
            }
            if (current == '"')
            {
                output.Append(current);
                insideString = false;
                continue;
            }
            if (current != '\\')
            {
                output.Append(current);
                continue;
            }
            if (index + 1 >= json.Length)
            {
                output.Append("\\\\");
                continue;
            }

            char next = json[index + 1];
            bool validUnicode = next != 'u' || index + 5 < json.Length && json.AsSpan(index + 2, 4).ToString().All(Uri.IsHexDigit);
            if ("\"\\/bfnrtu".Contains(next) && validUnicode)
            {
                output.Append(current).Append(next);
                index++;
            }
            else
            {
                output.Append("\\\\");
            }
        }
        return output.ToString();
    }

    private static void NormalizeProfileEscapes(TokenProfile profile)
    {
        foreach (TokenRule rule in profile.Rules) rule.Pattern = NormalizeControlEscapes(rule.Pattern);
        foreach (LiteralToken literal in profile.Literals)
            if (literal.Category == "escape_sequence") literal.Value = NormalizeControlEscapes(literal.Value);
    }

    private static string NormalizeControlEscapes(string value) => (value ?? string.Empty)
        .Replace("\b", "\\b", StringComparison.Ordinal)
        .Replace("\f", "\\f", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private sealed record Candidate(int Index, int Length, string Raw, string Category, string RuleId, int Priority);
    private sealed record PreparedRule(TokenRule Rule, Regex Regex);
    private sealed record PreparedProfile(IReadOnlyList<PreparedRule> Rules, IReadOnlyList<LiteralToken> Literals);
    private sealed class CandidateInfo
    {
        public int DetectorIndex { get; set; }
        public int Count { get; set; }
        public List<string> Examples { get; } = [];
    }
    private sealed class TokenKeyComparer : IEqualityComparer<(string Category, string Raw)>
    {
        public bool Equals((string Category, string Raw) x, (string Category, string Raw) y) =>
            x.Category == y.Category && x.Raw == y.Raw;
        public int GetHashCode((string Category, string Raw) obj) => HashCode.Combine(obj.Category, obj.Raw);
    }
}
