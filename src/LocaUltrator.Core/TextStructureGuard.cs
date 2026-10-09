using System.Text;
using System.Text.RegularExpressions;

namespace LocaUltrator.Core;

public static class TextStructureGuard
{
    private static readonly Regex MarkerRegex = new(
        @"⟦(?<kind>[A-Z]+)_[0-9]+⟧",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Preserve(string sourceMasked, string translationMasked)
    {
        sourceMasked ??= string.Empty;
        translationMasked ??= string.Empty;

        string result = PreservePhysicalLineBoundaries(sourceMasked, translationMasked);
        Match[] sourceMarkers = MarkerRegex.Matches(sourceMasked).Cast<Match>().ToArray();

        foreach (Match marker in sourceMarkers.Where(match => IsEscapeMarker(match)))
        {
            string markerText = marker.Value;
            result = SetWhitespaceBefore(result, markerText, WhitespaceBefore(sourceMasked, marker.Index));
            result = SetWhitespaceAfter(result, markerText, WhitespaceAfter(sourceMasked, marker.Index + marker.Length));
        }

        for (int index = 0; index + 1 < sourceMarkers.Length; index++)
        {
            Match left = sourceMarkers[index];
            Match right = sourceMarkers[index + 1];
            string sourceGap = sourceMasked[(left.Index + left.Length)..right.Index];
            if (!IsHorizontalWhitespace(sourceGap)) continue;
            result = SetGapBetweenMarkers(result, left.Value, right.Value, sourceGap);
        }

        Match? first = sourceMarkers.FirstOrDefault();
        if (first is not null && IsStructuralMarker(first) &&
            IsHorizontalWhitespace(sourceMasked[..first.Index]))
        {
            int after = first.Index + first.Length;
            string followingWhitespace = WhitespaceAfter(sourceMasked, after);
            result = SetWhitespaceAfter(result, first.Value, followingWhitespace, requireMarkerAtStart: true);
        }

        return PreservePhysicalLineBoundaries(sourceMasked, result);
    }

    public static bool Validate(string sourceMasked, string translationMasked, out string error)
    {
        sourceMasked ??= string.Empty;
        translationMasked ??= string.Empty;
        List<TextLine> sourceLines = SplitLines(sourceMasked);
        List<TextLine> translatedLines = SplitLines(translationMasked);
        if (sourceLines.Count != translatedLines.Count)
        {
            error = "A fordítás fizikai sorszáma eltér az eredetitől.";
            return false;
        }
        for (int index = 0; index < sourceLines.Count; index++)
        {
            if (!string.Equals(sourceLines[index].Terminator, translatedLines[index].Terminator, StringComparison.Ordinal))
            {
                error = $"A fordítás {index + 1}. fizikai sorának lezárása eltér az eredetitől.";
                return false;
            }
        }

        Match sourceLeadingControl = LeadingControlMarker(sourceMasked);
        if (sourceLeadingControl.Success)
        {
            string sourceLeadingWhitespace = sourceMasked[..sourceLeadingControl.Index];
            int expectedMarkerIndex = sourceLeadingWhitespace.Length;
            if (translationMasked.Length < expectedMarkerIndex + sourceLeadingControl.Length ||
                !translationMasked.AsSpan(expectedMarkerIndex).StartsWith(sourceLeadingControl.Value, StringComparison.Ordinal))
            {
                error = $"A sor eleji {sourceLeadingControl.Value} vezérlőazonosító elmozdult.";
                return false;
            }
        }

        string expected = Preserve(sourceMasked, translationMasked);
        if (string.Equals(expected, translationMasked, StringComparison.Ordinal))
        {
            error = string.Empty;
            return true;
        }

        error = "A sor szerkezeti szóközei, tabulátorai vagy logikai sorvégei eltérnek az eredetitől.";
        return false;
    }

    private static Match LeadingControlMarker(string value)
    {
        int leadingLength = LeadingHorizontalWhitespace(value).Length;
        Match match = MarkerRegex.Match(value, leadingLength);
        return match.Success && match.Index == leadingLength && match.Groups["kind"].Value == "CONTROL" &&
            WhitespaceAfter(value, match.Index + match.Length).Contains('\t')
            ? match
            : Match.Empty;
    }

    private static string PreservePhysicalLineBoundaries(string source, string translation)
    {
        List<TextLine> sourceLines = SplitLines(source);
        List<TextLine> translatedLines = SplitLines(translation);
        if (sourceLines.Count != translatedLines.Count) return translation;

        for (int index = 0; index < sourceLines.Count; index++)
        {
            TextLine sourceLine = sourceLines[index];
            TextLine translatedLine = translatedLines[index];
            string leading = LeadingHorizontalWhitespace(sourceLine.Content);
            string trailing = TrailingHorizontalWhitespace(sourceLine.Content);
            string core = translatedLine.Content;
            core = core[LeadingHorizontalWhitespace(core).Length..];
            int trailingLength = TrailingHorizontalWhitespace(core).Length;
            if (trailingLength > 0) core = core[..^trailingLength];
            translatedLine.Content = leading + core + trailing;
            translatedLine.Terminator = sourceLine.Terminator;
        }

        return JoinLines(translatedLines);
    }

    private static string SetGapBetweenMarkers(string value, string leftMarker, string rightMarker, string desiredGap)
    {
        int left = value.IndexOf(leftMarker, StringComparison.Ordinal);
        int right = left < 0 ? -1 : value.IndexOf(rightMarker, left + leftMarker.Length, StringComparison.Ordinal);
        if (left < 0 || right < 0) return value;
        int gapStart = left + leftMarker.Length;
        string currentGap = value[gapStart..right];
        if (!IsHorizontalWhitespace(currentGap)) return value;
        return value[..gapStart] + desiredGap + value[right..];
    }

    private static string SetWhitespaceBefore(string value, string marker, string desiredWhitespace)
    {
        int markerIndex = value.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return value;
        int start = markerIndex;
        while (start > 0 && IsHorizontalWhitespace(value[start - 1])) start--;
        return value[..start] + desiredWhitespace + value[markerIndex..];
    }

    private static string SetWhitespaceAfter(
        string value, string marker, string desiredWhitespace, bool requireMarkerAtStart = false)
    {
        int markerIndex = value.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return value;
        if (requireMarkerAtStart && !IsHorizontalWhitespace(value[..markerIndex])) return value;
        int start = markerIndex + marker.Length;
        int end = start;
        while (end < value.Length && IsHorizontalWhitespace(value[end])) end++;
        return value[..start] + desiredWhitespace + value[end..];
    }

    private static string WhitespaceBefore(string value, int index)
    {
        int start = index;
        while (start > 0 && IsHorizontalWhitespace(value[start - 1])) start--;
        return value[start..index];
    }

    private static string WhitespaceAfter(string value, int index)
    {
        int end = index;
        while (end < value.Length && IsHorizontalWhitespace(value[end])) end++;
        return value[index..end];
    }

    private static bool IsStructuralMarker(Match match) =>
        match.Groups["kind"].Value is "CONTROL" or "TAG" or "ESCAPE" or "RESOURCE" or "SCRIPT";

    private static bool IsEscapeMarker(Match match) => match.Groups["kind"].Value == "ESCAPE";
    private static bool IsHorizontalWhitespace(string value) => value.All(IsHorizontalWhitespace);
    private static bool IsHorizontalWhitespace(char value) =>
        value is ' ' or '\t' or '\u00A0' or '\u1680' or '\u202F' or '\u205F' or '\u3000' ||
        value is >= '\u2000' and <= '\u200A';
    private static string LeadingHorizontalWhitespace(string value) =>
        value[..value.TakeWhile(IsHorizontalWhitespace).Count()];
    private static string TrailingHorizontalWhitespace(string value) =>
        value[(value.Length - value.Reverse().TakeWhile(IsHorizontalWhitespace).Count())..];

    private static List<TextLine> SplitLines(string text)
    {
        var lines = new List<TextLine>();
        int position = 0;
        while (position < text.Length)
        {
            int end = text.IndexOfAny(['\r', '\n'], position);
            if (end < 0)
            {
                lines.Add(new TextLine(text[position..], string.Empty));
                break;
            }
            string terminator = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n'
                ? "\r\n" : text[end].ToString();
            lines.Add(new TextLine(text[position..end], terminator));
            position = end + terminator.Length;
        }
        if (text.Length == 0) lines.Add(new TextLine(string.Empty, string.Empty));
        return lines;
    }

    private static string JoinLines(IEnumerable<TextLine> lines)
    {
        var result = new StringBuilder();
        foreach (TextLine line in lines) result.Append(line.Content).Append(line.Terminator);
        return result.ToString();
    }

    private sealed class TextLine(string content, string terminator)
    {
        public string Content { get; set; } = content;
        public string Terminator { get; set; } = terminator;
    }
}
