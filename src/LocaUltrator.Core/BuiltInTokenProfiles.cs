namespace LocaUltrator.Core;

public static class BuiltInTokenProfiles
{
    public static TokenProfile CreateSafeDefault() => new()
    {
        ProfileName = "Beépített biztonságos tokenvédelem",
        ActivatedUtc = DateTime.UtcNow,
        Rules =
        [
            Rule("double-brace", "Dupla kapcsos változó", "placeholder", @"\{\{[^{}\r\n]{1,100}\}\}", 300),
            Rule("dollar-brace", "Dollárjeles változó", "placeholder", @"\$\{[^{}\r\n]{1,100}\}", 290),
            Rule("brace", "Kapcsos változó", "placeholder", @"\{[^{}\r\n]{1,100}\}", 200),
            Rule("markup-tag", "Formázási címke", "format_tag", @"</?[A-Za-z][^<>\r\n]{0,150}>", 220),
            Rule("printf", "Printf helyőrző", "placeholder", @"%(?:[0-9]{1,3}\$)?[-+0-9.#]{0,20}(?:hh|h|ll|l|L|z|j|t)?[A-Za-z%]", 210),
            Rule("escape", "Escape szekvencia", "escape_sequence", @"\\(?:[""'\\nrt0]|x[0-9A-Fa-f]{2,8}|u[0-9A-Fa-f]{4})", 210),
            Rule("bracket-code", "Szögletes vezérlőkód", "control_code", @"\[\$?[A-Za-z_][A-Za-z0-9_.$:-]{0,80}\]", 180),
            Rule("at-resource", "Kukac erőforrás-azonosító", "resource_id", @"@[A-Z_][A-Z0-9_.:-]{1,80}@", 170)
        ],
        Warnings = ["A beépített profil kizárólag gyakori technikai tokenalakokat véd; export előtt a minőségellenőrzés minden sort újra vizsgál."]
    };

    public static void EnsureApplied(ProjectDocument project, TokenProtectionService protection)
    {
        TokenProfile profile = project.ActiveTokenProfile ?? CreateSafeDefault();
        Apply(project, profile, protection);
    }

    public static void Apply(ProjectDocument project, TokenProfile profile, TokenProtectionService protection)
    {
        TokenProfile? oldProfile = project.ActiveTokenProfile;
        foreach (TranslationUnit unit in project.Units)
        {
            MaskResult oldSource = protection.Mask(unit.SourceText, oldProfile);
            string rawTranslation = oldProfile is null
                ? unit.TranslationMasked
                : protection.Restore(unit.TranslationMasked, oldSource.Tokens);
            MaskResult source = protection.Mask(unit.SourceText, profile);
            string translation = protection.MaskExistingTranslation(rawTranslation, profile, source.Tokens);
            unit.MaskedSource = source.Text;
            unit.MaskedContext = string.IsNullOrEmpty(unit.Context) ? string.Empty : protection.Mask(unit.Context, profile).Text;
            unit.TokenSignature = source.Signature;
            unit.TranslationMasked = translation;
            if (!string.IsNullOrWhiteSpace(translation) &&
                (!protection.ValidateMarkers(source.Text, translation, out _) ||
                 !TextStructureGuard.Validate(source.Text, translation, out _)))
                unit.Status = UnitStatus.NeedsReview;
        }
        profile.ActivatedUtc = DateTime.UtcNow;
        project.ActiveTokenProfile = profile;
    }

    private static TokenRule Rule(string id, string name, string category, string pattern, int priority) => new()
    {
        Id = id,
        Name = name,
        Category = category,
        Pattern = pattern,
        Priority = priority,
        CaseSensitive = true
    };
}
