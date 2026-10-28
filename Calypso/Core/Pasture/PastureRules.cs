using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core.Granary;

namespace Calypso.Core.Pasture;

/// <summary>A feed the caretaker can be given (Item row) and the best mood it brings an animal to (item text).</summary>
public sealed record PastureFeed(uint ItemId, string Name, int MoodCap);

/// <summary>
/// The pasture's mechanics and the planner's knobs. "Game text" = the in-game help (DescriptionString rows 709-720) and
/// item text read from xivapi/ffxiv-datamining (game data, facts only, no licence). "Wiki" = Console Games Wiki "Island
/// Pasture" and Icy Veins' Island Sanctuary guide, read through search snippets only (both sites are blocked in the
/// cloud sandbox). Everything marked UNVERIFIED is on the "needs in-game verification" list.
/// </summary>
public sealed record PastureRules
{
    /// <summary>
    /// UNVERIFIED (MJIFarmPastureRank row 1, RankData[1..3].Unknown7 = 5 / 10 / 20 — the column's meaning is inferred;
    /// wiki snippets agree for Pasture I-III). Index = level (0 = not built).
    /// </summary>
    public IReadOnlyList<int> CapacityByLevel { get; init; } = new[] { 0, 5, 10, 20 };

    /// <summary>
    /// Pasture slots an animal of an MJIAnimals size takes. UNVERIFIED (ClientStructs, MIT: a fixed array of 20 animals,
    /// matching the level III capacity; wiki snippets count animals, not sizes). One low-quality page claims per-size
    /// limits; nothing backs it, so every size takes one slot.
    /// </summary>
    public int SlotsFor(int size) => 1;

    /// <summary>
    /// UNVERIFIED (wiki and Icy Veins snippets: one leaving is guaranteed a day). Assumed to be Reward[0]: each
    /// species' variant (MJIAnimals.Unknown0 = 2) swaps its pair's two rewards, which only makes sense if the order
    /// matters. Game text: leavings once a day per animal; mood changes "the type and amount".
    /// </summary>
    public int GuaranteedPerDay { get; init; } = 1;

    /// <summary>UNVERIFIED (snippets give a chance, not an amount): the second leaving (Reward[1]), when it comes.</summary>
    public int SecondPerDay { get; init; } = 1;

    /// <summary>
    /// UNVERIFIED (wiki and Icy Veins snippets agree): chance of the second leaving by mood — index = MJIAnimal.Mood,
    /// 0 Hostile, 1 Unhappy, 2 Content, 3 Chipper, 4 Gleeful (ClientStructs: 0 = very unhappy … 4 = very happy).
    /// </summary>
    public IReadOnlyList<double> SecondChanceByMood { get; init; } = new[] { 0, 0.15, 0.25, 0.5, 0.8 };

    /// <summary>
    /// Verified (game text and item text): the three feeds and the mood each can raise an animal to. Under the
    /// caretaker an animal is assumed to sit at its feed's cap (UNVERIFIED: the caretaker feeds it, how often is not stated).
    /// </summary>
    public IReadOnlyList<PastureFeed> Feeds { get; init; } = new PastureFeed[]
    {
        new(37612, "Island Sweetfeed", 2),
        new(37613, "Island Greenfeed", 3),
        new(37614, "Premium Island Greenfeed", 4),
    };

    /// <summary>
    /// Mood a newly captured animal is counted at: Content, what the cheapest feed (sweetfeed, from island apples)
    /// holds it at — the safe guess for an animal not yet in the pen.
    /// </summary>
    public int NewAnimalMood { get; init; } = 2;

    /// <summary>
    /// UNVERIFIED amount (wiki and Icy Veins: 10 per animal per day). Verified (game text): seafarer's cowries, taken at
    /// the daily reset from every animal marked Under Care. The adapter can read IslandState.PastureDailyCareFee.
    /// </summary>
    public int CareFee { get; init; } = 10;

    /// <summary>Verified (game text): care halts once an animal holds "the maximum of 20" uncollected leavings.</summary>
    public int AnimalCap { get; init; } = 20;

    /// <summary>Verified (Item.StackSize 999 for every leaving; the Addon warns that items over the limit are discarded).</summary>
    public int PouchCap { get; init; } = 999;

    /// <summary>Days of leavings the planner counts — a week, the workshop plan's horizon.</summary>
    public int HorizonDays { get; init; } = 7;

    /// <summary>Every leaving is kept at least this high (lowest priority need), as <c>FarmRules.ProduceFloor</c>.</summary>
    public int LeavingsFloor { get; init; } = 30;

    /// <summary>
    /// Score a capture must add over the animal released for it (one this-week workshop unit = 100). Releasing is
    /// permanent and catching takes the player's time, so small shifts in the need never trade animals.
    /// </summary>
    public double SwapThreshold { get; init; } = 100;

    /// <summary>Score a better feed must add before the planner suggests it (greenfeed costs produce, sweetfeed apples).</summary>
    public double FeedThreshold { get; init; } = 100;

    /// <summary>Score per unit of need covered — the granary planner's weights, so one need list ranks the same everywhere.</summary>
    public double Weight(NeedSource source) => GranaryWeights.Weight(source);

    private static readonly GranaryRules GranaryWeights = new();

    /// <summary>Animals a pasture level holds (0 for unbuilt/unknown levels).</summary>
    public int Capacity(int level) => level > 0 && level < CapacityByLevel.Count ? CapacityByLevel[level] : 0;

    public double SecondChance(int mood) => SecondChanceByMood[Math.Clamp(mood, 0, SecondChanceByMood.Count - 1)];

    /// <summary>The feed with this item id (MJIAnimal.AutoFoodItemId), or null (0 = not under care, or unknown).</summary>
    public PastureFeed? Feed(uint itemId) => Feeds.FirstOrDefault(f => itemId != 0 && f.ItemId == itemId);

    /// <summary>
    /// The restraint for an MJIAnimals size. Item names and "each restraint catches its own kind" are game data; which
    /// size takes which (1 small = net, 2 average = restraint, 3 large = soporific) is UNVERIFIED (wiki snippets).
    /// </summary>
    public static string CaptureTool(int size) => size switch
    {
        1 => "Makeshift Net",
        2 => "Makeshift Restraint",
        3 => "Makeshift Soporific",
        _ => "restraint",
    };

    public static string MoodName(int mood) => mood switch
    {
        0 => "Hostile",
        1 => "Unhappy",
        2 => "Content",
        3 => "Chipper",
        4 => "Gleeful",
        _ => $"mood {mood}",
    };
}
