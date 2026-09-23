# Release pipeline

Workflow: [`.github/workflows/release.yml`](../.github/workflows/release.yml).
Manual runs: Actions → Release → Run workflow → `version` (e.g. `v0.1.0`) and `dry_run=true`.
Tags `v*` on the main branch run the same path with publishing enabled. Never tag a feature
branch. The repository is public, so hosted runner minutes are free; the real cost is wall-clock
time, and the macOS (`apple`) job dominates it.

## .NET version requirement (measured in CI)

Godot 4.7.2 mono export templates require the C# project to target **net9.0**. The first CI run
failed the Android export with:

```
ERROR: Cannot export project with preset "Android" due to configuration errors:
Exporting to Android when using C#/.NET is experimental.
C# project targets 'net8.0' but the export template only supports 'net9.0'. Consider using gradle builds instead.
```

Source: `platform/android/export/export_plugin.cpp` @ `4.7.2-stable`,
`has_valid_export_configuration` lines 2944-2951 — the TFM gate runs only when
`!gradle_build_enabled`; `_validate_dotnet_tfm` (lines 2887-2932) compares the project's
`TargetFramework` (read via `dotnet build --getProperty:TargetFramework`, lines 2903-2906)
against the required `net9.0` (line 2947, comparison at 2926-2927). Gradle builds skip this
check, but with the project on net9.0 the normal (non-gradle) template route passes, so the
pipeline keeps the simpler non-gradle export. `Regress.csproj` now targets `net9.0`, and both
workflows use `dotnet-version: 9.0.x`.

Local .NET environment (measured on the user's machine; local launch verified):
`dotnet --list-sdks` shows only 10.0.101 and 10.0.301 (no 8.x, no 9.x SDK), and SDK 10 can
target net9.0, so a local `dotnet build` needs nothing installed — the earlier "needs a .NET 9
SDK" claim was wrong. `dotnet --list-runtimes` shows 8.0.28 / 10.0.1 / 10.0.9 (no 9.0 runtime),
yet the merged net9.0 tree still boots: `godot-mono --headless --path . -- --selftest` ends in
`SELFTEST PASS` (322 ok / 0 FAIL), and `python tools/run_game_tests.py --godot godot-mono
--tier A` reports 3/3. One prerequisite after the ETC2 setting change: the stale import cache
must be rebuilt first (`godot-mono --headless --path . --import`), otherwise the game logs
missing `.ctex` resources and does not finish. A `COREHOST_TRACE=1` run shows why the runtime
resolves: Godot's own host config (`GodotSharp/Api/Debug/GodotPlugins.runtimeconfig.json`:
net8.0 + `"rollForward": "LatestMajor"`) drives framework resolution, and hostfxr picks the
highest installed release — **Microsoft.NETCore.App 10.0.9**; the game assembly's own
runtimeconfig (net9.0 + `"rollForward": "LatestMinor"`, the Godot.NET.Sdk default) is not the
resolution path in the editor-host flow. No `<RollForward>` was added to `Regress.csproj`: the
measured launch does not need one.

Merge order: the TFM bump lands after `feat/scale-64` (all of scale-64's evidence was produced
on net8.0); after merging, the full suite and the local launch verification were rerun green
(above), so no `<RollForward>` line is needed.

The Apple embedded export plugin (iOS/macOS) at `4.7.2-stable` has no equivalent TFM
validation, so the exact Android error will not repeat on iOS; if the iOS export fails under
net9.0 it is the same template/.NET-version expectation surfacing elsewhere, not a new root
cause. CI is the evidence.

No `dotnet workload install` step was needed: the Android and iOS .NET publishes completed in
the green runs with no NETSDK1147.

## Android ETC2/ASTC import gate (measured in CI)

The second CI run got past the TFM check and stopped at:

```
ERROR: Cannot export project with preset "Android" due to configuration errors:
Exporting to Android when using C#/.NET is experimental.
ETC2/ASTC texture compression is required for Android export. In Project Settings, search for 'ETC2' in the search field, or enable 'Advanced Settings' and go to Rendering > Textures > VRAM Compression to enable 'Import ETC2 ASTC'.
```

Source: `platform/android/export/export_plugin.cpp` @ `4.7.2-stable` lines 3148-3153 —
`if (!ResourceImporterTextureSettings::should_import_etc2_astc())` fails the preset in
command-line mode (message at line 3151). The Android plugin registers no `texture_format/*`
preset option, so this is fixed in `project.godot`, not in the export preset. The setting is the
project-level import setting `rendering/textures/vram_compression/import_etc2_astc` (read by
`editor/import/resource_importer_texture_settings.cpp:47-48`), written in `project.godot` as
`textures/vram_compression/import_etc2_astc=true`.

Effect: it applies to every platform's `--import` (slower imports, larger `.godot` cache) and
makes the Android APK include ETC2/ASTC textures; desktop platforms still use S3TC/BPTC.

iOS/macOS do not have this gate: `editor/export/editor_export_platform_apple_embedded.cpp`
@ `4.7.2-stable` lines 2202-2234 (`has_valid_export_configuration`) contain no
texture-compression check (and no TFM check either).

## iOS icon gate (measured in CI)

The fifth CI run passed the Team ID gate and then failed on the icon:

```
ERROR: Error opening file ''.
[DONE] export
ERROR: Export Icons: Invalid icon (icons/settings_58x58): ''.
ERROR: Project export for preset "iOS" failed.
```

Source: `platform/ios/export/export_plugin.cpp` @ `4.7.2-stable`, `_export_icons()` lines
234-313: for every size × colour mode (NORMAL/DARK/TINTED) it takes the size-specific preset key
(ours is empty) → falls back to `icons/icon_1024x1024` (+ `_dark`/`_tinted` suffix) → if still
empty, non-NORMAL modes are skipped (`continue`, lines 282-285) → NORMAL falls back to the
project setting `application/config/icon` (line 287) → otherwise `Invalid icon` (line 291). The
preset keys are registered in `editor/export/editor_export_platform_apple_embedded.cpp`
@ `4.7.2-stable` lines 371-373.

Placeholder: `preset.4` uses
`icons/icon_1024x1024="res://texturepacks/default/tiles/grass_top.png"` (a 16×16 game tile)
with `application/icon_interpolation=0` (Nearest neighbor) to keep the pixel-art look when scaled
to 1024. `application/icon_interpolation` is a registered preset key, not a project setting
(`editor/export/editor_export_platform_apple_embedded.cpp` @ `4.7.2-stable` line 282 registers
the enum with default 4; it is read at lines 294, 300 and 318), so it stays in `preset.4`. This
is **not a real icon**; before a public release it must be replaced with a proper 1024×1024
icon. `_dark`/`_tinted` stay empty (the source above skips them).

## macOS editor bundle name (measured in CI)

`Godot_v4.7.2-stable_mono_macos.universal.zip` extracts as **`Godot_mono.app`**, executable
`Godot_mono.app/Contents/MacOS/Godot` (not `Godot.app`). The workflow finds
`-path '*/Contents/MacOS/Godot'` and prints `ls -R "$HOME/godot" | head -40` before the guard so
the next failure is diagnosable.

## Template pack contents (measured in CI, `4.7.2.stable.mono`)

```
ios.zip                     209,219,913 B   <- present, no template gap
android_release.apk         105,159,173 B
android_debug.apk           127,692,461 B
macos.zip                   123,960,003 B
linux_release.x86_64         73,672,920 B
windows_release_x86_64.exe  109,513,728 B
```

The mono template pack does contain the iOS template. The jobs still print the whole template
directory and fail with `TEMPLATE GAP: ios.zip missing` if it ever disappears; this is
reported, never skipped silently.

## Measured runs

### Five-platform green run 35840648834 (`517b0a6`)

All three jobs green. Wall clocks: `desktop-android` 09:03:16 → 09:05:04 (~1 min 48 s),
`apple` 09:03:24 → 09:10:24 (~7 min 00 s), `publish` 09:10:27 → 09:10:37 (~10 s; the release
step is skipped under `dry_run=true`).

Artifacts measured from the publish job's `ls -l build`:

```
Regress-linux-x86_64.zip       65,547,772 B
Regress-windows-x86_64.zip     75,241,544 B
Regress-android.apk           180,408,998 B
Regress-macos-universal.zip   130,299,171 B
Regress-ios-xcodeproj.zip     150,933,194 B
Regress-desktop-android.zip   246,545,877 B   (uploaded bundle, run 35839335298; run 35840648834 same magnitude)
Regress-apple.zip             278,355,278 B   (uploaded bundle)
```

`MACOS_ARCHES=x86_64 arm64` (lipo on the packaged binary). The smoke test hit both expected
lines: `SELFTEST PASS` and
`texpack: using 'Default' (res://texturepacks/default) tile_size=16 tiles=12/12`. The iOS
project-only tree (`build/ios`: `.xcodeproj` + `xcframework` + MoltenVK) is 608,976,896 B.

### Desktop baseline, run 35837339941

First `desktop-android`-only green run (the apple job still failed on the Team ID gate): wall
clock 08:28:48 → 08:31:01 (~2 min 13 s); `Regress-linux-x86_64.zip` 65,547,770 B,
`Regress-windows-x86_64.zip` 75,241,548 B, `Regress-android.apk` 180,408,998 B, uploaded bundle
`Regress-desktop-android.zip` 246,607,208 B. The macOS export succeeded in the same run
(`[ DONE ] export`, `build/macos/Regress.zip`).

ETC2 change vs desktop artifact size: **not measured** — no earlier run reached packaging, so
there is no baseline to compare against.

## Jobs

### `desktop-android` (ubuntu-latest)

Installs: .NET 9 SDK (`setup-dotnet`), Godot `4.7.2-stable` mono Linux editor (cached,
`~/godot`), `4.7.2.stable.mono` export templates (cached,
`~/.local/share/godot/export_templates/4.7.2.stable.mono`), plus the runner's preinstalled
JDK (`JAVA_HOME`) and Android SDK (`ANDROID_HOME`) with `build-tools/apksigner`.

Commands:

```sh
dotnet build
"$HOME/godot/godot" --headless --path . --import
"$HOME/godot/godot" --headless --path . --export-release "Linux"         build/linux/Regress.x86_64
"$HOME/godot/godot" --headless --path . --export-release "Windows Desktop" build/windows/Regress.exe
"$HOME/godot/godot" --headless --path . --export-release "Android"       build/android/Regress.apk
./build/linux/Regress.x86_64 --headless -- --selftest   # grep: SELFTEST PASS + texpack line
```

Android uses the normal template export (`gradle_build/use_gradle_build=false`). The APK is
signed with the debug keystore **Godot generates on the ephemeral runner**, not with the
workflow's own keytool step: `_create_editor_debug_keystore_if_needed()`
(`platform/android/export/export_plugin.cpp` @ `4.7.2-stable` lines 927-983) writes
`~/.local/share/godot/keystores/debug.keystore` using keytool
`-dname "cn=Godot, ou=Godot Engine, o=Stichting Godot, c=NL"` (alias `androiddebugkey`,
store/key password `android`). Run 35837339941 measured the APK signer as `CN=Godot, OU=Godot
Engine, O=Stichting Godot, C=NL`, SHA-256
`91:3F:DA:7E:64:DB:68:5D:00:94:1D:9D:6E:A0:5D:BE:97:52:66:3A:5F:3A:15:C5:6A:65:01:A8:4F:BC:F3:19`,
matching the keystore's `androiddebugkey`. The key is random per run, never committed, and is
**not** a production signing key. Evidence: `keytool -printcert -jarfile` /
`apksigner verify --print-certs`.

The workflow's `keytool -genkeypair` step is only a **fallback** (it runs after `--import`, so on
the green run the file already existed and its `[ ! -f ]` guard skipped it; its
`-dname "CN=Android Debug,O=Android,C=US"` is not the DN that signed the APK):

```sh
keytool -genkeypair -keystore "$HOME/.local/share/godot/keystores/debug.keystore" \
  -storepass android -keypass android -alias androiddebugkey \
  -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Android Debug,O=Android,C=US"
```

A minimal `~/.config/godot/editor_settings-4.tres` (and `editor_settings-4.7.tres`) points
`export/android/debug_keystore`, `export/android/android_sdk_path` and
`export/android/java_sdk_path` at absolute runner paths. The release APK is signed with the same
Godot-generated debug keystore via `GODOT_ANDROID_KEYSTORE_RELEASE_PATH` / `_USER` /
`_PASSWORD`, because `sign_apk()`'s release branch reads only `keystore/release` or those env
vars and never falls back to the debug keystore
(`platform/android/export/export_plugin.cpp` @ `4.7.2-stable` lines 1761-1767, 3333-3349; env
names in `export_plugin.h:50-52`). `preset.3` keeps `keystore/release` empty.

Gradle builds were evaluated and deliberately not used: they would require installing the
Android build template (`ExportTemplateManager::get_android_build_directory` →
`res://android/build` unless `gradle_build/gradle_build_directory` is set; source
`templates/android_source.zip`, CLI `--install-android-build-template`). With net9.0 the
non-gradle route passes, so nothing extra is installed or committed.

Artifacts (bundle `Regress-desktop-android`): `Regress-linux-x86_64.zip`,
`Regress-windows-x86_64.zip`, `Regress-android.apk`.

Android launcher icon: `preset.3` has no `launcher_icons/*` keys and the export still succeeds,
so the APK carries the engine's default launcher icon. This is an appearance gap and a pending
user decision (engine default vs. a brand icon), not a blocker; it is tracked in
[roadmap.md](roadmap.md).

### `apple` (macos-latest)

Installs: .NET 9 SDK, Godot `4.7.2-stable` mono macOS universal editor
(`~/godot/Godot_mono.app/Contents/MacOS/Godot`, resolved as `$GODOT_BIN` — no symlink), templates into
`~/Library/Application Support/Godot/export_templates/4.7.2.stable.mono`, plus the runner's
preinstalled Xcode.

Commands:

```sh
dotnet build
"$GODOT_BIN" --headless --path . --import
"$GODOT_BIN" --headless --path . --export-release "macOS" build/macos/Regress.zip
"$GODOT_BIN" --headless --path . --export-release "iOS"   build/ios/Regress.ipa
unzip -q build/macos/Regress.zip -d "$RUNNER_TEMP/macos_app"
lipo -archs "$(find "$RUNNER_TEMP/macos_app" -type f -path '*/Contents/MacOS/*' -print -quit)"
xcodebuild -project <found.xcodeproj> -scheme <first scheme> -sdk iphoneos -configuration Release \
  -destination 'generic/platform=iOS' \
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" \
  -derivedDataPath /tmp/iosdd build
```

macOS `--import` hung twice with no progress under the old `~/godot/godot` symlink (no output
for ≥15 minutes after the banner). With the explicit `$GODOT_BIN` path (no symlink) it completed
in ~60 s on run 35837339941. The root cause is not settled, but the fix is effective; the
300-second watchdog (which prints `ps -ef`, the import log and Godot's own log before failing)
stays in place.

The workflow prints the actual `build/ios` shape before packaging and zips whatever exists
(project directory if the export produced one, otherwise the export tree). `lipo` evidence is
printed as `MACOS_ARCHES=<archs>`; the Xcode probe prints `IOS_XCODEBUILD_PROBE=PASS|FAIL` and
never fails the job.

Run 35839335298 reported `IOS_XCODEBUILD_PROBE=FAIL`, but that was **our probe script's bug, not
an iOS result**: the scheme name was extracted with its leading indentation (`        Regress`),
so xcodebuild exited with `The project named "Regress" does not contain a scheme named ...` and
the real build never ran. The probe was fixed in `517b0a6` (scheme trimmed with
`awk ... {print $1}`, plus a 1200-second watchdog). Run 35840648834 then gave
**`IOS_XCODEBUILD_PROBE=PASS`** (09:09:06 → 09:09:42, ~36 s): the generated project **builds but
is unsigned** (`xcodebuild -sdk iphoneos -configuration Release CODE_SIGNING_ALLOWED=NO`
succeeded).

Artifacts (bundle `Regress-apple`): `Regress-macos-universal.zip`,
`Regress-ios-xcodeproj.zip`.

### `publish` (ubuntu-latest, needs both jobs)

`actions/download-artifact@v4` with `pattern: 'Regress-*'`, `merge-multiple: true`,
`path: build`, then `ls -l build`, then `softprops/action-gh-release@v2` with
`files: build/*` unless `dry_run=true` (the release step is the only step that touches the
release API).

## What CI proves — and what it does not

- CI proves the exports completed and the artifacts exist and are non-empty (byte floors are
  checked). It does **not** prove the builds run on a phone, tablet or desktop, and it does
  **not** prove the APK/IPA can be installed.
- Android is signed with the debug keystore Godot generates on the runner (alias
  `androiddebugkey`, password `android`, DN `CN=Godot, OU=Godot Engine, O=Stichting Godot,
  C=NL`; random per run). It is **not** a production signing key and must not be used for a
  distributed build.
- iOS is an **unsigned Xcode project** (`application/export_project_only=true`), never an
  `.ipa`: the preset carries the placeholder `application/app_store_team_id="XXXXXXXXXX"`
  because the export gate requires a non-empty Team ID
  (`editor/export/editor_export_platform_apple_embedded.cpp` @ `4.7.2-stable` lines 178-181 and
  1674-1675) and we have no Apple credentials; a user must substitute their own team id to sign
  and deploy to a device. `IOS_XCODEBUILD_PROBE=PASS` in run 35840648834 means it **builds but
  stays unsigned** (`CODE_SIGNING_ALLOWED=NO`). CI must not claim it produces an installable
  iOS build.
- macOS/Windows/Linux artifacts are unsigned and not notarized; a downloaded macOS build is
  Gatekeeper-quarantined and the user must allow it manually.
- macOS architecture: `MACOS_ARCHES=x86_64 arm64` was measured by `lipo` in run 35840648834
  (the preset requests `binary_format/architecture="universal"` and the template really is
  universal; if a future template reports only `x86_64`, Apple Silicon needs Rosetta 2).
- The `publish` step has never executed: every run so far used `dry_run=true`, so the
  "tag → automatic GitHub release" path is **not end-to-end verified**; only its `if` guard has
  been checked.

## Unverified until CI runs

- Whether headless Godot loads the minimal `editor_settings-4.tres` (both `-4` and `-4.7`
  names are written; if neither is honored, the fallback is Godot's own default
  `export/android/debug_keystore` path, which is the same file).
- The `publish` step under a real tag has never executed (only its `if` guard is checked); see
  the limits section above.
- Any real-device iOS deployment (the artifact is an unsigned project, never an installable
  build). The local launch of the net9.0 build is now verified — see the .NET section.
