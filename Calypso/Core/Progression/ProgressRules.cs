using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Calypso.Core.Progression;

/// <summary>Client-language names of the HUD modes the tutorial rules act on (from MJIHudMode).</summary>
public sealed record HudModeNames(string Sow, string Water, string Capture = "", string Feed = "");

/// <summary>An island NPC from the level layout (planlive/planevent LGB): base id, client-language name, position.</summary>
public sealed record IslandNpc(uint BaseId, string Name, Vector3 Position);

/// <summary>One MJIProgress row: the tutorial objective text and the materials it lists (Unknown3-7 pouch rows, Unknown8-12 amounts).</summary>
public sealed record ProgressStep(int Row, string Objective, IReadOnlyList<(int PouchRow, int Amount)> Materials);

/// <summary>Something the objective text can name, and whether the character already has it.</summary>
public sealed record NamedThing(string Name, bool Done);

/// <summary>
/// A tool the tutorial can ask for, with its MJIRecipe row and materials. <see cref="Automatable"/> is false for recipes
/// with a category wildcard ("any produce") — Calypso only crafts recipes whose materials are concrete.
/// </summary>
public sealed record Craftable(string Name, bool Done, int RecipeRow, IReadOnlyList<(int PouchRow, int Amount)> Materials, bool Automatable);

public enum ProgressActionKind
{
    /// <summary>Collect the step's missing materials in Gather mode.</summary>
    Gather,

    /// <summary>Talk to an island NPC.</summary>
    Talk,

    /// <summary>Craft a tool (its materials are on hand); <see cref="ProgressAction.RecipeRow"/> says which.</summary>
    Craft,

    /// <summary>Sow mode + a seed + interact with an empty cropland plot (probe-verified 2026-10-17).</summary>
    Sow,

    /// <summary>Water mode + interact with a sown plot (probe-verified 2026-10-17).</summary>
    Water,

    /// <summary>Capture mode + a restraint + interact with a wild island animal of the restraint's size (probe 2026-10-17).</summary>
    Capture,

    /// <summary>Feed mode + a feed + interact with one of the character's pasture animals (probe 2026-10-17).</summary>
    Feed,

    /// <summary>Construct a building at a free logboard (probes 10 and 11, 2026-10-19); <see cref="ProgressAction.Build"/> says which.</summary>
    Construct,

    /// <summary>Nothing Calypso can do yet; <see cref="ProgressAction.Reason"/> says what the step needs.</summary>
    Blocked,

    /// <summary>The tutorial is finished (no objective).</summary>
    Finished,
}

public sealed record ProgressAction(ProgressActionKind Kind, string Reason, IReadOnlyList<(int PouchRow, int Missing)> Missing, IReadOnlyList<IslandNpc> Npcs)
{
    /// <summary>For <see cref="ProgressActionKind.Craft"/>: the MJIRecipe row; for a craft's gather: the tool it's for.</summary>
    public Craftable? Craft { get; init; }

    /// <summary>For <see cref="ProgressActionKind.Construct"/> (and the gather before it): the building.</summary>
    public Buildable? Build { get; init; }

    public static ProgressAction Blocked(string reason) => new(ProgressActionKind.Blocked, reason, Array.Empty<(int, int)>(), Array.Empty<IslandNpc>());
}

/// <summary>
/// Decides what the current tutorial step needs, from game data only — no step numbers are hardcoded. The rules, in
/// order (claude-memory.md "Island progression"):
/// 1. the step lists materials the Isleventory doesn't have → gather them;
/// 2. it names a HUD mode other than Free/Gather: Sow → sow (gathering a seed first if there's none), Water → water,
///    Capture → capture with a held restraint, Feed → feed a pasture animal; the rest (Cull, Pet, Beckon) are blocked;
/// 3. it names a tool not yet held → gather the recipe's missing materials, then craft it (probe-verified 2026-10-17;
///    recipes with a "any produce"-style wildcard stay blocked);
/// 4. it names a building not yet built → gather its first tier's missing materials, then construct it at a free
///    logboard (probes 10 and 11, 2026-10-19);
/// 5. it names an island NPC → talk to them (longest name wins: "mk. II" over "mk. I");
/// 6. anything else → blocked, quoting the objective.
/// Names are matched case-insensitively in the client's language, where the objective and the names both come from.
/// </summary>
public static class ProgressRules
{
    public static ProgressAction Decide(
        ProgressStep? step,
        Func<int, int> pouchCount,
        IReadOnlyList<IslandNpc> npcs,
        IReadOnlyList<string> otherHudModes,
        IReadOnlyList<Craftable> craftables,
        // [REPLACED: NamedThing carried no tier name or materials]
        // IReadOnlyList<NamedThing> buildings,
        IReadOnlyList<Buildable> buildings,
        HudModeNames? modes = null,
        (int PouchRow, bool Have)? seed = null,
        bool haveRestraint = false,
        bool haveFeed = false,
        Craftable? restraintRecipe = null)
    {
        if (step == null || string.IsNullOrWhiteSpace(step.Objective))
            return new ProgressAction(ProgressActionKind.Finished, "the island tutorial is complete", Array.Empty<(int, int)>(), Array.Empty<IslandNpc>());

        var text = step.Objective;

        var missing = step.Materials
            .Select(m => (m.PouchRow, Missing: m.Amount - pouchCount(m.PouchRow)))
            .Where(m => m.Missing > 0)
            .ToList();
        if (missing.Count > 0)
            return new ProgressAction(ProgressActionKind.Gather, "collect the step's materials", missing, Array.Empty<IslandNpc>());

        if (modes != null && Mentions(text, modes.Sow))
        {
            if (seed is not { } s)
                return ProgressAction.Blocked($"needs a seed and none can be gathered right now: \"{text}\"");
            return s.Have
                ? new ProgressAction(ProgressActionKind.Sow, "sow a seed in the cropland", Array.Empty<(int, int)>(), Array.Empty<IslandNpc>())
                : new ProgressAction(ProgressActionKind.Gather, "gather a seed to sow", new[] { (s.PouchRow, 1) }, Array.Empty<IslandNpc>());
        }

        if (modes != null && Mentions(text, modes.Water))
            return new ProgressAction(ProgressActionKind.Water, "water the sown plot", Array.Empty<(int, int)>(), Array.Empty<IslandNpc>());

        if (modes != null && modes.Capture.Length > 0 && Mentions(text, modes.Capture))
        {
            if (haveRestraint)
                return new ProgressAction(ProgressActionKind.Capture, "capture a wild animal", Array.Empty<(int, int)>(), Array.Empty<IslandNpc>());
            if (restraintRecipe is not { Automatable: true } net)
                return ProgressAction.Blocked($"needs a restraint and none can be crafted yet: \"{text}\"");

            // No restraint: craft one (net craft recorded 2026-10-17), gathering its materials first if needed.
            var netShort = net.Materials.Select(m => (m.PouchRow, Missing: m.Amount - pouchCount(m.PouchRow))).Where(m => m.Missing > 0).ToList();
            return netShort.Count > 0
                ? new ProgressAction(ProgressActionKind.Gather, $"collect materials for {net.Name}", netShort, Array.Empty<IslandNpc>()) { Craft = net }
                : new ProgressAction(ProgressActionKind.Craft, $"craft {net.Name}", Array.Empty<(int, int)>(), Array.Empty<IslandNpc>()) { Craft = net };
        }

        if (modes != null && modes.Feed.Length > 0 && Mentions(text, modes.Feed))
        {
            return haveFeed
                ? new ProgressAction(ProgressActionKind.Feed, "feed a pasture animal", Array.Empty<(int, int)>(), Array.Empty<IslandNpc>())
                : ProgressAction.Blocked($"needs feed (Island Sweetfeed — its recipe takes \"any produce\", so craft it by hand): \"{text}\"");
        }

        if (otherHudModes.FirstOrDefault(m => Mentions(text, m)) is { } mode)
            return ProgressAction.Blocked($"needs {mode} mode, which Calypso does not automate yet: \"{text}\"");

        // [REPLACED: crafting was blocked until the probe recorded how the craft log works]
        // if (Longest(craftables.Where(c => !c.Done), text) is { } craft)
        //     return ProgressAction.Blocked($"craft {craft.Name} — tool crafting is not automated yet (probe it): \"{text}\"");
        if (craftables.Where(c => !c.Done && Mentions(text, c.Name)).OrderByDescending(c => c.Name.Length).FirstOrDefault() is { } craft)
        {
            if (!craft.Automatable)
                return ProgressAction.Blocked($"craft {craft.Name} — its recipe takes \"any\" of a category, which Calypso doesn't pick yet: \"{text}\"");

            var short_ = craft.Materials
                .Select(m => (m.PouchRow, Missing: m.Amount - pouchCount(m.PouchRow)))
                .Where(m => m.Missing > 0)
                .ToList();
            return short_.Count > 0
                ? new ProgressAction(ProgressActionKind.Gather, $"collect materials for {craft.Name}", short_, Array.Empty<IslandNpc>()) { Craft = craft }
                : new ProgressAction(ProgressActionKind.Craft, $"craft {craft.Name}", Array.Empty<(int, int)>(), Array.Empty<IslandNpc>()) { Craft = craft };
        }

        // [REPLACED: construction blocked until probes 10 (workshop) and 11 (cabin) recorded it — and the names never
        // matched anyway: the sheet says "Cozy Cabin I", the objective "the cozy cabin"; Buildable.Name is the base name]
        // if (Longest(buildings.Where(b => !b.Done), text) is { } building)
        //     return ProgressAction.Blocked($"construct {building.Name} at its logboard — not automated yet (record it once with the probe): \"{text}\"");
        if (buildings.Where(b => !b.Done && Mentions(text, b.Name)).OrderByDescending(b => b.Name.Length).FirstOrDefault() is { } building)
        {
            var buildShort = building.Materials
                .Select(m => (m.PouchRow, Missing: m.Amount - pouchCount(m.PouchRow)))
                .Where(m => m.Missing > 0)
                .ToList();
            return buildShort.Count > 0
                ? new ProgressAction(ProgressActionKind.Gather, $"collect materials for {building.TierName}", buildShort, Array.Empty<IslandNpc>()) { Build = building }
                : new ProgressAction(ProgressActionKind.Construct, $"construct {building.TierName}", Array.Empty<(int, int)>(), Array.Empty<IslandNpc>()) { Build = building };
        }

        var named = npcs.Where(n => Mentions(text, n.Name)).ToList();
        if (named.Count > 0)
        {
            var longest = named.Max(n => n.Name.Length);
            var who = named.Where(n => n.Name.Length == longest).ToList();
            return new ProgressAction(ProgressActionKind.Talk, $"talk to the {who[0].Name}", Array.Empty<(int, int)>(), who);
        }

        return ProgressAction.Blocked($"Calypso does not know how to do this step yet: \"{text}\"");
    }

    // [REPLACED: its only caller (the building rule) now matches Buildable directly]
    // private static NamedThing? Longest(IEnumerable<NamedThing> things, string text) =>
    //     things.Where(t => Mentions(text, t.Name)).OrderByDescending(t => t.Name.Length).FirstOrDefault();

    /// <summary>Whole-phrase, case-insensitive: "mk. I" must not match inside "mk. II".</summary>
    public static bool Mentions(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var at = 0;
        while ((at = text.IndexOf(name, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = at + name.Length;
            var beforeOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var afterOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (beforeOk && afterOk)
                return true;
            at = end;
        }

        return false;
    }
}
