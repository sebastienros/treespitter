using System.Globalization;

namespace TreeSpitter.Lexing;

/// <summary>
/// Resolves the Unicode properties of <c>\p{...}</c> escapes to <see cref="CharSet"/> instances, using the Unicode data of
/// the runtime. General categories are exact, binary properties like <c>XID_Start</c> are close approximations built
/// from the categories. Scripts aren't supported.
/// </summary>
internal static class UnicodeProperties
{
    private static readonly object SyncRoot = new();
    private static CharSet[]? _categories;
    private static readonly Dictionary<string, CharSet> Cache = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, UnicodeCategory[]> CategoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["L"] = [UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter, UnicodeCategory.ModifierLetter, UnicodeCategory.OtherLetter],
        ["Letter"] = [UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter, UnicodeCategory.ModifierLetter, UnicodeCategory.OtherLetter],
        ["LC"] = [UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter],
        ["Cased_Letter"] = [UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter],
        ["Lu"] = [UnicodeCategory.UppercaseLetter],
        ["Uppercase_Letter"] = [UnicodeCategory.UppercaseLetter],
        ["Ll"] = [UnicodeCategory.LowercaseLetter],
        ["Lowercase_Letter"] = [UnicodeCategory.LowercaseLetter],
        ["Lt"] = [UnicodeCategory.TitlecaseLetter],
        ["Titlecase_Letter"] = [UnicodeCategory.TitlecaseLetter],
        ["Lm"] = [UnicodeCategory.ModifierLetter],
        ["Modifier_Letter"] = [UnicodeCategory.ModifierLetter],
        ["Lo"] = [UnicodeCategory.OtherLetter],
        ["Other_Letter"] = [UnicodeCategory.OtherLetter],
        ["M"] = [UnicodeCategory.NonSpacingMark, UnicodeCategory.SpacingCombiningMark, UnicodeCategory.EnclosingMark],
        ["Mark"] = [UnicodeCategory.NonSpacingMark, UnicodeCategory.SpacingCombiningMark, UnicodeCategory.EnclosingMark],
        ["Combining_Mark"] = [UnicodeCategory.NonSpacingMark, UnicodeCategory.SpacingCombiningMark, UnicodeCategory.EnclosingMark],
        ["Mn"] = [UnicodeCategory.NonSpacingMark],
        ["Nonspacing_Mark"] = [UnicodeCategory.NonSpacingMark],
        ["Mc"] = [UnicodeCategory.SpacingCombiningMark],
        ["Spacing_Mark"] = [UnicodeCategory.SpacingCombiningMark],
        ["Me"] = [UnicodeCategory.EnclosingMark],
        ["Enclosing_Mark"] = [UnicodeCategory.EnclosingMark],
        ["N"] = [UnicodeCategory.DecimalDigitNumber, UnicodeCategory.LetterNumber, UnicodeCategory.OtherNumber],
        ["Number"] = [UnicodeCategory.DecimalDigitNumber, UnicodeCategory.LetterNumber, UnicodeCategory.OtherNumber],
        ["Nd"] = [UnicodeCategory.DecimalDigitNumber],
        ["Decimal_Number"] = [UnicodeCategory.DecimalDigitNumber],
        ["digit"] = [UnicodeCategory.DecimalDigitNumber],
        ["Nl"] = [UnicodeCategory.LetterNumber],
        ["Letter_Number"] = [UnicodeCategory.LetterNumber],
        ["No"] = [UnicodeCategory.OtherNumber],
        ["Other_Number"] = [UnicodeCategory.OtherNumber],
        ["P"] = [UnicodeCategory.ConnectorPunctuation, UnicodeCategory.DashPunctuation, UnicodeCategory.OpenPunctuation, UnicodeCategory.ClosePunctuation, UnicodeCategory.InitialQuotePunctuation, UnicodeCategory.FinalQuotePunctuation, UnicodeCategory.OtherPunctuation],
        ["Punctuation"] = [UnicodeCategory.ConnectorPunctuation, UnicodeCategory.DashPunctuation, UnicodeCategory.OpenPunctuation, UnicodeCategory.ClosePunctuation, UnicodeCategory.InitialQuotePunctuation, UnicodeCategory.FinalQuotePunctuation, UnicodeCategory.OtherPunctuation],
        ["punct"] = [UnicodeCategory.ConnectorPunctuation, UnicodeCategory.DashPunctuation, UnicodeCategory.OpenPunctuation, UnicodeCategory.ClosePunctuation, UnicodeCategory.InitialQuotePunctuation, UnicodeCategory.FinalQuotePunctuation, UnicodeCategory.OtherPunctuation],
        ["Pc"] = [UnicodeCategory.ConnectorPunctuation],
        ["Connector_Punctuation"] = [UnicodeCategory.ConnectorPunctuation],
        ["Pd"] = [UnicodeCategory.DashPunctuation],
        ["Dash_Punctuation"] = [UnicodeCategory.DashPunctuation],
        ["Ps"] = [UnicodeCategory.OpenPunctuation],
        ["Open_Punctuation"] = [UnicodeCategory.OpenPunctuation],
        ["Pe"] = [UnicodeCategory.ClosePunctuation],
        ["Close_Punctuation"] = [UnicodeCategory.ClosePunctuation],
        ["Pi"] = [UnicodeCategory.InitialQuotePunctuation],
        ["Initial_Punctuation"] = [UnicodeCategory.InitialQuotePunctuation],
        ["Pf"] = [UnicodeCategory.FinalQuotePunctuation],
        ["Final_Punctuation"] = [UnicodeCategory.FinalQuotePunctuation],
        ["Po"] = [UnicodeCategory.OtherPunctuation],
        ["Other_Punctuation"] = [UnicodeCategory.OtherPunctuation],
        ["S"] = [UnicodeCategory.MathSymbol, UnicodeCategory.CurrencySymbol, UnicodeCategory.ModifierSymbol, UnicodeCategory.OtherSymbol],
        ["Symbol"] = [UnicodeCategory.MathSymbol, UnicodeCategory.CurrencySymbol, UnicodeCategory.ModifierSymbol, UnicodeCategory.OtherSymbol],
        ["Sm"] = [UnicodeCategory.MathSymbol],
        ["Math_Symbol"] = [UnicodeCategory.MathSymbol],
        ["Sc"] = [UnicodeCategory.CurrencySymbol],
        ["Currency_Symbol"] = [UnicodeCategory.CurrencySymbol],
        ["Sk"] = [UnicodeCategory.ModifierSymbol],
        ["Modifier_Symbol"] = [UnicodeCategory.ModifierSymbol],
        ["So"] = [UnicodeCategory.OtherSymbol],
        ["Other_Symbol"] = [UnicodeCategory.OtherSymbol],
        ["Z"] = [UnicodeCategory.SpaceSeparator, UnicodeCategory.LineSeparator, UnicodeCategory.ParagraphSeparator],
        ["Separator"] = [UnicodeCategory.SpaceSeparator, UnicodeCategory.LineSeparator, UnicodeCategory.ParagraphSeparator],
        ["Zs"] = [UnicodeCategory.SpaceSeparator],
        ["Space_Separator"] = [UnicodeCategory.SpaceSeparator],
        ["Zl"] = [UnicodeCategory.LineSeparator],
        ["Line_Separator"] = [UnicodeCategory.LineSeparator],
        ["Zp"] = [UnicodeCategory.ParagraphSeparator],
        ["Paragraph_Separator"] = [UnicodeCategory.ParagraphSeparator],
        ["C"] = [UnicodeCategory.Control, UnicodeCategory.Format, UnicodeCategory.Surrogate, UnicodeCategory.PrivateUse, UnicodeCategory.OtherNotAssigned],
        ["Other"] = [UnicodeCategory.Control, UnicodeCategory.Format, UnicodeCategory.Surrogate, UnicodeCategory.PrivateUse, UnicodeCategory.OtherNotAssigned],
        ["Cc"] = [UnicodeCategory.Control],
        ["Control"] = [UnicodeCategory.Control],
        ["cntrl"] = [UnicodeCategory.Control],
        ["Cf"] = [UnicodeCategory.Format],
        ["Format"] = [UnicodeCategory.Format],
        ["Cs"] = [UnicodeCategory.Surrogate],
        ["Surrogate"] = [UnicodeCategory.Surrogate],
        ["Co"] = [UnicodeCategory.PrivateUse],
        ["Private_Use"] = [UnicodeCategory.PrivateUse],
        ["Cn"] = [UnicodeCategory.OtherNotAssigned],
        ["Unassigned"] = [UnicodeCategory.OtherNotAssigned],
    };

    /// <summary>Returns the set of a property name, like <c>L</c>, <c>Letter</c>, <c>gc=Lu</c> or <c>XID_Start</c>.</summary>
    public static CharSet Get(string name)
    {
        lock (SyncRoot)
        {
            if (Cache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var set = Resolve(name);
            Cache[name] = set;
            return set;
        }
    }

    private static CharSet Resolve(string name)
    {
        var normalized = name.Replace(" ", "_").Replace("-", "_");

        var equals = normalized.IndexOfAny(['=', ':']);
        if (equals >= 0)
        {
            var key = normalized.Substring(0, equals);
            var value = normalized.Substring(equals + 1);
            if (key.Equals("gc", StringComparison.OrdinalIgnoreCase) || key.Equals("General_Category", StringComparison.OrdinalIgnoreCase))
            {
                return Resolve(value);
            }

            throw new NotSupportedException($"The Unicode property '\\p{{{name}}}' is not supported: only general categories and common binary properties are.");
        }

        if (CategoryNames.TryGetValue(normalized, out var categories))
        {
            return Categories(categories);
        }

        switch (normalized.ToUpperInvariant())
        {
            case "ANY":
                return CharSet.All;
            case "ASCII":
                return CharSet.Range(0, 127);
            case "ALPHABETIC":
            case "ALPHA":
                return Get("L").Union(Get("Nl")).Union(Get("Mn")).Union(Get("Mc"));
            case "WHITE_SPACE":
            case "WHITESPACE":
            case "SPACE":
                return CharSet.FromRanges([(0x09, 0x0D), (0x20, 0x20), (0x85, 0x85), (0xA0, 0xA0), (0x1680, 0x1680), (0x2000, 0x200A), (0x2028, 0x2029), (0x202F, 0x202F), (0x205F, 0x205F), (0x3000, 0x3000)]);
            case "UPPERCASE":
            case "UPPER":
                return Get("Lu");
            case "LOWERCASE":
            case "LOWER":
                return Get("Ll");
            case "ID_START":
            case "IDS":
            case "XID_START":
            case "XIDS":
                return Get("L").Union(Get("Nl")).Union(CharSet.FromRanges([(0x1885, 0x1886), (0x2118, 0x2118), (0x212E, 0x212E), (0x309B, 0x309C)]));
            case "ID_CONTINUE":
            case "IDC":
            case "XID_CONTINUE":
            case "XIDC":
                return Get("XID_Start").Union(Get("Mn")).Union(Get("Mc")).Union(Get("Nd")).Union(Get("Pc"))
                    .Union(CharSet.FromRanges([(0xB7, 0xB7), (0x387, 0x387), (0x1369, 0x1371), (0x19DA, 0x19DA), (0x200C, 0x200D)]));
            case "EMOJI":
            case "EXTENDED_PICTOGRAPHIC":
            case "EXTPICT":
                return CharSet.FromRanges([(0x23, 0x23), (0x2A, 0x2A), (0x30, 0x39), (0xA9, 0xA9), (0xAE, 0xAE), (0x203C, 0x203C), (0x2049, 0x2049), (0x2122, 0x2122), (0x2139, 0x2139), (0x2194, 0x21AA), (0x231A, 0x23FF), (0x24C2, 0x24C2), (0x25AA, 0x27BF), (0x2934, 0x2935), (0x2B05, 0x2B55), (0x3030, 0x3030), (0x303D, 0x303D), (0x3297, 0x3299), (0x1F000, 0x1FAFF)]);
        }

        throw new NotSupportedException($"The Unicode property '\\p{{{name}}}' is not supported.");
    }

    private static CharSet Categories(UnicodeCategory[] categories)
    {
        var all = _categories ??= BuildCategorySets();
        var result = CharSet.Empty;
        foreach (var category in categories)
        {
            result = result.Union(all[(int)category]);
        }

        return result;
    }

    private static CharSet[] BuildCategorySets()
    {
        var count = Enum.GetValues(typeof(UnicodeCategory)).Length;
        var ranges = new List<(int, int)>[count];
        for (var i = 0; i < count; i++)
        {
            ranges[i] = [];
        }

        var current = -1;
        var start = 0;
        for (var c = 0; c <= CharSet.MaxCodePoint + 1; c++)
        {
            var category = c > CharSet.MaxCodePoint ? -2 : (int)GetCategory(c);
            if (category != current)
            {
                if (current >= 0)
                {
                    ranges[current].Add((start, c - 1));
                }

                current = category;
                start = c;
            }
        }

        var sets = new CharSet[count];
        for (var i = 0; i < count; i++)
        {
            sets[i] = CharSet.FromRanges(ranges[i]);
        }

        return sets;
    }

    private static UnicodeCategory GetCategory(int codePoint)
    {
#if NET
        return CharUnicodeInfo.GetUnicodeCategory(codePoint);
#else
        if (codePoint <= 0xFFFF)
        {
            return CharUnicodeInfo.GetUnicodeCategory((char)codePoint);
        }

        return CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(codePoint), 0);
#endif
    }
}
