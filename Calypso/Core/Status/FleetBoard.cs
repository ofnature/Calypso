using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Calypso.Core.Status;

/// <summary>
/// Relay channel names and the control message. Frames travel over the Daedalus LAN relay
/// (<c>Daedalus.Relay.Publish</c>/<c>Message</c>), which reaches other machines AND sibling clients on this
/// machine, never the publisher itself.
/// </summary>
public static class RelayChannels
{
    /// <summary>Periodic <see cref="CalypsoStatus"/> JSON from every box.</summary>
    public const string Status = "calypso.status";

    /// <summary><see cref="ControlMessage"/> JSON — fleet start/stop.</summary>
    public const string Control = "calypso.control";
}

/// <summary>Fleet command. <see cref="Target"/> is "Name@World", or "*" for every box.</summary>
public sealed class ControlMessage
{
    public const string AllTargets = "*";
    public const string ActStart = "start";
    public const string ActStop = "stop";

    public string Target { get; set; } = AllTargets;
    public string Act { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ControlMessage? TryParse(string json)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<ControlMessage>(json, JsonOptions);
            return msg is { Act: ActStart or ActStop } ? msg : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool IsFor(string self) =>
        Target == AllTargets || string.Equals(Target, self, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Latest status heard from each other box; entries older than <see cref="StaleAfter"/> drop off.</summary>
public sealed class FleetBoard
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(95);

    private readonly Dictionary<string, (CalypsoStatus Status, DateTime SeenUtc)> _boxes =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(CalypsoStatus status, DateTime seenUtc)
    {
        if (string.IsNullOrEmpty(status.Character))
            return;

        _boxes[status.Character] = (status, seenUtc);
    }

    public IReadOnlyList<(CalypsoStatus Status, DateTime SeenUtc)> Current(DateTime utcNow)
    {
        foreach (var stale in _boxes.Where(kv => utcNow - kv.Value.SeenUtc > StaleAfter).Select(kv => kv.Key).ToList())
            _boxes.Remove(stale);

        return _boxes.Values.OrderBy(v => v.Status.Character, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
