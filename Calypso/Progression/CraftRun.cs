using System;
using System.Linq;
using Calypso.Core.Tasks;
using Calypso.Game;

namespace Calypso.Progression;

/// <summary>
/// Craft island recipes the way the probe recorded them. Net craft (2026-10-17) completed the picture:
/// MJIHud [15] opens the log → MJIRecipeNoteBook [i:11, u:tab] picks the tab (0 Tools, 1 Feed, 2 Restraints) →
/// [i:12, u:index] picks the recipe in that tab → [i:14, u:quantity] crafts. Verified by the output appearing (tool) or
/// its Isleventory count rising (restraint / feed).
///
/// (Original summary:) Craft one island tool the way the probe recorded it (claude-memory.md "Probe 2", 2026-10-17):
/// MJIHud callback [15] opens the craft log (MJIRecipeNoteBook), MJIRecipeNoteBook callback [14, u:N] crafts N of the
/// recipe the log has SELECTED. N is a QUANTITY — settled 2026-10-17: sending recipe row+1 (=2) for the stone hammer
/// got "You cannot craft more than one of this item." The log opened with the new tool selected at the top of Tools;
/// choosing another recipe needs the log's selection callbacks (not recorded yet). Success is only claimed when the
/// tool's key item appears.
/// </summary>
public sealed class CraftRun
{
    private const string Hud = "MJIHud";
    private const string CraftLog = "MJIRecipeNoteBook";
    private const int OpenCraftLog = 15;
    private const int CraftSelected = 14;
    private const int SelectTab = 11;
    private const int SelectRecipe = 12;

    private readonly IslandActions _act;
    private readonly IslandLayout _layout;

    private int _recipeRow;
    private int _keyItemRow;
    private uint _outputItem;
    private int _countBefore;
    private int _quantity = 1;
    private int _selectStage;
    private DateTime _lastSelect;
    private readonly Func<uint, int> _itemCount;
    private string _name = string.Empty;
    private bool _fired;
    private DateTime _phaseStart;
    private DateTime _lastOpen;

    public CraftRun(IslandActions act, IslandLayout layout, Func<uint, int> itemCount)
    {
        _act = act;
        _layout = layout;
        _itemCount = itemCount;
    }

    public void Begin(int recipeRow, string name, DateTime now, int quantity = 1)
    {
        _recipeRow = recipeRow;
        _keyItemRow = _layout.KeyItemOf(recipeRow);
        _outputItem = _layout.Recipes.FirstOrDefault(r => r.Row == recipeRow)?.OutputItemId ?? 0;
        _countBefore = _outputItem == 0 ? 0 : _itemCount(_outputItem);
        _quantity = Math.Max(1, quantity);
        _selectStage = 0;
        _lastSelect = DateTime.MinValue;
        _name = name;
        _fired = false;
        _phaseStart = now;
        _lastOpen = DateTime.MinValue;
    }

    public StepResult Step(DateTime now)
    {
        if (_keyItemRow > 0 && _layout.HasKeyItem(_keyItemRow))
            return StepResult.Succeeded($"crafted {_name}");
        if (_keyItemRow == 0 && _outputItem != 0 && _fired && _itemCount(_outputItem) > _countBefore)
            return StepResult.Succeeded($"crafted {_name}");

        if (!_fired)
        {
            if (!_act.IsAddonVisible(CraftLog))
            {
                if (now - _phaseStart > TimeSpan.FromSeconds(10))
                    return StepResult.Failed("the craft log did not open");
                if (now - _lastOpen > TimeSpan.FromSeconds(2))
                {
                    _lastOpen = now;
                    _act.FireCallback(Hud, true, OpenCraftLog);
                }

                return StepResult.Running("opening the craft log");
            }

            if (_act.Busy)
                return StepResult.Running("waiting to craft");

            // Pick the tab, then the recipe, half a second apart, as the player's clicks were.
            if (_selectStage < 2)
            {
                if (now - _lastSelect < TimeSpan.FromMilliseconds(500))
                    return StepResult.Running($"choosing {_name}");
                if (_layout.CraftLogEntry(_recipeRow) is not { } entry)
                    return StepResult.Failed($"{_name} isn't in the craft log (recipe not unlocked?)");
                _act.FireCallback(CraftLog, true, _selectStage == 0 ? SelectTab : SelectRecipe, (uint)(_selectStage == 0 ? entry.Tab : entry.Index));
                _selectStage++;
                _lastSelect = now;
                return StepResult.Running($"choosing {_name}");
            }

            if (now - _lastSelect < TimeSpan.FromMilliseconds(500))
                return StepResult.Running($"choosing {_name}");

            // [REPLACED: u was assumed to be recipe row + 1; it is the quantity to craft]
            // _act.FireCallback(CraftLog, true, CraftSelected, (uint)(_recipeRow + 1));
            _act.FireCallback(CraftLog, true, CraftSelected, (uint)_quantity);
            _fired = true;
            _phaseStart = now;
            return StepResult.Running($"crafting {_name}");
        }

        // The probe saw the materials go ~3.3 s after the craft click.
        return now - _phaseStart < TimeSpan.FromSeconds(15)
            ? StepResult.Running($"crafting {_name}")
            : StepResult.Failed($"{_name} did not appear after crafting — the craft log may have had a different recipe selected");
    }

    public void Abort()
    {
    }
}
