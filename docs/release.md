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

User-visible cost: a local `dotnet build` now needs a .NET 9 SDK (or a newer SDK that can
target net9.0). CI installs 9.0.x explicitly.

The Apple embedded export plugin (iOS/macOS) at `4.7.2-stable` has no equivalent TFM
validation, so the exact Android error will not repeat on iOS; if the iOS export fails under
net9.0 it is the same template/.NET-version expectation surfacing elsewhere, not a new root
cause. CI is the evidence.

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

Android uses the normal template export (`gradle_build/use_gradle_build=false`) and a throwaway
debug keystore generated on the runner:

```sh
keytool -genkeypair -keystore "$HOME/.local/share/godot/keystores/debug.keystore" \
  -storepass android -keypass android -alias androiddebugkey \
  -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Android Debug,O=Android,C=US"
```

A minimal `~/.config/godot/editor_settings-4.tres` (and `editor_settings-4.7.tres`) points
`export/android/debug_keystore`, `export/android/android_sdk_path` and
`export/android/java_sdk_path` at absolute runner paths. The APK signer is printed with
`keytool -printcert -jarfile build/android/Regress.apk` and
`apksigner verify --print-certs build/android/Regress.apk`.

Gradle builds were evaluated and deliberately not used: they would require installing the
Android build template (`ExportTemplateManager::get_android_build_directory` →
`res://android/build` unless `gradle_build/gradle_build_directory` is set; source
`templates/android_source.zip`, CLI `--install-android-build-template`). With net9.0 the
non-gradle route passes, so nothing extra is installed or committed.

Artifacts (bundle `Regress-desktop-android`): `Regress-linux-x86_64.zip`,
`Regress-windows-x86_64.zip`, `Regress-android.apk`.

### `apple` (macos-latest)

Installs: .NET 9 SDK, Godot `4.7.2-stable` mono macOS universal editor
(`~/godot/Godot_mono.app/Contents/MacOS/Godot`, symlinked as `~/godot/godot`), templates into
`~/Library/Application Support/Godot/export_templates/4.7.2.stable.mono`, plus the runner's
preinstalled Xcode.

Commands:

```sh
dotnet build
"$HOME/godot/godot" --headless --path . --import
"$HOME/godot/godot" --headless --path . --export-release "macOS" build/macos/Regress.zip
"$HOME/godot/godot" --headless --path . --export-release "iOS"   build/ios/Regress.ipa
unzip -q build/macos/Regress.zip -d "$RUNNER_TEMP/macos_app"
lipo -archs "$(find "$RUNNER_TEMP/macos_app" -type f -path '*/Contents/MacOS/*' -print -quit)"
xcodebuild -project <found.xcodeproj> -scheme <first scheme> -sdk iphoneos -configuration Release \
  -destination 'generic/platform=iOS' \
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" \
  -derivedDataPath /tmp/iosdd build
```

The workflow prints the actual `build/ios` shape before packaging and zips whatever exists
(project directory if the export produced one, otherwise the export tree). `lipo` evidence is
printed as `MACOS_ARCHES=<archs>`; the Xcode probe prints `IOS_XCODEBUILD_PROBE=PASS|FAIL` and
never fails the job.

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
- Android is signed with a throwaway CI debug keystore (alias `androiddebugkey`, password
  `android`, `CN=Android Debug`). It is **not** a production signing key and must not be used
  for a distributed build.
- iOS is an **unsigned Xcode project** (`application/export_project_only=true`). Whether Xcode
  can build it is exactly what `IOS_XCODEBUILD_PROBE` says: `PASS` = buildable but unsigned;
  `FAIL` or not run = buildability not verified. Never claim it "should build".
- macOS/Windows/Linux artifacts are unsigned and not notarized.
- macOS architecture: `MACOS_ARCHES=<lipo -archs>` is the evidence. The preset requests
  `binary_format/architecture="universal"`, but only `lipo` shows what the template really
  contains; if it reports only `x86_64`, Apple Silicon needs Rosetta 2.

## Unverified until CI runs

- Whether headless Godot loads the minimal `editor_settings-4.tres` (both `-4` and `-4.7`
  names are written; if neither is honored, the fallback is Godot's own default
  `export/android/debug_keystore` path, which is the same file).
- The real shape of the iOS project-only export (`build/ios/...`); the workflow prints it and
  packages whichever shape appears.
- Whether the iOS export under net9.0 passes (the Apple plugin has no TFM check; the publish
  step and Xcode are the remaining unknowns).
- Whether `dotnet publish -r ios-arm64` / `-r android-arm64` needs `dotnet workload install`
  (NETSDK1147); Godot docs do not mention it, so the job only prints `dotnet --info` /
  `dotnet workload list`.
- Whether `keytool -printcert -jarfile` can read the APK (v1/JAR signature); `apksigner
  verify --print-certs` is the v2/v3 fallback and fails loudly if the APK is unsigned.
- `MACOS_ARCHES` and `IOS_XCODEBUILD_PROBE` results, and the first five-platform green run.
