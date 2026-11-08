using System.Globalization;
using System.Text;

namespace erp_inteligente.Core.Memory;

/// <summary>Case- and accent-insensitive text folding shared by the memory and knowledge search code.</summary>
public static class TextFold
{
    /// <summary>Lower-cases (invariant) and removes diacritics: "Refrigeração" becomes "refrigeracao".</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
