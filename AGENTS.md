# Instructions for AI coding agents

HPEngine runs the 2001 game Harry Potter and the Philosopher's Stone (Unreal Engine 1) on new code. Core
(`HP1.Core/`) executes the original UnrealScript bytecode and C# ports of the original native functions; the frontend
(`hpengine/`) only draws, plays sound and passes input. The goal is behaviour identical to the original game. Details
and reasons: [docs/contributing.md](docs/contributing.md); code map: [docs/architecture.md](docs/architecture.md).

## Build and test

    dotnet build hpengine/HPEngine.csproj -c Release      # must succeed
    sh hpengine/tests/run_all.sh                          # must exit 0; needs no game files
    sh hpengine/tests/style_check.sh <base commit>        # changed C# files get no new whitespace findings

Running the game needs an installed copy: `dotnet run --project hpengine/HPEngine.csproj -c Release -- --root <game
folder>`. Without one, do not claim in-game behaviour; say it was not played. Exit code 3 from a test (`REFUSED`,
`NOVERDICT`) is not a pass: the compared path did not run.

## Do

- Port from the original binaries: disassembly of the shipped DLLs first (a decompiler only as a reading aid), then the
  UnrealScript bytecode and shipped data.
- Put an origin tag directly above every function that ports an original body, one line per original function:
  `// Original: Engine.dll ?PlayAnim@AActor@@QAEHVFName@@_NMMMW4EAnimType@@0@Z [HP1 0x10408E20]`. Cite the real body,
  not an incremental-linking `jmp` thunk. Non-exported: `not exported: FUN_xxxxxxxx [HP1 0x...]` plus how to find it.
- Keep game state, visibility, poses, the frame tree, coronas and script-visible sound in Core. Give the frontend a new
  value through a new `WorldFrame` field (`HP1.Core/Runtime/VM/WorldContract.cs`).
- Write code that more than one supported game runs once; branch on the game profile at the exact line that differs.
- Mark any deliberate difference from the original "not original" in the code and list it in `docs/differences.md`;
  only with the maintainer's agreement and only when it is visibly better for the player.
- Make a new check print which branch it compared and exit 3 when that branch did not execute; show it turns red on the
  error it targets.
- Keep the diff minimal: every changed line belongs to the task.
- Write code, identifiers, comments, logs and docs in English.
- In the pull request, list what only the maintainer's checks against the original can judge (native bodies, physics,
  collision, animation, timing, sound, saves, what is drawn) and what you verified yourself.

## Don't

- Don't use SurrealEngine, Epic's Unreal source or headers (or headers derived from them), or other reimplementations
  as a source.
- Don't claim the original does *not* do something without reading the disassembly.
- Don't invent values: no numbers by eye, multipliers without a source, nearest-match name lookups (lookups are exact
  and case-insensitive), or special cases by asset or class name.
- Don't let frontend code write Core state, call Core code with side effects, or use the game's random stream.
- Don't copy a function to make a per-game variant; don't mix one game's addresses or listings with another's.
- Don't "improve" game logic, physics or progress, even when the original looks wrong.
- Don't accept a snapshot of the port's own output as proof of correctness.
- Don't reformat or refactor code outside the task.

## Never commit

- Game files or anything extracted from them (maps, packages, textures, sounds, music, saves, ini/int files).
- Original binaries, decompiled or disassembled listings, pasted decompiler output, screenshots of the original.
- Binaries other than the ones already in the tree, or any file over 1 MiB.

## Stop and ask instead of guessing

Stop and report (in the pull request or to the person you work for) when:

- the correct behaviour cannot be established from the binaries, bytecode or data;
- the sources contradict each other (give the options found);
- the change would differ from the original in anything the player sees or the game logic reads;
- a test or check you did not change starts failing, or contradicts your conclusion;
- the task would need game files, original binaries or a check you cannot run.

A precise description of what is unknown is a complete result; a plausible default is not.
