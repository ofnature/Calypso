using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace Calypso.Game;

/// <summary>
/// Market-board prices from Universalis (user decision 2026-10-17). Sends only item ids and the world name, reads only.
/// One request at a time on a background thread; answers come from a 30-minute cache.
/// Price = lowest current NQ listing; velocity = regular sale velocity (sales per day) — an item with no sales is not gil.
/// </summary>
public sealed class MarketPrices : IDisposable
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://universalis.app/api/v2/"), Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConcurrentDictionary<uint, (long Price, double Velocity, DateTime At)> _cache = new();
    private readonly IPluginLog _log;
    private Task? _inflight;

    public MarketPrices(IPluginLog log)
    {
        _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Calypso-Dalamud/0.0.1");
    }

    public bool Busy => _inflight is { IsCompleted: false };
    public string LastError { get; private set; } = string.Empty;
    public DateTime LastRefreshUtc { get; private set; }

    public (long Price, double Velocity)? Get(uint itemId) =>
        _cache.TryGetValue(itemId, out var e) ? (e.Price, e.Velocity) : null;

    /// <summary>Fetch prices for items not cached in the last 30 minutes (or all, when forced). False when a fetch is running.</summary>
    public bool Refresh(string world, IEnumerable<uint> itemIds, bool force = false)
    {
        if (Busy || string.IsNullOrWhiteSpace(world))
            return false;

        var now = DateTime.UtcNow;
        var ids = itemIds.Distinct().Where(id => force || !_cache.TryGetValue(id, out var e) || now - e.At > CacheFor).ToList();
        if (ids.Count == 0)
            return true;

        _inflight = Task.Run(async () =>
        {
            try
            {
                foreach (var chunk in ids.Chunk(100))
                {
                    var url = $"{Uri.EscapeDataString(world)}/{string.Join(",", chunk)}?listings=1&entries=0";
                    using var doc = JsonDocument.Parse(await _http.GetStringAsync(url).ConfigureAwait(false));
                    // One id → the item object itself; several → { "items": { "<id>": {…} } }.
                    var items = doc.RootElement.TryGetProperty("items", out var many)
                        ? many.EnumerateObject().Select(p => p.Value)
                        : new[] { doc.RootElement };
                    foreach (var item in items)
                    {
                        if (!item.TryGetProperty("itemID", out var idEl))
                            continue;
                        var price = item.TryGetProperty("minPriceNQ", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                        var velocity = item.TryGetProperty("regularSaleVelocity", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                        _cache[idEl.GetUInt32()] = (price, velocity, DateTime.UtcNow);
                    }
                }

                LastError = string.Empty;
                LastRefreshUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log.Warning(ex, "Universalis price fetch failed");
            }
        });
        return true;
    }

    public void Dispose() => _http.Dispose();
}
