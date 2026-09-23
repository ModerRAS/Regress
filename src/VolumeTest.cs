using System.Collections.Generic;
using Godot;

namespace Regress;

/// <summary>
/// Headless operation-volume scenario: the default 4x4x4 break/place volume, the fine-mode
/// 1-voxel path, the hardest-block time rule and the ghost's visible volume. The whole script
/// runs in the first _Process and quits the same frame — ApplyPendingEdits is called directly,
/// so there is no frame timing and no wall-clock assertion. Every check can fail.
/// </summary>
public partial class VolumeTest : Node
{
	// The fixture sits in air far outside the spawn area; SetBlock creates its chunks on demand.
	// The base is deliberately not 4-aligned: the hit cell below then sits two cells inside the
	// 8-cube, so the 4-aligned volume is fully surrounded by fixture on every side.
	private const int Fx = 60002, Fy = 4002, Fz = 60002;
	private static readonly Vector3I FixtureSize = new(8, 8, 8);
	// Pinned as a literal on purpose: the scenario asserts the shipped coarse volume, so a
	// drifting BlockBehaviors.OperationVolumeCells cannot silently redefine the expectation.
	private const int CoarseCells = 64;

	private readonly VoxelWorld _world;
	private readonly List<string> _passed = new();

	private string _fail;
	private bool _ran;

	public VolumeTest(VoxelWorld world)
	{
		_world = world;
		ProcessPriority = 1; // runs after Game._Process
	}

	public override void _Process(double delta)
	{
		if (_ran) return;
		_ran = true;
		Run();
		Finish();
	}

	private void Run()
	{
		// 1. the default is coarse.
		Check(!new WorldRules().FineMode, "default-coarse", "a fresh WorldRules is fine");

		// 2. the CLI reached WorldRules: the scenario argv passes --fine.
		Check(_world.Rules.FineMode, "cli-fine", $"world.Rules.FineMode={_world.Rules.FineMode}");

		// 3. coarse break: one request removes the whole 4x4x4 volume as ONE edit.
		FillFixture(Block.Stone);
		_world.Rules.FineMode = false;
		var hit = new Vector3I(Fx + 2, Fy + 2, Fz + 2);
		var anchor = BlockBehaviors.OperationAnchor(_world, hit);
		int before = CountCells(new Vector3I(Fx, Fy, Fz), FixtureSize, Block.Stone);
		_world.RequestEdit(EditRequest.Break(hit, default));
		int applied = _world.ApplyPendingEdits();
		int after = CountCells(new Vector3I(Fx, Fy, Fz), FixtureSize, Block.Stone);
		Check(applied == 1, "default-break-64", $"one break request applied {applied} edits");
		Check(before - after == CoarseCells, "default-break-64",
			$"removed {before - after} fixture cells, want {CoarseCells}");
		Check(CountCells(anchor, new Vector3I(4, 4, 4), Block.Air) == CoarseCells,
			"default-break-64", "the anchored volume is not all air");
		// The cells just outside the volume (all still inside the fixture) are untouched.
		var outside = new[]
		{
			anchor + new Vector3I(-1, 0, 0), anchor + new Vector3I(4, 0, 0),
			anchor + new Vector3I(0, -1, 0), anchor + new Vector3I(0, 4, 0),
			anchor + new Vector3I(0, 0, -1), anchor + new Vector3I(0, 0, 4),
		};
		int outsideSolid = 0;
		foreach (var cell in outside)
			if (Blocks.IsSolid(_world.GetBlock(cell.X, cell.Y, cell.Z))) outsideSolid++;
		Check(outsideSolid == outside.Length, "default-break-64",
			$"{outsideSolid}/{outside.Length} cells outside the volume are solid");

		// 4. fine break: exactly the hit cell.
		_world.Rules.FineMode = true;
		var fineHit = new Vector3I(Fx, Fy, Fz);
		before = CountCells(new Vector3I(Fx, Fy, Fz), FixtureSize, Block.Stone);
		_world.RequestEdit(EditRequest.Break(fineHit, default));
		applied = _world.ApplyPendingEdits();
		after = CountCells(new Vector3I(Fx, Fy, Fz), FixtureSize, Block.Stone);
		Check(applied == 1, "fine-break-1", $"one break request applied {applied} edits");
		Check(before - after == 1, "fine-break-1", $"removed {before - after} fixture cells, want 1");
		Check(_world.GetBlock(fineHit.X, fineHit.Y, fineHit.Z) == Block.Air, "fine-break-1",
			"the hit cell is still solid");

		// 5. coarse place in high air: one request fills exactly the aligned volume.
		_world.Rules.FineMode = false;
		var place = new Vector3I(60000, 8000, 60000);
		var placeAnchor = BlockBehaviors.OperationAnchor(_world, place);
		_world.RequestEdit(EditRequest.Place(place, Block.Stone, default));
		applied = _world.ApplyPendingEdits();
		Check(applied == 1, "default-place-64", $"one place request applied {applied} edits");
		Check(CountCells(placeAnchor, new Vector3I(4, 4, 4), Block.Stone) == CoarseCells,
			"default-place-64", "the anchored volume is not 64 Stone cells");
		// A 6-cube around the anchor holds only the volume: nothing outside it was touched.
		Check(CountCells(placeAnchor - new Vector3I(1, 1, 1), new Vector3I(6, 6, 6), Block.Stone)
			== CoarseCells,
			"default-place-64", "a cell outside the volume was placed");

		// 6. fine place: exactly the hit cell.
		_world.Rules.FineMode = true;
		var finePlace = new Vector3I(60008, 8008, 60008);
		_world.RequestEdit(EditRequest.Place(finePlace, Block.Stone, default));
		applied = _world.ApplyPendingEdits();
		Check(applied == 1, "fine-place-1", $"one place request applied {applied} edits");
		Check(CountCells(finePlace - new Vector3I(1, 1, 1), new Vector3I(3, 3, 3), Block.Stone) == 1,
			"fine-place-1", "the 3-cube around the hit cell does not hold exactly one Stone");
		Check(_world.GetBlock(finePlace.X, finePlace.Y, finePlace.Z) == Block.Stone, "fine-place-1",
			"the hit cell was not filled");

		// 7. the time is the hardest block in the volume, not the hit cell's.
		_world.Rules.FineMode = false;
		var hardBase = new Vector3I(60000, 9000, 60000);
		for (int y = 0; y < 4; y++)
			for (int z = 0; z < 4; z++)
				for (int x = 0; x < 4; x++)
					_world.SetBlock(hardBase.X + x, hardBase.Y + y, hardBase.Z + z, Block.Dirt);
		_world.SetBlock(hardBase.X + 1, hardBase.Y + 1, hardBase.Z + 1, Block.Stone);
		float hardest = BlockBehaviors.OperationHardness(_world, hardBase); // a Dirt hit beside the Stone
		Check(hardest == Blocks.HardnessOf(Block.Stone) && hardest > Blocks.HardnessOf(Block.Dirt),
			"hardest-block", $"a Dirt hit in a dirt+stone volume costs {hardest}s");
		float air = BlockBehaviors.OperationHardness(_world, hardBase + new Vector3I(8, 0, 0));
		Check(air == -1f, "hardest-block", $"an Air hit costs {air}s, want -1");

		// 8. the ghost shows the real volume: aligned anchor and extent, coarse and fine.
		var ghost = new PlacementGhost();
		AddChild(ghost);
		var ghostCell = new Vector3I(60001, 10001, 60001); // not 4-aligned: the anchor differs
		_world.Rules.FineMode = false;
		var ghostAnchor = BlockBehaviors.OperationAnchor(_world, ghostCell);
		ghost.Update(_world, new PlacementTarget(ghostCell, Face.Top), Block.Stone, Orientation.None);
		Check(ghost.Position == (Vector3)ghostAnchor, "ghost-volume",
			$"coarse ghost at {ghost.Position}, want the anchor {ghostAnchor}");
		Check(ghost.VolumeBounds.Size == Vector3.One * 4, "ghost-volume",
			$"coarse ghost volume {ghost.VolumeBounds.Size}, want (4,4,4)");
		_world.Rules.FineMode = true;
		ghost.Update(_world, new PlacementTarget(ghostCell, Face.Top), Block.Stone, Orientation.None);
		Check(ghost.Position == (Vector3)ghostCell, "ghost-volume",
			$"fine ghost at {ghost.Position}, want the cell {ghostCell}");
		Check(ghost.VolumeBounds.Size == Vector3.One, "ghost-volume",
			$"fine ghost volume {ghost.VolumeBounds.Size}, want (1,1,1)");
	}

	private void FillFixture(Block block)
	{
		for (int y = 0; y < FixtureSize.Y; y++)
			for (int z = 0; z < FixtureSize.Z; z++)
				for (int x = 0; x < FixtureSize.X; x++)
					_world.SetBlock(Fx + x, Fy + y, Fz + z, block);
	}

	private int CountCells(Vector3I min, Vector3I size, Block block)
	{
		int count = 0;
		for (int y = 0; y < size.Y; y++)
			for (int z = 0; z < size.Z; z++)
				for (int x = 0; x < size.X; x++)
					if (_world.GetBlock(min.X + x, min.Y + y, min.Z + z) == block) count++;
		return count;
	}

	private void Check(bool ok, string name, string detail)
	{
		if (!ok)
		{
			if (_fail == null) _fail = $"{name}: {detail}";
		}
		else if (!_passed.Contains(name))
		{
			_passed.Add(name);
		}
	}

	private void Finish()
	{
		GD.Print(_fail == null
			? $"scenario volume: PASS {string.Join(" ", _passed)}"
			: $"scenario volume: FAIL {_fail}");
		GetTree().Quit(_fail == null ? 0 : 1);
	}
}
