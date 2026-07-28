# Safe Video Transfer

An iOS-only .NET 10 MAUI milestone for recording large videos into the app
sandbox, uploading them to a local WD My Cloud over FTP, verifying the remote
content, and deleting the local file only after explicit confirmation.

Open `SafeVideoTransfer.sln` in JetBrains Rider. Visual Studio for Mac is not
required.

## Validated toolchain

This revision is pinned by `global.json` and has been compiled with:

- .NET SDK 10.0.302
- .NET MAUI 10.0.20
- Microsoft.iOS 26.5.10301
- Xcode 26.6 (build 17F113)
- macOS 26.5.2 on Apple Silicon

The Microsoft.iOS 26.5 workload targets the iOS 26.5 SDK shipped inside Xcode
26.6. Xcode 16.4 is no longer required.

## Architecture

The dependency direction is:

```text
MainPage (XAML)
  -> MainPageViewModel
      -> IVideoRecordingService       -> iOS camera picker
      -> IVideoStorageService         -> app-local Videos directory
      -> IVideoTransferService        -> passive FTP upload/resume
      -> ITransferVerificationService -> FTP SIZE + optional RETR/SHA-256
      -> IPhotoLibraryService         -> optional Photos copy
      -> IVideoRecordRepository       -> atomic JSON recovery index
```

`VideoRecord` is the persisted state machine. Recording, upload, verification,
and deletion each have independent states. `VideoStorageService.DeleteLocalAsync`
checks `VerificationState.Verified` and `KeepLocal == false` before it can call
`File.Delete`.

The original is stored below `FileSystem.AppDataDirectory/Videos`, which maps to
the app's private iOS Library container. It is not a Photos asset. Deleting it
therefore does not involve the Photos library and does not put it in Photos'
Recently Deleted album. The only code that writes to Photos is
`IosPhotoLibraryService`, called from the optional button.

## Transfer safety

- Completed recordings use the local-date format `yyyy-MM-dd-N.mov`. The daily
  counter includes persisted and local records, survives restarts and local-file
  cleanup, and resets to 1 on a new date.
- Retry uses the same `VideoRecord.Id`, remote name, and URI.
- A preflight FTP `SIZE` avoids uploading an already present object of the expected
  length. Full verification still runs afterward.
- A partial remote file is resumed when the FTP server supports restart; uploads
  are retried up to three times with exponential delays.
- Cancellation tokens cover FTP requests, retry delays, hashing,
  repository access, and deletion checks.
- A transfer is never considered verified merely because FTP upload returned success.
- Default verification checks FTP file length, then downloads the remote
  object and compares its SHA-256 with the local file.
- Delete remains disabled until verification succeeds. Verification failure,
  cancellation, or an exception always leaves the local file in place.

The configured endpoint is `ftp://192.168.178.40/Public/recording/`. The WD
username is stored in Preferences and the password in iOS Secure Storage.
Anonymous access is not used. FTP is unencrypted, so this target must remain
restricted to the trusted local network and must not be port-forwarded.

This milestone supports interruption and FTP restart/resume where the server
supports it. Transfers run in the foreground; iOS may suspend the connection
when the app is backgrounded. A future production version should use a server
and protocol supported by background `NSUrlSession`.

## Restart recovery and cleanup

`video-index.json` is replaced atomically after every meaningful state change.
At startup:

- an `Uploading` record becomes `Interrupted`;
- an interrupted verification becomes `NotStarted`;
- unindexed `.mov` files in the local Videos folder are recovered;
- missing indexed files are reported without pretending they were deleted.

Storage cleanup is deliberately user-controlled. “Delete local” is available
only after verification; “Keep local” persists that choice. No age-based task
can silently remove an unverified recording.

## Required Mac setup

```bash
xcode-select -p
xcodebuild -version
dotnet --info

# Install the .NET 10.0.302 SDK for Apple Silicon from:
# https://dotnet.microsoft.com/download/dotnet/10.0
dotnet --version

# This repository's global.json expects 10.0.302.
# Install or repair the iOS-only MAUI workload.
sudo dotnet workload install maui-ios
dotnet workload list

# Restore the workload and NuGet dependencies for this repository:
dotnet workload restore SafeVideoTransfer.sln
dotnet restore SafeVideoTransfer.sln
```

The exact commands used to create this structure from an empty folder are:

```bash
dotnet new maui -n SafeVideoTransfer -o SafeVideoTransfer --no-restore
dotnet new sln -n SafeVideoTransfer
dotnet sln SafeVideoTransfer.sln add SafeVideoTransfer/SafeVideoTransfer.csproj
```

The generated project was then reduced to `net10.0-ios`; the Android,
Mac Catalyst, Windows, and Tizen platform sources were removed.

## Build

```bash
dotnet restore SafeVideoTransfer.sln
dotnet build SafeVideoTransfer/SafeVideoTransfer.csproj \
  -f net10.0-ios \
  -p:RuntimeIdentifier=iossimulator-arm64
```

If Xcode reports that no simulator runtime is installed, open Xcode, choose
**Settings > Components**, and install an iOS runtime compatible with the
installed Xcode. Accept the Xcode license and first-launch components if needed:

```bash
sudo xcodebuild -license accept
sudo xcodebuild -runFirstLaunch
```

## Run on an iOS Simulator

Camera recording is not available in the iOS Simulator, so use it to exercise
startup, settings, recovery UI, and non-camera behavior. Use a physical iPhone
for the end-to-end recording flow.

```bash
open -a Simulator
xcrun simctl list devices available

# Copy an available simulator UDID from the preceding command.
SIMULATOR_UDID="PASTE-SIMULATOR-UDID"

dotnet build SafeVideoTransfer/SafeVideoTransfer.csproj \
  -t:Run \
  -f net10.0-ios \
  -p:RuntimeIdentifier=iossimulator-arm64 \
  -p:_DeviceName=:v2:udid="$SIMULATOR_UDID"
```

On an Intel Mac use `iossimulator-x64` instead.

## Run on a physical iPhone

1. In `SafeVideoTransfer.csproj`, replace `com.example.safevideotransfer` with
   a bundle identifier belonging to your Apple developer team.
2. Connect and trust the iPhone, enable Developer Mode, and select your team in
   Rider's iOS signing settings or provide signing properties on the command line.
3. Find the device and its identifier:

```bash
xcrun devicectl list devices
security find-identity -v -p codesigning
```

Then run:

```bash
IPHONE_UDID="PASTE-IPHONE-UDID"
SIGNING_KEY="Apple Development: Your Name (TEAMID)"

dotnet build SafeVideoTransfer/SafeVideoTransfer.csproj \
  -t:Run \
  -f net10.0-ios \
  -p:RuntimeIdentifier=ios-arm64 \
  -p:_DeviceName=:v2:udid="$IPHONE_UDID" \
  -p:CodesignKey="$SIGNING_KEY"
```

With automatic signing configured in Rider/Xcode, omit `CodesignKey`. If your
team requires an explicit profile, also pass
`-p:CodesignProvision="PROFILE NAME OR UUID"`.

## Files to review or edit in Rider

- `SafeVideoTransfer/SafeVideoTransfer.csproj`
  - Change `ApplicationId` to your unique bundle ID.
  - The target is intentionally only `net10.0-ios`.
  - `CodesignEntitlements` points to the iOS entitlements file.
- `SafeVideoTransfer/MauiProgram.cs`
  - Contains every dependency injection registration.
- `SafeVideoTransfer/Platforms/iOS/Info.plist`
  - Camera, microphone, Photos-add, and local-network descriptions are present.
  - Local-network access is declared for the WD My Cloud connection.
- `SafeVideoTransfer/Platforms/iOS/Entitlements.plist`
  - Empty for v1; camera, microphone, and Photos do not require entitlements.
  - Add only capabilities actually enabled for the Apple App ID.
- `SafeVideoTransfer/Services/RemoteTransferSettings.cs`
  - Change credential or endpoint persistence policy if needed.
- `SafeVideoTransfer/Services/FtpVideoTransferService.cs`
  - Contains FTP preflight, resume, cancellation, progress, and retry behavior.
- `SafeVideoTransfer/Services/FtpTransferVerificationService.cs`
  - Verifies FTP size and optionally downloads the file for SHA-256 comparison.

## iOS permissions

The app requests camera and microphone access immediately before recording. It
requests Photos add-only access only when “Save a copy to Photos” is tapped. A
local-network prompt can appear only when the configured endpoint is on the LAN.
Denial is surfaced as an error and never triggers deletion.

## First test checklist

1. Change the bundle ID and configure signing.
2. Run on a physical iPhone.
3. Record a short video and confirm it appears in the app but not in Photos.
4. Cancel an upload and confirm the local file remains and retry is enabled.
5. Enter the WD user credentials and upload to
   `ftp://192.168.178.40/Public/recording/`.
6. Confirm length and SHA-256 verification report success.
7. Choose “Keep local” once and confirm deletion is disabled.
8. With a different verified recording, choose “Delete local” and confirm it
   does not appear in Photos' Recently Deleted album.
9. Force-quit during upload, relaunch, and confirm the record is recovered as
   interrupted and can be retried.
