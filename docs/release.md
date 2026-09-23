# Release pipeline

Workflow: [`.github/workflows/release.yml`](../.github/workflows/release.yml).
Manual runs: Actions → Release → Run workflow → `version` (e.g. `v0.1.0`) and `dry_run=true`.
Tags `v*` on the main branch run the same path with publishing enabled. Never tag a feature
branch. The repository is public, so hosted runner minutes are free; the real cost is wall-clock
time, and the macOS (`apple`) job dominates it.

## Jobs

### `desktop-android` (ubuntu-latest)

Installs: .NET 8 SDK (`setup-dotnet`), Godot `4.7.2-stable` mono Linux editor (cached,
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

Android signing uses a throwaway debug keystore generated on the runner:

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

Artifacts (bundle `Regress-desktop-android`): `Regress-linux-x86_64.zip`,
`Regress-windows-x86_64.zip`, `Regress-android.apk`.

### `apple` (macos-latest)

Installs: .NET 8 SDK, Godot `4.7.2-stable` mono macOS universal editor
(`~/godot/Godot.app/Contents/MacOS/Godot`, symlinked as `~/godot/godot`), templates into
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
- Template completeness: the jobs print the whole template directory and fail with
  `TEMPLATE GAP: ios.zip missing` if the template pack has no iOS template. This is reported,
  never skipped silently.

## Unverified until CI runs

- Whether headless Godot loads the minimal `editor_settings-4.tres` (both `-4` and `-4.7`
  names are written; if neither is honored, the fallback is Godot's own default
  `export/android/debug_keystore` path, which is the same file).
- The real shape of the iOS project-only export (`build/ios/...`); the workflow prints it and
  packages whichever shape appears.
- Whether the `4.7.2.stable.mono` template pack contains `ios.zip`.
- Whether `dotnet workload install ios` is needed: Godot 4.7 export docs do not mention it, so
  the job only prints `dotnet --info` / `dotnet workload list`. If `dotnet publish -r ios-arm64`
  fails with NETSDK1147, add the workload step.
- Whether `keytool -printcert -jarfile` can read the APK (v1/JAR signature); `apksigner
  verify --print-certs` is the v2/v3 fallback and fails loudly if the APK is unsigned.
