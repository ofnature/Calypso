using System;
using System.Collections.Generic;
using Calypso.Core.Granary;

namespace Calypso.Core.Pasture;

/// <summary>One animal in the pasture, from MJIPastureHandler.MJIAnimals[i] (an MJIAnimal).</summary>
public sealed record PenAnimal
{
    /// <summary>Its index in MJIPastureHandler.MJIAnimals — only for the advice text, no call takes it yet.</summary>
    public required int Slot { get; init; }

    /// <summary>MJIAnimal.AnimalType — the <see cref="AnimalDef.Row"/> (MJIAnimals row).</summary>
    public required int Animal { get; init; }

    /// <summary>MJIAnimal.Mood: 0 Hostile … 4 Gleeful.</summary>
    public int Mood { get; init; }

    public bool UnderCare { get; init; }

    /// <summary>Care suspended (game text: cowries short, or 20 leavings waiting); it resumes by itself.</summary>
    public bool CareHalted { get; init; }

    /// <summary>MJIAnimal.AutoFoodItemId: the feed the caretaker uses, 0 when not under care.</summary>
    public uint FeedItemId { get; init; }

    /// <summary>MJIAnimal.AutoAvailableLeavings, both kinds together: what the caretaker holds for this animal.</summary>
    public int Waiting { get; init; }
}

public sealed record PastureInput
{
    /// <summary>Pasture level 1-3, 0 = not built (<c>IslandSnapshot.Pasture</c>).</summary>
    public required int Level { get; init; }

    public bool UnderConstruction { get; init; }

    /// <summary>The animals in the pen (empty MJIAnimal entries left out).</summary>
    public required IReadOnlyList<PenAnimal> Animals { get; init; }

    /// <summary>Every species (MJIAnimals), with leavings as pouch rows.</summary>
    public required IReadOnlyList<AnimalDef> Species { get; init; }

    /// <summary>
    /// Leavings the caretaker gathered and CollectLeavingsAll() would collect, by pouch row
    /// (MJIPastureHandler.AvailableMammetLeavings, keyed by item id — the adapter maps it through MJIItemPouch).
    /// </summary>
    public IReadOnlyDictionary<int, int> Waiting { get; init; } = new Dictionary<int, int>();

    /// <summary>Isleventory count by pouch row before anything is collected.</summary>
    public required IReadOnlyDictionary<int, int> OnHand { get; init; }

    /// <summary>Seafarer's cowries the pasture may spend on care fees (shared with the granary and the cropland).</summary>
    public required int Cowries { get; init; }

    /// <summary>What the island wants (<c>FarmNeeds.FromWorkshop</c>, reservations); needs on rows no animal gives are ignored.</summary>
    public IReadOnlyList<GranaryNeed> Needs { get; init; } = Array.Empty<GranaryNeed>();

    /// <summary>
    /// Species (BNpcBase) <see cref="AnimalBook"/> has a spot for. Capture advice prefers them — a known place, not a
    /// spawn condition (no sheet has those). Null = treat none as seen.
    /// </summary>
    public IReadOnlySet<uint>? Sighted { get; init; }

    /// <summary>Item names by pouch row, for the advice and warnings only. Missing = "pouch N".</summary>
    public IReadOnlyDictionary<int, string>? MaterialNames { get; init; }

    public PastureRules Rules { get; init; } = new();
}

public enum PastureAction
{
    Nothing,

    /// <summary>CollectLeavingsAll() — the one pasture call the adapter carries out.</summary>
    CollectLeavings,

    /// <summary>Nothing to call: <see cref="PasturePlan.Advice"/> asks the player to capture, release or change feed.</summary>
    Advise,
}

public enum AdviceKind
{
    /// <summary>This animal gives something short: do not release it.</summary>
    Keep,

    /// <summary>Catch one of this species (MJIAnimals row in <see cref="PastureAdvice.Animal"/>).</summary>
    Capture,

    /// <summary>Release the animal in <see cref="PastureAdvice.Slot"/> (dismiss its caretaker and collect first).</summary>
    Release,

    /// <summary>Give this animal another feed (<see cref="PastureAdvice.Feed"/>), by hand or as the caretaker's feed.</summary>
    Feed,
}

/// <summary>
/// ADVICE ONLY — for the player to read, never for the adapter to carry out: entrusting, feeding, releasing and
/// capturing are not reverse-engineered. <see cref="Slot"/> is the pen slot (Keep / Release / Feed), null for a capture.
/// </summary>
public sealed record PastureAdvice(AdviceKind Kind, int Animal, int? Slot, uint? Feed, string Text);

/// <summary>
/// The pasture's day. When <see cref="Action"/> is CollectLeavings the adapter calls CollectLeavingsAll(), which takes
/// everything in <see cref="PastureInput.Waiting"/>: <see cref="Collected"/> lands in the pouch, <see cref="Discarded"/>
/// is lost to the cap. <see cref="Advice"/> is filled whatever the action. <see cref="Expected"/> = leavings by pouch
/// row the pen should give over <see cref="PastureRules.HorizonDays"/>; <see cref="CowrieCost"/> = today's care fees.
/// </summary>
public sealed record PasturePlan(
    PastureAction Action,
    IReadOnlyDictionary<int, int> Collected,
    IReadOnlyDictionary<int, int> Discarded,
    IReadOnlyDictionary<int, int> Expected,
    int CowrieCost,
    IReadOnlyList<PastureAdvice> Advice,
    IReadOnlyList<string> Warnings,
    string Reason);
