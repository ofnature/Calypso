using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Calypso.Core.Workshop;

/// <summary>
/// Reads a community (Overseas Casuals) schedule the user pasted, for comparison only (decided: never bundled).
/// Tolerant by design, because the exact text varies with how it was copied. The OC post format (see
/// OcScheduleParserTests for the cited posts) is "Season 41, Cycle 7: Workshops #1-3: ∟ Rope (4h) - Cavalier's Hat (6h)
/// … Workshop #4: ∟ Popoto Salad (4h) - …", either line by line or flattened to one line.
/// - "Cycle N" (also "Season X, Cycle N" or "C3") at the start of a line starts day N; "Rest" right after it, or alone on
///   the next line, makes it a rest day. "rest C1" / "rest cycle 1" anywhere makes that day a rest day.
/// - "4th" / "Workshop #4" switches to the fourth workshop; "First 3" / "All" / "Schedule" / "Workshops #1-3" to the
///   main group. Text after a marker on the same line is read as crafts.
/// - Anything else is split on commas, arrows, slashes, bullets and dashes, and each piece is matched to a craft by name
///   with the "Isleworks"/"Islefish"/"Island" prefix, durations like "(4h)" and counts like "x2" ignored; a piece that is
///   not exactly a name matches the longest craft name it contains as whole words.
/// A cycle with neither crafts nor a rest is left out: the text said nothing about it.
/// The parse result lists every piece it could not match so the format can be fixed.
/// </summary>
public static partial class OcScheduleParser
{
    public sealed record ParseResult(WeekPlan Plan, IReadOnlyList<string> Unmatched);

    public static ParseResult Parse(string text, CraftCatalog catalog, long week)
    {
        var byName = catalog.Crafts
            .GroupBy(c => Normalize(c.Name))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.First().Id);
        var namesLongestFirst = byName.Keys.OrderByDescending(k => k.Length).ToList();

        var days = new SortedDictionary<int, (List<int> Main, List<int> Fourth, bool IsRest, bool SawFourth)>();
        var unmatched = new List<string>();
        int? day = null;
        var inFourth = false;

        // Typographic dashes and quotes as plain ones; then every cycle header and workshop marker starts a line, so a
        // post flattened to one line reads like the original.
        text = text.Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u2019', '\'').Replace('\u2018', '\'');
        text = BreakRegex().Replace(text, "\n");

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            foreach (Match restOn in RestOnRegex().Matches(line))
                Set(int.Parse(restOn.Groups[1].Value) - 1, rest: true);
            line = RestOnRegex().Replace(line, " ").Trim();

            var cycle = HeaderRegex().Match(line);
            if (cycle.Success)
            {
                day = int.Parse(cycle.Groups[1].Value) - 1;
                inFourth = false;
                Ensure(day.Value);
                line = line[cycle.Length..].Trim();
                if (Normalize(line).StartsWith("rest", StringComparison.Ordinal))
                {
                    Set(day.Value, rest: true);
                    continue;
                }
            }

            if (day == null || line.Length == 0)
                continue;

            if (RestRegex().IsMatch(line) && Normalize(line).Length <= 12)
            {
                Set(day.Value, rest: true);
                continue;
            }

            if (FourthRegex().Match(line) is { Success: true } fourth)
            {
                inFourth = true;
                days[day.Value] = days[day.Value] with { SawFourth = true };
                line = line[(fourth.Index + fourth.Length)..];
            }
            else if (MainRegex().Match(line) is { Success: true } main)
            {
                inFourth = false;
                line = line[(main.Index + main.Length)..];
            }

            foreach (var piece in SplitRegex().Split(line))
            {
                var name = Normalize(NoiseRegex().Replace(piece, " "));
                if (!name.Any(char.IsLetter) || piece.TrimStart().StartsWith('#') || piece.Contains("http", StringComparison.OrdinalIgnoreCase))
                    continue;

                var padded = $" {name} ";
                var id = byName.TryGetValue(name, out var exact)
                    ? exact
                    : namesLongestFirst.FirstOrDefault(n => padded.Contains($" {n} ", StringComparison.Ordinal)) is { } partial ? byName[partial] : -1;

                if (id < 0)
                {
                    unmatched.Add(piece.Trim());
                    continue;
                }

                var entry = days[day.Value];
                (inFourth ? entry.Fourth : entry.Main).Add(id);
            }
        }

        var plan = new WeekPlan
        {
            Week = week,
            Source = "pasted",
            CreatedUtc = DateTime.UtcNow,
            Cycles = days.Where(kv => kv.Key is >= 0 and < 7 && (kv.Value.IsRest || kv.Value.Main.Count > 0 || kv.Value.Fourth.Count > 0))
                .Select(kv => new CyclePlan
                {
                    Day = kv.Key,
                    Rest = kv.Value.IsRest || kv.Value.Main.Count == 0,
                    Main = kv.Value.Main.Count > 0 ? kv.Value.Main : null,
                    Fourth = kv.Value.Fourth.Count > 0 ? kv.Value.Fourth : null,
                    MainCount = kv.Value.Fourth.Count > 0 || kv.Value.SawFourth ? 3 : 4,
                }).ToList(),
        };

        return new ParseResult(plan, unmatched);

        void Ensure(int d)
        {
            if (!days.ContainsKey(d))
                days[d] = (new List<int>(), new List<int>(), false, false);
        }

        void Set(int d, bool rest)
        {
            Ensure(d);
            days[d] = days[d] with { IsRest = rest };
        }
    }

    /// <summary>
    /// Lower-case, accent-free, prefix-free, letters/digits/apostrophes/spaces only: "Isleworks Garnet Rapier" →
    /// "garnet rapier", "Runner Bean Sauté" → "runner bean saute".
    /// </summary>
    public static string Normalize(string s)
    {
        var folded = new string(s.Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray());
        var t = NonNameRegex().Replace(folded.Replace('\u2019', '\'').ToLowerInvariant(), " ");
        t = PrefixRegex().Replace(t.Trim(), string.Empty);
        return SpaceRegex().Replace(t, " ").Trim();
    }

    /// <summary>A cycle header at the start of a line: "Cycle 3", "C3", "Season 12, Cycle 3:", "Cycle 3 -".</summary>
    [GeneratedRegex(@"^(?:season\s*\d+\s*[,:\-]?\s*)?(?:cycle\s*|c)(\d)\b[\s:.\-]*", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderRegex();

    /// <summary>"rest C1", "rest on cycle 1": OC's standing advice about a specific day.</summary>
    [GeneratedRegex(@"\brest\s+(?:on\s+)?(?:cycle\s*|c)(\d)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RestOnRegex();

    /// <summary>Where a flattened post needs its line breaks back: before each cycle header and workshop marker.</summary>
    [GeneratedRegex(@"(?<=\S[ \t]*)(?=\bseason\s*\d+\s*[,:\-]?\s*cycle\s*\d)|(?<=\S[ \t]*)(?<!season\s*\d+\s*[,:\-]?\s*)(?=\bcycle\s*\d)|(?<=\S[ \t]*)(?=\b(?:workshops?|ws)\s*#\s*\d)", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"\brest\b", RegexOptions.IgnoreCase)]
    private static partial Regex RestRegex();

    [GeneratedRegex(@"\b(?:4th|fourth)(?:\s*(?:workshops?|ws))?\b|\b(?:workshops?|ws)\s*#?\s*4\b[\s:]*", RegexOptions.IgnoreCase)]
    private static partial Regex FourthRegex();

    [GeneratedRegex(@"\bfirst\s*(?:3|three)(?:\s*(?:workshops?|ws))?\b|\ball\s*(?:workshops?|ws)\b|\bschedule\b|\b(?:workshops?|ws)\s*#?\s*1\s*-\s*#?\s*3\b[\s:]*", RegexOptions.IgnoreCase)]
    private static partial Regex MainRegex();

    [GeneratedRegex(@"\s*(?:,|;|→|->|=>|>|/|\||•|·|∟|\s-\s|\s+x\d+\b)\s*")]
    private static partial Regex SplitRegex();

    /// <summary>Durations and counts that ride along with a name: "(4h)", "4h", "x2", "×2".</summary>
    [GeneratedRegex(@"\(?\b\d+\s*h(?:rs?|ours?)?\b\)?|[x×]\s*\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"[^a-z0-9' ]+")]
    private static partial Regex NonNameRegex();

    [GeneratedRegex(@"^(?:isleworks|islefish|island)\s+")]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
