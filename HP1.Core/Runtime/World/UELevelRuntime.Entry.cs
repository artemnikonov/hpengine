// RESIDENT ENTRY WORLD — Core-owned.
//
// Shipped keeps TWO live levels. UGameEngine::Init (Engine.dll.c:38219-38243),
// before parsing the command line, calls LoadMap(FURL("Entry")) — there are no
// client viewports yet, so no player is created — and swaps GLevel and
// GEntry (+0x70/+0x74). Then UGameEngine::Tick (FUN_103a0900,
// Engine.dll.c:249090-249096) ticks GLevel every frame, then GEntry if it is a
// different level. A map change does not touch Entry; APlayerPawn::GetEntryLevel
// reads its Actors(0).
//
// Observed on the live original 1.53 (tools/origprobe, 39 maps):
//   * GameInfo1/Mutator1/GameReplicationInfo1 on EVERY map — Entry consumes the
//     zero UClass::ClassUnique numbers;
//   * GameReplicationInfo.Timer and GameInfo.Timer run TWICE as often as in a
//     port without Entry (398 vs 200 on Lev_Tut1): timers tick in both levels.
//
// Port model. The Entry world is a second UELevelRuntime owned by the main one
// (it lives for the whole session in both frontends). While Entry code runs,
// the main runtime exposes it as CurrentWorld (so frontend natives find "their"
// level), the interpreter sees the Entry LevelInfo and spawns are logged to the
// Entry journal. Outside that window the Entry world is invisible to the game map.
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        public UELevelRuntime EntryWorld { get; private set; }
        UELevelRuntime _worldOverride;
        UEObject _entryLevelInfo;
        readonly List<UEObject> _entrySpawnLog = new();

        // The world whose code is running now (a frontend native asks IT).
        public UELevelRuntime CurrentWorld => _worldOverride ?? this;

        // UGameEngine::Init -> LoadMap(Entry). Called ONCE per session, before
        // the first game map. Services (RNG, journal, config, error policy) are
        // the same as the game world's: in shipped this is one process.
        public void LoadEntryWorld(UEInterpreter interp, string entryMapPath, PlayWorldConfig cfg)
        {
            if (interp is null) throw new ArgumentNullException(nameof(interp));
            if (EntryWorld is not null)
                throw new InvalidOperationException("the Entry world is already up in this session");
            var e = new UELevelRuntime();
            e.AdoptSessionServices(this);
            e._entryLevelInfo = null;
            EntryWorld = e;
            UEObject before = interp.PeekLevelInfo();
            var savedLog = interp.ActiveSpawnLog;
            var savedActors = interp.LevelActors;
            _worldOverride = e;
            interp.ActiveSpawnLog = e._entrySpawnLog;
            interp.LevelActors = e.Actors;
            try
            {
                e.LoadWorld(entryMapPath, interp);
                e.InitializeLevelClock(interp);
                e.BootstrapLevelWithoutPlayer(interp, cfg);
                e._entryLevelInfo = interp.PeekLevelInfo()
                    ?? throw new InvalidOperationException("Entry: LevelInfo not bound");
                Log($"entry_loaded actors={e.Actors.Count} level={e._entryLevelInfo.GetProp(PK.Name)}");
            }
            finally
            {
                _worldOverride = null;
                interp.ActiveSpawnLog = savedLog;
                interp.LevelActors = savedActors;
                // Exchange(GLevel, GEntry): the game level is empty after Init.
                interp.RestoreWorldLevelInfo(before);
            }
        }

        // Single entry point for both frontends: the Entry world from the install (Maps/Entry.unr
        // next to System/; config is the same ini parse as for the game map;
        // FURL(L"Entry") carries no options). A repeated call in a session does nothing.
        public void EnsureEntryWorld(UEInterpreter interp, string systemDir)
        {
            if (EntryWorld is not null) return;
            string maps = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(
                System.IO.Path.GetFullPath(systemDir)), "Maps");
            PlayWorldConfig cfg = ConfigFromInstall(systemDir);
            cfg.Options = "";
            LoadEntryWorld(interp, System.IO.Path.Combine(maps, "Entry.unr"), cfg);
        }

        // GEntry->Tick(LEVELTICK_All, DeltaSeconds) — after the game level.
        public void TickEntryWorld(UEInterpreter interp, float realDelta, Action<Exception> exceptionSink = null)
        {
            UELevelRuntime e = EntryWorld;
            if (e is null || interp is null) return;
            InEntryWorld(interp, () =>
            {
                float levelDelta = e.AdvanceLevelClock(interp, realDelta);
                e.TickActorsPass(interp, levelDelta, exceptionSink);
            });
        }

        void InEntryWorld(UEInterpreter interp, Action body)
        {
            UEObject saved = interp.PeekLevelInfo();
            var savedLog = interp.ActiveSpawnLog;
            var savedActors = interp.LevelActors;
            _worldOverride = EntryWorld;
            interp.ActiveSpawnLog = EntryWorld._entrySpawnLog;
            interp.LevelActors = EntryWorld.Actors;
            interp.RestoreWorldLevelInfo(EntryWorld._entryLevelInfo);
            try { body(); }
            finally
            {
                _worldOverride = null;
                interp.ActiveSpawnLog = savedLog;
                interp.LevelActors = savedActors;
                interp.RestoreWorldLevelInfo(saved);
            }
        }

        void AdoptSessionServices(UELevelRuntime main)
        {
            Config = main.Config;
            LocalTimeSource = main.LocalTimeSource;
            HighDetailActors = main.HighDetailActors;
            Journal = main.Journal;
            DetailedJournal = main.DetailedJournal;
            Guard = main.Guard;
            GuardResult = main.GuardResult;
            FrandHook = main.FrandHook;
            LatentCompletedProbe = main.LatentCompletedProbe;
            AnimationHook = main.AnimationHook;
            DestroyCleanupHook = main.DestroyCleanupHook;
            DestroyTeardownHook = main.DestroyTeardownHook;
            SpawnFindSpotHook = main.SpawnFindSpotHook;
            MeshHashFieldsHook = main.MeshHashFieldsHook;
            BoneLookupHook = main.BoneLookupHook;
            BrushModelHook = main.BrushModelHook;
            SpawnPrepareHook = main.SpawnPrepareHook;
            DescribeCandidates = main.DescribeCandidates;
            WalkMoveHook = main.WalkMoveHook;
            LineTraceHook = main.LineTraceHook;
            FarMoveHook = main.FarMoveHook;
            TraceMovesHook = main.TraceMovesHook;
            LifecycleCandidatesHook = main.LifecycleCandidatesHook;
        }
    }
}
