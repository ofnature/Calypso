namespace Calypso.Core.Farm;

/// <summary>
/// One crop: an MJICropSeed row joined with MJIItemPouch. <see cref="Id"/> is the MJICropSeed row (what
/// MJIFarmState.SeedType names, UNVERIFIED); <see cref="SeedItemId"/> is what Entrust(slot, seedItemId) takes.
/// MJICropSeed.Item is the produce; the seed is the MJIItemPouch row of category 3 (Gardening Starter) whose Crop column
/// points at this row. Pouch rows are MJIItemPouch rows, the ids the Isleventory uses.
/// </summary>
public sealed record CropSeed(int Id, int SeedPouchRow, uint SeedItemId, int ProducePouchRow, uint ProduceItemId, string Name);
