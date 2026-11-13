#!/bin/sh
# Checks of the Windows/Linux Launcher (launcher/Desktop): HP1Setup protocol parsing, Mods.ini round-trip, data directory
# per OS, release choice, and a headless Avalonia window in both states (no window opens). Needs .NET SDK 9 (+ NuGet).
#   sh launcher/test_desktop.sh
# Red mutations: sh launcher/test_desktop_redtest.sh. Exit: 0 all passed, 1 a check failed, 2 build, 3 NOVERDICT.
HERE=$(cd "$(dirname "$0")" && pwd)
dotnet run --project "$HERE/Desktop.Tests" -c Release
