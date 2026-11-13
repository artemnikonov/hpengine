// GAME SAVE/LOAD.
//
// What shipped does: `SaveGame N` (UGameEngine::Exec → UGameEngine::SaveGame,
// Engine.dll.c:94223) — UObject::SavePackage of the whole level into `Save\SaveN.usa`;
// `open saveN.usa` — UGameEngine::LoadMap with the save package: actors
// come from the package, `bBegunPlay` is already set, so the init block
// (FixUpLevel, InitExecution, InitGame, BeginPlay/PostBeginPlay, Attach/Base —
// FUN_1039c3d0:247314-247383) is SKIPPED; every PlayerPawn gets Player zeroed
// (246657), then SpawnPlayActor → GameInfo.Login finds the
// "unclaimed" player (Player==None, PRI, bIsPlayer, name ≠ default —
// GameInfo.uc Login, NM_Standalone branch) and assigns it to the viewport.
//
// The port's file format is ITS OWN and declared as such (author decision): the shipped
// loader does not run on macOS, so nothing would read `.usa`; what is kept is the
// SEMANTICS (what survives a save): all world objects with their properties, the
// state and state frame (name, code position, probe mask, latent wait), timers,
// disabled events, the Touching list, ULevel::Actors order, ExportIndex of placed
// actors, the level clock.
// Not saved (nor by shipped — these are not UProperties): particle buffers,
// RNG state, animation caches, viewport/console objects (recreated by
// CreateLocalViewport).
//
// Values are encoded by the types the VM actually stores in Props;
// an unknown type is a loud STOP naming the property (§8), not a skip.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UELib.Core;
using HP1.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        const string SaveMagic = "HP1SAV";
        // 2: name counters (UClass::ClassUnique) are NOT written — in shipped that is a
        //    class field, not a level one, and the save package does not carry it.
        // 3: the state frame's code position is the bytecode offset of the token (Token.Position — the offset execJump
        //    and labels use), -1 = past the last token, and the latent action is written by NAME. Versions 1..2 wrote the
        //    index into the port's decoded token list and the LatentRunState enum number — both port internals that a
        //    decoder or enum change would silently shift.
        // 4: a class-valued property is written with its package (tag ClassIn: package, name) and read through the
        //    same linker route as an object's class (UEActorGraphLoader.ResolveSavedClass): in shipped the class is an
        //    import `Class <Package>.<Name>` of the save package, and the linker loads that package whatever the process
        //    loaded before. Versions 1..3 wrote the short name (tag Class), resolved among the loaded packages only — a
        //    Lev3_Intro save loaded from the menu in a fresh process stopped on Trigger.ClassProximityType = Doxie1.
        // 5: the SCRIPT RECORD and the LEVEL-ENTRY TRAVEL SET. Layout (BinaryWriter: string = 7-bit length + UTF-8):
        //      "HP1SAV", int32 version, int32 N, N × string mod name      <- header: the enabled HPEngine mods in order
        //      string map, int64 clock, int32 levelIndex, int32 actors, int32 objects, objects...
        //      int32 M, M × (string package, string GUID)                  <- script packages the save depends on
        //      int32 K, K × entry-travel record set (WriteEntryTravel)     <- ULevel::TravelInfo of the level
        //    The mod names come first so a small reader (the launchers: which slots were saved with other mods) needs no
        //    engine. The packages are those of the saved objects' classes with the super chains, of the state frames'
        //    code and of class values, with the package GUID (FPackageFileSummary.Guid: UE1 gives a package a new one each
        //    time it is compiled, so it names the exact bytecode a state frame's code offset points into); read by
        //    SaveScriptMismatch before the old world is torn down. The travel set is LevelEntryTravel: what the engine's
        //    travel carried into the level (a port record, D-82, NOT ORIGINAL; it is not LevelTravelInfo, whose fate
        //    across a shipped save load is not established, so the game's own ?RESTART/travel base stays untouched). A
        //    load restores it, and a save that does not fit the scripts restarts its level with it. Versions 1..4 have
        //    neither: written before mods existed — "saved without mods", GUIDs and entry set unknown.
        const int SaveVersion = 5;
        // COMPATIBILITY ACROSS BUILDS (author decision): a save of ANY earlier format
        // version must load. Game bytecode and map data are immutable, so only this
        // record can break the format: every change to it bumps SaveVersion and adds a
        // read branch for the previous version (ReadVersion below), not a refusal.
        //   1: after levelIndex — a list of name counters (string, int); absent in
        //     version 2, skipped on read.
        //   1..2: state frame cursor = token index, latent = enum number in the order below (frozen: the order
        //     LatentRunState had when these versions were written: unchanged since ffd1f76e, 2026-07-17).
        const int OldestReadableVersion = 1;
        static readonly string[] LatentNamesV2 =
        {
            "Continue", "Stop", "Sleep", "FinishAnim", "FinishInterpolation", "MoveTo", "MoveToward",
            "StrafeTo", "StrafeFacing", "TurnTo", "TurnToward", "WaitForLanding",
        };

        static LatentRunState LatentFromName(string name)
            => Enum.TryParse(name, false, out LatentRunState l) && name == l.ToString()
                ? l : throw new InvalidDataException($"unknown latent action '{name}'");

        static LatentRunState LatentFromV2(int n)
            => n >= 0 && n < LatentNamesV2.Length ? LatentFromName(LatentNamesV2[n])
                : throw new InvalidDataException($"latent action #{n} outside the version 1..2 table");

        static int ReadVersion(BinaryReader r)
        {
            if (r.ReadString() != SaveMagic) throw new InvalidDataException("not an HP1 save file (magic)");
            int ver = r.ReadInt32();
            if (ver < OldestReadableVersion || ver > SaveVersion)
                throw new InvalidDataException($"save version {ver}: this build reads {OldestReadableVersion}..{SaveVersion}"
                    + (ver > SaveVersion ? " — the save was made by a newer build" : ""));
            return ver;
        }

        // ── value tags ──────────────────────────────────────────────────────
        enum V : byte
        {
            Null = 0, Bool = 1, Byte = 2, Int = 3, Float = 4, String = 5, Name = 6,
            Vector = 7, Rotator = 8, Plane = 9, ObjRef = 10, Struct = 11, List = 12,
            Resource = 13, Class = 14, PackageObject = 15, Double = 16, Long = 17,
            ObjArray = 18, StringList = 19, ModelRef = 20, ClassIn = 21,
        }

        // ── SAVE ───────────────────────────────────────────────────────────
        // mods: the enabled HPEngine mods (UEGameEngine.Boot), written into the script record; null = none.
        public byte[] SaveGame(UEInterpreter interp, IReadOnlyList<string> mods = null)
        {
            if (interp is null) throw new ArgumentNullException(nameof(interp));
            if (string.IsNullOrEmpty(LoadedMapPath))
                throw new InvalidOperationException("SaveGame: world not loaded (LoadedMapPath empty)");
            UEObject level = interp.GetLevelInfo()
                ?? throw new InvalidOperationException("SaveGame: no LevelInfo");

            // SHIPPED ORDER of UGameEngine::SaveGame (Engine.dll.c:94277..94359):
            //   LevelAction = LEVACT_Saving (2) -> PaintProgress() -> ... SavePackage ...
            //   -> LevelAction = LEVACT_None (0).
            // So (1) LevelAction=2 does NOT reach the file: the field is CPF_Transient (0x2000, Engine.u),
            // the persistent archive skips it (TransientProps below); (2) the player is shown ONE frame
            // with the saving caption (baseConsole.DrawLevelAction, Localize key
            // `nearly_nick_40` — ABSENT from the shipped .int, shipped prints the Localize
            // placeholder). Disasm 0x103a2379-0x103a2382: LevelAction = 2, then this->PaintProgress()
            // (vtbl+0xc0 = 0x10302419): a Core frame with its script and game RNG, BEFORE SavePackage;
            // the frontend only decides whether to show PendingProgressFrame.
            level.Props["LevelAction"] = (byte)2;
            try
            {
                PaintProgress(interp);
                // 0x103a23af-0x103a23c5: delete Level->BrushTracker; = 0 — BEFORE
                // CleanupDestroyed: the model is truncated to the constructor counts (mover fragments
                // go away, UEMovingBrushTracker.cs); the destructor does not touch actors.
                DeleteBrushTracker();
                // GLevel->CleanupDestroyed(1) (vtable +0x9c, Engine.dll.c:94285) —
                // AFTER the progress frame and BEFORE SavePackage.
                CleanupDestroyed();
                FirstDeletedCount = 0;   // bForce: the list is freed entirely
                byte[] saved = SerializeWorld(interp, level, mods);
                // 0x103a256e-0x103a2610: AFTER SavePackage (does not reach the file)
                // every Mover in the actor list gets SavedPos = (-1,-1,-1).
                foreach (UEObject a in Actors)
                    if (a is not null && !a.Destroyed && UEInterpreter.ClassIsA(a.Class, "Mover"))
                        a.Props["SavedPos"] = new Vector3(-1f, -1f, -1f);
                // 0x103a2616-0x103a2628: Level->BrushTracker = GNewBrushTracker(Level)
                // (SetupActorBrush, then Update(0): SavedPos/SavedRot = Location/Rotation).
                CreateBrushTracker();
                // 0x103a26b2: LevelAction = 0, then 0x103a26b9: GCache.Flush(0, ~0, 0).
                level.Props["LevelAction"] = (byte)0;
                FlushGCache();
                return saved;
            }
            finally { level.Props["LevelAction"] = (byte)0; }
        }

        // FMemCache::Flush(&GCache, 0, ~0, 0) — Core.dll.c:93626-93680 (FUN_10127c70): Id 0 zeroes the mask, so every
        // item is flushed. The game-visible GCache items Core models: the skeleton headers (CFSkelHeader: pose, root
        // motion, tween — _skelCache) and the ULodMesh::GetFrame vertex tween caches (id index·0x100+0x1d,
        // Engine.dll.c:211483-211517 — _vertexFrames); the next draw takes the no-cache branch. Callers: SaveGame
        // (0x103a26b9) and UEngine::Flush (Engine.dll.c:46718-46735, the FLUSH console command).
        // GCache is process-wide: the resident Entry world's items go too.
        public void FlushGCache()
        {
            _skelCache.Clear();
            _vertexFrames.Clear();
            if (EntryWorld is UELevelRuntime e && !ReferenceEquals(e, this)) e.FlushGCache();
        }

        // ── ULevel::CleanupDestroyed(bForce=1) ─────────────────────────────
        //
        // Engine.dll.c:23740 (export 0x1753). With bForce (or > 0x7f pending)
        // for every NON-empty Actors slot it calls Class->CleanupDestroyed(actor)
        // (class slot +0x7c = UStruct::CleanupDestroyed, Core.dll.c:36045):
        // along the class's object property chain (RefLink, +0x5c/+0x60) every
        // element [0..ArrayDim) referencing an object with IsPendingKill
        // (slot +0x2c; for an actor — bDeleteMe) is zeroed; struct properties
        // are walked recursively (+0x60 → Struct->CleanupDestroyed). Then the
        // destroyed actors of the FirstDeleted list are freed — they do not get
        // into the save package.
        //
        // The port keeps no FirstDeleted list (a destroyed actor is swept from
        // Actors by SweepDestroyed); in shipped it is empty only after a previous
        // cleanup that has already zeroed these references. So the result is the same:
        // after this step no object property of a live actor holds a destroyed one.
        // References from NON-actors and from latent frame locals are untouched by
        // shipped — and by the port.
        internal void CleanupDestroyed()
        {
            foreach (UEObject a in Actors)
            {
                if (a is null || a.Destroyed || a.Class is null) continue;
                CleanupDestroyedStruct(a, a.Class);
            }
        }

        static void CleanupDestroyedStruct(UEObject bag, UStruct st)
        {
            foreach (var (key, objRef, inner) in CleanupPlan(bag, st))
            {
                if (!bag.Props.TryGetValue(key, out object v) || v is null) continue;
                if (objRef)
                {
                    if (v is UEObject u && !u.IsStruct && u.Destroyed) bag.Props[key] = null;
                    else if (v is UEObject[] arr)
                    { for (int i = 0; i < arr.Length; i++) if (arr[i] is { Destroyed: true }) arr[i] = null; }
                    else if (v is System.Collections.IList list && v is not string)
                    { for (int i = 0; i < list.Count; i++) if (list[i] is UEObject e && !e.IsStruct && e.Destroyed) list[i] = null; }
                    continue;
                }
                if (inner is null) continue;
                if (v is UEObject sb && sb.IsStruct) CleanupDestroyedStruct(sb, inner);
                else if (v is System.Collections.IList sl)
                    foreach (object e in sl) if (e is UEObject eb && eb.IsStruct) CleanupDestroyedStruct(eb, inner);
            }
        }

        // The object/class/struct properties of st's chain in RefLink walk order, with the bag key of each: a
        // function of the class layout only, so it is built once per struct (perf/spikes: rebuilding keys per
        // actor per property made the 128-deletes sweep a 12-15 ms tick). Rebuilt when the registry changes
        // (CanonicalSuper resolves stubs against the loaded packages). A struct bag keys by the plain name, an
        // actor by PropKey of its class — the top call passes st == bag.Class, so st and IsStruct fix the key.
        sealed class CleanupPlanEntry
        {
            public HP1.Core.UEClassRegistry Registry; public int Packages; public bool IsStruct;
            public (string Key, bool ObjRef, UStruct Inner)[] Fields;
        }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UStruct, CleanupPlanEntry> _cleanupPlans = new();

        static (string Key, bool ObjRef, UStruct Inner)[] CleanupPlan(UEObject bag, UStruct st)
        {
            var reg = HP1.Core.UEClassRegistry.Shared;
            if (_cleanupPlans.TryGetValue(st, out var hit) && ReferenceEquals(hit.Registry, reg)
                && hit.Packages == reg.Packages.Count && hit.IsStruct == bag.IsStruct)
                return hit.Fields;
            var fields = new List<(string, bool, UStruct)>();
            for (UStruct s = st; s is not null;
                 s = s is UClass c ? reg.CanonicalSuper(c) : s.Super as UStruct)
                foreach (UProperty p in s.EnumerateFields<UProperty>())
                {
                    if (p is null) continue;
                    bool objRef = p.Type == UELib.Types.PropertyType.ObjectProperty
                               || p.Type == UELib.Types.PropertyType.ClassProperty;
                    bool strukt = p.Type == UELib.Types.PropertyType.StructProperty;
                    if (!objRef && !strukt) continue;
                    string key = bag.IsStruct ? p.Name.ToString()
                        : UEObject.PropKey(bag.Class, (p.Outer as UClass)?.Name ?? p.Outer?.Name.ToString(), p.Name.ToString());
                    fields.Add((key, objRef, objRef ? null : (p as UStructProperty)?.Struct));
                }
            var plan = new CleanupPlanEntry { Registry = reg, Packages = reg.Packages.Count, IsStruct = bag.IsStruct, Fields = fields.ToArray() };
            _cleanupPlans.AddOrUpdate(st, plan);
            return plan.Fields;
        }

        // UGameEngine::PaintProgress (Engine.dll.c:55123): player FlashFog
        // (+0x4f8..+0x504) temporarily = (0, 0.1, 0.25, 0.2), Draw(Viewport, 1, 0, 0),
        // previous FlashFog restored. The frame carries LevelAction=2 — drawn by
        // baseConsole.DrawLevelAction (the save branch ran on the live original:
        // Steam Deck journal, 21 times).
        //
        // THE GATE is byte UGameEngine+0x108 (disasm 0x10397bbb-0x10397bd0: nonzero -> the whole body is
        // skipped). It is UGameEngine::Draw's own "drawing" flag: Draw (0x1039fa7c-0x1039fa8a) returns at once
        // when it is set, sets it to 1 and clears it at its end (0x103a001c). So a save made from
        // inside a frame — HPConsole.Tick's level-start autosave, ticked by UConsole::PreRender
        // (Engine.dll.c:91493) — paints NO progress frame at all: no FlashFog write, no script, no RNG.
        // _inEngineFrame is that flag (RunEngineFrame).
        //
        // The frame does not replace the tick frame: it is deferred into PendingProgressFrame,
        // and the frontend shows it as a SEPARATE frame before the tick frame —
        // the shipped process is inside SavePackage at that time.
        public WorldFrame PendingProgressFrame { get; private set; }

        public void PaintProgress(UEInterpreter interp)
        {
            if (_inEngineFrame) return;
            if (Viewport?.GetProp(PK.Actor) is not UEObject player || FrameProducer is null) return;
            UEObject level = interp.GetLevelInfo();
            object savedFog = player.GetProp(PK.FlashFog);
            player.Props["FlashFog"] = new UEPlane(0f, 0.1f, 0.25f, 0.2f);
            try { PendingProgressFrame = RunEngineFrame(interp, NextInsideTickFrameNumber(), "paint_progress"); }
            finally { player.Props["FlashFog"] = savedFog; }
            Log($"paint_progress level_action={level?.GetProp("LevelAction")}");
            // Presented BEFORE SavePackage: shipped draws the frame and only then
            // writes the package. A host able to show the frame synchronously (the Core
            // thread waits for the main thread to draw) takes it here; otherwise the
            // frame stays deferred (TakeProgressFrame).
            if (PendingProgressFrame is WorldFrame f && PresentProgressNow is not null && PresentProgressNow(f))
                PendingProgressFrame = null;
        }

        // Host: show the progress frame NOW and return true once it is on screen;
        // false — cannot show (the frame stays deferred).
        public Func<WorldFrame, bool> PresentProgressNow;

        public WorldFrame TakeProgressFrame()
        {
            WorldFrame f = PendingProgressFrame;
            PendingProgressFrame = null;
            return f;
        }
        // Level LevelAction as carried by the last loaded save: "absent" — the field is not in the file
        // (CPF_Transient), else the number; null — nothing loaded.
        public string LoadedSaveLevelAction { get; private set; }

        // How many times the world was serialised (observer of the "frame -> package" order for fixtures).
        public int SaveSerializations { get; private set; }

        // Script package -> GUID of everything the save being written depends on (SerializeWorld, format 5).
        SortedDictionary<string, string> _saveDeps;

        void AddSaveDeps(UClass c)
        {
            for (; c is not null; c = UEInterpreter.CanonicalSuper(c))
            {
                UClass real = UEClassRegistry.IsImportStub(c) ? UEClassRegistry.Shared.FindForStub(c) ?? c : c;
                if (real.Package is { } pkg) _saveDeps[pkg.PackageName] = pkg.GUID;
            }
        }

        byte[] SerializeWorld(UEInterpreter interp, UEObject level, IReadOnlyList<string> mods)
        {
            SaveSerializations++;

            // Object table: first ULevel::Actors in their order, then everything
            // reachable from properties that is neither an actor nor a struct.
            var table = new List<UEObject>();
            var index = new Dictionary<UEObject, int>(UERefComparer<UEObject>.Instance);
            foreach (UEObject a in Actors)
            {
                if (a is null || index.ContainsKey(a)) continue;
                index[a] = table.Count; table.Add(a);
            }
            int actorCount = table.Count;
            for (int i = 0; i < table.Count; i++)
                CollectReachable(table[i], table, index);

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(SaveMagic);
            w.Write(SaveVersion);
            w.Write(mods?.Count ?? 0);                                   // format 5 header: the mod names
            if (mods is not null) foreach (string m in mods) w.Write(m);
            w.Write(Path.GetFileNameWithoutExtension(LoadedMapPath));
            w.Write(_clockFixed);
            w.Write(index.TryGetValue(level, out int li) ? li : -1);
            w.Write(actorCount);
            w.Write(table.Count);
            _saveDeps = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                for (int i = 0; i < table.Count; i++) WriteObject(w, table[i], index, interp);
                // Script packages (format 5).
                w.Write(_saveDeps.Count);
                foreach (var kv in _saveDeps) { w.Write(kv.Key); w.Write(kv.Value); }
            }
            finally { _saveDeps = null; }
            WriteEntryTravel(w, LevelEntryTravel);
            w.Flush();
            Log($"savegame_entry_travel {EntryTravelText(LevelEntryTravel)}");
            Log($"savegame map={Path.GetFileName(LoadedMapPath)} actors={actorCount} objects={table.Count} bytes={ms.Length} "
                + $"clock={_clockFixed} time={UEInterpreter.AsFloat(level.GetProp(PK.TimeSeconds))} "
                + $"player={PlayerPawn?.GetProp(PK.Name)} at={PlayerPawn?.GetProp(PK.Location)} state={PlayerPawn?.CurrentStateName}");
            return ms.ToArray();
        }

        void CollectReachable(UEObject o, List<UEObject> table, Dictionary<UEObject, int> index)
        {
            foreach (var kv in o.Props) CollectValue(kv.Value, table, index);
            if (o.StateFrame?.Variables is not null)
                foreach (var kv in o.StateFrame.Variables) CollectValue(kv.Value, table, index);
        }

        void CollectValue(object v, List<UEObject> table, Dictionary<UEObject, int> index)
        {
            switch (v)
            {
                case UEObject u:
                    if (u.IsStruct) { CollectReachable(u, table, index); return; }
                    if (IsRuntimeOwned(u) || index.ContainsKey(u)) return;
                    index[u] = table.Count; table.Add(u);
                    return;
                case UEObject[] arr:
                    foreach (UEObject e in arr) if (e is not null) CollectValue(e, table, index);
                    return;
                case System.Collections.IList list when v is not string:
                    foreach (object e in list) CollectValue(e, table, index);
                    return;
            }
        }

        // Runtime objects recreated by the bootstrap (CreateLocalViewport):
        // references to them are written as None. Shipped has no such record: LoadMap
        // (Engine.dll.c:246649-246660) zeroes Player only on Viewport.Actor of the
        // OLD level and destroys it, and the reference to the transient viewport is
        // not serialised into the .usa package — same result, own mechanism (port format).
        bool IsRuntimeOwned(UEObject u)
            => ReferenceEquals(u, Viewport) || ReferenceEquals(u, ViewportConsole)
               || (u.Class is not null && (UEInterpreter.ClassIsA(u.Class, "Viewport")
                                          || UEInterpreter.ClassIsA(u.Class, "Console")
                                          || UEInterpreter.ClassIsA(u.Class, "Player")
                                          // The world's ULevel (Actor.XLevel, UEInterpreter.XLevel): in the port a stub object
                                          // the world recreates; only transient fields hold it
                                          // (XLevel, WarpZoneInfo.OtherSideLevel), and they are not written to the file.
                                          || UEInterpreter.ClassIsA(u.Class, "Level")));

        void WriteObject(BinaryWriter w, UEObject o, Dictionary<UEObject, int> index, UEInterpreter interp)
        {
            AddSaveDeps(o.Class);
            w.Write(o.Class?.Name?.ToString() ?? "");
            w.Write(o.Class?.Package?.PackageName ?? "");
            w.Write(o.Destroyed);
            w.Write(o.CurrentStateName ?? "");
            w.Write(o.TimerRate); w.Write(o.TimerCounter); w.Write(o.TimerLoop);
            // The former by-name event disable table (removed, D-20): the format field
            // remains for compatibility with versions 1..2 and is always written empty.
            // The disable lives in the frame mask (probe below), as in shipped.
            w.Write(0);
            // State frame.
            UEFrame sf = o.StateFrame;
            if (sf is null || sf.Func is null) w.Write(false);
            else
            {
                if (sf.Func is not UState st)
                    throw new NotSupportedException(
                        $"SaveGame: the state frame of {o.GetProp(PK.Name)} holds {sf.Func.GetType().Name} ({sf.Func.Name}), not a UState");
                if (sf.IteratorStack.Count > 0)
                    throw new NotSupportedException(
                        $"SaveGame: {o.GetProp(PK.Name)} has an open iterator inside latent state code {st.Name} — UE1 does not allow that, nothing to save");
                w.Write(true);
                AddSaveDeps(st.Outer as UClass);
                // The state is EXACT: the frame may run state code of a
                // PARENT class (ResolveStateLabel follows the label
                // up through Super and switches Func), so the owner class is
                // written too, not just the name.
                w.Write(st.Name.ToString());
                w.Write((st.Outer as UClass)?.Name?.ToString() ?? "");
                if (sf.Cursor < 0 || sf.Cursor > sf.Tokens.Count)
                    throw new NotSupportedException($"SaveGame: {o.GetProp(PK.Name)} state {st.Name}: cursor {sf.Cursor} outside {sf.Tokens.Count} tokens");
                int codeOffset = sf.Cursor == sf.Tokens.Count ? -1 : sf.Tokens[sf.Cursor].Position;
                if (codeOffset >= 0 && !(sf.OffsetToIndex is { } o2i && o2i.TryGetValue(codeOffset, out int back) && back == sf.Cursor))
                    throw new NotSupportedException(
                        $"SaveGame: {o.GetProp(PK.Name)} state {st.Name}: token #{sf.Cursor} at offset {codeOffset} does not map back to itself");
                w.Write(codeOffset);
                w.Write(sf.ProbeMask);
                w.Write(sf.LatentState.ToString());
                w.Write(sf.SleepTimeLeft);
                WriteProps(w, sf.Variables, index, o, "StateFrame.Variables");
            }
            var kept = new List<KeyValuePair<string, object>>(o.Props.Count);
            HashSet<string> transient = TransientProps(o.Class);
            foreach (var kv in o.Props) if (!transient.Contains(kv.Key)) kept.Add(kv);
            WriteProps(w, kept.Count, kept, index, o, "Props");
        }

        // UProperty::ShouldSerializeValue (Core.dll.c:21717): a persistent archive (SavePackage, Ar+0x1c != 0)
        // does not write a CPF_Transient (0x2000) property — after load it has the class default. Among them
        // LevelInfo.LevelAction: SaveGame writes the package during LEVACT_Saving, but 2 never reaches the file.
        // CPF_Native (0x1000) is written by C++ Serialize in shipped, while the port keeps such fields in Props — untouched.
        readonly Dictionary<UClass, HashSet<string>> _transientProps = new(HP1.UEScript.UERefComparer<UClass>.Instance);
        HashSet<string> TransientProps(UClass cls)
        {
            var set = new HashSet<string>(UEPropNameComparer.Instance);
            if (cls is null) return set;
            if (_transientProps.TryGetValue(cls, out var cached)) return cached;
            // The instance class is real (import stubs occur among ancestors — CanonicalSuper substitutes them).
            for (UClass c = cls; c is not null; c = UEInterpreter.CanonicalSuper(c))
                foreach (UProperty p in c.EnumerateFields<UProperty>())
                    if (p is not null && p.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Transient))
                        set.Add(p.Name.ToString());
            return _transientProps[cls] = set;
        }

        void WriteProps(BinaryWriter w, Dictionary<string, object> props, Dictionary<UEObject, int> index, UEObject owner, string where)
            => WriteProps(w, props.Count, props, index, owner, where);
        void WriteProps(BinaryWriter w, UEProps props, Dictionary<UEObject, int> index, UEObject owner, string where)
            => WriteProps(w, props.Count, props, index, owner, where);
        void WriteProps(BinaryWriter w, int count, IEnumerable<KeyValuePair<string, object>> props, Dictionary<UEObject, int> index, UEObject owner, string where)
        {
            w.Write(count);
            foreach (var kv in props)
            {
                w.Write(kv.Key);
                WriteValue(w, kv.Value, index, owner, kv.Key);
            }
        }

        void WriteValue(BinaryWriter w, object v, Dictionary<UEObject, int> index, UEObject owner, string prop)
        {
            switch (v)
            {
                case null: w.Write((byte)V.Null); return;
                case bool b: w.Write((byte)V.Bool); w.Write(b); return;
                case byte by: w.Write((byte)V.Byte); w.Write(by); return;
                case int i: w.Write((byte)V.Int); w.Write(i); return;
                case float f: w.Write((byte)V.Float); w.Write(f); return;
                case double d: w.Write((byte)V.Double); w.Write(d); return;
                case long l: w.Write((byte)V.Long); w.Write(l); return;
                case string s: w.Write((byte)V.String); w.Write(s); return;
                case UEName n: w.Write((byte)V.Name); w.Write(n.Value ?? "None"); return;
                case UEVector vec: w.Write((byte)V.Vector); w.Write(vec.x); w.Write(vec.y); w.Write(vec.z); return;
                case UERotator r: w.Write((byte)V.Rotator); w.Write(r.Pitch); w.Write(r.Yaw); w.Write(r.Roll); return;
                case UEPlane p: w.Write((byte)V.Plane); w.Write(p.X); w.Write(p.Y); w.Write(p.Z); w.Write(p.W); return;
                case UEResourceRef rr:
                    w.Write((byte)V.Resource); w.Write(rr.Class ?? ""); w.Write(rr.Name ?? "");
                    w.Write(rr.ObjectPath ?? ""); w.Write(rr.IdentityKey ?? ""); return;
                case UClass c:
                {
                    // An import stub (a class literal of another package) stands for the class of its import's package.
                    UClass real = UEClassRegistry.IsImportStub(c) ? UEClassRegistry.Shared.FindForStub(c) ?? c : c;
                    AddSaveDeps(real);
                    w.Write((byte)V.ClassIn); w.Write(real.Package?.PackageName ?? ""); w.Write(real.Name.ToString()); return;
                }
                case UEModelRef mr: w.Write((byte)V.ModelRef); w.Write(mr.Name ?? ""); w.Write(mr.ExportIndex); return;
                case UEObject u when u.IsStruct:
                    w.Write((byte)V.Struct); w.Write(u.Class?.Name?.ToString() ?? "");
                    WriteProps(w, u.Props, index, u, prop); return;
                case UEObject u:
                    w.Write((byte)V.ObjRef);
                    w.Write(index.TryGetValue(u, out int id) ? id : -1); return;
                case UELib.Core.UObject uo:
                    w.Write((byte)V.PackageObject); w.Write(uo.GetPath()); return;
                case UEObject[] arr:
                    w.Write((byte)V.ObjArray); w.Write(arr.Length);
                    foreach (UEObject e in arr) w.Write(e is null ? -1 : (index.TryGetValue(e, out int eid) ? eid : -1));
                    return;
                case List<string> sl:
                    w.Write((byte)V.StringList); w.Write(sl.Count); foreach (string s in sl) w.Write(s ?? ""); return;
                case System.Collections.IList list:
                    w.Write((byte)V.List); w.Write(list.Count);
                    foreach (object e in list) WriteValue(w, e, index, owner, prop); return;
            }
            throw new NotSupportedException(
                $"SaveGame: property {owner?.GetProp(PK.Name)}.{prop} holds a value of type {v.GetType().FullName}, no encoding exists for it");
        }

        // ── LOAD ────────────────────────────────────────────────────────────
        // BODY CHECK BEFORE THE WORLD CHANGES. Reading only the header would let a
        // truncated file with an intact header fail only in LoadSavedWorld —
        // after the previous world is torn down. Here — the same record grammar
        // (WriteObject/WriteValue) as LoadSavedWorld, but without side effects:
        // nothing is created, no classes or packages are loaded.
        // Checked: lengths, tags, table reference bounds, and that the stream
        // ends exactly at the last byte. Returns the map name from the header.
        // Every way a damaged file can trip the reader (EndOfStream, a bad 7-bit length, …) comes out as
        // InvalidDataException: the tick turns that into the player-facing load failure (LoadMapBadSave).
        public static string ValidateSavedGame(byte[] data) => ScanSavedGame(data, null).Map;

        // What a body scan returns: the header map and the script record (Deps null: format 1..4, no record).
        sealed class SaveScan
        {
            public string Map; public List<string> Mods = new(); public List<(string Pkg, string Guid)> Deps;
            public long EntryTravelAt = -1;   // stream offset of the level-entry travel set (format 5), -1: none
        }
        // With a script check the scan also resolves, without creating anything, everything LoadSavedWorld resolves
        // against the current scripts: each object's class (the linker route, ResolveSavedClass — it may raise the
        // class's package, as the load itself would), its state node, its state frame (RestoreStateFrame on a scratch
        // object: state of the owner class, code offset at a token start), class values and package objects.
        sealed class ScriptCheck { public string MapPath; public UEInterpreter Interp; }

        static SaveScan ScanSavedGame(byte[] data, ScriptCheck sc)
        {
            try { return ValidateSavedGameBody(data, sc); }
            catch (Exception e) when (e is IOException or FormatException or ArgumentException or OverflowException)
            { throw new InvalidDataException($"damaged save: {e.Message}", e); }
        }

        // SAVE AGAINST THE CURRENT SCRIPTS — before the old world is torn down (UEGameEngineNatives.TryReadSave). null:
        // the save fits; else the reason (LoadMapBadSave). The script record decides first (format 5: a package GUID
        // differs; formats 1..4: mods are on now), worded by the mod names; then the per-object resolution, which
        // also covers 1..4 saves loaded without mods on an install whose own packages changed.
        public static string SaveScriptMismatch(byte[] data, string mapPath, UEInterpreter interp, IReadOnlyList<string> mods)
        {
            mods ??= Array.Empty<string>();
            try
            {
                SaveScan scan = ScanSavedGame(data, null);
                string cur = mods.Count == 0 ? "none" : string.Join(", ", mods);
                string differs = null;
                if (scan.Deps is null) { if (mods.Count > 0) differs = "the save predates the mod record"; }
                else
                    foreach (var (pkg, guid) in scan.Deps)
                    {
                        UELib.UnrealPackage now = UEActorGraphLoader.SavedPackage(pkg, mapPath);
                        if (now is null) { differs = $"script package {pkg} is not in the game"; break; }
                        if (now.GUID != guid) { differs = $"script package {pkg} differs"; break; }
                    }
                if (differs is not null)
                {
                    string head = ModsHead(scan.Mods, mods) ?? "saved with different game scripts";
                    return $"{head} (active mods: {cur}; {differs})";
                }
                ScanSavedGame(data, new ScriptCheck { MapPath = mapPath, Interp = interp });
                return null;
            }
            catch (InvalidDataException e) { return e.Message; }
        }

        // How the save's mod names differ from the current ones; null: the same names (or none on both sides).
        static string ModsHead(List<string> saved, IReadOnlyList<string> mods)
            => saved.Count == 0 && mods.Count > 0 ? "saved without mods"
                : saved.Count > 0 && !System.Linq.Enumerable.SequenceEqual(saved, mods, StringComparer.OrdinalIgnoreCase)
                    ? $"saved with mods: {string.Join(", ", saved)}"
                : null;

        // The load notice's wording of a validated save's mod names (UEGameEngineNatives), capitalised.
        public static string SavedWithMods(byte[] data, IReadOnlyList<string> mods)
            => ModsHead(ScanSavedGame(data, null).Mods, mods ?? Array.Empty<string>()) is { } h ? "S" + h[1..] : null;

        static SaveScan ValidateSavedGameBody(byte[] data, ScriptCheck sc)
        {
            using var r = new BinaryReader(new MemoryStream(data ?? Array.Empty<byte>()), Encoding.UTF8);
            int ver = ReadVersion(r);
            List<string> headerMods = ReadHeaderMods(r, ver);
            string map = r.ReadString();
            r.ReadInt64();
            int levelIndex = r.ReadInt32();
            if (ver == 1) { int c = r.ReadInt32(); if (c < 0) throw new InvalidDataException("name counter count < 0"); for (int i = 0; i < c; i++) { r.ReadString(); r.ReadInt32(); } }
            int actorCount = r.ReadInt32();
            int total = r.ReadInt32();
            if (total < 0 || actorCount < 0 || actorCount > total || levelIndex >= total)
                throw new InvalidDataException($"object table: actors={actorCount} total={total} level={levelIndex}");
            if (levelIndex < 0) throw new InvalidDataException("the save has no LevelInfo");
            for (int i = 0; i < total; i++)
            {
                string clsName = r.ReadString(), pkg = r.ReadString(); r.ReadBoolean(); string state = r.ReadString();
                r.ReadSingle(); r.ReadSingle(); r.ReadBoolean();
                UClass cls = sc is null ? null : UEActorGraphLoader.ResolveSavedClass(pkg, clsName, sc.MapPath);
                if (cls is not null && state.Length > 0 && sc.Interp.FindState(cls, state) is null)
                    throw new InvalidDataException($"save: {clsName}#{i} is in state {state}, which class {pkg}.{clsName} does not have");
                int dis = r.ReadInt32();
                if (dis < 0) throw new InvalidDataException($"#{i}: disabled events < 0");
                for (int d = 0; d < dis; d++)
                {
                    r.ReadString(); int n = r.ReadInt32();
                    if (n < 0) throw new InvalidDataException($"#{i}: events < 0");
                    for (int k = 0; k < n; k++) r.ReadString();
                }
                if (r.ReadBoolean())
                {
                    string stName = r.ReadString(), stOwner = r.ReadString(); int cursor = r.ReadInt32(); r.ReadUInt64();
                    LatentRunState latent = ver >= 3 ? LatentFromName(r.ReadString()) : LatentFromV2(r.ReadInt32());
                    r.ReadSingle();
                    // The same lookup LoadSavedWorld makes, on a scratch object (the frame lands there, not in a world).
                    if (cls is not null)
                        sc.Interp.RestoreStateFrame(new UEObject { Class = cls, Interp = sc.Interp }, stName, stOwner, cursor, 0,
                            latent, 0f, null, cursorIsCodeOffset: ver >= 3);
                    SkipProps(r, total, i, sc);
                }
                SkipProps(r, total, i, sc);
            }
            var scan = new SaveScan { Map = map, Mods = headerMods };
            if (ver >= 5)
            {
                scan.Deps = ReadDeps(r);
                scan.EntryTravelAt = r.BaseStream.Position;
                SkipEntryTravel(r);
            }
            if (r.BaseStream.Position != r.BaseStream.Length)
                throw new InvalidDataException($"{r.BaseStream.Length - r.BaseStream.Position} extra bytes after the "
                    + (ver >= 5 ? "level-entry travel set" : "object table"));
            return scan;
        }

        // Format 5 header: the mod names right after the version (none for 1..4).
        static List<string> ReadHeaderMods(BinaryReader r, int ver)
        {
            var mods = new List<string>();
            if (ver < 5) return mods;
            int n = r.ReadInt32();
            if (n < 0) throw new InvalidDataException("mod count < 0");
            for (int i = 0; i < n; i++) mods.Add(r.ReadString());
            return mods;
        }

        static List<(string, string)> ReadDeps(BinaryReader r)
        {
            int deps = r.ReadInt32();
            if (deps < 0) throw new InvalidDataException("script package count < 0");
            var list = new List<(string, string)>(deps);
            for (int i = 0; i < deps; i++) list.Add((r.ReadString(), r.ReadString()));
            return list;
        }

        static void SkipProps(BinaryReader r, int total, int owner, ScriptCheck sc)
        {
            int n = r.ReadInt32();
            if (n < 0) throw new InvalidDataException($"#{owner}: properties < 0");
            for (int i = 0; i < n; i++) { r.ReadString(); SkipValue(r, total, owner, sc); }
        }

        static void SkipValue(BinaryReader r, int total, int owner, ScriptCheck sc)
        {
            var tag = (V)r.ReadByte();
            switch (tag)
            {
                case V.Null: return;
                case V.Bool: r.ReadBoolean(); return;
                case V.Byte: r.ReadByte(); return;
                case V.Int: r.ReadInt32(); return;
                case V.Float: r.ReadSingle(); return;
                case V.Double: r.ReadDouble(); return;
                case V.Long: r.ReadInt64(); return;
                case V.String: case V.Name: r.ReadString(); return;
                case V.Class:
                {
                    string name = r.ReadString();
                    if (sc is not null && UEClassRegistry.Shared.Find(name) is null)
                        throw new InvalidDataException($"save: #{owner}: class {name} not found");
                    return;
                }
                case V.PackageObject:
                {
                    string path = r.ReadString();
                    if (sc is not null && UEClassRegistry.Shared.FindExport(path) is null)
                        throw new InvalidDataException($"save: #{owner}: package object {path} not found");
                    return;
                }
                case V.ClassIn:
                {
                    string pkg = r.ReadString(), name = r.ReadString();
                    if (sc is not null) UEActorGraphLoader.ResolveSavedClass(pkg, name, sc.MapPath);
                    return;
                }
                case V.Vector: r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); return;
                case V.Rotator: r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); return;
                case V.Plane: r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); return;
                case V.Resource: r.ReadString(); r.ReadString(); r.ReadString(); r.ReadString(); return;
                case V.ModelRef: r.ReadString(); r.ReadInt32(); return;
                case V.Struct: r.ReadString(); SkipProps(r, total, owner, sc); return;
                case V.ObjRef:
                {
                    int id = r.ReadInt32();
                    if (id >= total) throw new InvalidDataException($"#{owner}: reference to object #{id} outside the table ({total})");
                    return;
                }
                case V.ObjArray:
                {
                    int n = r.ReadInt32();
                    if (n < 0) throw new InvalidDataException($"#{owner}: array length < 0");
                    for (int i = 0; i < n; i++) { int id = r.ReadInt32(); if (id >= total) throw new InvalidDataException($"#{owner}: reference #{id} outside the table"); }
                    return;
                }
                case V.StringList:
                {
                    int n = r.ReadInt32();
                    if (n < 0) throw new InvalidDataException($"#{owner}: list length < 0");
                    for (int i = 0; i < n; i++) r.ReadString();
                    return;
                }
                case V.List:
                {
                    int n = r.ReadInt32();
                    if (n < 0) throw new InvalidDataException($"#{owner}: list length < 0");
                    for (int i = 0; i < n; i++) SkipValue(r, total, owner, sc);
                    return;
                }
            }
            throw new InvalidDataException($"#{owner}: unknown tag {tag}");
        }

        // World from a save: map geometry + objects from the file instead of
        // placed actors. Returns the placed records (ExportIndex>0) —
        // for the frontend's by-name index, like LoadWorld.
        public List<UEActorGraphLoader.ActorEntry> LoadSavedWorld(string mapPath, byte[] data, UEInterpreter interp)
        {
            if (interp is null) throw new ArgumentNullException(nameof(interp));
            _loadingSaveMapPath = mapPath;
            using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            int ver = ReadVersion(r);
            ReadHeaderMods(r, ver);
            string map = r.ReadString();
            if (!string.Equals(map, Path.GetFileNameWithoutExtension(mapPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"the save was made on {map}, but {Path.GetFileName(mapPath)} is being loaded");
            // FTime from the file is a port format field (loadgame journal); shipped
            // does NOT take the world clock from it (see LoadMap below, 0x1039e17b).
            long fileClockFixed = r.ReadInt64();
            int levelIndex = r.ReadInt32();
            if (ver == 1)
            {
                // v1: name counters are class state in shipped, not level state; not restored.
                int counters = r.ReadInt32();
                for (int i = 0; i < counters; i++) { r.ReadString(); r.ReadInt32(); }
            }
            int actorCount = r.ReadInt32();
            int total = r.ReadInt32();

            try
            {
                LoadWorldGeometry(mapPath);
                // Pass 1: objects by class (references resolve in the second).
                // The class comes from the (package, name) pair in the file; the package
                // is loaded as a linker import (UEActorGraphLoader.ResolveSavedClass).
                // Shells for all objects are created UP FRONT: properties of object #0
                // reference #1217 (LevelInfo.Game), which lies later in the stream —
                // the reference resolves to an already existing instance whose class
                // is filled in when its record is read.
                var table = new UEObject[total];
                for (int i = 0; i < total; i++) table[i] = new UEObject { Interp = interp };
                int packagesBefore = UEClassRegistry.Shared.Packages.Count;
                for (int i = 0; i < total; i++)
                {
                    LoadProgress?.Invoke();
                    string clsName = r.ReadString();
                    string pkg = r.ReadString();
                    UClass cls = UEActorGraphLoader.ResolveSavedClass(pkg, clsName, mapPath)
                        ?? throw new InvalidDataException($"LoadSavedWorld: class {pkg}.{clsName} not found in the registry");
                    table[i].Class = cls;
                    // The linker constructs every export like any object: class defaults, then the class C++
                    // constructor (StaticConstructObject, Core.dll.c:14176-14179); the saved properties overwrite
                    // them below. CPF_Transient properties (not in the file) and constructor fields (AParticleFX
                    // LOD=1, ElapsedTime=0, bUpdate) keep those values. Without it LOD came back empty and every
                    // emitter of a loaded save emitted at the 0.1 floor (author 2026-11-10, Lev3_Lumos).
                    table[i].LoadDefaults();
                    table[i].Destroyed = r.ReadBoolean();
                    string state = r.ReadString();
                    table[i].CurrentStateName = state.Length == 0 ? null : state;
                    table[i].TimerRate = r.ReadSingle();
                    table[i].TimerCounter = r.ReadSingle();
                    table[i].TimerLoop = r.ReadBoolean();
                    // The former by-name disable table (D-20). Production never filled it
                    // (natives 117/118 change only the mask), so every real save file
                    // has 0 here. Anything else is not a game save — loud failure.
                    int dis = r.ReadInt32();
                    if (dis != 0)
                        throw new InvalidDataException(
                            $"LoadSavedWorld: {clsName}#{i} carries {dis} entries of the removed event-disable table "
                            + "(D-20) — the game did not write it; the file is not a game save");
                    bool hasFrame = r.ReadBoolean();
                    if (hasFrame)
                    {
                        string stName = r.ReadString();
                        string stOwner = r.ReadString();
                        int cursor = r.ReadInt32();
                        ulong probe = r.ReadUInt64();
                        LatentRunState latent = ver >= 3 ? LatentFromName(r.ReadString()) : LatentFromV2(r.ReadInt32());
                        float sleep = r.ReadSingle();
                        var vars = ReadProps(r, table, interp, $"{clsName}#{i}.StateFrame");
                        interp.RestoreStateFrame(table[i], stName, stOwner, cursor, probe, latent, sleep, vars,
                            cursorIsCodeOffset: ver >= 3);
                    }
                    var props = ReadProps(r, table, interp, $"{clsName}#{i}");
                    foreach (var kv in props) table[i].Props[kv.Key] = kv.Value;
                }
                // Format 5 trailer: the script packages (checked before teardown) and the level-entry travel set, which
                // BootstrapLoadedWorld makes this level's TravelInfo again. 1..4: unknown, the level carries none.
                _loadedEntryTravel = null;
                if (ver >= 5)
                {
                    ReadDeps(r);
                    _loadedEntryTravel = ReadEntryTravel(r, interp, mapPath, null);
                }
                // Which packages the load ITSELF brought up (the catalog only grows):
                // witness that the "package not yet in the process" branch ran.
                var raised = new List<string>();
                for (int p = packagesBefore; p < UEClassRegistry.Shared.Packages.Count; p++)
                    raised.Add(UEClassRegistry.Shared.Packages[p].PackageName);
                // References resolved by table indices: objects were created in the
                // first loop BEFORE reading properties, so an ObjRef to a not yet
                // read object points at an already existing instance.
                var actors = new List<UEObject>(actorCount);
                for (int i = 0; i < actorCount; i++) actors.Add(table[i]);
                // The save package is `open saveN.usa`: package name from URL.Map
                // (saveN), not the map it was taken on. Without a travel request
                // (direct call) there is no name — the actor path becomes a STOP label.
                LevelPackageName = _pendingLevelPackage;
                _pendingLevelPackage = null;
                foreach (UEObject a in actors) if (a is not null) a.OuterPath = LevelPackageName;
                SetActors(actors);
                RebuildAnimChannelsFromAuxAnims();
                UEObject level = levelIndex >= 0 ? table[levelIndex] : null;
                if (level is null) throw new InvalidDataException("LoadSavedWorld: the save has no LevelInfo");
                // What the save ITSELF carries: LevelAction is CPF_Transient, not written to the file, so
                // here it is the class default (0). Exposed for the verify_savegame_packages fixture.
                LoadedSaveLevelAction = level.Props.TryGetValue("LevelAction", out object savedAction)
                    ? UEInterpreter.ToInt(savedAction).ToString() : "absent";
                // BindWorldLevelInfo zeroes TimeSeconds and may substitute Game
                // from the interpreter stub — for a LOADED world both come
                // from the file (level clock and GameInfo survived the save).
                object savedTime = level.GetProp(PK.TimeSeconds);
                object savedGame = level.GetProp(PK.Game);
                interp.BindWorldLevelInfo(level);
                // Ownership in the same step as the binding (as UEActorGraphLoader.Load): the next LoadMap releases this
                // level like any other (ResetWorld). Without it a travel after a save load took the saved LevelInfo as
                // the bootstrap of the next map (its Game, NetMode) and a failed save load left it bound.
                NoteBoundWorldLevelInfo(interp, level);
                level.Props["TimeSeconds"] = savedTime ?? 0f;
                if (savedGame is UEObject g) { level.Props["Game"] = g; g.Props["Level"] = level; }
                // UGameEngine::LoadMap, bBegunPlay branch (disasm 0x1039e17b..0x1039e19b):
                // GLevel->TimeSeconds(FTime) = _ftol(LevelInfo.TimeSeconds · 2^32) —
                // from the SAVED float, not the exact FTime: fractions below float ULP are lost.
                RestoreLevelClock(level, checked((long)((double)UEInterpreter.AsFloat(level.GetProp(PK.TimeSeconds)) * 4294967296.0)));
                // LoadMap of a save — the same UpdateTime call (0x1039d868).
                UpdateTime(level);
                // Names of restored objects are claimed in the level package just as
                // for a freshly loaded map (MakeUniqueSpawnName). Non-actor map exports
                // (Model/Polys/…) are not carried by the save; their names do not
                // collide with the name bases of actor classes.
                _occupiedObjectNames.Clear();
                foreach (UEObject a in actors)
                    if (a?.GetProp(PK.Name) is { } n) _occupiedObjectNames.Add(n.ToString());
                InitCollisionHash();
                // UGameEngine::LoadMap for a save too: LevelInfo0 from the file,
                // empty LevelEnterText = URL.Map (the save file name).
                ApplyLoadMapLevelEnterText(_capturedUrl?.Map ?? Path.GetFileName(mapPath));

                var entries = new List<UEActorGraphLoader.ActorEntry>();
                foreach (UEObject a in actors)
                {
                    int export = UEInterpreter.ToInt(a.GetProp(PK.ExportIndex));
                    if (export <= 0) continue;
                    entries.Add(new UEActorGraphLoader.ActorEntry(export, a.GetProp(PK.Name)?.ToString(),
                        a.Class?.Name?.ToString(), null, a));
                }
                UEObject savedPlayer = null;
                foreach (UEObject a in actors)
                    if (a is not null && !a.Destroyed && UEInterpreter.ClassIsA(a.Class, "PlayerPawn") && UEInterpreter.ToBool(a.GetProp(PK.bIsPlayer)))
                    { savedPlayer = a; break; }
                Log($"loadgame map={Path.GetFileName(mapPath)} actors={actorCount} objects={total} placed={entries.Count} "
                    + $"packages_raised={(raised.Count == 0 ? "-" : string.Join(",", raised))} "
                    + $"clock={_clockFixed} file_clock={fileClockFixed} time={UEInterpreter.AsFloat(level.GetProp(PK.TimeSeconds))} "
                    + $"player={savedPlayer?.GetProp(PK.Name)} at={savedPlayer?.GetProp(PK.Location)} state={savedPlayer?.CurrentStateName}");
                return entries;
            }
            catch
            {
                ResetWorld(interp);
                throw;
            }
        }

        string _loadingSaveMapPath;   // the map of the save LoadSavedWorld is reading (ResolveSavedClass finds System/ from it)

        Dictionary<string, object> ReadProps(BinaryReader r, UEObject[] table, UEInterpreter interp, string where)
        {
            int n = r.ReadInt32();
            var props = new Dictionary<string, object>(UEPropNameComparer.Instance);
            for (int i = 0; i < n; i++)
            {
                string key = r.ReadString();
                props[key] = ReadValue(r, table, interp, where + "." + key);
            }
            return props;
        }

        object ReadValue(BinaryReader r, UEObject[] table, UEInterpreter interp, string where)
        {
            var tag = (V)r.ReadByte();
            switch (tag)
            {
                case V.Null: return null;
                case V.Bool: return r.ReadBoolean();
                case V.Byte: return r.ReadByte();
                case V.Int: return r.ReadInt32();
                case V.Float: return r.ReadSingle();
                case V.Double: return r.ReadDouble();
                case V.Long: return r.ReadInt64();
                case V.String: return r.ReadString();
                case V.Name: return new UEName(r.ReadString());
                case V.Vector: return new UEVector(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                case V.Rotator: return new UERotator(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                case V.Plane: { float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle(), wv = r.ReadSingle(); return new UEPlane(x, y, z, wv); }
                case V.Resource:
                {
                    string cls = r.ReadString(), name = r.ReadString(), path = r.ReadString(), ident = r.ReadString();
                    return new UEResourceRef(cls, name, path.Length == 0 ? null : path, ident.Length == 0 ? null : ident);
                }
                case V.ClassIn:
                {
                    string pkg = r.ReadString(), name = r.ReadString();
                    try { return UEActorGraphLoader.ResolveSavedClass(pkg, name, _loadingSaveMapPath); }
                    catch (InvalidDataException) when (_tolerantRead) { return new SaveUnresolved($"class {pkg}.{name}"); }
                }
                case V.Class:   // versions 1..3
                {
                    string name = r.ReadString();
                    return (object)UEClassRegistry.Shared.Find(name)
                        ?? (_tolerantRead ? new SaveUnresolved($"class {name}") : null)
                        ?? throw new InvalidDataException($"LoadSavedWorld: {where}: class {name} not found");
                }
                case V.ModelRef: { string n = r.ReadString(); return new UEModelRef(n, r.ReadInt32()); }
                case V.Struct:
                {
                    string cls = r.ReadString();
                    var s = new UEObject { Class = cls.Length == 0 ? null : UEClassRegistry.Shared.Find(cls), Interp = interp, IsStruct = true };
                    foreach (var kv in ReadProps(r, table, interp, where)) s.Props[kv.Key] = kv.Value;
                    return s;
                }
                case V.ObjRef:
                {
                    int id = r.ReadInt32();
                    if (id < 0) return null;
                    if (id >= table.Length || table[id] is null)
                        throw new InvalidDataException($"LoadSavedWorld: {where}: reference to object #{id} outside the table");
                    return table[id];
                }
                case V.PackageObject:
                {
                    string path = r.ReadString();
                    return (object)UEClassRegistry.Shared.FindExport(path)
                        ?? (_tolerantRead ? new SaveUnresolved($"package object {path}") : null)
                        ?? throw new InvalidDataException($"LoadSavedWorld: {where}: package object {path} not found");
                }
                case V.ObjArray:
                {
                    int n = r.ReadInt32();
                    var arr = new UEObject[n];
                    for (int i = 0; i < n; i++) { int id = r.ReadInt32(); arr[i] = id < 0 ? null : table[id]; }
                    return arr;
                }
                case V.StringList:
                {
                    int n = r.ReadInt32(); var l = new List<string>(n);
                    for (int i = 0; i < n; i++) l.Add(r.ReadString());
                    return l;
                }
                case V.List:
                {
                    int n = r.ReadInt32(); var l = new List<object>(n);
                    for (int i = 0; i < n; i++) l.Add(ReadValue(r, table, interp, where + "[" + i + "]"));
                    return l;
                }
            }
            throw new InvalidDataException($"LoadSavedWorld: {where}: unknown tag {tag}");
        }

        // ── RESTART WITH PROGRESS — IMPROVEMENT, NOT ORIGINAL (author decisions 2026-11-12) ─────────────────────────────
        // A save that does not fit the current scripts (mods turned on/off, SaveScriptMismatch) is not refused: the saved
        // map is entered from its beginning the way a level travel enters it, with the travel set THE LEVEL WAS ENTERED
        // WITH (LevelEntryTravel, format 5 trailer) — not the values as of the save, which would let the restarted level's
        // pickups be collected twice. That is the death reload's semantics (slot 99, saved at level entry). HP1 carries
        // campaign progress between levels only through travel (baseHarry: house points, beans, stars, WizardCards,
        // lifePotions, bHas*; Pawn.Health/Weapon/SelectedItem; the travel inventory). Mid-level state is lost by design.
        // The set is decoded against the CURRENT classes: a record whose class is gone, or a property that is no travel
        // property of the current class or whose value does not fit its type, is dropped and listed (dropped).
        // null: the save has no entry set (format 1..4).
        public Dictionary<string, List<TravelActor>> TravelInfoFromSave(byte[] data, string mapPath, UEInterpreter interp,
            List<string> dropped)
        {
            SaveScan scan = ScanSavedGame(data, null);
            if (scan.EntryTravelAt < 0) return null;
            using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            r.BaseStream.Position = scan.EntryTravelAt;
            return ReadEntryTravel(r, interp, mapPath, dropped);
        }

        // The level-entry travel set (D-82): what the engine's travel carried into this level (AdoptLoadedUrl); a save
        // load restores the saved one (BootstrapLoadedWorld). Saved by SaveGame, read by TravelInfoFromSave.
        internal Dictionary<string, List<TravelActor>> LevelEntryTravel { get; set; } = new(UEPropNameComparer.Instance);
        Dictionary<string, List<TravelActor>> _loadedEntryTravel;

        // Record set layout: int32 keys; per key: string key, int32 records; per record: string class package, string
        // class name, string actor name, int32 props; per prop: string key, byte kind (0 value: the save's value
        // encoding, object references none; 1 actor reference: bool + name; 2 struct delta: int32 fields, per field string
        // name + value).
        void WriteEntryTravel(BinaryWriter w, Dictionary<string, List<TravelActor>> set)
        {
            var noIndex = new Dictionary<UEObject, int>(UERefComparer<UEObject>.Instance);
            w.Write(set.Count);
            foreach (var kv in set)
            {
                w.Write(kv.Key ?? "");
                w.Write(kv.Value.Count);
                foreach (TravelActor rec in kv.Value)
                {
                    UClass c = UEClassRegistry.IsImportStub(rec.Class) ? UEClassRegistry.Shared.FindForStub(rec.Class) ?? rec.Class : rec.Class;
                    w.Write(c?.Package?.PackageName ?? ""); w.Write(c?.Name?.ToString() ?? ""); w.Write(rec.Name ?? "");
                    w.Write(rec.Props.Count);
                    foreach (var p in rec.Props)
                    {
                        w.Write(p.Key);
                        switch (p.Value)
                        {
                            case TravelActorRef a: w.Write((byte)1); w.Write(a.Name is not null); w.Write(a.Name ?? ""); break;
                            case TravelStructDelta d:
                                w.Write((byte)2); w.Write(d.Fields.Count);
                                foreach (var f in d.Fields) { w.Write(f.Key); WriteValue(w, f.Value, noIndex, null, p.Key); }
                                break;
                            default: w.Write((byte)0); WriteValue(w, p.Value, noIndex, null, p.Key); break;
                        }
                    }
                }
            }
        }

        static void SkipEntryTravel(BinaryReader r)
        {
            for (int k = Count(r, "travel keys"); k > 0; k--)
            {
                r.ReadString();
                for (int n = Count(r, "travel records"); n > 0; n--)
                {
                    r.ReadString(); r.ReadString(); r.ReadString();
                    for (int p = Count(r, "travel props"); p > 0; p--)
                    {
                        r.ReadString();
                        switch (r.ReadByte())
                        {
                            case 0: SkipValue(r, 0, -1, null); break;
                            case 1: r.ReadBoolean(); r.ReadString(); break;
                            case 2: for (int f = Count(r, "struct fields"); f > 0; f--) { r.ReadString(); SkipValue(r, 0, -1, null); } break;
                            default: throw new InvalidDataException("travel: unknown property kind");
                        }
                    }
                }
            }
        }

        static int Count(BinaryReader r, string what)
        {
            int n = r.ReadInt32();
            return n >= 0 ? n : throw new InvalidDataException($"{what} < 0");
        }

        // dropped == null: strict (a save that fits the scripts: every class resolves, values are taken as written);
        // otherwise tolerant against the current classes, every drop listed.
        Dictionary<string, List<TravelActor>> ReadEntryTravel(BinaryReader r, UEInterpreter interp, string mapPath, List<string> dropped)
        {
            var set = new Dictionary<string, List<TravelActor>>(UEPropNameComparer.Instance);
            var noTable = Array.Empty<UEObject>();
            _loadingSaveMapPath = mapPath;
            _tolerantRead = dropped is not null;
            try
            {
                for (int k = Count(r, "travel keys"); k > 0; k--)
                {
                    string key = r.ReadString();
                    var records = new List<TravelActor>();
                    for (int n = Count(r, "travel records"); n > 0; n--)
                    {
                        string pkg = r.ReadString(), cls = r.ReadString(), name = r.ReadString();
                        UClass c = null;
                        if (dropped is null) c = UEActorGraphLoader.ResolveSavedClass(pkg, cls, mapPath);
                        else
                        {
                            try { c = UEActorGraphLoader.ResolveSavedClass(pkg, cls, mapPath); }
                            catch (InvalidDataException) { dropped.Add($"{name}: class {pkg}.{cls} is not in the current scripts"); }
                        }
                        var rec = new TravelActor { Class = c, Name = name };
                        for (int p = Count(r, "travel props"); p > 0; p--)
                        {
                            string pk = r.ReadString();
                            object v;
                            switch (r.ReadByte())
                            {
                                case 0: v = ReadValue(r, noTable, interp, "travel." + pk); break;
                                case 1: { bool has = r.ReadBoolean(); string an = r.ReadString(); v = new TravelActorRef(has ? an : null); break; }
                                case 2:
                                {
                                    var d = new TravelStructDelta();
                                    for (int f = Count(r, "struct fields"); f > 0; f--)
                                    { string fk = r.ReadString(); d.Fields[fk] = ReadValue(r, noTable, interp, "travel." + pk + "." + fk); }
                                    v = d; break;
                                }
                                default: throw new InvalidDataException("travel: unknown property kind");
                            }
                            if (c is null) continue;
                            if (dropped is not null && TravelMisfit(c, pk, v) is string why) { dropped.Add($"{name}.{pk}: {why}"); continue; }
                            rec.Props[pk] = v;
                        }
                        if (c is not null) records.Add(rec);
                    }
                    set[key] = records;
                }
                return set;
            }
            finally { _tolerantRead = false; }
        }

        // Why a saved travel value cannot go into the current class (null: it fits): the key names a CPF_Travel property
        // of the class chain (nearest first, as ExportTravelActor), "Name[i]" an element of a static array; a struct delta
        // names fields of that struct; an actor reference needs an actor reference property.
        static string TravelMisfit(UClass cls, string key, object v)
        {
            int br = key.IndexOf('[');
            string name = br > 0 ? key.Substring(0, br) : key;
            UProperty prop = null;
            for (UClass c = cls; c is not null && prop is null; c = UEClassRegistry.Shared.CanonicalSuper(c))
                foreach (UProperty p in c.EnumerateFields<UProperty>())
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { prop = p; break; }
            if (prop is null || !prop.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Travel)) return "no such travel property now";
            if (br > 0 && !(int.TryParse(key.AsSpan(br + 1, key.Length - br - 2), out int idx) && idx >= 0 && idx < prop.ArrayDim))
                return $"no element {key} now";
            if (br < 0 && prop.ArrayDim > 1) return "now a static array";
            switch (v)
            {
                case TravelActorRef: return IsActorReferenceProperty(prop) ? null : "not an actor reference now";
                case TravelStructDelta d:
                    if (prop is not UStructProperty sp) return "not a struct now";
                    foreach (var f in d.Fields)
                    {
                        UProperty fp = null;
                        for (UStruct s = sp.Struct; s is not null && fp is null; s = s.Super as UStruct)
                            foreach (UProperty x in s.EnumerateFields<UProperty>())
                                if (string.Equals(x.Name, f.Key, StringComparison.OrdinalIgnoreCase)) { fp = x; break; }
                        if (fp is null || !TravelValueFits(fp, f.Value, false)) return $"struct field {f.Key} does not fit";
                    }
                    return null;
                default:
                    return TravelValueFits(prop, v, true) ? null : $"saved {v?.GetType().Name ?? "None"} does not fit {prop.GetType().Name}";
            }
        }

        // Deterministic text of a travel set (journal: savegame_entry_travel / loadgame_entry_travel).
        static string EntryTravelText(Dictionary<string, List<TravelActor>> set)
        {
            var sb = new StringBuilder();
            foreach (var kv in set)
            {
                sb.Append('[').Append(kv.Key).Append(']');
                foreach (TravelActor rec in kv.Value)
                {
                    sb.Append(' ').Append(rec.Class?.Name).Append(':').Append(rec.Name).Append('{');
                    foreach (var p in rec.Props)
                    {
                        sb.Append(p.Key).Append('=');
                        if (p.Value is TravelStructDelta d)
                        {
                            sb.Append('(');
                            foreach (var f in d.Fields) sb.Append(f.Key).Append(':').Append(f.Value).Append(',');
                            sb.Append(')');
                        }
                        else sb.Append(p.Value is float fl ? fl.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : p.Value);
                        sb.Append(';');
                    }
                    sb.Append('}');
                }
            }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        // Restart with progress: the taken `open saveN.usa` request now names the saved map, so the level is entered as a
        // travel to it (URL.Map -> empty LevelEnterText, level package name), not as a save package.
        internal void RetargetTravelToMap(string map)
        {
            if (_capturedUrl is not null) _capturedUrl.Map = map;
            _pendingLevelPackage = map;
        }

        static bool TravelValueFits(UProperty p, object v, bool element)
        {
            if (v is null) return true;   // never set: the zero value, the same in any layout
            if (!element && p.ArrayDim > 1)
            {
                if (v is not System.Collections.IList l || l.Count != p.ArrayDim) return false;
                foreach (object e in l) if (!TravelValueFits(p, e, true)) return false;
                return true;
            }
            switch (p)
            {
                case UBoolProperty: return v is bool;
                case UFloatProperty: return v is float;
                case UIntProperty: return v is int;
                case UByteProperty: return v is byte;
                case UStrProperty: return v is string;
                case UNameProperty: return v is UEName;
                case UClassProperty: return v is UClass;
                case UObjectProperty: return v is UEObject { IsStruct: false };
                case UStructProperty sp:
                    if (v is not UEObject { IsStruct: true } bag) return false;
                    for (UStruct s = sp.Struct; s is not null; s = s.Super as UStruct)
                        foreach (UProperty f in s.EnumerateFields<UProperty>())
                            if (!bag.Props.TryGetValue(f.Name, out object fv) || !TravelValueFits(f, fv, false)) return false;
                    return true;
            }
            return false;
        }

        // Read side of ReadEntryTravel (tolerant): a class value or package object the current scripts do not have reads as this
        // marker instead of stopping the read; no travel property type accepts it (TravelValueFits).
        bool _tolerantRead;
        sealed class SaveUnresolved { public readonly string What; public SaveUnresolved(string what) { What = what; } }

        // Bootstrap of a LOADED world — the bBegunPlay branch of UGameEngine::LoadMap:
        // no InitGameInfo/InitExecution/InitGame/BeginPlay/Attach; only the
        // viewport and the player login (GameInfo.Login finds the saved pawn).
        public UEObject BootstrapLoadedWorld(UEInterpreter interp, PlayWorldConfig cfg)
        {
            FrameProducer?.ResetForNewLevel();
            ArmFirstWindowFrame();             // see UELevelRuntime.EngineFrameOwedAfterLoad
            ArmLoadStutter();                  // see UELevelRuntime.EmulateLoadStutter
            cfg.TravelInfo = null;
            WriteLoadMapComputerName(interp);                      // 246880..246917
            AdoptLoadedUrl(cfg);
            // The loaded level keeps the entry set it was saved with (format 5), not the empty one of this load (D-82).
            LevelEntryTravel = _loadedEntryTravel ?? new Dictionary<string, List<TravelActor>>(UEPropNameComparer.Instance);
            _loadedEntryTravel = null;
            Log($"loadgame_entry_travel {EntryTravelText(LevelEntryTravel)}");
            BuildLoadMapZoneDist();                                // 247117..247188
            // LoadMap for a level with bBegunPlay: the BeginPlay branch is skipped, but
            // bHighDetailMode (247315..247322), Actors rebuild (247511..247590,
            // placed AFTER the branch), ClientInit and DetailChange run here too.
            // Slot Actors(1) of a saved level is NULL: the editor brush was not loaded
            // into the game level, so it did not get into the save either.
            SetHighDetailMode(interp, withViewport: true);
            _levelSlotOneLoaded = false;
            SortActorsForPlay();
            foreach (UEObject a in Actors)
                if (a is not null && !a.Destroyed && UEInterpreter.ClassIsA(a.Class, "PlayerPawn"))
                    // Port format mechanism, not a shipped body: LoadMap 246649-246660
                    // zeroes Player only on Viewport.Actor of the old level, and .usa
                    // does not store the transient viewport. See IsRuntimeOwned.
                    a.Props["Player"] = null;
            // Music after the load is the original's (D-43): PostLogin -> ClientSetMusic(Level.Song = None, MTRAN_Fade), the
            // song playing before the load fades out. The save's own song is not restarted (D-49 withdrawn, author 2026-11-09).
            UEObject pawn = SpawnPlayActorThenBrushTracker(interp, cfg); // 0x1039e541..0x1039e5ab
            DetailChange(interp);
            WarmFirstUseCaches();   // port: data caches only (UELevelRuntime.LoadWarm.cs)
            Log($"loadgame_bootstrap player={pawn?.GetProp(PK.Name)} state={pawn?.CurrentStateName} "
                + $"firstDynamic={FirstDynamicActor} actors={Actors.Count}");
            return pawn;
        }

        public UEObject LoadSavedWorldAndBootstrap(UEInterpreter interp, string mapPath, byte[] data, PlayWorldConfig cfg)
        {
            TeardownPlayWorld(interp);
            LoadSavedWorld(mapPath, data, interp);
            BuildLoadMapScreenOfLoadedLevel(interp);
            return BootstrapLoadedWorld(interp, cfg);
        }

        // Console `open <url>` (UGameEngine::Exec → Browse → LoadMap): in the port
        // this is the same travel request that TryTakeLevelTravel takes; no
        // item carry-over (bNextItems=false).
        // UGameEngine::Exec OPEN (Engine.dll.c:244355-244375): with a viewport — SetClientTravel(Viewport, URL, bItems 0,
        // TRAVEL_Partial) (vtbl +0x8c; body 0x103a0480: Viewport.TravelURL +0x98, TravelType +0x94, bTravelItems +0xa4).
        // LevelInfo is NOT touched (D-14 items 12/16: writing NextURL/NextSwitchCountdown/bNextItems would be seen by
        // scripts, and ServerTravel would refuse in that window). TryTakeLevelTravel takes the request (block 0x103a1346).
        public void RequestOpen(UEInterpreter interp, string url)
        {
            _openRequestUrl = url ?? "";
            Log($"open_request url={url}");
        }

        // GameSaveInfo (natives 325/326 — AActor::execSaveGameSaveInfo 138213 /
        // execLoadGameSaveInfo 121710): file `GSys->SavePath * Name`
        // (FString::operator* — joined with the path separator, NO
        // extension), GFileManager->CreateFileWriter/Reader, then
        // Serialize(+0x28..+0x34, 4) × 4 (numBeans, numStars, numPoints,
        // savePointID) and operator<<(FArchive&, FString&) for
        // currentLevelString (+0x38); the answer is whether the file opened.
        //
        // The SavePath directory is a platform boundary: shipped takes
        // `[Core.System] SavePath=../Save` relative to System (headless —
        // the same `<game>/Save`), a frontend may use its host's writable directory.
        // The file NAME is the shipped name in both cases, without an extension of our own.
        public static string GameSaveInfoPath(string saveDir, string name)
            => Path.Combine(saveDir, name ?? "");

        public static bool WriteGameSaveInfo(string path, UEObject info)
        {
            if (info is null) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using var w = new BinaryWriter(File.Create(path));
                w.Write(UEInterpreter.ToInt(info.GetProp(PK.numBeans)));
                w.Write(UEInterpreter.ToInt(info.GetProp(PK.numStars)));
                w.Write(UEInterpreter.ToInt(info.GetProp(PK.numPoints)));
                w.Write(UEInterpreter.ToInt(info.GetProp(PK.savePointID)));
                WriteFString(w, info.GetProp(PK.currentLevelString)?.ToString() ?? "");
                return true;
            }
            catch (IOException) { return false; }
        }

        // S4 (author decision 2026-11-01, D-01 — NOT ORIGINAL): the slot pages list only the port's own saves.
        // HPConsole.doLevelSave(I) runs `SaveGame I` (the port's body save<I>.hp1sav) and then writes
        // GameSaveInfo<I>; a GameSaveInfo<I> without that body is the original game's (its body Save<I>.usa is
        // not read by the port). Native 326 answers "the file did not open" for it, so FESlotPage/FEFilePage show
        // the slot empty and it can never send `open save<I>.usa`. Shipped answers from the GameSaveInfo file alone.
        public static bool LoadGameSaveInfo(string saveDir, string name, UEObject info)
        {
            const string prefix = "GameSaveInfo";
            if (name is not null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !File.Exists(SaveBodyPath(saveDir, "save" + name.Substring(prefix.Length))))
                return false;
            return ReadGameSaveInfo(GameSaveInfoPath(saveDir, name), info);
        }

        // The port's save body (D-01, own format): `SaveGame N` writes and `open saveN.usa` reads this file.
        public static string SaveBodyPath(string saveDir, string stem) => Path.Combine(saveDir, stem + ".hp1sav");

        // execLoadGameSaveInfo reads through CreateFileReader(path, 0, *GNull) (Engine.dll.c:121752): a short file
        // is not an error the script sees. FArchiveFileReader::Serialize (FFileManagerWindows; the copy in Editor.dll,
        // FUN_10215750) copies what is left, then sets ArIsError and logs "ReadFile beyond EOF" to the GNull error
        // device; every later read copies nothing. A destination keeps the bytes it had — a field cut short keeps
        // its previous value (partly overwritten, little-endian) — and the native still returns 1. The GameSaveInfo
        // object is the caller's: FESlotPage reuses one across its six slots, so "previous" is the last slot's value.
        sealed class ShippedFileReader
        {
            readonly byte[] _data; int _pos;
            public ShippedFileReader(byte[] data) { _data = data; }
            // Returns how many bytes were copied into dst[0..].
            public int Serialize(byte[] dst, int len)
            {
                int n = Math.Min(len, _data.Length - _pos);
                Array.Copy(_data, _pos, dst, 0, n);
                _pos += n;
                return n;
            }
            public int Int(int current)
            {
                byte[] b = BitConverter.GetBytes(current);
                Serialize(b, 4);
                return BitConverter.ToInt32(b, 0);
            }
            public byte Byte(byte current) { var b = new[] { current }; Serialize(b, 1); return b[0]; }
        }

        public static bool ReadGameSaveInfo(string path, UEObject info)
        {
            if (info is null || !File.Exists(path)) return false;
            var r = new ShippedFileReader(File.ReadAllBytes(path));
            info.Props["numBeans"] = r.Int(UEInterpreter.ToInt(info.GetProp(PK.numBeans)));
            info.Props["numStars"] = r.Int(UEInterpreter.ToInt(info.GetProp(PK.numStars)));
            info.Props["numPoints"] = r.Int(UEInterpreter.ToInt(info.GetProp(PK.numPoints)));
            info.Props["savePointID"] = r.Int(UEInterpreter.ToInt(info.GetProp(PK.savePointID)));
            info.Props["currentLevelString"] = ReadFString(r, info.GetProp(PK.currentLevelString)?.ToString() ?? "", path);
            return true;
        }

        // operator<<(FArchive&, FString&) — Core.dll.c:11986, body 0x10150830
        // (disasm checked). FString = TArray<TCHAR> WITH A TERMINATOR: Num = length+1,
        // an empty string has Num = 0.
        //   Save: SaveNum = all chars <= 0xFF ? Num : -Num;
        //         Ar << FCompactIndex(SaveNum);
        //         SaveNum >= 0 → Num bytes (char >= 0x100 → 0x7F, unreachable
        //         here), else Num UTF-16 words — the terminator IS included.
        //   Load: |SaveNum| elements of the same size; Num == 1 (terminator
        //         only) → empty string.
        // The compact index is FCompactIndex (0x10163d40): for the file writer
        // (ArIsSaving) — not raw 4 bytes. That this is so follows from
        // consistency: the reader (ArIsLoading, +0x10) reads compactly, and
        // shipped reads its own files.
        internal static void WriteFString(BinaryWriter w, string s)
        {
            int num = s.Length == 0 ? 0 : s.Length + 1;
            bool ansi = true;
            foreach (char c in s) if (c > 0xFF) { ansi = false; break; }
            WriteCompactIndex(w, ansi ? num : -num);
            for (int i = 0; i < num; i++)
            {
                char c = i < s.Length ? s[i] : '\0';
                if (ansi) w.Write((byte)c);
                else w.Write((ushort)c);
            }
        }

        // Load half of 0x10150830 over ShippedFileReader. SaveNum starts as the CURRENT string's ±Num
        // (0x10150866-0x10150891), so an index read that copies nothing yields it back (FCompactIndex below).
        // Num = |SaveNum|; every char is read into one stack slot ([ebp+0xb] bytes 0x101508d5, [ebp+0xa] words
        // 0x101508f6) that keeps the previous char when a read copies nothing; Num == 1 -> empty (0x10150915).
        // Before the first char that slot holds the high byte/word of the archive pointer — not established (§4),
        // a loud STOP; so is a char array without a terminator (FString::operator* would read past it).
        static string ReadFString(ShippedFileReader r, string current, string where)
        {
            int curNum = current.Length == 0 ? 0 : current.Length + 1;
            bool ansi = true;
            foreach (char c in current) if (c > 0xFF) { ansi = false; break; }
            int saveNum = ReadCompactIndex(r, ansi ? curNum : -curNum);
            int num = saveNum < 0 ? -saveNum : saveNum;
            int size = saveNum < 0 ? 2 : 1;
            var chars = new char[num];
            var slot = new byte[2];
            bool known = false;
            for (int i = 0; i < num; i++)
            {
                int got = r.Serialize(slot, size);
                if (got == size) known = true;
                if (!known)
                    throw new InvalidDataException($"GameSaveInfo {where}: the file ends inside the first character of a {num}-element "
                        + "string — shipped keeps the archive pointer's high byte there (0x101508d5/0x101508f6), not established (§4)");
                chars[i] = size == 1 ? (char)slot[0] : (char)BitConverter.ToUInt16(slot, 0);
            }
            if (num <= 1) return "";
            int len = Array.IndexOf(chars, '\0');
            if (len < 0)
                throw new InvalidDataException($"GameSaveInfo {where}: {num} characters without a terminator — "
                    + "shipped FString::operator* would read past the array (§4)");
            return new string(chars, 0, len);
        }

        // FCompactIndex (0x10163d40): first byte — sign 0x80 | "more" 0x40 |
        // low 6 bits of |V|; then 7 bits each with a "more" flag 0x80.
        internal static void WriteCompactIndex(BinaryWriter w, int v)
        {
            uint a = (uint)(v < 0 ? -(long)v : v);
            byte b0 = (byte)((v < 0 ? 0x80 : 0) | (a >= 0x40 ? 0x40 : 0) | (a & 0x3F));
            w.Write(b0);
            if ((b0 & 0x40) == 0) return;
            a >>= 6;
            while (true)
            {
                byte b = (byte)((a >= 0x80 ? 0x80 : 0) | (a & 0x7F));
                w.Write(b);
                if ((b & 0x80) == 0) return;
                a >>= 7;
            }
        }

        // Load half of FCompactIndex (0x10163d40-0x10163edd). Each byte is first set to the encoding of the
        // CURRENT value (B0 = sign 0x80 | more 0x40 | |V| low 6 bits; Bk = 7 bits of |V| >> (6+7(k-1)) | more 0x80;
        // B4 = |V| >> 27) and then read — a read that copies nothing leaves it; B1..B4 are read only while the
        // previous byte (as read) says "more". Decode: B4 whole, then 7 bits per byte down to B1, then B0's 6.
        static int ReadCompactIndex(ShippedFileReader r, int current)
        {
            uint v = (uint)(current < 0 ? -(long)current : current);
            byte b0 = r.Byte((byte)((current < 0 ? 0x80 : 0) + (v < 0x40 ? v : (v & 0x3F) + 0x40)));
            var more = new byte[4];
            int depth = 0;
            if ((b0 & 0x40) != 0)
            {
                uint s = v >> 6;
                for (int k = 0; k < 4; k++)
                {
                    byte init = k == 3 ? (byte)s : (byte)(s < 0x80 ? s : (s & 0x7F) + 0x80);
                    more[k] = r.Byte(init);
                    depth = k + 1;
                    if (k == 3 || (more[k] & 0x80) == 0) break;
                    s >>= 7;
                }
            }
            int value = 0;
            for (int k = depth - 1; k >= 0; k--)
                value = k == 3 ? more[3] : (value << 7) + (more[k] & 0x7F);
            value = (value << 6) + (b0 & 0x3F);
            return (b0 & 0x80) != 0 ? -value : value;
        }
    }
}
