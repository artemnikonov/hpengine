# Building from source

Requirements: .NET SDK 9 or newer (the projects target `net9.0`). Nothing else: SDL3 comes from the `ppy.SDL3-CS` NuGet
package, and the shader blobs are committed (`hpengine/shaders/build.sh` rebuilds them with DXC and SPIRV-Cross).

    dotnet build hpengine/HPEngine.csproj -c Release
    dotnet run --project hpengine/HPEngine.csproj -c Release -- --root <game folder>

Release packages (self-contained, `win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`, all can be built on one machine):

    sh hpengine/packaging/publish.sh                     # -> .artifacts/hpengine-packages/
    sh hpengine/packaging/publish.sh --out DIR osx-arm64

Tests that need no game files:

    sh hpengine/tests/run_all.sh

## iOS

Use .NET SDK 11.0.100 with iOS workload set 11.0.100.1 and Xcode 27.2. The `BuildForIos`
property selects the iOS app target. The app uses the same engine and Metal shaders as the desktop builds.

From the repository root, the simulator build script installs these dependencies into a local build
directory and writes a ZIP containing the resulting app. The first run needs network access.

    sh hpengine/ios/build-simulator.sh

For an existing toolchain, install the workload manually:

    dotnet workload install ios --version 11.0.100.1

Download the disk image from the [SDL 3.4.16 release](https://github.com/libsdl-org/SDL/releases/tag/release-3.4.16)
and copy `SDL3.xcframework` out of it. If you build SDL from source, use the **SDL3.xcframework** target
in `Xcode/SDL/SDL.xcodeproj`. Set `sdl_framework` to the absolute path to that XCFramework.

From the repository root, build for an Apple Silicon simulator:

```sh
dotnet build hpengine/HPEngine.csproj -c Release -r iossimulator-arm64 \
    -p:BuildForIos=true -p:SdlIosFramework="$sdl_framework" -p:EnableCodeSigning=false
```

For a physical device, change the runtime identifier to `ios-arm64`, remove `EnableCodeSigning=false`,
and supply the signing identity and provisioning profile for your Apple development team. Override
`ApplicationId` if your profile uses a different bundle identifier.

### Xcode

`HPEngine.xcodeproj` has one iOS app target, `HPEngine`. Its build phase (`hpengine/ios/xcode-build-phase.sh`) runs
the build script above for the selected destination (`iossimulator-arm64` or `ios-arm64`), copies the .NET app into
Xcode's product and signs nested code with the identity Xcode chose. Pick a team under **Signing & Capabilities**
(automatic signing) and change the bundle identifier if `org.hpengine.ios` is taken for it; Build and Run then work as
for any iOS app, on a device or in "My Mac (Designed for iPad)". For gameplay tests, enable the shared scheme's `--map Lev_Tut1`
argument (Edit Scheme > Run > Arguments) to start in a level instead of the start-up sequence. The iOS Simulator builds and installs, but its GPU
(Metal family Apple2) is below what SDL_GPU needs, so the game draws nothing there.

### Game files

Copy an installed PC game folder (the folder containing `System` and `Maps`) into the app's folder
under **On My iPhone** or **On My iPad** in Files. Name it `game` for HP1 or place it at `HP2Data/game`
for HP2. If both folders are present, the app starts HP1. HP2 uses slot 1 and resumes it when a save
exists.

For development, a Mac can serve the game folders instead, and the app downloads a missing one on start:

    sh hpengine/ios/content-server.sh setup --hp1 <HP1 game folder> --hp2 <HP2 game folder>

This writes its files to `~/Library/Application Support/HPEngine Content Server` (outside the repository) and starts
Caddy (`brew install caddy`) on port 8443 at login through a LaunchAgent: HTTPS with a self-signed certificate the app
pins, and a bearer token. The Xcode build copies the server's address, token and certificate hash into the app; rebuild
after `setup` and after the Mac's name or address changes. `content-server.sh manifest` refreshes the file lists,
`stop` and `status` control the service. A device asks for Local Network access on the first download.

### Controls

A paired gamepad works as on the desktop. Without one, on-screen touch controls are a virtual gamepad while the player
directly controls Harry: drag on the left part of the screen to move, on the right part to turn the camera, the green
button jumps (A), the blue button casts (X), the small top-left button opens the menu (Start). Touch and hold still for a
moment on the right part, then drag: the finger is the PC mouse with its button held (Cast). The overlay hides while a
physical pad is connected and the screen is not touched. In cut-scenes, menus and pauses the sticks and buttons are off
(the Menu button stays): in a menu a tap is a click where the finger is (drag for sliders). In a spell lesson any touch
draws at once and the wand follows the finger, so the pattern is traced by dragging along it. The desktop launcher is
outside this iOS target.

## What is not in this repository

The port is checked against the original game: every native function is compared with the original machine code run
in an emulator, frames are compared draw call by draw call with logs of the running original, and long play sessions
are compared tick by tick. Those judges, their corpora and the tests that load real maps need the game files and
decompiled code of the original executables, so they **stay private** and are not published here:

- the decompiled and disassembled original DLLs and the tools that run them;
- data extracted from the game and captures of the running original;
- the headless validation harness, the acceptance suite and the tests that load the game (`hpengine/tests` here holds
  only the tests that run without game files);
- the project's internal notes.

Comments in the code still cite the original functions by address and name (e.g. `Engine.dll 0x103f5f80`) and mention
these private checks; they are references, not files you are missing for the build. `HP1.Core/ThirdParty/Eliot.UELib.dll`
is a patched build of UELib (see `HP1.Core/ThirdParty/README.md`).

