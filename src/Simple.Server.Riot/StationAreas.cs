using System.Text.RegularExpressions;

namespace Simple.Server.Riot;

/// <summary>
/// Which areas a RIoT station serves. ADR 0002 leaves how a map writes them to
/// the adapter; on the D8 maps the areas are the station's name, which is the
/// previous line's rule (its MapStationResolver.ParseAreaCodes and
/// AreaCodeFormat), carried over: the name, trimmed, split on <c>_</c>, where
/// every part is an area (a capital letter, letters or digits, a hyphen,
/// digits, for example <c>N2-2</c>) and no part repeats, for example
/// <c>N2-2_N3-1</c> for two machines across the aisle from one stop. Any
/// other name (<c>关卡</c>, <c>充电点1</c>) serves no area. The previous line
/// also allowed at most three parts; that limit is not carried over.
/// </summary>
public static partial class StationAreas
{
    public static IReadOnlyList<string> Of(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var parts = name.Trim().Split('_');
        return parts.All(p => Area().IsMatch(p)) && parts.Distinct(StringComparer.Ordinal).Count() == parts.Length ? parts : [];
    }

    [GeneratedRegex("^[A-Z][A-Z0-9]*-[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Area();
}
