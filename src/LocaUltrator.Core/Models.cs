using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace LocaUltrator.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnitStatus
{
    Pending,
    Waiting,
    Completed,
    NeedsReview,
    Skipped,
    Archived
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClipboardLimitMode
{
    Rows,
    Characters
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationTransport
{
    Automatic,
    GeminiApi,
    Clipboard
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HungarianAccentMode
{
    Standard,
    Circumflex
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualitySeverity
{
    Info,
    Warning,
    Error,
    Critical
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceDocumentFormat
{
    PlainText,
    JsonLines,
    LuTextJsonLines
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlainTextLayoutKind
{
    WholeLine,
    DelimitedColumns,
    LabeledPairs
}

public sealed class TranslationUnit : INotifyPropertyChanged
{
    private string _translationMasked = string.Empty;
    private UnitStatus _status = UnitStatus.Pending;

    public string Id { get; set; } = string.Empty;
    public bool HasStableExternalId { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string SourceDocumentId { get; set; } = string.Empty;
    public string SourceDocumentKey { get; set; } = string.Empty;
    public int SourceRecordIndex { get; set; } = -1;
    public string JsonTargetFieldName { get; set; } = string.Empty;
    public PlainTextLayoutKind TextLayoutKind { get; set; } = PlainTextLayoutKind.WholeLine;
    public int TextTargetRecordIndex { get; set; } = -1;
    public int TextSourceStart { get; set; } = -1;
    public int TextSourceLength { get; set; } = -1;
    public string TextSourceQuote { get; set; } = string.Empty;
    public int TextTargetStart { get; set; } = -1;
    public int TextTargetLength { get; set; } = -1;
    public string TextTargetQuote { get; set; } = string.Empty;
    public string TextDelimiter { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string MaskedSource { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public string TokenSignature { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
    public string MaskedContext { get; set; } = string.Empty;
    public string RoutingJson { get; set; } = "null";
    public string CurrentTargetTextRaw { get; set; } = string.Empty;
    public string ConflictTargetTextRaw { get; set; } = string.Empty;
    public string PreviousTranslationMasked { get; set; } = string.Empty;
    public string LastAiTranslationMasked { get; set; } = string.Empty;
    public string ReviewNotes { get; set; } = string.Empty;
    public string TranslationOrigin { get; set; } = string.Empty;
    public string LastExportedHash { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Active { get; set; } = true;
    public UnitStatus? StatusBeforeArchive { get; set; }

    public string TranslationMasked
    {
        get => _translationMasked;
        set
        {
            if (_translationMasked == value) return;
            _translationMasked = value ?? string.Empty;
            UpdatedUtc = DateTime.UtcNow;
            Raise();
        }
    }

    public UnitStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            UpdatedUtc = DateTime.UtcNow;
            Raise();
            Raise(nameof(StatusLabel));
        }
    }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public string SourceDisplay => string.IsNullOrEmpty(MaskedSource) ? SourceText : MaskedSource;
    [JsonIgnore] public string ContextDisplay => string.IsNullOrEmpty(MaskedContext) ? Context : MaskedContext;
    [JsonIgnore] public string StatusLabel => Status switch
    {
        UnitStatus.Pending => "Fordítandó",
        UnitStatus.Waiting => "Válaszra vár",
        UnitStatus.Completed => "Kész",
        UnitStatus.NeedsReview => "Ellenőrzendő",
        UnitStatus.Skipped => "Kihagyva",
        UnitStatus.Archived => "Archivált",
        _ => Status.ToString()
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshDisplay()
    {
        Raise(nameof(SourceDisplay));
        Raise(nameof(ContextDisplay));
        Raise(nameof(TranslationMasked));
        Raise(nameof(StatusLabel));
    }
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class TokenProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string Engine { get; set; } = "dotnet-regex";
    public string ProfileName { get; set; } = "Tokenprofil";
    public List<TokenRule> Rules { get; set; } = [];
    public List<LiteralToken> Literals { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public DateTime ActivatedUtc { get; set; }
}

public sealed class TokenRule
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "other";
    public string Pattern { get; set; } = string.Empty;
    public int Priority { get; set; } = 100;
    public bool CaseSensitive { get; set; } = true;
    public List<string> Examples { get; set; } = [];
}

public sealed class LiteralToken
{
    public string Value { get; set; } = string.Empty;
    public string Category { get; set; } = "other";
    public bool CaseSensitive { get; set; } = true;
}

public sealed class PendingBatch
{
    public int ProtocolVersion { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string ClipboardText { get; set; } = string.Empty;
    public Dictionary<int, string> UnitIds { get; set; } = [];
    public int CharacterCount { get; set; }
}

public sealed class ProjectDocument
{
    public const int DefaultClipboardMaximumItems = 60;
    public const int DefaultClipboardMaximumCharacters = 16_000;
    public const int DefaultMaximumPendingBatches = 5;
    public int FormatVersion { get; set; } = 2;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Új projekt";
    public string SourceLanguage { get; set; } = "angol";
    public string TargetLanguage { get; set; } = "magyar";
    public string SourceVersion { get; set; } = string.Empty;
    public string TranslationInstruction { get; set; } = "Természetes, pontos magyar fordítást készíts. A szereplők hangját és a kontextust tartsd meg.";
    public int ClipboardMaximumItems { get; set; } = DefaultClipboardMaximumItems;
    public int ClipboardMaximumCharacters { get; set; } = DefaultClipboardMaximumCharacters;
    public ClipboardLimitMode ClipboardLimitMode { get; set; } = ClipboardLimitMode.Rows;
    public int MaximumPendingBatches { get; set; } = DefaultMaximumPendingBatches;
    public bool AutoProcessClipboardResponses { get; set; } = true;
    public bool ShowStatusHud { get; set; } = true;
    public int StatusHudDisplaySeconds { get; set; } = 3;
    public bool EnableGlobalClipboardHotkey { get; set; } = true;
    public string ClipboardHotkey { get; set; } = "Insert";
    public TranslationTransport TranslationTransport { get; set; } = TranslationTransport.Automatic;
    public HungarianAccentMode ExportAccentMode { get; set; } = HungarianAccentMode.Standard;
    public string TokenPromptTemplate { get; set; } = string.Empty;
    public TokenProfile? ActiveTokenProfile { get; set; }
    public List<TranslationUnit> Units { get; set; } = [];
    public List<PendingBatch> PendingBatches { get; set; } = [];
    [JsonPropertyName("pendingBatch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PendingBatch? LegacyPendingBatch { get; set; }
    public List<string> SourceFiles { get; set; } = [];
    public List<SourceDocumentInfo> SourceDocuments { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastExportUtc { get; set; }
    public string GameRootPath { get; set; } = string.Empty;
    public string DetectedGameEngine { get; set; } = string.Empty;
    public string ExtractionAdapterId { get; set; } = string.Empty;
    public DateTime? LastQualityAuditUtc { get; set; }
    [JsonIgnore] public string ProjectFilePath { get; set; } = string.Empty;
}

public sealed record ImportedUnit(
    string Id,
    bool HasStableExternalId,
    string SourceFile,
    string SourceText,
    string SourceHash,
    string CurrentTargetText,
    string Context,
    string RoutingJson,
    int Order)
{
    public string SourceDocumentId { get; init; } = string.Empty;
    public string SourceDocumentKey { get; init; } = string.Empty;
    public int SourceRecordIndex { get; init; } = -1;
    public string JsonTargetFieldName { get; init; } = string.Empty;
    public PlainTextLayoutKind TextLayoutKind { get; init; } = PlainTextLayoutKind.WholeLine;
    public int TextTargetRecordIndex { get; init; } = -1;
    public int TextSourceStart { get; init; } = -1;
    public int TextSourceLength { get; init; } = -1;
    public string TextSourceQuote { get; init; } = string.Empty;
    public int TextTargetStart { get; init; } = -1;
    public int TextTargetLength { get; init; } = -1;
    public string TextTargetQuote { get; init; } = string.Empty;
    public string TextDelimiter { get; init; } = string.Empty;
}

public sealed record ImportedSource(string SourceVersion, IReadOnlyList<ImportedUnit> Units, IReadOnlyList<string> Files)
{
    public IReadOnlyList<SourceDocumentInfo> SourceDocuments { get; init; } = [];
}

public sealed class SourceDocumentInfo
{
    public string Id { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string OutputRelativePath { get; set; } = string.Empty;
    public string SnapshotRelativePath { get; set; } = string.Empty;
    public SourceDocumentFormat Format { get; set; }
    public PlainTextLayoutKind TextLayoutKind { get; set; } = PlainTextLayoutKind.WholeLine;
    public string TextLayoutDescription { get; set; } = string.Empty;
    public int CodePage { get; set; } = 65001;
    public bool HasBom { get; set; }
}
public sealed record UpdateSummary(int Unchanged, int Moved, int New, int Changed, int Removed, int Conflicts);
public sealed record TokenMapItem(string Marker, string Raw, string Category, string RuleId, int SourceIndex);
public sealed record MaskResult(string Text, IReadOnlyList<TokenMapItem> Tokens, string Signature, int OverlapCount);
public sealed record TokenProfileValidation(bool Success, IReadOnlyList<string> Errors, int MatchCount, int OverlapCount);
public sealed record BatchApplyResult(int Applied, int Rejected, int Missing, IReadOnlyList<string> Errors)
{
    public string BatchId { get; init; } = string.Empty;
    public bool IsSuccessful => Applied > 0 && Rejected == 0 && Missing == 0 && Errors.Count == 0;
}

public sealed class QualityIssue
{
    public string UnitId { get; set; } = string.Empty;
    public QualitySeverity Severity { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class QualityReport
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public int CheckedUnits { get; set; }
    public List<QualityIssue> Issues { get; set; } = [];
    [JsonIgnore] public int ErrorCount => Issues.Count(issue => issue.Severity >= QualitySeverity.Error);
    [JsonIgnore] public int WarningCount => Issues.Count(issue => issue.Severity == QualitySeverity.Warning);
    [JsonIgnore] public bool IsSafe => ErrorCount == 0;
}
