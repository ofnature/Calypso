using System;
using System.Numerics;
using Calypso.Core;
using Calypso.Core.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Calypso.Windows;

/// <summary>One-line status strip for multibox at-a-glance: state dot, what's running, island rank.</summary>
public sealed class StatusOverlay : Window
{
    private readonly TaskRunner _runner;
    private readonly Func<IslandSnapshot> _island;
    private readonly Func<bool> _enabled;

    public StatusOverlay(TaskRunner runner, Func<IslandSnapshot> island, Func<bool> enabled)
        : base("Calypso Status##CalypsoOverlay",
            // Saved settings stay ON so the strip remembers where it was dragged.
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav)
    {
        _runner = runner;
        _island = island;
        _enabled = enabled;
        RespectCloseHotkey = false;
        BgAlpha = 0.85f;
        IsOpen = true;
    }

    public override bool DrawConditions() => _enabled();

    public override void Draw()
    {
        var color = _runner.State switch
        {
            RunnerState.Running => CalypsoTheme.StatusGreen,
            RunnerState.Waiting => CalypsoTheme.StatusYellow,
            _ => CalypsoTheme.StatusGrey,
        };

        ImGui.TextColored(CalypsoTheme.Accent, "Calypso");
        ImGui.SameLine();
        CalypsoTheme.Dot(color);
        ImGui.TextUnformatted(_runner.IsBusy ? _runner.Detail : Truncate(_runner.LastResult, 48, "Idle"));

        var island = _island();
        if (island.Available)
        {
            ImGui.SameLine(0, 12);
            ImGui.TextColored(CalypsoTheme.TextSecondary, $"Rank {island.Rank}");
        }
    }

    private static string Truncate(string text, int max, string fallback) =>
        string.IsNullOrEmpty(text) ? fallback : text.Length <= max ? text : text[..(max - 1)] + "…";
}
