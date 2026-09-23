#!/usr/bin/env python3
"""Run Regress game scenarios and check their output.

Usage:
  python tools/run_game_tests.py [--godot PATH] [--tier A|B|AB] [--only n1,n2]
                                 [--list] [--dry-run] [--selfcheck] [--out DIR]
                                 [--timeout SEC] [--build] [--verbose]

Stdlib only. Scenarios run strictly one at a time.
"""
import argparse
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / ".godot/mono/temp/bin/Debug/Regress.dll"
IMPORTED = ROOT / ".godot/imported"
ARTIFACT_MARKER = "screenshot saved to"

# Scenario data. Phase 2 adds entries here; nothing else needs to change.
SCENARIOS = [
    dict(
        name="selftest",
        tier="A",
        argv=["--headless", "--path", ".", "--", "--selftest"],
        markers=["SELFTEST PASS"],
        exit=0,
    ),
    dict(
        name="demo",
        tier="A",
        argv=["--headless", "--path", ".", "--", "--demo"],
        # FAIL variants print "walk: FAIL" / "interact: FAIL" / "-> FAIL" /
        # "DIGDOWN FAIL", so none of these markers is satisfiable by a FAIL run.
        markers=["walk: PASS", "interact: PASS", " -> PASS", "DIGDOWN PASS"],
        exit=0,
        # echoed on PASS so the CI log carries the walk gate's landing poll (waited=)
        echo=["walk:"],
    ),
    # tier A proven headless (lead, 8.4s, bench landing PASS). CI asserts only
    # completion + landing; headless render columns are dead (adapter empty,
    # draw calls 0) and frame times are dummy-loop pacing, never assert them.
    dict(
        name="bench",
        tier="A",
        argv=["--headless", "--path", ".", "--", "--bench"],
        markers=["bench landing PASS"],
        exit=0,
    ),
    # tier A: touch adapter intent checks (headless, real touch events via Viewport.PushInput).
    dict(
        name="touch",
        tier="A",
        argv=["--headless", "--path", ".", "--", "--touchtest"],
        markers=["scenario touch: PASS"],
        exit=0,
    ),
    # tier B: needs a real renderer; never wired into CI.
    dict(
        name="shot",
        tier="B",
        argv=["--path", ".", "--", "--shot=build/game-tests/shot.png", "--shot-frame=90"],
        markers=[ARTIFACT_MARKER],
        exit=0,
        artifacts=[dict(path="build/game-tests/shot.png", min_bytes=1024)],
    ),
]

# PNG quirk: Godot silently produces no PNG if it dies before --shot-frame, so
# every artifact scenario must also prove it reached the save call.
for _s in SCENARIOS:
    if _s.get("artifacts") and ARTIFACT_MARKER not in _s["markers"]:
        _s["markers"].append(ARTIFACT_MARKER)


def evaluate(scenario, output, exit_code, timed_out, artifact_sizes, timeout=None):
    """Pure check of one scenario run. Returns (ok, detail)."""
    if timed_out:
        return False, f"timeout after {timeout}s" if timeout else "timeout"
    missing = [m for m in scenario["markers"] if m not in output]
    if missing:
        return False, "missing marker: " + ", ".join(missing)
    if exit_code != scenario["exit"]:
        return False, f"exit {exit_code} (expected {scenario['exit']})"
    for art in scenario.get("artifacts", []):
        size = artifact_sizes.get(art["path"])
        if size is None:
            return False, f"missing artifact: {art['path']}"
        if size < art["min_bytes"]:
            return False, f"artifact too small: {art['path']} {size} B < {art['min_bytes']} B"
    if artifact_sizes:
        return True, ", ".join(f"{p} {s} B" for p, s in sorted(artifact_sizes.items()))
    if len(scenario["markers"]) <= 2:
        return True, ", ".join(scenario["markers"])
    return True, f"{len(scenario['markers'])} markers OK"


def resolve_godot(explicit):
    if explicit:
        if not Path(explicit).exists() and not shutil.which(explicit):
            return None, f"--godot not found: {explicit}"
        return explicit, None
    env = os.environ.get("GODOT")
    if env:
        return env, None
    for name in ("godot-mono", "godot"):
        found = shutil.which(name)
        if found:
            return found, None
    return None, ("no Godot binary found; use --godot PATH, set $GODOT, "
                  "or put godot-mono/godot on PATH")


def _rel(p):
    try:
        return p.relative_to(ROOT)
    except ValueError:
        return p


def warn_stale_dll():
    srcs = sorted(ROOT.glob("src/*.cs"))
    newest_src = None
    newest_mtime = 0.0
    for p in srcs + [ROOT / "Regress.csproj"]:
        if p.exists() and p.stat().st_mtime > newest_mtime:
            newest_mtime = p.stat().st_mtime
            newest_src = p
    if not DLL.exists():
        print(f"WARN: Regress.dll missing ({_rel(DLL)}) - run `dotnet build` first.")
    elif newest_src and DLL.stat().st_mtime < newest_mtime:
        print(f"WARN: Regress.dll is older than {_rel(newest_src)} - "
              "stale build, run `dotnet build` first.")


def warn_imports():
    if not IMPORTED.is_dir():
        print("WARN: .godot/imported missing - new res:// textures need "
              "`godot-mono --headless --path . --import` first (no auto-import).")


def run_dotnet_build(out_dir, verbose):
    cmd = ["dotnet", "build"]
    if verbose:
        print("$ " + " ".join(cmd))
    print("[build] dotnet build ...")
    try:
        proc = subprocess.run(
            cmd, cwd=str(ROOT), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace")
    except OSError as exc:
        print(f"build FAILED: {exc}")
        return False
    log = out_dir / "dotnet-build.log"
    log.write_text(proc.stdout or "", encoding="utf-8")
    if proc.returncode != 0:
        print(f"build FAILED (exit {proc.returncode}); last lines of {log}:")
        print(_tail(proc.stdout or ""))
        return False
    return True


def _tail(text, n=40):
    lines = (text or "").strip().splitlines()
    return "\n".join(lines[-n:])


def run_scenario(scenario, godot, out_dir, timeout, verbose):
    """Run one scenario sequentially. Returns (exit_code, output, timed_out, elapsed, sizes)."""
    cmd = [godot] + scenario["argv"]
    if verbose:
        print("$ " + " ".join(cmd))
    print(f"[{scenario['name']}] running ...")
    start = time.monotonic()
    timed_out = False
    try:
        proc = subprocess.Popen(
            cmd, cwd=str(ROOT), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace")
    except OSError as exc:
        return 127, f"failed to launch: {exc}", False, time.monotonic() - start, {}
    try:
        output, _ = proc.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        timed_out = True
        # ponytail: kills the Godot parent only; child processes may outlive it on
        # Windows. Process-tree kill only if stray children are ever observed.
        proc.kill()
        output, _ = proc.communicate()
    elapsed = time.monotonic() - start

    log = out_dir / f"{scenario['name']}.log"
    log.write_text(output or "", encoding="utf-8")
    if verbose and output:
        print(output.rstrip())

    sizes = {}
    for art in scenario.get("artifacts", []):
        p = ROOT / art["path"]
        sizes[art["path"]] = p.stat().st_size if p.exists() else None
    return proc.returncode, output or "", timed_out, elapsed, sizes


def print_table(headers, rows):
    widths = [max(len(str(r[i])) for r in [headers] + rows) for i in range(len(headers))]
    fmt = "  ".join("{:<%d}" % w for w in widths)
    print(fmt.format(*headers).rstrip())
    for row in rows:
        print(fmt.format(*[str(c) for c in row]).rstrip())


def select(tier, only):
    """Resolve scenarios. Raises SystemExit (non-zero) on bad or tier-excluded names."""
    if only:
        names = [n.strip() for n in only.split(",") if n.strip()]
        by_name = {s["name"]: s for s in SCENARIOS}
        unknown = [n for n in names if n not in by_name]
        if unknown:
            raise SystemExit(f"unknown scenario(s): {', '.join(unknown)} "
                             f"(known: {', '.join(by_name)})")
        for n in names:
            s = by_name[n]
            if tier != "AB" and s["tier"] != tier:
                raise SystemExit(f"scenario '{s['name']}' is tier {s['tier']}; pass --tier AB")
        return [s for s in SCENARIOS if s["name"] in names]
    return [s for s in SCENARIOS if tier == "AB" or s["tier"] == tier]


def cmd_scenarios(selected):
    """Print the scenario table with expectations. Runs nothing."""
    rows = [[s["name"], s["tier"], str(s["exit"]),
             " | ".join(s["markers"]),
             ", ".join(f"{a['path']}>{a['min_bytes']}B" for a in s.get("artifacts", [])) or "-",
             " ".join(s["argv"])]
            for s in selected]
    print_table(["name", "tier", "exit", "markers", "artifact", "argv"], rows)


def cmd_dry_run(selected, godot):
    for s in selected:
        print(f"[dry-run] {s['name']}: {godot} " + " ".join(s["argv"]))


def selfcheck():
    base = dict(name="selftest", tier="A", argv=[], markers=["SELFTEST PASS"], exit=0)
    art = dict(name="shot", tier="B", argv=[], markers=[ARTIFACT_MARKER], exit=0,
               artifacts=[dict(path="shot.png", min_bytes=1024)])
    cases = [
        ("marker present + exit 0", base, "SELFTEST PASS\n", 0, False, {}, True),
        ("marker missing", base, "SELFTEST FAIL (1)\n", 0, False, {}, False),
        ("exit 1", base, "SELFTEST PASS\n", 1, False, {}, False),
        ("timeout", base, "SELFTEST PASS\n", 0, True, {}, False),
        ("artifact missing", art, "screenshot saved to x\n", 0, False, {"shot.png": None}, False),
        ("artifact 0 bytes", art, "screenshot saved to x\n", 0, False, {"shot.png": 0}, False),
        ("artifact ok", art, "screenshot saved to x\n", 0, False, {"shot.png": 4096}, True),
    ]
    bad = []
    for label, scn, out, code, timed, sizes, want in cases:
        got, detail = evaluate(scn, out, code, timed, sizes, timeout=300)
        if got != want:
            bad.append(f"{label}: got {'PASS' if got else 'FAIL'} ({detail}), want "
                       f"{'PASS' if want else 'FAIL'}")
    if bad:
        print("run_game_tests selfcheck: FAIL")
        for line in bad:
            print("  " + line)
        return 1
    print("run_game_tests selfcheck: PASS")
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description="Run Regress game scenarios.")
    ap.add_argument("--godot", default=None, help="Godot binary (default: $GODOT, godot-mono, godot)")
    ap.add_argument("--tier", default=None, choices=["A", "B", "AB"],
                    help="tier to run (default A; --list defaults to all)")
    ap.add_argument("--only", default=None, help="comma-separated scenario names")
    ap.add_argument("--list", action="store_true", help="print scenario table, run nothing")
    ap.add_argument("--dry-run", action="store_true", help="print commands, run nothing")
    ap.add_argument("--selfcheck", action="store_true", help="check the evaluator, run no Godot")
    ap.add_argument("--out", default="build/game-tests", help="output dir (default build/game-tests)")
    ap.add_argument("--timeout", type=int, default=300, help="seconds per scenario (default 300)")
    ap.add_argument("--build", action="store_true", help="run `dotnet build` first")
    ap.add_argument("--verbose", action="store_true", help="print each exact command and its output")
    args = ap.parse_args(argv)

    if args.selfcheck:
        return selfcheck()

    # explicit --tier wins; --only / --list default to all tiers
    tier = args.tier or ("AB" if (args.only or args.list) else "A")
    selected = select(tier, args.only)
    if not selected:
        print("no scenarios selected")
        return 0

    if args.list:
        cmd_scenarios(selected)
        return 0

    godot, err = resolve_godot(args.godot)
    if err:
        if not args.dry_run:
            print(f"ERROR: {err}")
            return 2
        print(f"WARN: {err}")
        godot = "<godot>"

    out_dir = ROOT / args.out
    if args.dry_run:
        warn_stale_dll()
        warn_imports()
        if args.build:
            print("[dry-run] dotnet build")
        cmd_dry_run(selected, godot)
        return 0

    out_dir.mkdir(parents=True, exist_ok=True)
    if args.build and not run_dotnet_build(out_dir, args.verbose):
        return 1
    warn_stale_dll()
    warn_imports()

    rows = []
    failed = []
    for s in selected:
        code, output, timed_out, elapsed, sizes = run_scenario(
            s, godot, out_dir, args.timeout, args.verbose)
        ok, detail = evaluate(s, output, code, timed_out, sizes, timeout=args.timeout)
        if ok:
            for line in output.splitlines():
                if any(p in line for p in s.get("echo", ())):
                    print(f"[{s['name']}] {line.rstrip()}")
        rows.append([s["name"], s["tier"], "PASS" if ok else "FAIL", f"{elapsed:.1f}s", detail])
        if not ok:
            failed.append(s["name"])
            print(f"--- {s['name']} output (last 40 lines) ---")
            print(_tail(output))

    print()
    print_table(["name", "tier", "result", "time", "detail"], rows)
    print()
    print(f"summary: {len(selected) - len(failed)}/{len(selected)} passed"
          + (f"; failed: {', '.join(failed)}" if failed else ""))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
