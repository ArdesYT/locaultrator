using System.Text;

namespace LocaUltrator.Core;

public static class TextEncodingRepair
{
    private static readonly UTF8Encoding Utf8Strict = new(false, true);
    private static readonly Encoding Windows1250;
    private static readonly Encoding Windows1252;

    static TextEncodingRepair()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Windows1250 = Encoding.GetEncoding(1250, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        Windows1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static string RepairMojibake(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        string current = value;
        for (int pass = 0; pass < 2; pass++)
        {
            int before = MojibakeScore(current);
            if (before == 0) break;
            string best = current;
            int bestScore = before;
            foreach (Encoding sourceEncoding in new[] { Windows1250, Windows1252 })
            {
                try
                {
                    string repaired = Utf8Strict.GetString(sourceEncoding.GetBytes(current));
                    int repairedScore = MojibakeScore(repaired);
                    if (repairedScore < bestScore)
                    {
                        best = repaired;
                        bestScore = repairedScore;
                    }
                }
                catch (EncoderFallbackException) { }
                catch (DecoderFallbackException) { }
            }
            if (bestScore >= before) break;
            current = best;
        }
        return current;
    }

    private static int MojibakeScore(string value)
    {
        int score = 0;
        foreach (char character in value)
            if (character is 'Ã' or 'Â' or 'Å' or 'Ä' or 'Æ' or 'Ð' or 'Ñ' or 'ð' or 'ï' or
                'Ă' or 'Ĺ' or 'Ľ' or 'Ě' or 'Ď' or 'Ř' or '\uFFFD') score++;
        score += Count(value, "â€") * 2;
        score += Count(value, "ï»¿") * 3;
        score += Count(value, "ï¿½") * 3;
        return score;
    }

    private static int Count(string value, string needle)
    {
        int count = 0;
        for (int index = 0; (index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0; index += needle.Length) count++;
        return count;
    }
}
