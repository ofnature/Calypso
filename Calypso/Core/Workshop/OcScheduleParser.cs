using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Calypso.Core.Workshop;

/// <summary>
/// Reads a community (Overseas Casuals) schedule the user pasted, for comparison only (decided: never bundled).
/// Tolerant by design, because the exact text varies with how it was copied:
/// - "Cycle N" (also "Season X, Cycle N" or "C3") starts day N; "rest" on that line or the next makes it a rest day.
/// - A line naming "4th" switches to the fourth workshop; "First 3" / "All" / "Schedule" / "Workshops 1-3" to the main group.
/// - Any other line is split on commas, arrows, slashes, bullets and dashes, and each piece is matched to a craft by
///   name with the "Isleworks"/"Islefish"/"Island" prefix ignored.
/// UNVERIFIED against a real paste — the parse result lists every piece it could not match so the format can be fixed.
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

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            var cycle = CycleRegex().Match(line);
            if (cycle.Success)
            {
                day = int.Parse(cycle.Groups[1].Value) - 1;
                inFourth = false;
                Ensure(day.Value);
                if (RestRegex().IsMatch(line))
                    Set(day.Value, rest: true);
                continue;
            }

            if (day == null)
                continue;

            if (RestRegex().IsMatch(line) && Normalize(line).Length <= 12)
            {
                Set(day.Value, rest: true);
                continue;
            }

            if (FourthRegex().IsMatch(line))
            {
                inFourth = true;
                var d = days[day.Value];
                days[day.Value] = d with { SawFourth = true };
                continue;
            }

            if (MainRegex().IsMatch(line))
            {
                inFourth = false;
                continue;
            }

            foreach (var piece in SplitRegex().Split(line))
            {
                var name = Normalize(piece);
                if (name.Length == 0)
                    continue;

                var id = byName.TryGetValue(name, out var exact)
                    ? exact
                    : namesLongestFirst.FirstOrDefault(n => name.Contains(n, StringComparison.Ordinal)) is { } partial ? byName[partial] : -1;

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
            Cycles = days.Where(kv => kv.Key is >= 0 and < 7).Select(kv => new CyclePlan
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

    /// <summary>Lower-case, prefix-free, letters/digits/apostrophes/spaces only: "Isleworks Garnet Rapier" → "garnet rapier".</summary>
    public static string Normalize(string s)
    {
        var t = NonNameRegex().Replace(s.ToLowerInvariant(), " ");
        t = PrefixRegex().Replace(t.Trim(), string.Empty);
        return SpaceRegex().Replace(t, " ").Trim();
    }

    [GeneratedRegex(@"(?:\bcycle\s*|\bc)(\d)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CycleRegex();

    [GeneratedRegex(@"\brest\b", RegexOptions.IgnoreCase)]
    private static partial Regex RestRegex();

    [GeneratedRegex(@"\b4th\b|\bfourth\b|\bworkshop\s*4\b", RegexOptions.IgnoreCase)]
    private static partial Regex FourthRegex();

    [GeneratedRegex(@"\bfirst\s*(?:3|three)\b|\ball\s*workshops?\b|\bschedule\b|\bworkshops?\s*1\s*[-–]\s*3\b", RegexOptions.IgnoreCase)]
    private static partial Regex MainRegex();

    [GeneratedRegex(@"\s*(?:,|;|→|->|=>|>|/|\||•|·|\s[-–]\s|\s+x\d+\b)\s*")]
    private static partial Regex SplitRegex();

    [GeneratedRegex(@"[^a-z0-9' ]+")]
    private static partial Regex NonNameRegex();

    [GeneratedRegex(@"^(?:isleworks|islefish|island)\s+")]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
