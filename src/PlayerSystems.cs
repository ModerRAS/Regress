using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>Marks the single-player entity.</summary>
public struct PlayerTag : ITag { }

/// <summary>Handles to the Godot nodes that back the player.</summary>
public struct PlayerBody : IComponent
{
    public CharacterBody3D Node;
    public Camera3D Camera;
}

public struct PlayerState : IComponent
{
    public bool Flying;
    public bool Creative;
    public Block Selected;
    public float Yaw;
    public float Pitch;

    /// <summary>Orientation byte used by the next placement. The placement rule seeds it,
    /// the rotate key steps it with <see cref="Blocks.NextAllowed"/>.</summary>
    public byte PendingOrientation;

    /// <summary>Last values pushed to the scene graph. Node3D.Rotation is derived from the
    /// basis, so comparing against it is not bit-exact and would re-write every frame.</summary>
    public float AppliedYaw;
    public float AppliedPitch;
    public bool Applied;
}

/// <summary>Per-frame intent. Written by the input system, consumed by move / mine / build.</summary>
public struct PlayerIntent : IComponent
{
    public Vector3 Wish;
    public bool Jump, Up, Down, Sprint;

    /// <summary>Left mouse held. Breaking is a duration, not a click.</summary>
    public bool Mining;

    /// <summary>Queued right-clicks. A counter, because a frame can contain several.</summary>
    public int Place;

    /// <summary>Scripted input for --demo and --bench, OR-ed with the keyboard.</summary>
    public bool AutoWalk, AutoSprint, AutoMine;
}

/// <summary>Break progress on the block currently under the crosshair.</summary>
public struct PlayerMining : IComponent
{
    public Vector3I Target;
    public float Progress;
    public bool Active;
}

/// <summary>
/// Player systems. They work on component data rather than on the scene graph, so the ray
/// used for digging comes from the yaw/pitch components instead of from a camera transform
/// that may not have propagated yet. Nothing here mutates the world: mining and building
/// only *request* edits, and the world decides what a break does.
/// </summary>
public static class PlayerSystems
{
    public const float Reach = 6f;
    public const float EyeHeight = 1.62f;
    private const float WalkSpeed = 5.5f;
    private const float SprintSpeed = 9f;
    private const float FlySpeed = 14f;
    private const float Gravity = 26f;
    private const float JumpVelocity = 8.5f;

    /// <summary>Keyboard and mouse buttons -> intent. The only place that reads Input.</summary>
    public static void PollInput(EntityStore store)
    {
        store.Query<PlayerIntent, PlayerState, PlayerBody>().ForEachEntity(
            (ref PlayerIntent intent, ref PlayerState state, ref PlayerBody body, Entity _) =>
        {
            var yaw = Basis.FromEuler(new Vector3(0, state.Yaw, 0));
            var wish = Vector3.Zero;
            if (intent.AutoWalk || Input.IsKeyPressed(Key.W)) wish -= yaw.Z;
            if (Input.IsKeyPressed(Key.S)) wish += yaw.Z;
            if (Input.IsKeyPressed(Key.A)) wish -= yaw.X;
            if (Input.IsKeyPressed(Key.D)) wish += yaw.X;

            intent.Wish = wish.Normalized();
            intent.Jump = Input.IsKeyPressed(Key.Space);
            intent.Up = Input.IsKeyPressed(Key.Space);
            intent.Down = Input.IsKeyPressed(Key.Ctrl);
            intent.Sprint = intent.AutoSprint || Input.IsKeyPressed(Key.Shift);
            intent.Mining = intent.AutoMine
                || (Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsMouseButtonPressed(MouseButton.Left));
        });
    }

    /// <summary>yaw / pitch components -> node rotations.</summary>
    public static void Look(EntityStore store)
    {
        store.Query<PlayerState, PlayerBody>().ForEachEntity((ref PlayerState state, ref PlayerBody body, Entity _) =>
        {
            if (state.Applied && state.AppliedYaw == state.Yaw && state.AppliedPitch == state.Pitch) return;

            body.Node.Rotation = new Vector3(0, state.Yaw, 0);
            body.Camera.Rotation = new Vector3(state.Pitch, 0, 0);
            state.AppliedYaw = state.Yaw;
            state.AppliedPitch = state.Pitch;
            state.Applied = true;
        });
    }

    /// <summary>intent -> velocity -> Godot physics.</summary>
    public static void Move(EntityStore store, VoxelWorld world, float delta)
    {
        store.Query<PlayerIntent, PlayerState, PlayerBody>().ForEachEntity(
            (ref PlayerIntent intent, ref PlayerState state, ref PlayerBody body, Entity entity) =>
        {
            var node = body.Node;

            if (state.Flying)
            {
                float up = (intent.Up ? 1f : 0f) - (intent.Down ? 1f : 0f);
                float speed = intent.Sprint ? FlySpeed * 1.6f : FlySpeed;
                node.Velocity = (intent.Wish + Vector3.Up * up).Normalized() * speed;
            }
            else
            {
                float speed = intent.Sprint ? SprintSpeed : WalkSpeed;
                node.Velocity = new Vector3(intent.Wish.X * speed, node.Velocity.Y, intent.Wish.Z * speed);
                if (node.IsOnFloor())
                {
                    if (intent.Jump) node.Velocity = new Vector3(node.Velocity.X, JumpVelocity, node.Velocity.Z);
                }
                else
                {
                    node.Velocity = new Vector3(node.Velocity.X, node.Velocity.Y - Gravity * delta, node.Velocity.Z);
                }
            }

            node.MoveAndSlide();

            // A kinematic body that ends up inside solid geometry has no way out: MoveAndSlide
            // accumulates velocity but cannot depenetrate. Put the player back on the surface.
            var feet = node.GlobalPosition + new Vector3(0, 0.1f, 0);
            if (Blocks.IsSolid(world.GetBlock(Mathf.FloorToInt(feet.X), Mathf.FloorToInt(feet.Y), Mathf.FloorToInt(feet.Z)))
                || node.GlobalPosition.Y < world.BedrockY - 16f) TeleportToSurface(entity, world);
        });
    }

    /// <summary>Accumulates break progress and emits a request when the block gives way.</summary>
    public static void Mine(EntityStore store, VoxelWorld world, float delta)
    {
        store.Query<PlayerIntent, PlayerState, PlayerBody, PlayerMining>().ForEachEntity(
            (ref PlayerIntent intent, ref PlayerState state, ref PlayerBody body, ref PlayerMining mining, Entity entity) =>
        {
            if (!intent.Mining || !CrosshairBlock(world, body.Node, state.Yaw, state.Pitch, out var cell))
            {
                StopMining(ref mining);
                return;
            }

            var block = world.GetBlock(cell.X, cell.Y, cell.Z);
            float hardness = Blocks.HardnessOf(block);
            if (hardness < 0f)
            {
                StopMining(ref mining);
                return;
            }

            if (!mining.Active || mining.Target != cell)
            {
                mining.Target = cell;
                mining.Progress = 0f;
                mining.Active = true;
            }

            mining.Progress = state.Creative || hardness <= 0f ? 1f : mining.Progress + delta / hardness;
            if (mining.Progress < 1f) return;

            world.RequestEdit(EditRequest.Break(cell, entity));
            mining.Progress = 0f;
        });
    }

    /// <summary>Queued place clicks -> place requests.</summary>
    public static void Build(EntityStore store, VoxelWorld world)
    {
        store.Query<PlayerIntent, PlayerState, PlayerBody>().ForEachEntity(
            (ref PlayerIntent intent, ref PlayerState state, ref PlayerBody body, Entity entity) =>
        {
            for (int i = 0; i < intent.Place; i++)
                if (RequestPlaceAtCrosshair(world, entity) == null) break;
            intent.Place = 0;
        });
    }

    public static void TeleportToSurface(Entity player, VoxelWorld world)
    {
        ref var body = ref player.GetComponent<PlayerBody>();
        var node = body.Node;
        int wx = Mathf.FloorToInt(node.GlobalPosition.X);
        int wz = Mathf.FloorToInt(node.GlobalPosition.Z);
        int y = world.SurfaceY(wx, wz);
        world.EnsureAreaAround(new Vector3(wx + 0.5f, y, wz + 0.5f), 1);
        node.GlobalPosition = new Vector3(wx + 0.5f, y + 0.5f, wz + 0.5f);
        node.Velocity = Vector3.Zero;
    }

    // ---- helpers shared by the systems and the scripted tests ------------

    /// <summary>Block under the crosshair, if it is solid.</summary>
    public static bool CrosshairBlock(VoxelWorld world, CharacterBody3D node, float yaw, float pitch, out Vector3I cell)
    {
        cell = default;
        if (!Raycast(world, node, yaw, pitch, out var point, out var normal)) return false;
        var block = BlockAt(point - normal * 0.5f);
        if (!Blocks.IsSolid(world.GetBlock(block.X, block.Y, block.Z))) return false;
        cell = block;
        return true;
    }

    /// <summary>Empty cell the crosshair is aiming at, if any. <paramref name="clickedFace"/> is
    /// the snapped face of the surface that was hit, or Face.Top when nothing was hit.</summary>
    public static bool PlaceTarget(VoxelWorld world, CharacterBody3D node, float yaw, float pitch,
        out Vector3I cell, out int clickedFace)
    {
        cell = default;
        clickedFace = Regress.Face.Top;
        if (!Raycast(world, node, yaw, pitch, out var point, out var normal)) return false;
        cell = BlockAt(point + normal * 0.5f);
        clickedFace = BlockBehaviors.FaceOfNormal(normal);
        return world.GetBlock(cell.X, cell.Y, cell.Z) == Block.Air;
    }

    /// <summary>Immediate break request for scripted runs, bypassing mining time.</summary>
    public static Vector3I? RequestBreakAtCrosshair(VoxelWorld world, Entity player)
    {
        ref var state = ref player.GetComponent<PlayerState>();
        ref var body = ref player.GetComponent<PlayerBody>();
        if (!CrosshairBlock(world, body.Node, state.Yaw, state.Pitch, out var cell)) return null;
        if (!Blocks.IsBreakable(world.GetBlock(cell.X, cell.Y, cell.Z))) return null;
        world.RequestEdit(EditRequest.Break(cell, player));
        return cell;
    }

    /// <summary>The one place that decides whether a placement is legal, so the interactive
    /// path and the scripted path cannot drift apart.</summary>
    public static Vector3I? RequestPlaceAtCrosshair(VoxelWorld world, Entity player)
    {
        ref var state = ref player.GetComponent<PlayerState>();
        ref var body = ref player.GetComponent<PlayerBody>();
        // The pending orientation is read once here; the rotate key owns every write to it.
        if (!PlaceTarget(world, body.Node, state.Yaw, state.Pitch, out var cell, out int clickedFace)) return null;
        if (PlayerBox(body.Node).Intersects(BlockBox(cell))) return null; // never inside the player
        // The request layer owns the guarantee: a pending byte the selected type disallows is
        // re-derived from the placement rule before it can reach the world.
        if (!Blocks.Allows(state.Selected, state.PendingOrientation))
            state.PendingOrientation = BlockBehaviors.PlaceOrientation(state.Selected, clickedFace, state.Yaw, state.Pitch);
        world.RequestEdit(EditRequest.Place(cell, state.Selected, player, state.PendingOrientation));
        return cell;
    }

    /// <summary>True if something solid is directly in front of the player. Used to tell
    /// "cannot walk" apart from "blocked by a step", which are very different failures.</summary>
    public static bool BlockedAhead(VoxelWorld world, Entity player, float distance)
    {
        ref var state = ref player.GetComponent<PlayerState>();
        ref var body = ref player.GetComponent<PlayerBody>();
        var node = body.Node;
        var eye = node.GlobalPosition + new Vector3(0, 0.9f, 0);
        var dir = -Basis.FromEuler(new Vector3(0, state.Yaw, 0)).Z;
        var query = PhysicsRayQueryParameters3D.Create(eye, eye + dir * distance);
        query.Exclude = new Godot.Collections.Array<Rid> { node.GetRid() };
        return node.GetWorld3D().DirectSpaceState.IntersectRay(query).Count > 0;
    }

    public static Basis CameraBasis(float yaw, float pitch)
        => Basis.FromEuler(new Vector3(pitch, yaw, 0));

    private static void StopMining(ref PlayerMining mining)
    {
        mining.Active = false;
        mining.Progress = 0f;
    }

    private static bool Raycast(VoxelWorld world, CharacterBody3D node, float yaw, float pitch,
        out Vector3 point, out Vector3 normal)
    {
        point = Vector3.Zero;
        normal = Vector3.Zero;

        var eye = node.GlobalPosition + new Vector3(0, EyeHeight, 0);
        var dir = -CameraBasis(yaw, pitch).Z;
        var query = PhysicsRayQueryParameters3D.Create(eye, eye + dir * Reach);
        query.Exclude = new Godot.Collections.Array<Rid> { node.GetRid() };
        var hit = node.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return false;

        point = (Vector3)hit["position"];
        normal = (Vector3)hit["normal"];
        return true;
    }

    private static Vector3I BlockAt(Vector3 p)
        => new(Mathf.FloorToInt(p.X), Mathf.FloorToInt(p.Y), Mathf.FloorToInt(p.Z));

    private static Aabb PlayerBox(CharacterBody3D node)
        => new(node.GlobalPosition - new Vector3(0.35f, 0.1f, 0.35f), new Vector3(0.7f, 1.9f, 0.7f));

    private static Aabb BlockBox(Vector3I cell) => new(new Vector3(cell.X, cell.Y, cell.Z), Vector3.One);
}
