using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.Tests;

/// <summary>Keeps Styles/Tokens.axaml in line with the WCAG table of references/UI.md, section 9.5.</summary>
public partial class PaletteContrastTests
{
    private static readonly Lazy<Dictionary<string, string>> Tokens = new(LoadTokens);

    public static TheoryData<string, string, double> Pairs => new()
    {
        { "TextPrimary", "Background", 7 }, { "TextPrimary", "Surface", 7 }, { "TextPrimary", "SurfaceRaised", 7 },
        { "TextSecondary", "Background", 4.5 }, { "TextSecondary", "Surface", 4.5 }, { "TextSecondary", "SurfaceRaised", 4.5 },
        { "TextDisabled", "Surface", 3 },
        { "OnBrand", "BrandPrimary", 4.5 }, { "OnBrand", "BrandAccent", 4.5 },
        { "BrandPrimary", "SurfaceSidebar", 3 },
        { "BrandAccent", "Surface", 3 }, { "BrandAccent", "SurfaceRaised", 3 },
        { "StatusSuccess", "Surface", 4.5 }, { "StatusWarning", "Surface", 4.5 },
        { "StatusError", "Surface", 4.5 }, { "StatusInfo", "Surface", 4.5 },
        { "OnStatus", "StatusSuccess", 4.5 }, { "OnStatus", "StatusWarning", 4.5 },
        { "Data1", "Surface", 3 }, { "Data2", "Surface", 3 }, { "Data3", "Surface", 3 }, { "Data4", "Surface", 3 }, { "Data5", "Surface", 3 },
        { "Data1", "Background", 3 }, { "Data5", "Background", 3 },
        // Highlighted entities in the Nexus answers are text: 4.5:1 on the transcript and on boards (UI.md, 4.2).
        { "Data1", "Background", 4.5 }, { "Data2", "Background", 4.5 }, { "Data3", "Background", 4.5 }, { "Data4", "Background", 4.5 },
        { "Data1", "Surface", 4.5 }, { "Data2", "Surface", 4.5 }, { "Data3", "Surface", 4.5 }, { "Data4", "Surface", 4.5 },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Token_pair_meets_minimum_contrast(string foreground, string background, double minimum)
    {
        var ratio = Contrast(Tokens.Value[foreground], Tokens.Value[background]);
        Assert.True(ratio >= minimum, $"{foreground} on {background}: {ratio:F2}:1 < {minimum}:1");
    }

    [Fact]
    public void Brand_red_is_distinct_from_error_red()
    {
        Assert.NotEqual(Tokens.Value["BrandPrimary"], Tokens.Value["StatusError"]);
        Assert.NotEqual(Tokens.Value["BrandAccent"], Tokens.Value["StatusError"]);
    }

    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            var c = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    private static Dictionary<string, string> LoadTokens()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "erp_inteligente.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var xaml = File.ReadAllText(Path.Combine(dir.FullName, "Styles", "Tokens.axaml"));
        return ColorToken().Matches(xaml).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant());
    }

    [GeneratedRegex(@"<Color x:Key=""(\w+)Color"">(#[0-9A-Fa-f]{6})</Color>")]
    private static partial Regex ColorToken();
}
