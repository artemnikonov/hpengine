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

Use the .NET 10 SDK with the iOS workload and the Xcode version supported by that workload. The `BuildForIos`
property selects the iOS app target. The app uses the same engine and Metal shaders as the desktop builds.

    dotnet workload install ios

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

Copy an installed PC game folder (the folder containing `System` and `Maps`) into the app's folder
under **On My iPhone** or **On My iPad** in Files. Name it `game` for HP1 or place it at `HP2Data/game`
for HP2. If both folders are present, the app starts HP1. HP2 uses slot 1 and resumes it when a save
exists. Use a paired gamepad for gameplay. The desktop launcher and on-screen gamepad controls are
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

