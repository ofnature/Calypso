using System.Numerics;
using Calypso.Core.Pasture;

namespace Calypso.Tests.Core;

public class AnimalBookTests
{
    private const uint OpoOpo = 14908; // MJIAnimals row 3 (probe 2026-10-17)

    [Fact]
    public void NearbySightingsAreOneSpot()
    {
        var book = new AnimalBook();
        Assert.True(book.Note(OpoOpo, new Vector3(100, 40, 100)));
        Assert.False(book.Note(OpoOpo, new Vector3(110, 40, 105)));
        Assert.True(book.Note(OpoOpo, new Vector3(200, 40, 100)));
        Assert.Equal(2, book.SpotsOf(new[] { OpoOpo, OpoOpo }).Count());
        Assert.Empty(book.SpotsOf(new uint[] { 1 }));
    }

    [Fact]
    public void SpotsPerSpeciesAreCapped()
    {
        var book = new AnimalBook();
        for (var i = 0; i < AnimalBook.MaxSpotsPerSpecies + 5; i++)
            book.Note(OpoOpo, new Vector3(i * 100, 0, 0));
        Assert.Equal(AnimalBook.MaxSpotsPerSpecies, book.SpotsOf(new[] { OpoOpo }).Count());
    }
}
