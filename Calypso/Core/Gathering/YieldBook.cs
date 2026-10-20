using System;
using System.Collections.Generic;
using System.Linq;

namespace Calypso.Core.Gathering;

/// <summary>What one node type gave while the character held one particular set of gathering tools.</summary>
public sealed class NodeObservation
{
    public int Visits { get; set; }

    /// <summary>Item id → total items received.</summary>
    public Dictionary<uint, int> Items { get; set; } = new();
}

/// <summary>
/// What each island node type has given when gathered, learned from Isleventory deltas (decided: no sheet maps node
/// type → items, so the table is learned and corrected at runtime). Keyed by the node's client-language name, and —
/// because tools change drops (a Sugarcane node gives Island Vine without the copper scythe, Sugarcane with it) — by
/// the <see cref="Signature"/> of gathering tools held at the time. Global, not per character.
///
/// After a tool upgrade a node counts as untried again under the new signature; older observations stay useful as
/// hints, because a tool only ever adds drops.
/// </summary>
public sealed class YieldBook
{
    /// <summary>Node name → tool signature → what it gave.</summary>
    public Dictionary<string, Dictionary<string, NodeObservation>> Observed { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // [REPLACED: v1 learned yields without a tool signature — kept only so Migrate() can move them into the
    // no-tools signature; everything recorded before tools existed was, in fact, recorded with no tools]
    public Dictionary<string, Dictionary<uint, int>> Yields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> Visits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stable key for a set of MJIGatheringTool rows: "" = no tools, else sorted rows joined by commas.</summary>
    public static string Signature(IEnumerable<int> toolRows) => string.Join(",", toolRows.Distinct().OrderBy(r => r));

    /// <summary>Move v1 data (no signature) into the no-tools signature. Returns true when anything moved.</summary>
    public bool MigrateLegacy()
    {
        if (Visits.Count == 0 && Yields.Count == 0)
            return false;

        foreach (var (node, visits) in Visits)
        {
            var obs = Get(node, Signature(Array.Empty<int>()));
            obs.Visits += visits;
            if (Yields.TryGetValue(node, out var items))
            {
                foreach (var (item, n) in items)
                    obs.Items[item] = obs.Items.GetValueOrDefault(item) + n;
            }
        }

        Visits.Clear();
        Yields.Clear();
        return true;
    }

    public void Record(string node, string signature, IReadOnlyDictionary<uint, int> gained)
    {
        // [REPLACED: an empty gather counted as a visit — but a node always gives something unless the bag for it is
        // full ("You cannot carry any more…", Korha 2026-10-19), so "gave nothing" taught the book a lie]
        // if (string.IsNullOrWhiteSpace(node))
        //     return;
        if (string.IsNullOrWhiteSpace(node) || !gained.Values.Any(n => n > 0))
            return;

        var obs = Get(node, signature);
        obs.Visits++;
        foreach (var (item, n) in gained)
        {
            if (n > 0)
                obs.Items[item] = obs.Items.GetValueOrDefault(item) + n;
        }
    }

    /// <summary>Tried with exactly these tools.</summary>
    // [REPLACED: visits that gave nothing (full bag, recorded before Record skipped them) made a node look tried]
    // public bool Known(string node, string signature) =>
    //     Observed.TryGetValue(node, out var bySig) && bySig.TryGetValue(signature, out var o) && o.Visits > 0;
    public bool Known(string node, string signature) =>
        Observed.TryGetValue(node, out var bySig) && bySig.TryGetValue(signature, out var o) && o.Visits > 0 && o.Items.Values.Any(n => n > 0);

    /// <summary>Every item this node type has given, under any tools.</summary>
    public IEnumerable<uint> Gives(string node) =>
        Observed.TryGetValue(node, out var bySig) ? bySig.Values.SelectMany(o => o.Items.Where(i => i.Value > 0).Select(i => i.Key)).Distinct() : Enumerable.Empty<uint>();

    /// <summary>Items per visit for any of <paramref name="wanted"/> under one signature (0 when never seen).</summary>
    public double Rate(string node, string signature, IReadOnlyCollection<uint> wanted)
    {
        if (!Observed.TryGetValue(node, out var bySig) || !bySig.TryGetValue(signature, out var o) || o.Visits == 0)
            return 0;
        return wanted.Sum(w => o.Items.GetValueOrDefault(w)) / (double)o.Visits;
    }

    /// <summary>Best rate this node ever showed for <paramref name="wanted"/>, under any tools.</summary>
    public double BestRate(string node, IReadOnlyCollection<uint> wanted) =>
        Observed.TryGetValue(node, out var bySig) ? bySig.Keys.Max(sig => Rate(node, sig, wanted)) : 0;

    /// <summary>
    /// How much a node is worth visiting for <paramref name="wanted"/> holding <paramref name="signature"/>' tools:
    /// tier 0 = gives a wanted item (now, or under older tools), or untried while some wanted item has no known
    /// source; tier 1 = untried; tier 2 = tried with these tools and gave nothing wanted.
    /// </summary>
    public (int Tier, double Rate) Priority(string node, string signature, IReadOnlyCollection<uint> wanted)
    {
        var best = BestRate(node, wanted);
        if (best > 0)
            return (0, best);
        if (Known(node, signature))
            return (2, 0);
        return (Unsourced(wanted).Any() ? 0 : 1, 0);
    }

    /// <summary>
    /// Wanted items no node has ever given. While any exist, untried node types rank with known producers — otherwise
    /// a run farms one item's known source to its goal before looking for the others (seen 2026-10-17: six Tualong
    /// Trees in a row while Island Stone and Palm Leaf sat at 0).
    /// </summary>
    public IEnumerable<uint> Unsourced(IReadOnlyCollection<uint> wanted) =>
        wanted.Where(w => !Observed.Values.SelectMany(bySig => bySig.Values).Any(o => o.Items.GetValueOrDefault(w) > 0));

    // [REPLACED: v1 API without a tool signature — tools change what a node drops, so every query now takes one]
    // public void Record(string node, IReadOnlyDictionary<uint, int> gained) { Visits[node]++; Yields[node][item] += n; }
    // public bool Known(string node) => Visits.GetValueOrDefault(node) > 0;
    // public double Rate(string node, IReadOnlyCollection<uint> wanted) => wanted.Sum(w => Yields[node][w]) / (double)Visits[node];
    // public (int Tier, double Rate) Priority(string node, IReadOnlyCollection<uint> wanted)
    // {
    //     if (!Known(node)) return (Unsourced(wanted).Any() ? 0 : 1, 0);
    //     var rate = Rate(node, wanted);
    //     return rate > 0 ? (0, rate) : (2, 0);
    // }

    private NodeObservation Get(string node, string signature)
    {
        if (!Observed.TryGetValue(node, out var bySig))
            Observed[node] = bySig = new Dictionary<string, NodeObservation>();
        if (!bySig.TryGetValue(signature, out var obs))
            bySig[signature] = obs = new NodeObservation();
        return obs;
    }
}
