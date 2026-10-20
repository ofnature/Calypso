using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Calypso.Windows;

/// <summary>
/// Calypso's palette, following the suite's ImGui guide (Olympus/.cursor/rules/SKILL.md): dark surfaces, a text
/// ramp, status colours, one identity accent. Calypso's accent is coral (Daedalus is gold, Charon teal).
/// </summary>
internal static class CalypsoTheme
{
    public static readonly Vector4 Accent = new(0.98f, 0.55f, 0.44f, 1.00f);
    public static readonly Vector4 AccentDim = new(0.62f, 0.35f, 0.29f, 1.00f);

    public static readonly Vector4 StatusGreen = new(0.46f, 0.86f, 0.66f, 1.00f);
    public static readonly Vector4 StatusYellow = new(0.92f, 0.74f, 0.34f, 1.00f);
    public static readonly Vector4 StatusRed = new(0.93f, 0.42f, 0.50f, 1.00f);
    public static readonly Vector4 StatusBlue = new(0.40f, 0.68f, 0.98f, 1.00f);
    public static readonly Vector4 StatusGrey = new(0.55f, 0.58f, 0.62f, 1.00f);

    public static readonly Vector4 TextPrimary = new(0.96f, 0.96f, 0.97f, 1.00f);
    public static readonly Vector4 TextSecondary = new(0.70f, 0.72f, 0.76f, 1.00f);
    public static readonly Vector4 TextMuted = new(0.45f, 0.47f, 0.51f, 1.00f);

    /// <summary>
    /// Accent section header. The suite guide's SeparatorText isn't in Dalamud.Bindings.ImGui (API 15), so this is
    /// accent text over a plain separator.
    /// </summary>
    public static void Section(string label)
    {
        ImGui.Spacing();
        ImGui.TextColored(Accent, label);
        ImGui.Separator();
    }

    public static void Dot(Vector4 color)
    {
        ImGui.TextColored(color, "●");
        ImGui.SameLine();
    }

    /// <summary>Hover "(?)" tooltip for a non-obvious control.</summary>
    public static void Help(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    public static void Label(string label, string value)
    {
        ImGui.TextColored(TextSecondary, label);
        ImGui.SameLine();
        ImGui.TextUnformatted(value);
    }
}
