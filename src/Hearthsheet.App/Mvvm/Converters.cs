using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Hearthsheet.Core.Domain;
using Hearthsheet.Core.Rules;

namespace Hearthsheet.App.Mvvm;

/// <summary>Formats an int as a signed modifier ("+2", "−1").</summary>
public sealed class ModifierConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? CharacterRules.FormatModifier(i) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value is true / non-null / non-zero / non-empty; "Invert" parameter flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            null => false,
            bool b => b,
            int i => i != 0,
            string s => s.Length > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "Invert") truthy = !truthy;
        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when an int value is at least the integer ConverterParameter (death-save pips).</summary>
public sealed class AtLeastConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && int.TryParse(parameter as string, out var n) && i >= n;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => "—",
        Skill s => s.DisplayName(),
        Ability a => a.ToString(),
        RecoveryTiming.ShortRest => "Short or long rest",
        RecoveryTiming.LongRest => "Long rest",
        RecoveryTiming.Manual => "Manual",
        EffectExpiry.ShortRest => "Ends on any rest",
        EffectExpiry.LongRest => "Ends on long rest",
        EffectExpiry.Manual => "Until removed",
        ProficiencyLevel.Half => "Half proficiency",
        _ => SplitWords(value.ToString() ?? ""),
    };

    private static string SplitWords(string s) =>
        string.Concat(s.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString()));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Option lists for combo boxes, exposed statically for XAML.</summary>
public static class Lookups
{
    public sealed record Option(object? Value, string Label);

    public static Ability[] Abilities { get; } = Enum.GetValues<Ability>();

    /// <summary>Abilities plus "none" for nullable ability fields.</summary>
    public static Option[] OptionalAbilities { get; } =
        [new(null, "None"), .. Enum.GetValues<Ability>().Select(a => new Option(a, a.ToString()))];

    public static ProficiencyLevel[] ProficiencyLevels { get; } = Enum.GetValues<ProficiencyLevel>();
    public static ProficiencyCategory[] ProficiencyCategories { get; } = Enum.GetValues<ProficiencyCategory>();
    public static FeatureSource[] FeatureSources { get; } = Enum.GetValues<FeatureSource>();
    public static RecoveryTiming[] RecoveryTimings { get; } = Enum.GetValues<RecoveryTiming>();
    public static EffectExpiry[] EffectExpiries { get; } = Enum.GetValues<EffectExpiry>();
    public static ModifierKind[] ModifierKinds { get; } = Enum.GetValues<ModifierKind>();
    public static IReadOnlyList<string> StatTargetKeys => StatTargets.All;
    public static IReadOnlyList<string> ClassNames { get; } = Core.Content.SrdCatalog.Classes.Select(c => c.Name).ToList();
    public static IReadOnlyList<string> Species => Core.Content.SrdCatalog.Species;
    public static IReadOnlyList<string> Alignments => Core.Content.SrdCatalog.Alignments;
    public static IReadOnlyList<int> HitDieSizes => Core.Content.SrdCatalog.HitDieSizes;
    public static int[] SpellLevels { get; } = [.. Enumerable.Range(0, 10)];
    public static string[] SpellSchools { get; } =
        ["Abjuration", "Conjuration", "Divination", "Enchantment", "Evocation", "Illusion", "Necromancy", "Transmutation"];
}
