using System;
using System.Collections.Generic;
using Calypso.Core.Tasks;
using Dalamud.Configuration;

namespace Calypso;

/// <summary>
/// Root persistent configuration. Global switches live here; everything a character might want differently
/// lives in a <see cref="CharacterProfile"/> keyed by ContentId, so several toons on one install each keep their
/// own rules. A character seen for the first time gets a copy of <see cref="Defaults"/>.
/// </summary>
public sealed class CalypsoConfig : IPluginConfiguration
{
    /// <summary>Config schema version — bump with a step in <see cref="Migrate"/> when a saved value must change.</summary>
    public int Version { get; set; } = 1;

    /// <summary>2 = gather yields learned before tool signatures existed move into the no-tools signature.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Small always-on-top status strip (state, current task, rank).</summary>
    public bool ShowStatusOverlay { get; set; } = true;

    /// <summary>Publish this box's status on the Daedalus LAN relay. Silent no-op when Daedalus is absent.</summary>
    public bool PublishStatusToFleet { get; set; } = true;

    /// <summary>Obey start/stop commands from other boxes. Off by default: a box only takes orders once you say so.</summary>
    public bool AcceptFleetControl { get; set; } = false;

    /// <summary>Template copied into a character's profile the first time that character logs in.</summary>
    public CharacterProfile Defaults { get; set; } = new();

    public Dictionary<ulong, CharacterProfile> Characters { get; set; } = new();

    /// <summary>What each island node type has given — learned while gathering; the same on every island, so global.</summary>
    public Core.Gathering.YieldBook GatherYields { get; set; } = new();

    /// <summary>Where Isle Return put the player last time (the cozy cabin) — learned, the same on every island.</summary>
    public System.Numerics.Vector3? IsleReturnLanding { get; set; }

    /// <summary>Where wild island animals have been seen (learned; the game files don't place them). Global.</summary>
    public Core.Pasture.AnimalBook AnimalSpots { get; set; } = new();

    /// <summary>The profile for this character, created from <see cref="Defaults"/> on first sight.</summary>
    public CharacterProfile ProfileFor(ulong contentId, string displayName, out bool created)
    {
        created = false;
        if (!Characters.TryGetValue(contentId, out var profile))
        {
            profile = Defaults.CloneSettings();
            Characters[contentId] = profile;
            created = true;
        }

        if (!string.IsNullOrEmpty(displayName) && profile.DisplayName != displayName)
        {
            profile.DisplayName = displayName;
            created = true; // caller saves either way
        }

        return profile;
    }

    /// <summary>Bring a loaded config up to <see cref="CurrentVersion"/>. Returns true when something changed.</summary>
    public bool Migrate()
    {
        if (Version >= CurrentVersion)
            return false;

        if (Version < 2)
            GatherYields.MigrateLegacy();

        Version = CurrentVersion;
        return true;
    }
}

public sealed class CharacterProfile
{
    /// <summary>"Name@World", refreshed on login — display only, the key is the ContentId.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Daily-loop tasks this character skips. Stored as a deny-list so a task added in a later version is part of
    /// the loop unless someone turned it off.
    /// </summary>
    public HashSet<string> DisabledTasks { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Today's unobservable facts (see <see cref="CycleLedger"/>). Never copied between characters.</summary>
    public CycleLedger Ledger { get; set; } = new();

    public Core.Workshop.WorkshopSettings Workshop { get; set; } = new();

    public Gathering.GatherSettings Gathering { get; set; } = new();

    public Exports.MarketSettings Market { get; set; } = new();

    /// <summary>The island layout fingerprint Ariadne last recaptured this character's island with (LayoutWatcher).</summary>
    public string MeshLayout { get; set; } = string.Empty;

    /// <summary>This week's observations, production and plan. Never copied between characters.</summary>
    public Core.Workshop.WorkshopWeek WorkshopWeek { get; set; } = new();

    public bool IsTaskEnabled(string taskId) => !DisabledTasks.Contains(taskId);

    public void SetTaskEnabled(string taskId, bool enabled)
    {
        if (enabled)
            DisabledTasks.Remove(taskId);
        else
            DisabledTasks.Add(taskId);
    }

    /// <summary>A new profile with this one's settings and an empty ledger.</summary>
    public CharacterProfile CloneSettings() => new()
    {
        DisabledTasks = new HashSet<string>(DisabledTasks, StringComparer.Ordinal),
        Workshop = new Core.Workshop.WorkshopSettings
        {
            Mode = Workshop.Mode,
            Objective = Workshop.Objective,
            GatherCostPerUnit = Workshop.GatherCostPerUnit,
        },
        Gathering = new Gathering.GatherSettings
        {
            Goals = new Dictionary<int, int>(Gathering.Goals),
            ForWorkshopPlan = Gathering.ForWorkshopPlan,
        },
        Market = new Exports.MarketSettings
        {
            Keep = Market.Keep,
            SpendIslanders = Market.SpendIslanders,
            SpendSeafarers = Market.SpendSeafarers,
        },
    };
}
