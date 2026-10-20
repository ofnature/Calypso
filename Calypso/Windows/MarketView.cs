using System;
using System.Linq;
using Calypso.Core.Market;
using Calypso.Core.Tasks;
using Calypso.Game;
using Calypso.Gathering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Calypso.Windows;

/// <summary>
/// Exports &amp; Shop tab: the selling rule (at the cap, sell all but Keep), and the cowrie shops ranked by market value
/// per cowrie (Universalis). Buying itself waits for a probe recording of a purchase at the Horrendous Hoarder.
/// </summary>
public sealed class MarketView
{
    private readonly ExportTask _export;
    private readonly TaskRunner _runner;
    private readonly CowrieShop _shop;
    private readonly MarketPrices _prices;
    private readonly IslandActions _act;
    private readonly Func<CharacterProfile?> _profile;
    private readonly Func<string> _world;
    private readonly Action _save;

    public MarketView(ExportTask export, TaskRunner runner, CowrieShop shop, MarketPrices prices, IslandActions act,
        Func<CharacterProfile?> profile, Func<string> world, Action save)
    {
        _export = export;
        _runner = runner;
        _shop = shop;
        _prices = prices;
        _act = act;
        _profile = profile;
        _world = world;
        _save = save;
    }

    public void Draw()
    {
        var profile = _profile();
        if (profile == null)
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, "Log in to see this character's exports and shop.");
            return;
        }

        var m = profile.Market;

        CalypsoTheme.Section("Selling (Enterprising Exporter)");
        ImGui.TextWrapped("When an Isleventory item reaches its cap (999), sell all but");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        var keep = m.Keep;
        if (ImGui.InputInt("##keep", ref keep))
        {
            m.Keep = Math.Clamp(keep, 0, 998);
            _save();
        }
        CalypsoTheme.Help("Anything your gather goals or the workshop plan need is kept too.\nThe island-XP grind sells by itself once its items are full, then goes back to gathering.");

        var full = _export.FullCount();
        ImGui.TextColored(CalypsoTheme.TextSecondary, $"{full} item(s) at the cap now");
        using (ImRaii.Disabled(_runner.IsBusy || full == 0))
        {
            if (ImGui.Button("Sell now"))
                _runner.StartSingle(_export.Id);
        }

        CalypsoTheme.Section("Buying for the market board (cowrie shops)");
        var islanders = _act.ItemCount(CowrieShop.IslandersCowrie);
        var seafarers = _act.ItemCount(CowrieShop.SeafarersCowrie);
        CalypsoTheme.Label("Islander's Cowries:", islanders.ToString("N0"));
        ImGui.SameLine(0, 20);
        CalypsoTheme.Label("Seafarer's Cowries:", seafarers.ToString("N0"));

        var spendIsl = m.SpendIslanders;
        if (ImGui.Checkbox("Spend Islander's", ref spendIsl))
        {
            m.SpendIslanders = spendIsl;
            _save();
        }

        ImGui.SameLine();
        var spendSea = m.SpendSeafarers;
        if (ImGui.Checkbox("Spend Seafarer's", ref spendSea))
        {
            m.SpendSeafarers = spendSea;
            _save();
        }
        CalypsoTheme.Help("Seafarer's Cowries also buy the island's untradable rewards (mounts, minions...), so they're off by default.");

        int Spendable(uint currency) => currency == CowrieShop.IslandersCowrie
            ? (m.SpendIslanders ? islanders - m.ReserveIslanders : 0)
            : (m.SpendSeafarers ? seafarers - m.ReserveSeafarers : 0);

        var world = _world();
        using (ImRaii.Disabled(_prices.Busy || world.Length == 0))
        {
            if (ImGui.Button(_prices.Busy ? "Fetching prices…" : "Refresh prices"))
                _prices.Refresh(world, _shop.Offers.Select(o => o.ItemId), force: true);
        }
        CalypsoTheme.Help($"Market prices from Universalis for {(world.Length > 0 ? world : "your world")} — sends item ids and the world name, nothing else.");
        if (!string.IsNullOrEmpty(_prices.LastError))
            ImGui.TextColored(CalypsoTheme.StatusRed, _prices.LastError);
        else if (_prices.LastRefreshUtc != default)
            ImGui.TextColored(CalypsoTheme.TextMuted, $"prices from {_prices.LastRefreshUtc.ToLocalTime():HH:mm}");

        var ranked = ShoppingPlanner.Rank(_shop.Offers, _prices.Get, Spendable);
        var best = ShoppingPlanner.Best(ranked);
        if (best != null)
            ImGui.TextColored(CalypsoTheme.StatusGreen, $"Best value: {best.Offer.Name} — {best.GilPerCowrie:0.0} gil per cowrie, can buy {best.Affordable}");

        if (ImGui.BeginTable("offers", 6, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY,
                new System.Numerics.Vector2(-1, 240)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Sales/day", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Gil/cowrie", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Can buy", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            foreach (var r in ranked.Take(40))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(r == best ? CalypsoTheme.StatusGreen : CalypsoTheme.TextPrimary, r.Offer.Quantity > 1 ? $"{r.Offer.Quantity}× {r.Offer.Name}" : r.Offer.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.Offer.Cost:N0} {(r.Offer.CurrencyItemId == CowrieShop.IslandersCowrie ? "Isl" : "Sea")}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.UnitPrice > 0 ? r.UnitPrice.ToString("N0") : "-");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.SaleVelocity > 0 ? r.SaleVelocity.ToString("0.#") : "-");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.GilPerCowrie > 0 ? r.GilPerCowrie.ToString("0.0") : "-");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.Affordable.ToString());
            }

            ImGui.EndTable();
        }

        ImGui.TextColored(CalypsoTheme.StatusYellow, "Buying isn't automated yet: record one purchase at the Horrendous Hoarder with the Probe.");
    }
}
