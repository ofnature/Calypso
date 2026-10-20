using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core.Pasture;
using Dalamud.Game.ClientState.Objects.Enums;

namespace Calypso.Game;

/// <summary>
/// Notes every wild island animal loaded nearby into the <see cref="AnimalBook"/>, a few times a minute, whatever the
/// character is doing — gathering runs cover the island, so the book fills without a dedicated search.
/// </summary>
public sealed class AnimalSpotter
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(30);

    private readonly IslandActions _act;
    private readonly AnimalBook _book;
    private readonly HashSet<uint> _species;
    private readonly Action _save;
    private DateTime _next;
    private DateTime _lastSave;
    private bool _dirty;

    public AnimalSpotter(IslandActions act, IslandLayout layout, AnimalBook book, Action save)
    {
        _act = act;
        _book = book;
        _save = save;
        _species = layout.Animals.Select(a => a.BNpcBase).ToHashSet();
    }

    public void Tick(bool onIsland, DateTime now)
    {
        if (!onIsland || now < _next)
            return;
        _next = now + Every;

        foreach (var o in _act.Nearby(o => o.ObjectKind == ObjectKind.BattleNpc && _species.Contains(o.BaseId)))
        {
            if (!_act.IsPastureAnimal(o) && _book.Note(o.BaseId, o.Position))
                _dirty = true;
        }

        if (_dirty && now - _lastSave > SaveEvery)
        {
            _dirty = false;
            _lastSave = now;
            _save();
        }
    }
}
