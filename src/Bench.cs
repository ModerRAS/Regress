using System.Collections.Generic;
using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>
/// Scripted performance run. Vsync is off so frame times are real. Each phase reports its
/// own frame-time distribution *and* a per-frame breakdown of the ECS systems, so a
/// regression can be attributed instead of guessed at.
/// </summary>
public partial class Bench : Node
{
    private const int WarmupEnd = 150;
    private const int SteadyEnd = 450;
    private const int FlyEnd = 750;
    private const int TeleportEnd = 1050;
    private const int LandEnd = 1110;
    private const int TeleportStride = 45;

    private readonly VoxelWorld _world;
    private readonly Player _player;
    private readonly List<double> _frames = new();

    private double[] _snapshot = Prof.Snapshot();
    private int _frame;
    private double _peakCpu;

    public Bench(VoxelWorld world, Player player)
    {
        _world = world;
        _player = player;
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;
    }

    public override void _Process(double delta)
    {
        _frame++;
        _frames.Add(delta * 1000.0);
        _peakCpu = Mathf.Max(_peakCpu, (double)Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0);

        if (_frame > FlyEnd && _frame <= TeleportEnd && (_frame - FlyEnd) % TeleportStride == 0)
        {
            _player.GlobalPosition += new Vector3(300, 0, 0);
            PlayerSystems.TeleportToSurface(_player.Self, _world);
        }

        switch (_frame)
        {
            case WarmupEnd:
                EndPhase("warmup");
                break;

            case SteadyEnd:
                EndPhase("steady");
                ref var intent = ref _player.Self.GetComponent<PlayerIntent>();
                intent.AutoWalk = true;
                intent.AutoSprint = true;
                _player.Self.GetComponent<PlayerState>().Flying = true;
                break;

            case FlyEnd:
                EndPhase("fly");
                _player.Self.GetComponent<PlayerIntent>().AutoWalk = false;
                _player.Self.GetComponent<PlayerState>().Flying = false;
                break;

            case TeleportEnd:
                EndPhase("teleport");
                PlayerSystems.TeleportToSurface(_player.Self, _world);
                break;

            case LandEnd:
                EndPhase("land");
                bool onGround = _player.IsOnFloor() && _player.GlobalPosition.Y > _world.BedrockY;
                GD.Print(onGround ? "bench landing PASS" : "bench landing FAIL");
                Report();
                GetTree().Quit(0);
                break;
        }
    }

    private void EndPhase(string label)
    {
        if (_frames.Count == 0) return;

        var sorted = new List<double>(_frames);
        sorted.Sort();
        double sum = 0;
        foreach (var f in sorted) sum += f;
        double avg = sum / sorted.Count;
        double p99 = sorted[Mathf.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))];

        GD.Print($"bench {label,-9} n={sorted.Count,4}  avg={avg,7:F2}ms {1000.0 / avg,7:F1}fps   "
            + $"1%low={p99,7:F2}ms {1000.0 / p99,7:F1}fps   best={sorted[0],6:F2}ms");

        var now = Prof.Snapshot();
        for (int i = 0; i < now.Length; i++) now[i] = (now[i] - _snapshot[i]) / sorted.Count;
        double systems = 0;
        foreach (var v in now) systems += v;
        GD.Print($"          per frame: plan={now[0]:F2} gen={now[1]:F2} "
            + $"meshQuery={now[2]:F2} meshWork={now[3]:F2} colQuery={now[4]:F2} colWork={now[5]:F2} "
            + $"| poll={now[6]:F2} look={now[7]:F2} edit={now[8]:F2} move={now[9]:F2} "
            + $"=> systems={systems:F2}ms engine={avg - systems:F2}ms");

        _snapshot = Prof.Snapshot();
        _frames.Clear();
    }

    private void Report()
    {
        GD.Print($"bench adapter: {RenderingServer.GetVideoAdapterName()}");
        GD.Print($"bench chunks: gen avg={Avg(_world.GenMsTotal, _world.GenCount):F2}ms max={_world.GenMsMax:F2}ms "
            + $"n={_world.GenCount} | mesh avg={Avg(_world.MeshMsTotal, _world.MeshCount):F2}ms "
            + $"max={_world.MeshMsMax:F2}ms | collision avg={Avg(_world.CollisionMsTotal, _world.CollisionCount):F2}ms "
            + $"max={_world.CollisionMsMax:F2}ms n={_world.CollisionCount}");
        GD.Print($"bench alloc: mesh={_world.MeshAllocBytes / (double)Mathf.Max(1, _world.MeshCount) / 1024.0:F0} KB/chunk, "
            + $"collision={_world.CollisionAllocBytes / (double)Mathf.Max(1, _world.CollisionCount) / 1024.0:F0} KB/chunk");
        GD.Print($"bench streaming: plans={_world.StreamPlans} unloadScans={_world.UnloadScans}");
        GD.Print($"bench render: draw calls={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} "
            + $"primitives={Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),0} "
            + $"video mem={Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:F1} MB");
        GD.Print($"bench godot: physics step={Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0:F2}ms, "
            + $"peak cpu frame={_peakCpu:F1}ms, static mem={Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0:F1} MB, "
            + $"nodes={Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)}, chunks={_world.LoadedChunks}");
    }

    private static double Avg(double total, int count) => count > 0 ? total / count : 0;
}
