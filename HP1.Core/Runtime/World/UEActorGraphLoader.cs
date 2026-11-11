using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UELib;
using UELib.Core;
using UELib.Types;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    // The canonical placed-actor graph, read straight from the level .unr.
    //
    // Core-owned because it IS the actor identity: export order, ExportIndex,
    // the Brush UModelRef a mover collides with, every instance property tag and
    // every object reference between placed actors. A frontend that builds its
    // own actor list from a baked sidecar cannot carry those — it produces
    // UEObjects that look right and are missing exactly the fields the collision
    // and gameplay layers dispatch on, which surfaces one field at a time
    // (Brush, then ExportIndex, then the next) with no end to the sequence.
    //
    // Lifted verbatim out of the headless runner, where it had been sitting; the
    // only removal is that runner's own wiring of the result (SetActors /
    // LevelActors), which stays with whichever frontend calls this.
    public static class UEActorGraphLoader
    {
        // The linker's load context for a RUNNING game (Core.dll.c:111046-111052,
        // ULinker's constructor): GIsEditor→RF_LoadForEdit, GIsClient→
        // RF_LoadForClient, GIsServer→RF_LoadForServer. A standalone game is
        // client AND server and is NOT the editor, so the mask is exactly these
        // two bits. ULinkerLoad::CreateExport (Core.dll.c:6111-6113,
        // UnLinker.cpp:0x289) creates an export only when
        // `ObjectFlags & ContextFlags != 0` — everything else simply does not
        // exist in the running world.
        const uint RF_LoadForClient = 0x00010000;
        const uint RF_LoadForServer = 0x00020000;
        const uint GameLoadContext = RF_LoadForClient | RF_LoadForServer;

        // `owner` is the world loading this map. The parameter is REQUIRED and has no default:
        // the level is bound to the interpreter here, and "who must release it later" must not
        // have a silent answer — a default would hide lost ownership exactly where level identity matters.
        public static List<ActorEntry> Load(string mapPath, UEInterpreter interp, UELevelRuntime owner)
        {
            // LOUD AND BEFORE LOADING STARTS. `null` is not an answer to the owner question:
            // the binding below would succeed with no registered owner — exactly the state
            // "level bound, nobody to release it" this parameter exists to exclude. The check runs
            // BEFORE reading the package so a refusal leaves no half-done work behind.
            if (owner is null)
                throw new ArgumentNullException(nameof(owner),
                    "loading binds LevelInfo to the interpreter — the owning world, "
                    + "which must release it on teardown, has to be specified");
            var package = UnrealLoader.LoadPackage(mapPath, FileAccess.Read);
            package.InitializePackage(UnrealPackage.InitFlags.All);
            // All map exports are objects of the package in which runtime spawning looks for
            // a free name (UELevelRuntime.MakeUniqueSpawnName).
            var exportNames = new List<string>(package.Exports.Count);
            foreach (var export in package.Exports) exportNames.Add(export.ObjectName.ToString());
            owner.SeedPackageObjectNames(exportNames);
            var result = new List<ActorEntry>();
            var byExport = new Dictionary<int, ActorEntry>();
            LoadImportedScriptPackages(package, mapPath);

            // ULevel::Actors, not the export table. The shipped BeginPlay sweeps
            // index THIS array (`Actors(0)->IsA(ALevelInfo::StaticClass())`,
            // UnLevel.h:0xf7-0xf8), and the order is player-visible: AddPawn
            // inserts at the HEAD of PawnList during PreBeginPlay, so the sweep
            // order is what decides which pawn GameInfo.Login reuses. Read
            // through the format reader; UELib has no ULevel. The same reader below
            // provides byte-exact instance tags (see ApplyInstanceProperties).
            var reader = new UEPackageReader(mapPath);
            int[] actorOrder = ReadLevelActorOrder(reader, mapPath);

            int duplicateSlots = 0;
            foreach (int exportIndex in actorOrder)
            {
                owner.LoadProgress?.Invoke();
                if (exportIndex == 0) continue;             // UE1 keeps holes in Actors
                // ONE ACTOR IN TWO SLOTS — shipped data, not a read defect.
                // Measured with ReadLevelActorOrder: Lev2_Fire2 1 duplicate (ZoneInfo0), Lev2_HogFront 39
                // (brushes), Lev3_Quid2 4 (CutDest/CutMark), Lev3_Troll 133 (beans), Lev_Tut3b 34
                // (MusicEvent/Trigger). Shipped does not compact the array on load: `ULevel::Actors`
                // is deserialized as is, and every walk (tick with the bTicked guard, BeginPlay sweeps
                // guarded by bScriptInitialized, AllActors iterators) sees both slots. The port keeps
                // the list the same way: the second slot points to THE SAME object; instance properties
                // are applied once (`distinct` below), and the sweep guard is bScriptInitialized as in
                // shipped (Engine.dll.c:247428-247464, bit 0x10000 of word AActor+0x14c per prop_offsets).
                if (byExport.TryGetValue(exportIndex, out ActorEntry seen))
                {
                    result.Add(seen);
                    duplicateSlots++;
                    continue;
                }
                var export = package.Exports[exportIndex - 1];
                // Editor-only content — the default brush, every CSG brush, and
                // UnrealEd's own viewport cameras (Tag="TextureBrowser") — is
                // flagged RF_NotForClient|RF_NotForServer and never enters a
                // running game. This replaces the old `className == "Brush"`
                // exclusion, which was a name-keyed special case that happened to
                // cover the brushes and missed the cameras.
                if ((export.ObjectFlags & GameLoadContext) == 0) continue;
                string className = export.Class?.ObjectName?.ToString() ?? "";
                UClass cls = ResolveExportClass(export, className);
                if (cls is null)
                {
                    // LOUD (§8). Shipped has no such outcome: the actor class is a map import and the
                    // linker loads its package. A silent skip here lost 435 placed actors on 30 maps
                    // (InvisibleHarry, SneakFilch, QuidGoal, Snail, fluffy, BossQuirrel…).
                    HP1.Core.UETrace.Warn("STOP/unknown: actor "
                        + $"'{export.ObjectName}' of {Path.GetFileName(mapPath)} is DROPPED — class "
                        + $"'{ImportPackageName(export)}.{className}' is not in the class registry");
                    continue;
                }
                if (!UEInterpreter.ClassIsA(cls, "Actor")) continue;
                UObject source = export.Object;
                if (source is null) continue;
                source.Load<UObjectRecordStream>();
                var obj = new UEObject { Class = cls, Interp = interp };
                obj.LoadDefaults();
                obj.Props["Name"] = new UEName(export.ObjectName.ToString());
                obj.Props["ExportIndex"] = exportIndex;
                // The Outer of an actor export is the map package (outer = 0 in the export
                // table); the package name is held by the owning world.
                obj.OuterPath = owner.LevelPackageName ??= Path.GetFileNameWithoutExtension(mapPath);
                var entry = new ActorEntry(exportIndex, export.ObjectName.ToString(), className, source, obj);
                result.Add(entry);
                byExport.Add(exportIndex, entry);
            }

            // UE1 resolves every reference before any lifecycle event. Apply all
            // instance tags only after the complete export-index registry exists.
            if (duplicateSlots > 0)
                HP1.Core.UETrace.Warn(
                    $"[Loader] {Path.GetFileName(mapPath)}: ULevel::Actors holds {duplicateSlots} "
                    + "repeated slots (the same actor twice) — shipped data, the list is kept as is");
            var classRefs = new SortedDictionary<int, List<UClass>>();
            foreach (var entry in result.Distinct())
            {
                owner.LoadProgress?.Invoke();
                ApplyInstanceProperties(entry, package, reader, byExport, interp, classRefs[entry.ExportIndex] = new List<UClass>());
                // AActor::PostLoad (Engine.dll.c:34377-34396): after serializing a placed
                // actor, with CLASS_Localized — LoadLocalized(this, 0): package = the actor's
                // Outer name (the level, "MyLevel"), section = the actor name. The shipped
                // <Outer>.int may be absent — then Localize(Optional) is empty and values stay.
                UExportTableItem ex = package.Exports[entry.ExportIndex - 1];
                UEObjectConfig.LoadLocalized(entry.Runtime, entry.Runtime.Class, propagate: false, isInstance: true,
                    outerName: ex.OuterTable?.ObjectName.ToString(), objName: entry.Name);
            }
            // The port's class load order (UEClassRegistry.NoteClassLoaded; DrawFrame sprite sort key): the actor classes
            // were noted in Level.Actors order (LoadDefaults above); the classes named by instance-tag class literals
            // follow, in EXPORT order — UE1 EndLoad serializes the loaded objects sorted by linker index, and the linker
            // creates a referenced class then (Lev_Tut1 TriggerSpwnBsChrOnPPnt6, export 3227, is serialized first:
            // live gen_fem_2 < gen_male_3 < gen_fem_5).
            foreach (var refs in classRefs.Values)
                foreach (UClass c in refs) HP1.Core.UEClassRegistry.Shared?.NoteClassLoaded(c);
            // AMover::PostLoad (Engine 0x103be510; called after serializing each loaded mover):
            // SavedPos = (-12345,-12345,-12345)
            // (0x103be53f-0x103be573, 0xc640e400), SavedRot = (123,456,789)
            // (0x103be576-0x103be58d). The markers ensure the brush tracker
            // (FMovingBrushTracker::Update, Engine FUN_1038c260) does not take the
            // saved value for the current one. Brush polygon numbers
            // (iLink = index, 0x103be599-0x103be5c0) live in the render mirror —
            // RenderWorldMirror.ApplyMoverPostLoad.
            // SHAPE FROM SOURCE, NOT PROVEN (V9). The tracker constructor
            // (GNewBrushTracker: SetupActorBrush, then Update(0)) overwrites SavedPos/SavedRot of
            // every non-bStatic brush BEFORE any read, and never reads bStatic brushes. Measured
            // (--occludeprobe over all maps, "no markers" mutation): for no non-bStatic campaign
            // mover do serialized SavedPos/SavedRot equal Location/Rotation, so the tracker accepts
            // each one even without markers — the mutation is indistinguishable.
            foreach (var entry in result.Distinct())
                if (UEInterpreter.ClassIsA(entry.Runtime.Class, "Mover"))
                {
                    entry.Runtime.Props["SavedPos"] = new UEVector(-12345f, -12345f, -12345f);
                    entry.Runtime.Props["SavedRot"] = new UERotator(123, 456, 789);
                }

            // Shipped asserts this rather than searching for it: every
            // `GetLevelInfo()` in the decompile is `Actors(0)` guarded by
            // `Actors(0)->IsA(ALevelInfo::StaticClass())` (UnLevel.h:0xf7-0xf8).
            // Tut1 carries a SECOND LevelInfo (LevelInfo5, Actors(3)), so a
            // find-by-class search is not the same thing as slot 0.
            // ULevel::Actors(1) on every campaign map is an editor brush without
            // RF_LoadForClient|Server: in game the slot is empty (NULL), but the rebuild
            // 247517..247525 still keeps it in place. The port's list stores no empty slots,
            // so slot 1 is pinned only if an actor was actually loaded into it.
            owner._levelSlotOneLoaded = SlotOneLoaded(actorOrder, byExport, result);
            ActorEntry levelInfo = result.Count > 0 ? result[0] : null;
            if (levelInfo is null || !levelInfo.ClassName.Equals("LevelInfo", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"ULevel::Actors(0) is not a LevelInfo (got {levelInfo?.ClassName ?? "nothing"})");
            // SINGLE LevelInfo.
            //
            // In shipped there is ONE LevelInfo: `GetLevelInfo()` in the decompile is
            // Shipped has ONE LevelInfo: `GetLevelInfo()` is Actors(0) (UnLevel.h:0xf7-0xf8, see
            // above). The interpreter creates a pre-world stub before the map is read; after loading
            // it is replaced by the placed Actors(0), carrying over only what pre-world code already
            // read (Game, NetMode — see BindWorldLevelInfo). From then on the world has one level.
            // Identity matters: e.g. the "move hit the WORLD" check compares hitActor with
            // GetLevelInfo() by reference (APawn::Mount on climbable walls), as do the emitter
            // load prime (`Level.TimeSeconds == 0` gate), Trigger ReTriggerDelay and level-base
            // clearing in attach_base. Fixture spawn_region_levelinfo_until_after_beginplay
            // holds this behaviour.
            UEObject bootstrapLevel = interp.GetLevelInfo();
            levelInfo.Runtime.Props["Game"] = bootstrapLevel.GetProp(PK.Game);
            levelInfo.Runtime.Props["Level"] = levelInfo.Runtime;
            // The interpreter SWITCHES to the placed Actors(0) — from here on there is one
            // LevelInfo in the world, as in shipped.
            interp.BindWorldLevelInfo(levelInfo.Runtime);
            // Ownership is recorded in THE SAME step as the binding: there must be no state
            // between these two lines in which the level is bound but nobody can release it.
            owner.NoteBoundWorldLevelInfo(interp, levelInfo.Runtime);
            foreach (var entry in result)
                if (!entry.Runtime.Props.ContainsKey("Level")) entry.Runtime.Props["Level"] = levelInfo.Runtime;
            return result;
        }

        // ULevel::Actors lives in the Level export's own byte stream, which
        // UELib does not model. The format reader owns the layout.
        internal static bool SlotOneLoaded(int[] actorOrder, Dictionary<int, ActorEntry> byExport,
            List<ActorEntry> result)
            => actorOrder.Length > 1 && actorOrder[1] != 0
               && byExport.TryGetValue(actorOrder[1], out ActorEntry slotOne)
               && result.Count > 1 && ReferenceEquals(result[1], slotOne);

        static int[] ReadLevelActorOrder(UEPackageReader reader, string mapPath)
        {
            foreach (PackageExport e in reader.Exports)
                if (reader.ClassName(e).Equals("Level", StringComparison.OrdinalIgnoreCase))
                    return reader.ReadLevelActorOrder(e);
            throw new InvalidDataException($"no Level export in {mapPath} — cannot order ULevel::Actors");
        }

        // ANY EXPORT OF ANY PACKAGE AS A UEObject (resources: textures, meshes…) — the same path
        // as map actors: class defaults, then instance tags (UELib plus the entries the
        // UEPackageReader walker recovers). Object references inside the package that are not map
        // actors remain resource references, as DecodeValue returns them.
        // Both caches are keyed by the PHYSICAL file (UEClassRegistry.PhysicalPath): the same package through a
        // symlinked directory is one parse and one reader.
        static readonly Dictionary<string, UnrealPackage> _exportPackages = new(StringComparer.OrdinalIgnoreCase);
        // One reader per package path: it is immutable, and creating one per resource re-read the
        // whole .utx file each time (~520 MB of garbage in the first seconds of Lev3_Troll —
        // main-thread hitches).
        static readonly Dictionary<string, UEPackageReader> _exportReaders = new(StringComparer.OrdinalIgnoreCase);

        public static UEObject LoadExportObject(string packagePath, int exportIndex, UEInterpreter interp)
        {
            string file = HP1.Core.UEClassRegistry.PhysicalPath(packagePath);
            if (!_exportPackages.TryGetValue(file, out UnrealPackage package))
            {
                package = UnrealLoader.LoadPackage(packagePath, System.IO.FileAccess.Read);
                package.InitializePackage(UnrealPackage.InitFlags.All);
                _exportPackages[file] = package;
            }
            UExportTableItem export = package.Exports[exportIndex - 1];
            string className = export.Class?.ObjectName.ToString() ?? "Class";
            UClass cls = HP1.Core.UEClassRegistry.Shared.Find(
                    export.Class is UELib.UImportTableItem ci
                        ? HP1.Core.UEClassRegistry.ImportPackageName(package, ci) : null, className)
                ?? HP1.Core.UEClassRegistry.Shared.FindImportedClass(className)
                ?? throw new InvalidDataException($"export {export.ObjectName}: class {className} not found");
            var obj = new UEObject { Class = cls, Interp = interp };
            obj.LoadDefaults();
            obj.Props["Name"] = new UEName(export.ObjectName.ToString());
            UObject source = export.Object;
            // A resource's Outer is the export group or the package itself.
            obj.OuterPath = source.Outer is not null
                ? HP1.UEScript.Render.ShippedLayout.FullPath(source.Outer) : package.PackageName;
            source.Load<UObjectRecordStream>();
            var entry = new ActorEntry(exportIndex, export.ObjectName.ToString(), className, source, obj);
            if (!_exportReaders.TryGetValue(file, out UEPackageReader reader))
                _exportReaders[file] = reader = new UEPackageReader(packagePath);
            ApplyInstanceProperties(entry, package, reader,
                new Dictionary<int, ActorEntry>(), interp);
            return obj;
        }

        private static void ApplyInstanceProperties(
            ActorEntry entry, UnrealPackage package, UEPackageReader reader,
            IReadOnlyDictionary<int, ActorEntry> byExport, UEInterpreter interp, List<UClass> classRefs = null)
        {
            // Byte-exact tag list from OUR OWN format reader. The patched UELib tag parser silently
            // DROPS some entries ("PropertyTag value size error: Expected N, Actual 0" family —
            // mainly indexed struct tags: on Lev_Tut1b movers KeyRot(1) is listed but KeyPos(1..)
            // is missing ENTIRELY, and the raw by-name API returns zero pairs for them). Effect:
            // KeyPos = null×8 -> DoOpen interpolated 0→0 and the door "opened" without moving
            // (Flipendo slabs). So the byte source is the UEPackageReader walker; the UELib list
            // stays the working tag list (14545 pinned Tut1 properties are read through it), and
            // everything missing from it is RECOVERED from the walker below.
            List<UEPackageReader.RawPropertyTag> walk =
                reader.ReadInstanceProperties(reader.Exports[entry.ExportIndex - 1]);
            var walkByKey = new Dictionary<(string, int), UEPackageReader.RawPropertyTag>();
            foreach (var t in walk) walkByKey[(t.Name.ToLowerInvariant(), t.ArrayIndex)] = t;

            // Names+slots applied by the tag loop: the recovery pass below must not overwrite
            // what was already applied.
            var applied = new HashSet<(string, int)>();

            void ApplySlot(string name, int slot, object value, UProperty declaration)
            {
                // A class literal in the instance tags (the class load order, see Load).
                if (value is UClass refClass) classRefs?.Add(refClass);
                if (declaration?.ArrayDim > 1 || slot > 0)
                {
                    if (entry.Runtime.GetProp(name) is not System.Collections.IList list)
                    {
                        var created = new List<object>();
                        entry.Runtime.Props[name] = created;
                        list = created;
                    }
                    int required = Math.Max(slot + 1, declaration?.ArrayDim ?? 0);
                    while (list.Count < required) list.Add(null);
                    list[slot] = value;
                }
                else
                    entry.Runtime.Props[name] = value;
            }

            foreach (var tag in entry.Source.Properties ?? Enumerable.Empty<UDefaultProperty>())
            {
                if (tag?.Name is null) continue;
                string name = tag.Name.ToString();
                int slot = tag.ArrayIndex < 0 ? 0 : tag.ArrayIndex;
                applied.Add((name.ToLowerInvariant(), slot));
                byte[] raw = RawValue(entry.Source, name, tag.ArrayIndex);
                if (raw.Length == 0 && tag.Type != PropertyType.BoolProperty
                    && walkByKey.TryGetValue((name.ToLowerInvariant(), slot), out var wt))
                    raw = wt.Value;   // tag is in the list, UELib lost its bytes
                object value = DecodeValue(entry.Runtime.Class, tag, raw, package, byExport, interp);
                ApplySlot(name, slot, value, FindProperty(entry.Runtime.Class, name));
            }

            // ── RECOVERY PASS: tags DROPPED from the UELib list ──
            // File order is preserved (the walk is ordered); the type and struct name come
            // from THE TAG ITSELF, as ULinkerLoad reads them — including object references
            // by compact index.
            foreach (var t in walk)
            {
                if (applied.Contains((t.Name.ToLowerInvariant(), t.ArrayIndex))) continue;
                object value = DecodeWalkerValue(entry.Runtime.Class, t, package, byExport);
                ApplySlot(t.Name, t.ArrayIndex, value, FindProperty(entry.Runtime.Class, t.Name));
            }
        }

        // Decode a tag from our own walker — the same branches as DecodeValue, but the type is
        // the UE1 code (UnProp: 1=Byte 2=Int 3=Bool 4=Float 5=Object 6=Name 10=Struct 13=Str).
        // The campaign carries no other codes in instances — loud, as in DecodeValue.
        private static object DecodeWalkerValue(
            UClass ownerClass, UEPackageReader.RawPropertyTag tag, UnrealPackage package,
            IReadOnlyDictionary<int, ActorEntry> byExport)
        {
            byte[] raw = tag.Value;
            switch (tag.Type)
            {
                case 1: return raw.Length > 0 ? raw[0] : (byte)0;
                case 2: return raw.Length >= 4 ? BitConverter.ToInt32(raw, 0) : 0;
                case 3: return tag.BoolValue;
                case 4: return raw.Length >= 4 ? BitConverter.ToSingle(raw, 0) : 0f;
                case 5:
                {
                    int pos = 0; return ResolveObjectRef(ReadCompactIndex(raw, ref pos), package, byExport);
                }
                case 6:
                {
                    int pos = 0; return new UEName(ReadName(raw, ref pos, package));
                }
                case 10: return DecodeStruct(tag.StructName, raw, package, byExport, ownerClass);
                case 13:
                {
                    int pos = 0; return ReadString(raw, ref pos);
                }
                default:
                    throw new NotSupportedException(
                        $"instance property {ownerClass.Name}.{tag.Name}: walker type {tag.Type}");
            }
        }

        private static UProperty FindProperty(UClass cls, string name)
        {
            for (UClass current = cls; current is not null; current = UEInterpreter.CanonicalSuper(current))
                foreach (var prop in current.EnumerateFields<UProperty>())
                    if (string.Equals(prop.Name?.ToString(), name, StringComparison.OrdinalIgnoreCase)) return prop;
            return null;
        }

        private static object DecodeValue(
            UClass ownerClass, UDefaultProperty tag, byte[] raw, UnrealPackage package,
            IReadOnlyDictionary<int, ActorEntry> byExport, UEInterpreter interp)
        {
            switch (tag.Type)
            {
                case PropertyType.ByteProperty: return raw.Length > 0 ? raw[0] : (byte)0;
                case PropertyType.IntProperty: return raw.Length >= 4 ? BitConverter.ToInt32(raw, 0) : 0;
                case PropertyType.FloatProperty: return raw.Length >= 4 ? BitConverter.ToSingle(raw, 0) : 0f;
                case PropertyType.BoolProperty: return tag.BoolValue ?? false;
                case PropertyType.NameProperty:
                {
                    int pos = 0; return new UEName(ReadName(raw, ref pos, package));
                }
                case PropertyType.StrProperty:
                {
                    int pos = 0; return ReadString(raw, ref pos);
                }
                case PropertyType.ObjectProperty:
                {
                    int pos = 0; return ResolveObjectRef(ReadCompactIndex(raw, ref pos), package, byExport);
                }
                case PropertyType.StructProperty:
                    return DecodeStruct(tag.StructName?.ToString(), raw, package, byExport, ownerClass);
                default:
                    // Dynamic arrays and delegates require their own raw layout. None are
                    // needed by the current placed-actor lifecycle gate; keep them loud.
                    throw new NotSupportedException($"instance property {ownerClass.Name}.{tag.Name}: {tag.Type}");
            }
        }

        // ── CLASS PACKAGES COME FROM THE MAP'S IMPORT TABLE ─────────────────
        //
        // Shipped has no "list of game packages": ULinkerLoad loads the package of every import
        // referenced by an export being loaded (ULinkerLoad::VerifyImport ->
        // UObject::GetPackageLinker). A placed actor's class is an import `Class <Package>.<Name>`,
        // so the script package arrives with the map (maps whose classes live in
        // Hog1/Hog2/Hub3/Hub4/Hub5/Tut2/HPModels/DProps need this).
        //
        // Only CLASS source packages (file System/<Name>.u) are loaded here — transitively, like
        // the linker: the map package pulls Hub4, which pulls its ancestors. Textures/sounds/meshes
        // go through their own catalogs and the class registry does not need them.
        public static void LoadImportedScriptPackages(UnrealPackage package, string mapPath)
        {
            string systemDir = SystemDirFor(mapPath);
            if (systemDir is null) return;
            var registry = HP1.Core.UEClassRegistry.Shared;
            var pending = new Queue<UnrealPackage>();
            // Visited packages. An already registered package (e.g. loaded earlier by a frontend
            // list) is ALSO walked — once: otherwise its imports are not followed and a
            // second-level dependency is lost (HProps.u -> Fire.u: FireTexture without hierarchy,
            // Texture(DynamicLoadObject(..FireTexture)) on 164 BroomHoopStage rings gave None).
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Enqueue(package);
            while (pending.Count > 0)
            {
                UnrealPackage current = pending.Dequeue();
                if (current.Imports is null) continue;
                foreach (var import in current.Imports)
                {
                    if (import.OuterIndex != 0) continue;
                    if (!string.Equals(import.ClassName?.ToString(), "Package", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = import.ObjectName.ToString();
                    if (!visited.Add(name)) continue;
                    if (registry.HasPackage(name)) { pending.Enqueue(registry.FindPackage(name)); continue; }
                    string path = UEInstallPath.File(Path.Combine(systemDir, name + ".u"));
                    if (path is null) continue;       // not a script package (utx/uax/…)
                    pending.Enqueue(registry.GetOrLoadPackage(path));
                }
            }
        }

        static string SystemDirFor(string mapPath)
        {
            string gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(mapPath)));
            return gameRoot is null ? null : UEInstallPath.Dir(Path.Combine(gameRoot, "System"));
        }

        // Class of an object from a SAVE — by (package, name). In shipped the save file is a level
        // package and each object's class arrives as an import `Class <Package>.<Name>`: the linker
        // loads the import's package itself (VerifyImport -> GetPackageLinker), whether or not that
        // map was loaded earlier in the process. A lookup by short name in what is already loaded
        // fails (loading a Lev5_Final slot in a fresh process: Hub4.DummyPotionBottle) and would
        // pick same-named HPModels/HProps classes arbitrarily.
        public static UClass ResolveSavedClass(string packageName, string className, string mapPath)
        {
            var registry = HP1.Core.UEClassRegistry.Shared;
            if (!string.IsNullOrEmpty(packageName) && !registry.HasPackage(packageName))
            {
                string systemDir = SystemDirFor(mapPath);
                string path = systemDir is null ? null : UEInstallPath.File(Path.Combine(systemDir, packageName + ".u"));
                if (path is not null)
                    LoadImportedScriptPackages(registry.GetOrLoadPackage(path), mapPath);
            }
            UnrealPackage home = registry.FindPackage(packageName);
            if (home is not null)
                foreach (var e in home.Exports)
                    if (e.Object is UClass c && e.OuterIndex == 0
                        && string.Equals(c.Name, className, StringComparison.OrdinalIgnoreCase)
                        && c.Super is not null)
                        return c;
            // Shipped linker: the class is not in the import package — VerifyImport refuses (D-14 item 23); a short-name
            // lookup would pick a same-named class from another package. No campaign map (39) reaches this.
            throw new InvalidDataException($"save: class {packageName}.{className} not found in the import package (VerifyImport)");
        }

        // Export class by the import PACKAGE, not the short name: the shipped data has same-named
        // classes in different packages (HPModels/HProps: hagridhutrock, levshadow,
        // savepointfloatbook), and a short name would pick the last registered one.
        static UClass ResolveExportClass(UExportTableItem export, string className)
        {
            var registry = HP1.Core.UEClassRegistry.Shared;
            UnrealPackage home = registry.FindPackage(ImportPackageName(export));
            if (home is not null)
                foreach (var e in home.Exports)
                    if (e.Object is UClass c && e.OuterIndex == 0
                        && string.Equals(c.Name, className, StringComparison.OrdinalIgnoreCase)
                        && c.Super is not null)
                        return c;
            throw new InvalidDataException($"export class {ImportPackageName(export) ?? "<not an import>"}.{className} not found "
                + "in the import package (VerifyImport; D-14 item 23 — short-name matching removed)");
        }

        static string ImportPackageName(UExportTableItem export)
        {
            UObjectTableItem item = export.Class;
            while (item?.Outer is not null) item = item.Outer;
            return item is UImportTableItem ? item.ObjectName?.ToString() : null;
        }

        // Object reference by compact index — shared by UELib tags and our own walker's tags.
        private static object ResolveObjectRef(
            int index, UnrealPackage package, IReadOnlyDictionary<int, ActorEntry> byExport)
        {
            if (index == 0) return null;
            if (index > 0 && byExport.TryGetValue(index, out ActorEntry target)) return target.Runtime;
            // A reference to a non-actor export. The only one gameplay
            // consumes is a mover's Brush (a Model export): dropping it made
            // every isBrush branch dead and movers collided as CYLINDERS — a
            // forbidden primitive substitution. Keep the reference as a
            // typed marker carrying the export index for bounds lookups.
            if (index > 0 && index - 1 < package.Exports.Count)
            {
                var exportRef = package.Exports[index - 1];
                if (string.Equals(exportRef.Class?.ObjectName?.ToString(), "Model", StringComparison.OrdinalIgnoreCase))
                    return new UEModelRef(exportRef.ObjectName.ToString(), index);
                // Any other export of the map package: shipped hands the property the loaded object
                // itself (the linker resolves the export), never None. Kept as a resource reference
                // with its full export path, like an import (D-14 item 22). Campaign census
                // 2026-10-30: the only such reference is LevelInfo.Summary -> <map>.LevelSummary.
                return new Hp1ExactResourceRef(exportRef.Class?.ObjectName?.ToString() ?? "Object",
                    exportRef.ObjectName.ToString(), UETextureSource.FullPathOf(package.PackageName, exportRef));
            }
            if (index < 0 && -index - 1 < package.Imports.Count)
            {
                var import = package.Imports[-index - 1];
                string objectName = import.ObjectName.ToString();
                if (string.Equals(import.ClassName?.ToString(), "Class", StringComparison.OrdinalIgnoreCase))
                {
                    return HP1.Core.UEClassRegistry.Shared.Find(
                        HP1.Core.UEClassRegistry.ImportPackageName(package, import), objectName);
                }
                return new Hp1ExactResourceRef(import.ClassName?.ToString() ?? "Object", objectName,
                    ResolveImportObjectPath(package, index));
            }
            return null;
        }

        private static string ResolveImportObjectPath(UnrealPackage package, int index)
        {
            if (index >= 0 || -index - 1 >= package.Imports.Count)
                throw new InvalidDataException($"invalid import index {index}");
            var chain = new List<(string Name, string ClassName, string ClassPackage)>();
            int current = index;
            while (current < 0)
            {
                if (-current - 1 >= package.Imports.Count)
                    throw new InvalidDataException($"invalid import outer {current}");
                var item = package.Imports[-current - 1];
                chain.Add((item.ObjectName.ToString(), item.ClassName?.ToString(), item.PackageName?.ToString()));
                current = (int)item.OuterIndex;
            }
            chain.Reverse();
            // ULinkerLoad::VerifyImport — a single body in the Core registry.
            return HP1.Core.UEClassRegistry.Shared.VerifyImportPath(chain)
                ?? string.Join('.', chain.ConvertAll(c => c.Name));
        }

        private static object DecodeStruct(
            string structName, byte[] raw, UnrealPackage package,
            IReadOnlyDictionary<int, ActorEntry> byExport, UClass ownerClass)
        {
            int pos = 0;
            switch (structName?.ToLowerInvariant())
            {
                case "vector":
                    return new Vector3(ReadSingle(raw, ref pos), ReadSingle(raw, ref pos), ReadSingle(raw, ref pos));
                // FPlane = FVector(12) + float W(4): UStruct::SerializeBin
                // (Core.dll.c:45567-45628) reads all four fields. Sharing the vector case would lose
                // W of placed actors (ZoneInfo.ViewFog/ViewFlash, PlayerPawn.FlashFog…) and mismatch
                // the type of class defaults (UEObject.DecodeStructDefault returns UEPlane). D-14 WORLD.
                case "plane":
                {
                    float px = ReadSingle(raw, ref pos), py = ReadSingle(raw, ref pos), pz = ReadSingle(raw, ref pos);
                    return new UEPlane(px, py, pz, ReadSingle(raw, ref pos));
                }
                case "rotator":
                    return new UERotator(ReadInt32(raw, ref pos), ReadInt32(raw, ref pos), ReadInt32(raw, ref pos));
                case "coords":
                    return Struct(
                        ("Origin", new Vector3(ReadSingle(raw, ref pos), ReadSingle(raw, ref pos), ReadSingle(raw, ref pos))),
                        ("XAxis", new Vector3(ReadSingle(raw, ref pos), ReadSingle(raw, ref pos), ReadSingle(raw, ref pos))),
                        ("YAxis", new Vector3(ReadSingle(raw, ref pos), ReadSingle(raw, ref pos), ReadSingle(raw, ref pos))),
                        ("ZAxis", new Vector3(ReadSingle(raw, ref pos), ReadSingle(raw, ref pos), ReadSingle(raw, ref pos))));
                case "color":
                    return Struct(("R", ReadByte(raw, ref pos)), ("G", ReadByte(raw, ref pos)),
                                  ("B", ReadByte(raw, ref pos)), ("A", ReadByte(raw, ref pos)));
                case "floatparams":
                    return Struct(("Base", ReadSingle(raw, ref pos)), ("Rand", ReadSingle(raw, ref pos)));
                case "colorparams":
                    return Struct(("Base", Struct(("R", ReadByte(raw, ref pos)), ("G", ReadByte(raw, ref pos)),
                                                   ("B", ReadByte(raw, ref pos)), ("A", ReadByte(raw, ref pos)))),
                                  ("Rand", Struct(("R", ReadByte(raw, ref pos)), ("G", ReadByte(raw, ref pos)),
                                                   ("B", ReadByte(raw, ref pos)), ("A", ReadByte(raw, ref pos)))));
                case "pointregion":
                {
                    // Review #21 P2: the ONE Core Region constructor.
                    int zone = ReadCompactIndex(raw, ref pos);
                    return UEObject.MakeRegion(
                        zone > 0 && byExport.TryGetValue(zone, out var z) ? z.Runtime : null,
                        ReadInt32(raw, ref pos), ReadByte(raw, ref pos));
                }
                case "scale":
                    return Struct(("Scale", new Vector3(ReadSingle(raw, ref pos), ReadSingle(raw, ref pos), ReadSingle(raw, ref pos))),
                                  ("SheerRate", ReadSingle(raw, ref pos)), ("SheerAxis", ReadByte(raw, ref pos)));
                case "wizardlist":
                    return Struct(("ID", ReadInt32(raw, ref pos)), ("bHasCard", ReadBool(raw, ref pos)));
                case "emote":
                    return Struct(("EmoteName", ReadString(raw, ref pos)), ("StartAnim", new UEName(ReadName(raw, ref pos, package))),
                                  ("LoopAnim", new UEName(ReadName(raw, ref pos, package))),
                                  ("StopAnim", new UEName(ReadName(raw, ref pos, package))));
                case "stationdata":
                    return Struct(("stationDestination", new UEName(ReadName(raw, ref pos, package))),
                                  ("nextStationGroup", ReadInt32(raw, ref pos)),
                                  ("pathType", new UEName(ReadName(raw, ref pos, package))),
                                  ("firstPath", new UEName(ReadName(raw, ref pos, package))),
                                  ("Rotation", new UERotator(ReadInt32(raw, ref pos), ReadInt32(raw, ref pos), ReadInt32(raw, ref pos))),
                                  ("PauseTime", ReadSingle(raw, ref pos)), ("Behavior", ReadByte(raw, ref pos)));
                case "cutloc":
                {
                    int actor = ReadCompactIndex(raw, ref pos);
                    return Struct(("locName", actor > 0 && byExport.TryGetValue(actor, out var a) ? a.Runtime : null),
                                  ("alias", ReadString(raw, ref pos)));
                }
                case "cutcast":
                {
                    int actor = ReadCompactIndex(raw, ref pos);
                    return Struct(("actorName", actor > 0 && byExport.TryGetValue(actor, out var a) ? a.Runtime : null),
                                  ("alias", ReadString(raw, ref pos)), ("nCurAction", ReadInt32(raw, ref pos)),
                                  ("fNextActionTime", ReadSingle(raw, ref pos)), ("bWaiting", ReadBool(raw, ref pos)),
                                  ("strWaitingFor", ReadString(raw, ref pos)), ("bFinished", ReadBool(raw, ref pos)));
                }
                default:
                {
                    // Other structs — by DECLARATION, with the same UStruct::SerializeBin walk
                    // (Core.dll.c:45567-45628) used for class defaults. A struct with an object
                    // reference is not allowed here: an instance reference is a map actor (byExport),
                    // while the shared walk returns package resource markers.
                    object generic = UEObject.DecodeInstanceStruct(ownerClass, structName, raw);
                    if (generic is null)
                        throw new NotSupportedException($"struct {structName} size={raw.Length}");
                    return generic;
                }
            }
        }

        private static UEObject Struct(params (string Name, object Value)[] fields)
        {
            var value = new UEObject { IsStruct = true };
            foreach (var field in fields) value.Props[field.Name] = field.Value;
            return value;
        }

        public static byte[] RawValue(UObject owner, string name, int arrayIndex)
        {
            foreach (var pair in owner.GetRawPropertyValueBytesAll(name))
                if (pair.Key == arrayIndex) return pair.Value;
            return Array.Empty<byte>();
        }

        private static int ReadCompactIndex(byte[] bytes, ref int pos)
        {
            int first = bytes[pos++]; bool negative = (first & 0x80) != 0; int value = first & 0x3f;
            if ((first & 0x40) != 0)
            {
                // FCompactIndex (Core 0x10163d40): at most FIVE bytes — the "more" flag of the
                // fifth byte is not read (a sixth byte would consume following data).
                int shift = 6;
                for (int k = 0; k < 4; k++) { int next = bytes[pos++]; value |= (next & 0x7f) << shift; shift += 7; if ((next & 0x80) == 0) break; }
            }
            return negative ? -value : value;
        }

        private static int ReadInt32(byte[] bytes, ref int pos) { int v = BitConverter.ToInt32(bytes, pos); pos += 4; return v; }
        private static float ReadSingle(byte[] bytes, ref int pos) { float v = BitConverter.ToSingle(bytes, pos); pos += 4; return v; }
        private static byte ReadByte(byte[] bytes, ref int pos) => bytes[pos++];
        private static bool ReadBool(byte[] bytes, ref int pos) => bytes[pos++] != 0;
        private static string ReadName(byte[] bytes, ref int pos, UnrealPackage package)
            => package.Names[ReadCompactIndex(bytes, ref pos)].Name;
        // operator<<(FArchive&, FString&), load (Core 0x10150830, disasm
        // 0x101508a9..0x1015090c): SaveNum is a compact index; |SaveNum| elements:
        // SaveNum >= 0 — BYTES, each widened to TCHAR (`movzx cx, byte`, i.e. ISO-8859-1),
        // SaveNum < 0 — UTF-16 words. The string runs to the first zero (TCHAR array with
        // terminator); the position advances by ALL elements. Matches RawReader.FString and
        // UEObject.ReadAtomicFString. D-14 WORLD.
        internal static string ReadString(byte[] bytes, ref int pos)
        {
            int saveNum = ReadCompactIndex(bytes, ref pos);
            int num = saveNum < 0 ? -saveNum : saveNum;
            var chars = new char[num];
            for (int i = 0; i < num; i++)
            {
                if (saveNum >= 0) chars[i] = (char)bytes[pos++];
                else { chars[i] = (char)(bytes[pos] | bytes[pos + 1] << 8); pos += 2; }
            }
            int len = Array.IndexOf(chars, '\0');
            return new string(chars, 0, len < 0 ? num : len);
        }

        // Deliberately NOT a positional record: Unity's netstandard2.1 profile has
        // no IsExternalInit, so init-only setters do not compile there. Same reason
        // as UEValueTypes and the mesh hash fields.
        public sealed class ActorEntry
        {
            public readonly int ExportIndex;
            public readonly string Name;
            public readonly string ClassName;
            public readonly UObject Source;
            public readonly UEObject Runtime;

            public ActorEntry(int exportIndex, string name, string className,
                UObject source, UEObject runtime)
            {
                ExportIndex = exportIndex; Name = name; ClassName = className;
                Source = source; Runtime = runtime;
            }
        }
    }

    // Map-instance ObjectProperty path reconstructed from the serialized import (or
    // map export) outer chain, never a leaf-name search.
    public sealed class Hp1ExactResourceRef : UEResourceRef
    {
        public Hp1ExactResourceRef(string resourceClass, string name, string objectPath)
            : base(resourceClass, name, objectPath) { }
    }
}
