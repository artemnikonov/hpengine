// HP2's PERSISTENT ACTOR CACHE (HP1 has none). An actor with Actor.bPersistent keeps its properties across level
// travel: ULevel::SavePersistentActors writes the level's bPersistent actors into <slot>\cache\<Map>_pa.usa (the console
// command SAVEPACTORS, issued by hgame's harry.LoadLevel), and ULevel::LoadPersistentActors reads that file back on every
// non-Entry map load. Spec: the maintainer's notes hp2_saves_disasm.md (Q2, Q4) and hp2_saves.md.
//
// The FILE FORMAT is the port's own (D-01, DECISIONS 2026-11-16 HP2 save model (a)); what is the original's: the file
// name, which actors are written and in which order, which (property, element) values travel
// (Hp2PersistentProperties; object and class values carry nothing), the header checks, and every decision of the load
// (apply / spawn / skip / stop / destroy). Layout (BinaryWriter strings):
//   "HPPA", int32 1, string base, int32 map file size, int64 map mtime, int32 n,
//   n x (string actor name, string class name, fields), string "EOF"
//   fields: int32 count, count x (string key, byte kind: 0 the save's value encoding (WriteValue), 2 fields of a struct)
// Keys are a property name or "Name[i]" for an element of a static array (the travel record's keys).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UELib.Core;
using HP1.Core;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        internal sealed class PaRecord
        {
            public string Name, Class;
            public readonly Dictionary<string, object> Fields = new(UEPropNameComparer.Instance);
        }

        internal sealed class PaFile
        {
            public string Base = "";
            public int MapSize;
            public long MapTime;
            public int Count = -1;          // the declared record count; -1: Records.Count
            public readonly List<PaRecord> Records = new();
            public string Eof = "EOF";
        }

        // HP2: the slot directory (GSys+0x40) the cache lives under; set by the engine (UEGameEngineNatives.GameSaveDir).
        internal Func<string> Hp2SlotDir;
        string _loadUrlMap;   // URL.Map of the current LoadMap (the LevelEnterText fill's source)

        // The name derivation both bodies share (Save 0x103b9030.., Load 0x103b9e40..): base = MapArg after its last '\';
        // without FURL::DefaultMapExt in it (FString::InStr, case-sensitive) base += "." + DefaultMapExt;
        // MapPath = GSys+0x4c * base (the size/mtime check; GSys+0x4c is built by appInit from appBaseDir minus "System"
        // and a Paths entry's directory, 0x101574b6..0x1015764e: the maps directory — inferred, the port passes the
        // directory of the loaded map); PAFile = SlotDir * "cache" * Left(base, InStr(DefaultMapExt) - 1) + "_pa." +
        // FURL::DefaultSaveExt (Left clamps a negative count to 0).
        internal static (string Base, string MapPath, string PaFile) Hp2PaPaths(string slotDir, string mapsDir, string mapArg)
        {
            string ext = UEUrl.DefaultMapExt ?? throw new InvalidOperationException("persistent actors: FURL::DefaultMapExt is not set");
            string save = UEUrl.DefaultSaveExt ?? throw new InvalidOperationException("persistent actors: FURL::DefaultSaveExt is not set");
            string b = mapArg ?? "";
            b = b.Substring(b.LastIndexOf('\\') + 1);
            if (b.IndexOf(ext, StringComparison.Ordinal) < 0) b += "." + ext;
            string stem = b.Substring(0, Math.Clamp(b.IndexOf(ext, StringComparison.Ordinal) - 1, 0, b.Length));
            return (b, Path.Combine(mapsDir ?? "", b), Path.Combine(slotDir ?? "", "cache", stem + "_pa." + save));
        }

        // GFileManager->FileSize (INDEX_NONE when absent) and the +0x18 timestamp (_stat st_mtime, 0 on failure) of MapPath.
        static int MapSize(string mapPath) => UEInstallPath.File(mapPath) is { } f ? (int)new FileInfo(f).Length : -1;
        static long MapTime(string mapPath)
            => UEInstallPath.File(mapPath) is { } f ? new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeSeconds() : 0;

        internal void WritePaFile(string path, PaFile f)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var w = new BinaryWriter(File.Create(path), Encoding.UTF8);
            var noIndex = new Dictionary<UEObject, int>(UERefComparer<UEObject>.Instance);
            w.Write("HPPA"); w.Write(1);
            w.Write(f.Base); w.Write(f.MapSize); w.Write(f.MapTime);
            w.Write(f.Count < 0 ? f.Records.Count : f.Count);
            foreach (PaRecord r in f.Records)
            {
                w.Write(r.Name ?? ""); w.Write(r.Class ?? "");
                WritePaFields(w, r.Fields, noIndex);
            }
            w.Write(f.Eof ?? "");
        }

        void WritePaFields(BinaryWriter w, Dictionary<string, object> fields, Dictionary<UEObject, int> noIndex)
        {
            w.Write(fields.Count);
            foreach (var kv in fields)
            {
                w.Write(kv.Key);
                if (kv.Value is TravelStructDelta d) { w.Write((byte)2); WritePaFields(w, d.Fields, noIndex); }
                else { w.Write((byte)0); WriteValue(w, kv.Value, noIndex, null, kv.Key); }
            }
        }

        Dictionary<string, object> ReadPaFields(BinaryReader r, string where)
        {
            var fields = new Dictionary<string, object>(UEPropNameComparer.Instance);
            for (int n = Count(r, "pa fields"); n > 0; n--)
            {
                string k = r.ReadString();
                switch (r.ReadByte())
                {
                    case 0: fields[k] = ReadValue(r, Array.Empty<UEObject>(), null, where + "." + k); break;
                    case 2: { var d = new TravelStructDelta(); foreach (var kv in ReadPaFields(r, where + "." + k)) d.Fields[kv.Key] = kv.Value; fields[k] = d; break; }
                    default: throw new InvalidDataException($"{where}.{k}: unknown field kind");
                }
            }
            return fields;
        }

        // Original: Engine.dll ?SavePersistentActors@ULevel@@QAE_NABVFString@@@Z [HP2 0x103b9030] (HP1: none)
        // GIsEditor: nothing. The level's Actors in order, non-null with bPersistent (+0xc8 & 1, 0x103b9506); none: no file
        // is written (an older one stays). Else the writer on PAFile, the header (base, FileSize and timestamp of MapPath),
        // the count, per actor its Name, its Class's Name and AActor::SerializePersistence [HP2 0x103811d0] (the
        // persistent properties, Hp2PersistentFields), then "EOF". The result (true on a writer error) has no caller use.
        internal void SavePersistentActors(string mapArg)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Hp2SavePersistentActors(Hp2SlotDir?.Invoke(), Path.GetDirectoryName(LoadedMapPath ?? ""), mapArg, Actors,
                (a, rec) => { foreach (var kv in Hp2PersistentFields(a)) rec.Fields[kv.Key] = kv.Value; });
            SavePersistentActorsTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; SavePersistentActorsCalls++;
        }
        // Wall time spent here (Stopwatch ticks) and calls: read only by the product window's --frame-trace (audit/product_budget.sh).
        public long SavePersistentActorsTicks; public int SavePersistentActorsCalls;

        // Returns the PA file written, or null (no bPersistent actor: nothing is written).
        internal string Hp2SavePersistentActors(string slotDir, string mapsDir, string mapArg, IEnumerable<UEObject> actors,
            Action<UEObject, PaRecord> serialize)
        {
            var (b, mapPath, pa) = Hp2PaPaths(slotDir, mapsDir, mapArg);
            var f = new PaFile { Base = b };
            foreach (UEObject a in actors)
                if (a is not null && !a.Destroyed && UEInterpreter.ToBool(a.GetProp("bPersistent")))
                {
                    var rec = new PaRecord { Name = a.GetProp(PK.Name)?.ToString(), Class = a.Class?.Name?.ToString() };
                    serialize(a, rec);
                    f.Records.Add(rec);
                }
            Log($"save_persistent_actors map={b} file={Path.GetFileName(pa)} actors={f.Records.Count}");
            if (f.Records.Count == 0) return null;
            (f.MapSize, f.MapTime) = (MapSize(mapPath), MapTime(mapPath));
            WritePaFile(pa, f);
            return pa;
        }

        // AActor::SerializePersistence's values in the port: every (property, element) Hp2PersistentProperties selects but
        // object and class properties (UObjectProperty::SerializeItem writes nothing in the PA archives, and the load
        // keeps the level's value); a struct value by its own members under the same flags rule (UStructProperty::
        // SerializeItem [HP2 0x10173fc0] -> UStruct::SerializeBin [HP2 0x10129180]: PropertyFlags & 0x1000, & 0x2000 under
        // ArIsPersistent skipped, no AuxAnims rule).
        internal static IEnumerable<KeyValuePair<string, object>> Hp2PersistentFields(UEObject a)
        {
            foreach (var (name, dim, p) in PersistentPlan(a.Class))
            {
                object v = a.GetProp(name);
                if (dim <= 1) { yield return new(name, PersistentValue(p, v, name)); continue; }
                for (int i = 0; i < dim; i++) yield return new($"{name}[{i}]", PersistentValue(p, ElementAt(v, i), $"{name}[{i}]"));
            }
        }

        // The class's persistent (name, ArrayDim, property) list without object and class properties, cached per class until
        // the package catalogue changes: rebuilt per actor it was half of SAVEPACTORS's cost (right before every HP2 travel).
        sealed class PersistentPlanEntry { public HP1.Core.UEClassRegistry Registry; public int Generation; public (string, int, UProperty)[] Plan; }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UClass, PersistentPlanEntry> _persistentPlans = new();

        static (string Name, int Dim, UProperty Prop)[] PersistentPlan(UClass cls)
        {
            var reg = HP1.Core.UEClassRegistry.Shared;
            if (_persistentPlans.TryGetValue(cls, out var hit) && ReferenceEquals(hit.Registry, reg) && hit.Generation == reg.Generation)
                return hit.Plan;
            var props = new Dictionary<string, UProperty>(UEPropNameComparer.Instance);
            for (UClass c = cls; c is not null; c = UEInterpreter.CanonicalSuper(c))
                foreach (UProperty p in c.EnumerateFields<UProperty>())
                    if (p is not null) props.TryAdd(p.Name.ToString(), p);
            var plan = new List<(string, int, UProperty)>();
            foreach (var (name, dim) in Hp2PersistentProperties(cls))
                if (props[name] is not UObjectProperty) plan.Add((name, dim, props[name]));
            var arr = plan.ToArray();
            _persistentPlans.AddOrUpdate(cls, new PersistentPlanEntry { Registry = reg, Generation = reg.Generation, Plan = arr });
            return arr;
        }

        static object PersistentValue(UProperty p, object v, string where)
        {
            // UArrayProperty::SerializeItem [HP2 0x10173350]: the count, then Inner->SerializeItem per element; a load empties
            // the array and re-allocates count elements WITHOUT zeroing (0x10173388..0x101733c1) before the elements read:
            // the read list replaces the actor's. An object element reads nothing (its slot keeps the allocation's garbage)
            // and a struct element only its serialized members — not ported, stop.
            if (p is UArrayProperty ap)
                return ap.InnerProperty is UObjectProperty or UStructProperty
                    ? throw new NotSupportedException($"persistent actors: {where} is an array<> of {ap.InnerProperty.GetType().Name} (its load leaves unwritten memory)")
                    : v;
            if (p is not UStructProperty sp) return v;
            v ??= UEObject.ZeroStructValue(p);   // an absent struct value is the zero struct (the original writes its bytes)
            UStruct st = TravelStruct(sp, where);
            var d = new TravelStructDelta();
            for (UStruct s = st; s is not null; s = s.Super as UStruct)
                foreach (UProperty f in s.EnumerateFields<UProperty>())
                {
                    if (f is null || d.Fields.ContainsKey(f.Name.ToString())) continue;
                    ulong flags = Convert.ToUInt64(f.PropertyFlags);
                    if ((flags & 0x1000) != 0 || (flags & 0x2000) != 0 || f is UObjectProperty) continue;
                    object fv = Member(v, f.Name);
                    int dim = Math.Max(1, f.ArrayDim);
                    if (dim <= 1) { d.Fields[f.Name.ToString()] = PersistentValue(f, fv, $"{where}.{f.Name}"); continue; }
                    for (int i = 0; i < dim; i++) d.Fields[$"{f.Name}[{i}]"] = PersistentValue(f, ElementAt(fv, i), $"{where}.{f.Name}[{i}]");
                }
            return d;
        }

        // The values of a record into its actor (the load's SerializePersistence): a plain value is written, a struct's
        // listed members into the actor's struct (ApplyStructDelta), an element "Name[i]" into the static array.
        void ApplyPersistentFields(UEObject a, PaRecord rec)
        {
            foreach (var kv in rec.Fields)
            {
                int br = kv.Key.IndexOf('[');
                string name = br > 0 ? kv.Key.Substring(0, br) : kv.Key;
                UProperty p = PropertyOf(a.Class, name);
                if (br > 0)
                {
                    int idx = int.Parse(kv.Key.Substring(br + 1, kv.Key.Length - br - 2), System.Globalization.CultureInfo.InvariantCulture);
                    if (a.GetProp(name) is not System.Collections.IList arr || idx >= arr.Count)
                        throw new InvalidDataException($"persistent actors: {a.GetProp(PK.Name)}.{name} — no static array for {kv.Key}");
                    arr[idx] = kv.Value is TravelStructDelta ed ? ApplyStructDelta(arr[idx], ed, StructOf(p, kv.Key), kv.Key, out _) : kv.Value;
                    continue;
                }
                // An absent struct value is the zero struct (ApplyStructDelta; the port keeps Props sparse, the original's memory is there).
                a.Props[name] = kv.Value is TravelStructDelta sd ? ApplyStructDelta(a.GetProp(name), sd, StructOf(p, name), name, out _) : kv.Value;
            }
        }

        static UProperty PropertyOf(UClass cls, string name)
        {
            for (UClass c = cls; c is not null; c = UEInterpreter.CanonicalSuper(c))
                foreach (UProperty p in c.EnumerateFields<UProperty>())
                    if (p is not null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        // Original: Engine.dll ?LoadPersistentActors@ULevel@@QAE_NABVFString@@@Z [HP2 0x103b9e40] (HP1: none)
        // HP2 LoadMap calls it for every level but Entry (0x103a62c6..0x103a62f0, result unused), mapArg = URL.Map.
        // The reader on PAFile; none, an error or TotalSize 0: "could not load" and nothing (0x103baab3). The level's
        // bPersistent actors by name (TMap<FName,AActor*>, level order); none: nothing more. Header: the 8-byte ident is not
        // checked; the base name appStricmp (0x103ba4c3), the map's FileSize (0x103ba520) and timestamp (0x103ba556) must
        // equal, else nothing. Per record (name, class, size): the actor of that FName (case-insensitive); none -> the
        // class by StaticFindObject(NULL, ANY_PACKAGE, class) (0x103ba6e7): none -> the record is skipped (Seek past,
        // 0x103ba775); else SpawnActor(class, FName(name), bNoCollisionFail) (0x103ba753), NULL -> stop. A found actor
        // whose Name differs by appStrcmp (case) -> stop (0x103ba794). The name leaves the map (0x103ba7d9), then
        // SerializePersistence and bScriptInitialized cleared (+0x16c & ~0x20000, 0x103ba82d). A stop or a read error
        // goes to the "EOF" read (0x103ba931) at that point of the file, which then never reads "EOF" (an error, or a
        // property name of the payload): nothing more. After the records "EOF" (appStrcmp) or nothing more; then every
        // actor left in the map is destroyed (ULevel::DestroyActor(A, 0), 0x103ba9ad), in level order.
        internal void Hp2LoadPersistentActors(string slotDir, string mapsDir, string mapArg, IReadOnlyList<UEObject> actors,
            Func<string, UClass> findClass, Func<UClass, string, UEObject> spawn, Action<UEObject, PaRecord> apply,
            Action<UEObject> destroy, Action<string> log)
        {
            var (b, mapPath, paFile) = Hp2PaPaths(slotDir, mapsDir, mapArg);
            string file = UEInstallPath.File(paFile);
            if (file is null || new FileInfo(file).Length == 0) { log($"load_persistent_actors file={Path.GetFileName(paFile)} missing"); return; }
            var map = new List<UEObject>();
            foreach (UEObject a in actors)
                if (a is not null && !a.Destroyed && UEInterpreter.ToBool(a.GetProp("bPersistent"))) map.Add(a);
            if (map.Count == 0) { log($"load_persistent_actors file={Path.GetFileName(paFile)} no_persistent_actors"); return; }
            using var r = new BinaryReader(File.OpenRead(file), Encoding.UTF8);
            try
            {
                r.ReadString(); r.ReadInt32();
                string fb = r.ReadString();
                string bad = !string.Equals(fb, b, StringComparison.OrdinalIgnoreCase) ? "base_mismatch"
                    : r.ReadInt32() != MapSize(mapPath) ? "size_mismatch" : r.ReadInt64() != MapTime(mapPath) ? "time_mismatch" : null;
                if (bad is not null) { log($"load_persistent_actors file={Path.GetFileName(paFile)} {bad}"); return; }
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    var rec = new PaRecord { Name = r.ReadString(), Class = r.ReadString() };
                    UEObject a = map.Find(x => string.Equals(x.GetProp(PK.Name)?.ToString(), rec.Name, StringComparison.OrdinalIgnoreCase));
                    if (a is null)
                    {
                        UClass c = findClass(rec.Class);
                        if (c is null) { ReadPaFields(r, rec.Name); log($"load_persistent_actors skip name={rec.Name} class={rec.Class}"); continue; }
                        a = spawn(c, rec.Name);
                        if (a is null) { log($"load_persistent_actors spawn_failed name={rec.Name} class={rec.Class}"); return; }
                    }
                    else if (!string.Equals(a.GetProp(PK.Name)?.ToString(), rec.Name, StringComparison.Ordinal))
                    { log($"load_persistent_actors name_mismatch level={a.GetProp(PK.Name)} file={rec.Name}"); return; }
                    map.Remove(a);
                    foreach (var kv in ReadPaFields(r, rec.Name)) rec.Fields[kv.Key] = kv.Value;
                    apply(a, rec);
                    a.Props["bScriptInitialized"] = false;
                }
                if (r.ReadString() != "EOF") { log($"load_persistent_actors file={Path.GetFileName(paFile)} corrupt"); return; }
            }
            catch (EndOfStreamException) { log($"load_persistent_actors file={Path.GetFileName(paFile)} read_error"); return; }
            foreach (UEObject a in map) destroy(a);
            log($"load_persistent_actors file={Path.GetFileName(paFile)} loaded destroyed={map.Count}");
        }

        // ── The cache's snapshot in the slot directory (Core). <slot>\cache holds the live per-session PA files (SAVEPACTORS
        // writes, every map load reads); <slot>\<Map>_pa.usa is the snapshot that belongs to a save. FindFiles is
        // FindFirstFileW's (the port: the directory's files whose name matches, case-insensitively, in ordinal order).
        static IEnumerable<string> FindFiles(string dir, Func<string, bool> match)
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            var names = new List<string>();
            foreach (string f in Directory.GetFiles(dir)) if (match(Path.GetFileName(f))) names.Add(Path.GetFileName(f));
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        static bool PaName(string n) => n.IndexOf("_pa.", StringComparison.OrdinalIgnoreCase) >= 0;   // "*_pa.*"
        static long SizeOf(string p) => File.Exists(p) ? new FileInfo(p).Length : -1;

        // Original: Core.dll ?SavePersistentActorCache@@YAXXZ [HP2 0x10157790] (HP1: none; UGameEngine::SaveGame 0x103ab90d)
        // Nothing without a slot directory (GSys+0x40 empty). Every <slot>\*_pa.* with FileSize > 0 is deleted
        // (Delete(p, 0, 1)), then every <slot>\cache\*_pa.* with FileSize > 0 is copied over <slot>\<name>
        // (Copy(dest, src, Replace 1, EvenReadOnly 1)).
        internal static void SavePersistentActorCache(string slotDir, Action<string, string, string> trace = null)
        {
            if (string.IsNullOrEmpty(slotDir)) return;
            foreach (string f in FindFiles(slotDir, PaName))
            {
                string p = Path.Combine(slotDir, f);
                if (SizeOf(p) > 0) { trace?.Invoke("delete", p, null); File.Delete(p); }
            }
            string cache = Path.Combine(slotDir, "cache");
            foreach (string f in FindFiles(cache, PaName))
            {
                string src = Path.Combine(cache, f);
                if (SizeOf(src) > 0) { trace?.Invoke("copy", Path.Combine(slotDir, f), src); File.Copy(src, Path.Combine(slotDir, f), true); }
            }
        }

        // Original: Core.dll ?LoadPersistentActorCache@@YAXXZ [HP2 0x10157e20] (HP1: none; Exec LOADGAME 0x103a29e2)
        // Nothing without a slot directory. ClearCache, then every <slot>\*_pa.* with FileSize > 0 copied to
        // <slot>\cache\<name> (Replace 1, EvenReadOnly 1).
        internal static void LoadPersistentActorCache(string slotDir, Action<string, string, string> trace = null)
        {
            if (string.IsNullOrEmpty(slotDir)) return;
            ClearPersistentActorCache(slotDir, trace);
            string cache = Path.Combine(slotDir, "cache");
            foreach (string f in FindFiles(slotDir, PaName))
            {
                string src = Path.Combine(slotDir, f);
                if (SizeOf(src) > 0) { trace?.Invoke("copy", Path.Combine(cache, f), src); Directory.CreateDirectory(cache); File.Copy(src, Path.Combine(cache, f), true); }
            }
        }

        // Original: Core.dll not exported: FUN_10157b90 [HP2 0x10157b90] (ClearCache; thunk 0x1010369d; its callers:
        // LoadPersistentActorCache and appPreExit 0x10158108) — every <slot>\cache\*.* with FileSize > 0 deleted (Delete(p, 0, 1)).
        internal static void ClearPersistentActorCache(string slotDir, Action<string, string, string> trace = null)
        {
            if (string.IsNullOrEmpty(slotDir)) return;
            string cache = Path.Combine(slotDir, "cache");
            foreach (string f in FindFiles(cache, _ => true))
            {
                string p = Path.Combine(cache, f);
                if (SizeOf(p) > 0) { trace?.Invoke("delete", p, null); File.Delete(p); }
            }
        }

        // The LoadMap call: URL.Map of this load (_loadUrlMap), this level's actors, the port's SpawnActor and DestroyActor.
        internal void LoadPersistentActors(UEInterpreter interp)
        {
            if (UEGameProfile.Current.Game != UEEdition.Game.HP2 || IsEntryLevel) return;
            if (Hp2SlotDir?.Invoke() is null) { Log("load_persistent_actors no port save directory (nothing cached)"); return; }
            Hp2LoadPersistentActors(Hp2SlotDir?.Invoke(), Path.GetDirectoryName(LoadedMapPath ?? ""), _loadUrlMap, Actors.ToArray(),
                name => UEClassRegistry.Shared.Find(name),
                (c, name) => SpawnActor(interp, null, c, null, null, default, default, noCollisionFail: true, name: name),
                ApplyPersistentFields, a => DestroyActor(interp, a), Log);
        }
    }
}
