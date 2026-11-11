# Building from source

Requirements: .NET SDK 9 or newer (the projects target `net9.0`). Nothing else: SDL3 comes from the `ppy.SDL3-CS` NuGet
package, and the shader blobs are committed (`openhp1/shaders/build.sh` rebuilds them with DXC and SPIRV-Cross).

    dotnet build openhp1/OpenHP1.csproj -c Release
    dotnet run --project openhp1/OpenHP1.csproj -c Release -- --root <game folder>

Release packages (self-contained, `win-x64`, `linux-x64`, `osx-arm64`, all three can be built on one machine):

    sh openhp1/packaging/publish.sh                     # -> .artifacts/openhp1-packages/
    sh openhp1/packaging/publish.sh --out DIR osx-arm64

Tests that need no game files:

    sh openhp1/tests/run_all.sh

## What is not in this repository

The port is checked against the original game: every native function is compared with the original machine code run
in an emulator, frames are compared draw call by draw call with logs of the running original, and long play sessions
are compared tick by tick. Those judges, their corpora and the tests that load real maps need the game files and
decompiled code of the original executables, so they **stay private** and are not published here:

- the decompiled and disassembled original DLLs and the tools that run them;
- data extracted from the game and captures of the running original;
- the headless validation harness, the acceptance suite and the tests that load the game (`openhp1/tests` here holds
  only the tests that run without game files);
- the project's internal notes.

Comments in the code still cite the original functions by address and name (e.g. `Engine.dll 0x103f5f80`) and mention
these private checks; they are references, not files you are missing for the build. `HP1.Core/ThirdParty/Eliot.UELib.dll`
is a patched build of UELib (see `HP1.Core/ThirdParty/README.md`).

