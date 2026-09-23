namespace Regress;

/// <summary>Per-world rule switches. v1 is single-player: the host owns the values
/// (server-authoritative; see docs/multiplayer.md 6.5). One bool per rule.</summary>
public sealed class WorldRules
{
	/// <summary>true = fine mode (1 voxel per break/place). false (default) = one 4x4x4 volume
	/// aligned to the 4-voxel old-block grid.</summary>
	public bool FineMode;
}
