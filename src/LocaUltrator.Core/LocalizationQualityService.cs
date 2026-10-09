using System.Text.RegularExpressions;

namespace LocaUltrator.Core;

public sealed partial class LocalizationQualityService
{
    private readonly TokenProtectionService _protection;

    public LocalizationQualityService(TokenProtectionService? protection = null) =>
        _protection = protection ?? new TokenProtectionService();

    public QualityReport Audit(ProjectDocument project, CancellationToken cancellationToken = default)
    {
        var report = new QualityReport();
        TranslationUnit[] units = project.Units.Where(unit => unit.Active).ToArray();
        report.CheckedUnits = units.Length;
        int index = 0;
        foreach (TranslationUnit unit in units)
        {
            if ((index++ & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            AuditUnit(unit, report.Issues);
        }

        foreach (IGrouping<string, TranslationUnit> duplicate in units
            .Where(unit => unit.Status == UnitStatus.Completed && !string.IsNullOrWhiteSpace(unit.TranslationMasked))
            .GroupBy(unit => unit.SourceDisplay, StringComparer.Ordinal)
            .Where(group => group.Count() > 1 && group.Select(unit => unit.TranslationMasked).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            foreach (TranslationUnit unit in duplicate)
                report.Issues.Add(Issue(unit, QualitySeverity.Warning, "INCONSISTENT_DUPLICATE",
                    "Azonos forrásszöveghez több eltérő kész fordítás tartozik. Kontextus alapján ellenőrizd."));
        }

        project.LastQualityAuditUtc = report.CreatedUtc;
        return report;
    }

    public IReadOnlyList<QualityIssue> AuditUnit(TranslationUnit unit)
    {
        var issues = new List<QualityIssue>();
        AuditUnit(unit, issues);
        return issues;
    }

    private void AuditUnit(TranslationUnit unit, List<QualityIssue> issues)
    {
        if (unit.Status is UnitStatus.Pending or UnitStatus.Waiting)
        {
            if (!string.IsNullOrWhiteSpace(unit.TranslationMasked))
                issues.Add(Issue(unit, QualitySeverity.Warning, "STATUS_TEXT_MISMATCH",
                    "A sor fordítást tartalmaz, de az állapota még nem kész."));
            return;
        }
        if (unit.Status is UnitStatus.Skipped or UnitStatus.Archived) return;
        if (string.IsNullOrWhiteSpace(unit.TranslationMasked))
        {
            issues.Add(Issue(unit, QualitySeverity.Error, "EMPTY_TRANSLATION", "A késznek jelölt fordítás üres."));
            return;
        }
        if (!_protection.ValidateMarkers(unit.SourceDisplay, unit.TranslationMasked, out string markerError))
            issues.Add(Issue(unit, QualitySeverity.Critical, "TOKEN_MISMATCH", markerError));
        if (!TextStructureGuard.Validate(unit.SourceDisplay, unit.TranslationMasked, out string structureError))
            issues.Add(Issue(unit, QualitySeverity.Critical, "STRUCTURE_MISMATCH", structureError));
        if (BrokenMarkerRegex().IsMatch(unit.TranslationMasked))
            issues.Add(Issue(unit, QualitySeverity.Critical, "BROKEN_MARKER", "Sérült vagy kézzel átírt tokenmaszk található."));

        string sourceWords = LettersOnly(unit.SourceDisplay);
        string targetWords = LettersOnly(unit.TranslationMasked);
        if (sourceWords.Length >= 4 && sourceWords.Equals(targetWords, StringComparison.CurrentCultureIgnoreCase))
            issues.Add(Issue(unit, QualitySeverity.Warning, "UNCHANGED_SOURCE", "A fordítás szövege megegyezik a forrással."));

        int sourceLength = VisibleLength(unit.SourceDisplay);
        int targetLength = VisibleLength(unit.TranslationMasked);
        if (sourceLength >= 12 && targetLength > sourceLength * 2.4)
            issues.Add(Issue(unit, QualitySeverity.Warning, "EXPANSION", "A magyar szöveg több mint 2,4-szer hosszabb a forrásnál; UI-túlfutás lehetséges."));
        if (sourceLength >= 18 && targetLength < sourceLength * 0.28)
            issues.Add(Issue(unit, QualitySeverity.Warning, "TOO_SHORT", "A fordítás feltűnően rövid; jelentésvesztés lehetséges."));
        if (unit.TranslationMasked.Contains('\uFFFD'))
            issues.Add(Issue(unit, QualitySeverity.Error, "ENCODING_REPLACEMENT", "Unicode helyettesítő karakter található a fordításban."));
    }

    private static QualityIssue Issue(TranslationUnit unit, QualitySeverity severity, string code, string message) =>
        new() { UnitId = unit.Id, Severity = severity, Code = code, Message = message };

    private static int VisibleLength(string value) => MarkerRegex().Replace(value ?? string.Empty, string.Empty).Trim().Length;
    private static string LettersOnly(string value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());

    [GeneratedRegex(@"⟦[A-Z_]+_[0-9]{2,4}⟧", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"(?:⟦[^⟧\r\n]*$|^[^⟦\r\n]*⟧)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex BrokenMarkerRegex();
}
