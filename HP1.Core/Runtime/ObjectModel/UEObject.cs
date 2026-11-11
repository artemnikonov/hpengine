// The VM object model, extracted from the Unity frontend (plan step 2 slice
// "UEObject core"): identical semantics, zero Unity dependencies. The single
// class seam is `Interp` — the owning interpreter (typed since slice 4c).
using System;
using System.Collections.Generic;
using UELib.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    // Script-level UObject: the VM's object model. Props holds both class
    // defaultproperties and runtime assignments — slots of the class layout
    // (UEProps.cs), the counterpart of UObject memory addressed by UProperty.Offset
    // (execInstanceVariable: GPropAddr = this + Offset, Core.dll.c:61661).
    // Frontend state (the
    // Unity GameObject/host/physics binding) lives OUTSIDE this type — see the
    // frontend's UEObjectUnityExt side-table.
    public class UEObject
    {
        // Stable identity for the frontend proxy (stage 8): actor names are not
        // unique enough to key a proxy's lifecycle (spawns reuse names, structs
        // have none), so every UEObject gets a process-monotonic id at
        // construction. Deterministic because object creation order is
        // deterministic; NOT rendered into the SHA-pinned digest.
        static int _nextObjectId;
        public readonly int ObjectId = System.Threading.Interlocked.Increment(ref _nextObjectId);

        // The class decides the property slot layout; a class change moves values over by key
        // (UEProps.Rebind).
        UClass _class;
        public UClass Class
        {
            get => _class;
            set { _class = value; Props.Rebind(UEPropLayout.ForClass(value)); }
        }
        // Path of the outer (UObject::Outer, +0x18) when the outer is NOT a VM object but a
        // package: the map package for level actors (.unr export with outer = 0;
        // ULevel::SpawnActor uses the level's outer, Engine.dll 0x103a6863), "Transient" for
        // `new` without Outer (UObject::execNew, Core.dll.c:25129). Read only by
        // UObject::GetPathName (UEInterpreter.ObjectPathName); a VM outer object lives in
        // Props["Outer"] and is visible to script.
        public string OuterPath;
        public readonly UEProps Props;
        public UEObject() { Props = new UEProps(UEPropLayout.Loose); }
        // Struct bag with a known layout (UEPropLayout.ForStruct/ForStructName).
        public UEObject(UEPropLayout layout) { Props = new UEProps(layout); }
        // The driving interpreter. Typed object here: the interpreter class
        // still compiles in the frontend assembly; frontends read it back via
        // a typed extension (UEObjectUnityExt.Vm / the headless Vm shim).
        public UEInterpreter Interp;
        // Frontend fallback for objects materialised during default loading
        // (gesture bags); set once at runtime bootstrap alongside
        // UEClassRegistry.Shared.
        public static UEInterpreter DefaultInterp;
        public string CurrentStateName;
        // Set by native Destroy() (bDeleteMe). Shipped does NOT null references to a corpse
        // right away: execContext (Core.dll.c:47177) checks only NULL, so member access and
        // calls on a corpse DO execute (ContextToken likewise); object properties of live
        // actors pointing to a corpse are cleared by ULevel::CleanupDestroyed
        // (UELevelRuntime.SaveGame.cs, CleanupDestroyed). A corpse is not ticked. We also null
        // UnityActor on destroy so C# `?.` (which ignores Unity's fake-null ==)
        // can't deref a destroyed GameObject and throw (frontend nulls its
        // side-table binding on destroy).
        public bool Destroyed;
        // Engine timer (Actor.SetTimer/Timer). TimerRate>0 means active: TimerCounter
        // counts down each tick; at 0 the Timer() event fires and, if TimerLoop,
        // the counter reloads (else the timer stops). Driven by UEScriptedActor.Update;
        // bare objects (e.g. the bootstrap GameInfo) hold the timer but aren't ticked.
        public float TimerRate;
        public float TimerCounter;
        // WorldFrameProjector's per-actor projection memo (render scheduling only, never read by game code).
        internal object FrameProjMemo;
        internal int FrameProjStamp;   // WorldFrameProjector.ProjectWorld: projected in the walk with this stamp
        public bool  TimerLoop;
        // Event disabling on an object has ONE store, as in shipped: a bit of
        // FStateFrame.ProbeMask (UEFrame.ProbeMask in the port; natives 117/118,
        // execDisable Core.dll.c:31010, execEnable :54226, check UObject::IsProbing :40247).
        // A second store is locked out by verify_architecture.py ("dual store").
        // RF 0x1000 (state changed) and RF 0x2000 (EndState in progress) are object flags
        // UObject::GotoState uses to detect preemption
        // (Core.dll.c:58312-58383).
        internal bool StateChangedFlag, InEndState;

        // ── Engine touch lists (AActor::BeginTouch/TouchTo/EndTouch, UELifecycle) ──
        // UE1 Actor.Touching is an engine-maintained 4-slot static array kept on
        // BOTH overlapping actors (script readers: Trigger.CheckTouchList/Timer/
        // SpecialHandling, Engine Inventory/Teleporter, HPBase JumpLineTrigger).
        // Stored in Props so bytecode `Touching[i]` reads it via ArrayElementToken.
        // TouchTo (Engine.dll.c:225632, FUN_1037a0a0): 4 Touching slots (+0xd4).
        public const int TouchingArraySize = 4;                 // TouchTo FUN_1037a0a0: cmp 4 at 0x1037a12c over +0xd4
        public UEObject[] TouchList()
        {
            if (Props.TryGetValue("Touching", out var v) && v is UEObject[] arr) return arr;
            var a = new UEObject[TouchingArraySize];
            Props["Touching"] = a;
            return a;
        }
        // The engine-side touch link/unlink semantics live in UELifecycle
        // (shipped BeginTouch/TouchTo/EndTouch port) — the former SE-model
        // TouchListAdd/TouchListRemove pair is gone: shipped links the sides
        // SEQUENTIALLY with per-side events interleaved, which cannot be
        // expressed as a bookkeeping-only helper.
        // Persistent, suspendable state-body frame — FStateFrame (Core.dll.c:52914:
        // Node, Object, Code, StateNode, ProbeMask, LatentAction).
        // Set by EnterState; driven each tick by UEInterpreter.TickStateFrame
        // so latent natives (Sleep/MoveToward/...) can pause across frames.
        public UEFrame StateFrame;
        // RF_ bit 0x20 in ObjectFlags (+0x1c), set by UObject::ProcessInternal for the duration
        // of a singular function body (Core 0x101431ec..0x101431ff, cleared at
        // 0x10143297..0x101432a4): re-entering any singular function of this object while the
        // bit is set does not execute the body.
        public bool InSingularFunction;

        // Bridge methods — C# handlers for objects whose behaviour is genuinely
        // engine-side in this Unity port (for example PlayerHarry transform/input
        // bridge hooks). When a context-call resolves to no UFunction/native, the
        // VirtualFunctionToken handler consults this map.
        // Null for ordinary VM objects.
        public System.Collections.Generic.Dictionary<string, System.Func<object[], object>> BridgeMethods;

        // Value-struct marker. UE1 structs are value types: `X = Y` copies, member
        // writes `Y.A = 1` mutate in place. We model struct values as UEObject bags;
        // IsStruct flags them so the assignment path deep-copies (so the HUD's
        // `OldColor = DrawColor; DrawColor.R = 0; DrawColor = OldColor` restores).
        public bool IsStruct;
        public UEObject CloneStruct()
        {
            var c = new UEObject(Props.Layout) { Interp = Interp, IsStruct = true };
            c._class = Class;
            c.Props.CopyFrom(Props, CloneMember);
            return c;
        }

        // Members are values: structs and arrays (static array, array<>) are copied recursively (UEValue.CopyValue).
        static readonly Func<object, object> CloneMember = UEValue.CopyValue;

        public object GetProp(string n) => Props.Get(n);
        public void   SetProp(string n, object v) => Props[n] = v;
        // By key id (UEPropKeys.Id), for hot paths that keep the id in a static.
        public object GetProp(int keyId) => Props.Get(keyId);
        public void   SetProp(int keyId, object v) => Props.Set(keyId, v);

        // Declaring class of the property (UProperty.Outer; for a property from another package
        // this is the import stub, same name).
        public static string PropDeclClassName(UObject prop)
            => (prop?.Outer as UClass)?.Name ?? prop?.Outer?.Name?.ToString();

        // ── SHADOWED PROPERTIES: ONE NAME, TWO SLOTS ────────────────────────
        //
        // UnrealScript lets a subclass declare a property with an ancestor's property name
        // (Mover.AttachTag over Actor.AttachTag, baseSpell.Target over Actor.Target). In
        // shipped these are DIFFERENT object fields: a live probe of the original
        // (tools/origprobe/hp1setbase.c, Lev2_HogFront) showed movers with the map's AttachTag
        // at +0x2f8 (Mover.AttachTag) and None at +0xac (Actor.AttachTag), and the AttachTag
        // pass in LoadMap (disasm 0x1039e00c, reads +0xac) does not touch them. A by-name
        // dictionary merged both slots into one.
        //
        // Key: the slot of the MOST DERIVED declaration gets the simple name (that is how UE1
        // resolves map tags and defaultproperties from the object's class, and how all readers
        // unaware of the declaration see it); a shadowed slot gets "Class.Name". Code that in
        // shipped needs a specific level (a native reading an engine field by offset) asks
        // PropKey explicitly. The cache snapshot is read WITHOUT a lock: a published dictionary
        // never changes, a new class publishes a copy (hundreds of classes, one miss per class).
        static Dictionary<UClass, Dictionary<string, string>> _shadowByClass = new(UERefComparer<UClass>.Instance);
        static readonly object _shadowLock = new();

        // Name -> most derived declaring class, only for names declared more than once in the
        // chain; null if nothing is shadowed.
        internal static Dictionary<string, string> ShadowMap(UClass cls)
        {
            if (cls is null) return null;
            if (System.Threading.Volatile.Read(ref _shadowByClass).TryGetValue(cls, out var hit)) return hit;
            lock (_shadowLock)
            {
                if (_shadowByClass.TryGetValue(cls, out var cached)) return cached;
                var mostDerived = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> shadowed = null;
                for (var c = cls; c != null; c = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(c))
                    foreach (var p in c.EnumerateFields<UProperty>())
                    {
                        string n = p?.Name?.ToString();
                        if (n is null) continue;
                        if (mostDerived.TryGetValue(n, out string md))
                        {
                            if (!string.Equals(md, c.Name, StringComparison.OrdinalIgnoreCase))
                                (shadowed ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[n] = md;
                        }
                        else mostDerived[n] = c.Name;
                    }
                var snap = new Dictionary<UClass, Dictionary<string, string>>(_shadowByClass, UERefComparer<UClass>.Instance);
                snap[cls] = shadowed;
                System.Threading.Volatile.Write(ref _shadowByClass, snap);
                return shadowed;
            }
        }

        // Slot key of property `name` declared in class `declClass`, for an object of class
        // `objClass`.
        public static string PropKey(UClass objClass, string declClass, string name)
            => ShadowKey(ShadowMap(objClass), declClass, name);

        internal static string ShadowKey(Dictionary<string, string> m, string declClass, string name)
        {
            if (m is null || name is null || !m.TryGetValue(name, out string md)) return name;
            return declClass is null || string.Equals(declClass, md, StringComparison.OrdinalIgnoreCase)
                ? name : declClass + "." + name;
        }

        // Field declared in class declClass (engine code reading a field at the offset of a
        // specific C++ class, e.g. Pawn.MoveTarget).
        public object GetDeclared(string declClass, string name) => GetProp(ShadowKey(Props.Layout.Shadow, declClass, name));
        public void SetDeclared(string declClass, string name, object v) => Props[ShadowKey(Props.Layout.Shadow, declClass, name)] = v;

        // Class whose declaration of `name` is visible from `cls` (first up the chain).
        public static string DeclaringClass(UClass cls, string name)
        {
            for (var c = cls; c != null; c = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(c))
                foreach (var p in c.EnumerateFields<UProperty>())
                    if (string.Equals(p?.Name?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                        return c.Name;
            return null;
        }

        // The ONE Region constructor (review #20): every frontend builds the
        // PointRegion struct in this Core shape — a value-struct UEObject with
        // Zone/iLeaf/ZoneNumber — so WorldFrame projection and the particle
        // zone-gravity read one form everywhere (the shipped FPointRegion:
        // ZoneInfo* Zone, INT iLeaf, BYTE ZoneNumber).
        static readonly UEPropLayout RegionLayout = UEPropLayout.ForStructName("PointRegion");
        public static UEObject MakeRegion(UEObject zone, int iLeaf, byte zoneNumber)
        {
            var r = new UEObject(RegionLayout) { IsStruct = true };
            r.Props["Zone"] = zone;
            r.Props["iLeaf"] = iLeaf;
            r.Props["ZoneNumber"] = zoneNumber;
            return r;
        }

        // Populate Props from class defaultproperties. Walks parent chain so
        // inherited defaults are picked up; child entries shadow parents.
        // UELib parses defaultproperties into UClass.Properties (typed) — we
        // translate each into a runtime object via ParseDefaultValue.
        public void LoadDefaults()
        {
            // The port's class load order (sprite sort key, UESpriteSort): the first materialised defaults of a class.
            HP1.Core.UEClassRegistry.Shared?.NoteClassLoaded(Class);
            var chain = new List<UClass>();
            // Walk via CanonicalSuper so defaultproperties from base classes in
            // other packages (the real exports, not import stubs) are picked up.
            for (var c = Class; c != null; c = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(c)) chain.Add(c);
            chain.Reverse();
            // UE1 static arrays (`var T X[N]`) are fixed-size and fully zero-
            // initialized at load. Pre-allocate them to ArrayDim BEFORE applying
            // defaultproperties (which then overlay specific indices). Without
            // this, an array that has no defaultproperty (e.g. CutScene.OldCues,
            // a runtime scratch buffer) stays null, so element assignment
            // `OldCues[i] = x` finds a null backing list and silently drops the
            // write (WriteLValue ArrayIndex requires a non-null IList).
            //
            // STRUCT props get the same treatment: UE1 zero-fills the default
            // object's memory, so every declared struct EXISTS (zeroed) even
            // with no defaultproperty tag. Materialising them as zero bags lets
            // `Obj.SomeStruct.Member = x` land (the no-tag case previously read
            // a null parent and dropped the write — e.g. ParticleFX.Period,
            // written by SpellLearnFX.DrawSpell but tagged in no class).
            // Structs are NOT object refs, so this cannot flip `!= None` paths
            // (the documented reason the blanket SCALAR zero-init was reverted).
            foreach (var cls in chain) PreallocDeclaredProps(cls);
            // CONFIG AND LOCALIZATION: THE SAME LAYER AS IN SHIPPED. UClass::Serialize
            // (Core.dll.c:4230-4240) builds class defaults as "ancestor defaults + own package
            // tags" and IMMEDIATELY calls LoadConfig(Defaults, 0): section = this class
            // (GlobalConfig: the owning class), file = this class's ConfigName. A subclass copies
            // the ancestor's already loaded defaults, so the step "class tags, then its
            // LoadConfig" repeats down the chain from the root. The process GConfig is brought up
            // by UELevelRuntime.AppInit before packages load; without it (bare fixture rig) there
            // is no ini at all.
            var gconfig = UEConfig.Global;
            foreach (var cls in chain)
            {
                ApplyClassDefaults(cls);
                if (gconfig is not null)
                    UEObjectConfig.LoadConfig(gconfig, this, cls, cls, propagate: false, isInstance: false);
                // Then LoadLocalized(Defaults, 0) (Core.dll.c:4246): section = class name,
                // file = <class package>.<language>.
                UEObjectConfig.LoadLocalized(this, cls, propagate: false, isInstance: false);
            }
            // A hierarchy already re-read by the SaveConfig tail (LoadConfig(1) over the defaults
            // of all its classes) is carried by the default object in its re-read form.
            if (gconfig is not null && Class is not null && UEObjectConfig.WasReloaded(Class))
                UEObjectConfig.LoadConfig(gconfig, this, Class, Class, propagate: true, isInstance: false);
            // NOTE: scalar props absent from BOTH the JSON and the defaultproperties block
            // stay ABSENT (GetProp → null); the VM coerces null→zero on read (ToInt/ToBool/
            // AsFloat), matching UE1's type-zero default. A blanket zero-init of every
            // declared scalar was tried + REVERTED: it activated dormant code paths (e.g.
            // PotCam camera-collision → unimplemented natives 277/309/533) that we can't yet
            // honour. So the generic loader relies on null-coercion for absent defaults.
            //
            // ── THE CLASS C++ CONSTRUCTOR RUNS AFTER THE DEFAULTS COPY ───────
            // Order proven from the binary: StaticConstructObject = StaticAllocateObject (copy
            // of the default block) -> constructor call via UClass+0x4e8 on EVERY instance
            // (Core.dll.c:14176-14179). The AParticleFX constructor (Engine.dll.c:161064-161069)
            // overwrites the defaults: particle container, Age=0, ElapsedTime=0, bUpdate|=1,
            // LOD=1. Serialisation of a PLACED actor comes after and restores authored values; a
            // spawned one keeps the zeroes.
            //
            // Without this, defaults left over from the artist's editor session (WizCard_Explo:
            // Age=643 against a budget of ParticlesMax/pps+Lifetime ~2 s) killed a spawned
            // emitter on its FIRST tick before a single particle (the card explosion and flash
            // never drew).
            if (HP1.Core.UEClassRegistry.Shared is { } reg
                && Class is not null && reg.ClassIsA(Class, "ParticleFX"))
            {
                Props["Age"] = 0f;
                Props["ElapsedTime"] = 0f;
                Props["bUpdate"] = true;
                Props["LOD"] = 1f;
            }
        }

        void PreallocDeclaredProps(UClass cls)
        {
            foreach (var prop in cls.EnumerateFields<UELib.Core.UProperty>())
            {
                if (prop?.Name == null) continue;
                string name = PropKey(Class, cls.Name, prop.Name.ToString());
                if (prop.ArrayDim > 1)
                {
                    if (Props.TryGetValue(name, out var ex) && ex is System.Collections.IList) continue;
                    Props[name] = ZeroStaticArray(prop);
                }
                else if (prop.Type == UELib.Types.PropertyType.StructProperty)
                {
                    if (Props.ContainsKey(name)) continue;
                    Props[name] = ZeroStructValue(prop);
                }
            }
        }

        // UE1 zero-fill for a declared struct property: Vector/Rotator are the
        // VM's value-struct intrinsics; every other struct becomes an empty
        // IsStruct bag (member reads null-coerce to type zero, member writes
        // land on the bag — both matching a zero-filled UE1 struct).
        static object ZeroStructValue(UELib.Core.UProperty prop)
        {
            string sn = (prop as UELib.Core.UStructProperty)?.Struct?.Name?.ToString();
            if (string.Equals(sn, "Vector", StringComparison.OrdinalIgnoreCase)) return Vector3.zero;
            if (string.Equals(sn, "Rotator", StringComparison.OrdinalIgnoreCase)) return new UERotator(0, 0, 0);
            if (string.Equals(sn, "Plane", StringComparison.OrdinalIgnoreCase)) return new UEPlane(Vector3.zero, 0f);
            var st = (prop as UELib.Core.UStructProperty)?.Struct;
            var bag = new UEObject(UEPropLayout.ForStruct(st)) { IsStruct = true };
            PreallocStructMembers(bag, st);
            return bag;
        }

        // STATIC ARRAY: ArrayDim elements, and in an array of STRUCTS every element is ITS OWN
        // zeroed struct. UE1 holds the whole property memory (ElementSize x ArrayDim,
        // UStructProperty), so `Arr[i].Member = x` and `Arr[i].Inner[k] = x` are always
        // addressable. With null elements a nested array read empty and the write was lost:
        // "Accessed None in FESavePage.Created at pos 631"
        // (FEFilePage.uc:71-72, SaveFileDetails[xx].Details[k]).
        internal static List<object> ZeroStaticArray(UELib.Core.UProperty prop)
        {
            var list = new List<object>(prop.ArrayDim);
            bool isStruct = prop.Type == UELib.Types.PropertyType.StructProperty;
            object zero = isStruct ? null : ZeroValueForType(prop.Type);
            for (int k = 0; k < prop.ArrayDim; k++) list.Add(isStruct ? ZeroStructValue(prop) : zero);
            return list;
        }

        // Struct members that have SHAPE MEMORY: nested static arrays and nested structs
        // (recursively, with struct inheritance). Scalars are deliberately left alone, the same
        // policy as for objects: reading an absent scalar is coerced to the type's zero on the
        // VM side.
        static void PreallocStructMembers(UEObject bag, UELib.Core.UStruct st)
        {
            for (UELib.Core.UStruct s = st; s != null; s = s.Super as UELib.Core.UStruct)
                foreach (var m in s.EnumerateFields<UELib.Core.UProperty>())
                {
                    if (m?.Name == null) continue;
                    string mn = m.Name.ToString();
                    if (bag.Props.ContainsKey(mn)) continue;
                    if (m.ArrayDim > 1) bag.Props[mn] = ZeroStaticArray(m);
                    else if (m.Type == UELib.Types.PropertyType.StructProperty) bag.Props[mn] = ZeroStructValue(m);
                }
        }

        static object ZeroValueForType(UELib.Types.PropertyType t)
        {
            switch (t)
            {
                case UELib.Types.PropertyType.BoolProperty:  return false;
                case UELib.Types.PropertyType.IntProperty:   return 0;
                case UELib.Types.PropertyType.ByteProperty:  return (byte)0;
                case UELib.Types.PropertyType.FloatProperty: return 0f;
                case UELib.Types.PropertyType.StrProperty:   return "";
                case UELib.Types.PropertyType.NameProperty:  return new UEName("None");
                default:                                     return null; // object/class/struct
            }
        }

        void ApplyClassDefaults(UClass cls)
        {
            if (cls?.Properties == null) return;
            foreach (var dp in cls.Properties)
            {
                if (dp == null || dp.Name == null) continue;
                // A class cls defaultproperties tag belongs to the declaration visible from cls;
                // a shadowed declaration has its own slot.
                string name = PropKey(Class, DeclaringClass(cls, dp.Name.ToString()), dp.Name.ToString());
                object v = ParseDefaultValue(cls, dp);
                // Struct defaults decode to a shared prototype bag (cached per
                // tag) — deep-copy per instance, else one instance's member
                // write (`Sparkle.Lifetime.Base = x`) would mutate the class
                // default for every other instance.
                if (v is UEObject sproto && sproto.IsStruct) v = sproto.CloneStruct();
                int idx = dp.ArrayIndex < 0 ? 0 : dp.ArrayIndex;
                if (Props.TryGetValue(name, out var existing) && existing is System.Collections.IList list) {
                    while (list.Count <= idx) list.Add(null);
                    list[idx] = v;
                } else if (existing != null && idx > 0) {
                    var newList = new List<object> { existing };
                    while (newList.Count <= idx) newList.Add(null);
                    newList[idx] = v;
                    Props[name] = newList;
                } else if (idx > 0) {
                    var newList = new List<object>();
                    while (newList.Count <= idx) newList.Add(null);
                    newList[idx] = v;
                    Props[name] = newList;
                } else {
                    Props[name] = v;
                }
            }
        }

        // The class-default-object read shipped SpawnActor performs BEFORE any
        // actor exists (Engine.dll.c:20085-20113 reads the template's
        // collision gate/extent): resolve one SCALAR defaultproperty by
        // walking the canonical super chain most-derived-first (within a
        // class the LAST tag wins, matching ApplyClassDefaults' overwrite
        // order). Deliberately scalar-only — struct/object defaults would
        // materialize prototype bags (observable ObjectId consumption), and
        // the spawn gate needs none of them. Absent → null (the VM's
        // null-coerce zero, same as an instance without the tag).
        // Same for a VECTOR default (struct Vector). A vector is a UEVector value: no bag is
        // created and no ObjectId is spent (the prototype cache is per-tag, as in
        // DecodeStructDefault). Needed by APawn::moveToward
        // (Engine.dll.c:139018): Zone->Class->Defaults ZoneGravity.
        public static object ClassDefaultVector(UClass cls, string name)
        {
            for (var c = cls; c != null; c = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(c))
            {
                if (c.Properties == null) continue;
                UELib.Core.UDefaultProperty found = null;
                foreach (var dp in c.Properties)
                {
                    if (dp?.Name == null || dp.ArrayIndex > 0) continue;
                    if (!string.Equals(dp.Name.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (dp.Type == UELib.Types.PropertyType.StructProperty) found = dp;
                }
                if (found != null) return ParseDefaultValue(c, found);
            }
            return null;
        }

        public static object ClassDefaultScalar(UClass cls, string name)
        {
            for (var c = cls; c != null; c = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(c))
            {
                if (c.Properties == null) continue;
                UELib.Core.UDefaultProperty found = null;
                foreach (var dp in c.Properties)
                {
                    if (dp?.Name == null || dp.ArrayIndex > 0) continue;
                    if (!string.Equals(dp.Name.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                    switch (dp.Type)
                    {
                        case UELib.Types.PropertyType.BoolProperty:
                        case UELib.Types.PropertyType.IntProperty:
                        case UELib.Types.PropertyType.ByteProperty:
                        case UELib.Types.PropertyType.FloatProperty:
                        case UELib.Types.PropertyType.StrProperty:
                        case UELib.Types.PropertyType.NameProperty:
                            found = dp;
                            break;
                    }
                }
                if (found != null) return ParseDefaultValue(c, found);
            }
            return null;
        }

        static object ParseDefaultValue(UClass cls, UELib.Core.UDefaultProperty dp)
        {
            string raw = dp.Value ?? "";
            switch (dp.Type)
            {
                case UELib.Types.PropertyType.BoolProperty:
                    return dp.BoolValue ?? raw.Equals("true", StringComparison.OrdinalIgnoreCase);
                case UELib.Types.PropertyType.IntProperty:
                    return int.TryParse(raw, out var i) ? i : 0;
                case UELib.Types.PropertyType.ByteProperty:
                    return byte.TryParse(raw, out var b) ? (object)b : (byte)0;
                case UELib.Types.PropertyType.FloatProperty:
                    return float.TryParse(raw, System.Globalization.NumberStyles.Float,
                                          System.Globalization.CultureInfo.InvariantCulture,
                                          out var f) ? f : 0f;
                case UELib.Types.PropertyType.StrProperty:
                    return Unquote(raw);
                case UELib.Types.PropertyType.NameProperty:
                    // UELib renders a name default's raw value quoted — and the
                    // quote char varies ("run" for these HP1 defaults, 'run' in
                    // other dumps). UE names never contain quotes, so strip a
                    // matching leading/trailing pair of EITHER type. (Was '\''
                    // only, which left double-quoted names like "run" intact and
                    // broke every name-keyed lookup, e.g. LoopAnim(walkAnimName).)
                    return new UEName(UnquoteAny(raw));
                case UELib.Types.PropertyType.ObjectProperty:
                case UELib.Types.PropertyType.ClassProperty:
                    // Logic ACTOR refs stay None — making them non-null would
                    // activate dormant `if(ref != None)` paths game-wide. But:
                    //  - RENDER-RESOURCE refs (Texture/Font/Sound...) resolve to an
                    //    inert name-carrying marker for the HUD/UI bridge;
                    //  - Gesture refs (spellX.default.Gesture) resolve to a Points bag;
                    //  - CLASS literals (`WandClass=Class'HPBase.SpellWand'`) resolve
                    //    to the runtime UClass — UE1 resolves class references at
                    //    package load, and a None class made every default-driven
                    //    Spawn return None (the lesson wand/FX never spawned).
                    {
                        var gestureBag = ResolveGestureRef(raw);
                        if (gestureBag != null) return gestureBag;
                        var clsRef = ResolveClassRef(raw);
                        if (clsRef != null) return clsRef;
                        return ParseResourceRef(raw);
                    }
                case UELib.Types.PropertyType.StructProperty:
                    return DecodeStructDefault(cls, dp, raw);
            }
            return raw;
        }

        // ── Struct defaultproperty decoding ──────────────────────────────────
        // UE1 serializes struct default values ATOMICALLY (flat fields in
        // declaration order, no tags): UStructProperty::SerializeItem →
        // UStruct::SerializeBin (Core.dll.c:60734, :45563): fields in declaration order,
        // no tags; CPF_Native is always skipped, CPF_Transient when IsPersistent (Ar+0x1c,
        // :4795), i.e. on package load. The Ver < 0x4c branch (FloatParams/ColorParams.Rand)
        // does not run for v76 packages. UELib decompiles structs it can't resolve cross-package as
        // an empty "()" (HPParticle's FloatParams/ColorParams tags — the
        // declarations live in Engine.ParticleFX), so the VM decodes the RAW
        // VALUE BYTES (UObject.GetRawPropertyValueBytesAll, our UELib patch)
        // by the tag's struct name:
        //   FloatParams {float Base; float Rand}            (Engine.u decl)
        //   ColorParams {Color Base; Color Rand}            (Engine.u decl)
        //   Color       {byte R,G,B,A}                      (Core.u decl)
        //   Vector/Rotator — value intrinsics (also decoded from bytes when
        //   UELib's string form is present it parses fine, but raw is exact).
        // Unknown struct names log ONCE and keep the legacy raw string.
        // Prototypes are cached per tag; ApplyClassDefaults deep-copies bags
        // per instance.
        static readonly Dictionary<UELib.Core.UDefaultProperty, object> _structDefaultCache = new();
        static readonly HashSet<string> _loggedStructDecode = new(UEPropNameComparer.Instance);

        static object DecodeStructDefault(UClass cls, UELib.Core.UDefaultProperty dp, string raw)
        {
            if (_structDefaultCache.TryGetValue(dp, out var cached)) return cached;
            object result = raw;   // legacy fallback
            string structName = null;
            try { structName = dp.StructName?.ToString(); } catch { }
            byte[] b = null;
            try
            {
                var all = cls.GetRawPropertyValueBytesAll(dp.Name.ToString());
                foreach (var kv in all)
                    if (kv.Key == dp.ArrayIndex) { b = kv.Value; break; }
            }
            catch (Exception e)
            {
                LogStructDecodeOnce($"{cls.Name}.{dp.Name}", $"raw-bytes read failed: {e.Message}");
            }

            switch (structName?.ToLowerInvariant())
            {
                case "floatparams":   // {float Base; float Rand} — Engine.u ParticleFX.uc:21
                    if (b != null && b.Length >= 8)
                        result = MakeFloatParamsBag(BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4));
                    else { LogStructDecodeOnce($"{cls.Name}.{dp.Name}", $"FloatParams bytes={b?.Length ?? -1}"); result = MakeFloatParamsBag(0, 0); }
                    break;
                case "colorparams":   // {Color Base; Color Rand} — Engine.u ParticleFX.uc:27; Color = R,G,B,A (Core.u)
                    if (b != null && b.Length >= 8)
                        result = MakeColorParamsBag(b);
                    else { LogStructDecodeOnce($"{cls.Name}.{dp.Name}", $"ColorParams bytes={b?.Length ?? -1}"); result = MakeColorParamsBag(null); }
                    break;
                case "color":
                    if (b != null && b.Length >= 4)
                        result = MakeColorBag(b[0], b[1], b[2], b[3]);
                    break;
                case "vector":
                    if (b != null && b.Length >= 12)
                        result = new Vector3(BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8));
                    break;
                case "rotator":
                    if (b != null && b.Length >= 12)
                        result = new UERotator(BitConverter.ToInt32(b, 0), BitConverter.ToInt32(b, 4), BitConverter.ToInt32(b, 8));
                    break;
                case "plane":
                    // Core.Object.Plane = FPlane: FVector(12) + float W(4) —
                    // PlayerPawn.FlashFog/ConstantGlowFog/DesiredFlashFog/
                    // InstantFog and ZoneInfo.ViewFog/ViewFlash.
                    if (b != null && b.Length >= 16)
                        result = new UEPlane(BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4),
                            BitConverter.ToSingle(b, 8), BitConverter.ToSingle(b, 12));
                    else
                        LogStructDecodeOnce($"{cls.Name}.{dp.Name}", $"Plane bytes={b?.Length ?? -1}");
                    break;
                case "scale":
                    // Core FScale is packed as FVector(12) + float SheerRate(4)
                    // + ESheerAxis byte(1): Core.dll.c:36438-36449.
                    if (b != null && b.Length >= 17)
                    {
                        var scale = new UEObject(UEPropLayout.ForStructName("Scale")) { IsStruct = true };
                        scale.Props["Scale"] = new Vector3(
                            BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4),
                            BitConverter.ToSingle(b, 8));
                        scale.Props["SheerRate"] = BitConverter.ToSingle(b, 12);
                        scale.Props["SheerAxis"] = b[16];
                        result = scale;
                    }
                    else
                        LogStructDecodeOnce($"{cls.Name}.{dp.Name}", $"Scale bytes={b?.Length ?? -1}");
                    break;
                default:
                    // GENERIC atomic decode driven by the UELib struct
                    // declaration (fields in declaration order — UStruct::
                    // SerializeBin, Core.dll.c:45563). Covers FEBook.Background (6×Texture
                    // ref + float — the menu book art), WizardList, and any
                    // future script struct without a dedicated case.
                    var generic = DecodeStructByDefinition(cls, dp, structName, b);
                    if (generic != null) { result = generic; break; }
                    // Not decoded. Loud once per struct type so a gap is
                    // visible instead of silently feeding "()" strings to code.
                    if (!string.IsNullOrEmpty(structName) && (raw == "()" || raw == ""))
                        LogStructDecodeOnce(structName, $"undecoded struct type (size={dp.Size}) — first hit {cls.Name}.{dp.Name}");
                    break;
            }
            _structDefaultCache[dp] = result;
            return result;
        }

        // ── Generic atomic-struct decode ─────────────────────────────────────
        // The declaration is found via a UStructProperty on the class chain
        // (same property name, or any whose struct type matches the tag name).
        // Object refs are package-local FCompactIndex objrefs (0=None,
        // +N=Exports[N-1], -N=Imports[-N-1] — same convention the cutscene
        // extractor decodes); render resources land as UEResourceRef markers
        // (the Canvas/sound bridges resolve them by name), class refs as the
        // runtime UClass. Any layout mismatch → null (legacy raw + loud log),
        // never a partial guess.
        static object DecodeStructByDefinition(UClass cls, UELib.Core.UDefaultProperty dp,
                                               string structName, byte[] b)
        {
            if (b == null || cls == null) return null;
            UELib.Core.UStruct def = null;
            for (var c = cls; c != null && def == null; c = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(c))
                foreach (var p in c.EnumerateFields<UELib.Core.UStructProperty>())
                {
                    if (string.Equals(p.Name?.ToString(), dp.Name.ToString(), StringComparison.OrdinalIgnoreCase)
                     || string.Equals(p.Struct?.Name?.ToString(), structName, StringComparison.OrdinalIgnoreCase))
                    { def = p.Struct; break; }
                }
            if (def == null) return null;
            int pos = 0;
            var bag = ReadAtomicStruct(def, cls, b, ref pos);
            if (bag == null || pos != b.Length)
            {
                LogStructDecodeOnce(structName ?? dp.Name.ToString(),
                    $"generic decode mismatch (read {pos}/{b.Length} bytes) — first hit {cls.Name}.{dp.Name}");
                return null;
            }
            return bag;
        }

        // Value of an INSTANCE struct (property tag of a placed actor): the same SerializeBin
        // walk. The declaration is looked up by struct name along the owner class chain. null
        // if the declaration is not found, the struct holds an object reference (see
        // UEActorGraphLoader.DecodeStruct) or the bytes do not match the layout: the caller
        // must stop loudly.
        internal static UEObject DecodeInstanceStruct(UClass cls, string structName, byte[] b)
        {
            if (b == null || cls == null || string.IsNullOrEmpty(structName)) return null;
            UELib.Core.UStruct def = null;
            for (var c = cls; c != null && def == null; c = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(c))
                foreach (var p in c.EnumerateFields<UELib.Core.UStructProperty>())
                    if (string.Equals(p.Struct?.Name?.ToString(), structName, StringComparison.OrdinalIgnoreCase))
                    { def = p.Struct; break; }
            if (def == null) return null;
            foreach (var f in def.EnumerateFields<UELib.Core.UProperty>())
                if (f is UELib.Core.UObjectProperty || f is UELib.Core.UStructProperty) return null;
            int pos = 0;
            var bag = ReadAtomicStruct(def, cls, b, ref pos);
            return bag != null && pos == b.Length ? bag : null;
        }

        // UStruct::SerializeBin (Core.dll.c:45567-45628): walks the struct's
        // property chain in declaration order; a member flagged CPF_Native
        // (0x1000) is never serialised, CPF_Transient (0x2000) is skipped on a
        // persistent archive (package load IS persistent — FArchive+0x1c);
        // every other member is serialised ArrayDim times (piStack_34[0xd]),
        // element by element. A static array inside a struct therefore lands
        // as a List<object> — the same shape PreallocDeclaredProps gives a
        // class-level static array, so `S.Variant[i].DlgName` indexes it.
        static UEObject ReadAtomicStruct(UELib.Core.UStruct def, UClass pkgCls, byte[] b, ref int pos)
        {
            var bag = new UEObject(UEPropLayout.ForStruct(def)) { IsStruct = true };
            foreach (var f in def.EnumerateFields<UELib.Core.UProperty>())
            {
                if (f.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Native)
                 || f.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Transient)) continue;
                int dim = Math.Max(1, f.ArrayDim);
                if (dim == 1)
                {
                    if (!ReadAtomicField(f, pkgCls, b, ref pos, out var v)) return null;
                    bag.Props[f.Name.ToString()] = v;
                    continue;
                }
                var list = new List<object>(dim);
                for (int i = 0; i < dim; i++)
                {
                    if (!ReadAtomicField(f, pkgCls, b, ref pos, out var v)) return null;
                    list.Add(v);
                }
                bag.Props[f.Name.ToString()] = list;
            }
            return bag;
        }

        // FString serialisation (Core.dll.c operator<<(FArchive&,FString&),
        // 0x16b8): FCompactIndex N; N > 0 → N ANSI bytes INCLUDING the null
        // terminator; N < 0 → |N| UTF-16 code units including the terminator;
        // N == 0 → empty string. The string runs to the first null, and the position advances
        // over ALL elements (a TCHAR array read as a C string), the same rule as
        // UEActorGraphLoader.ReadString and RawReader.FString.
        internal static bool ReadAtomicFString(byte[] b, ref int pos, out string s)
        {
            s = "";
            if (pos >= b.Length) return false;
            int n = ReadAtomicCompactIndexSigned(b, ref pos);
            if (n == 0) return true;
            if (n > 0)
            {
                if (pos + n > b.Length) return false;
                var chars = new char[n - 1]; for (int i = 0; i < n - 1; i++) chars[i] = (char)b[pos + i]; s = new string(chars); pos += n;
            }
            else
            {
                n = -n;
                if (pos + n * 2 > b.Length) return false;
                s = System.Text.Encoding.Unicode.GetString(b, pos, (n - 1) * 2); pos += n * 2;
            }
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s.Substring(0, nul);
            return true;
        }

        static bool ReadAtomicField(UELib.Core.UProperty f, UClass pkgCls, byte[] b, ref int pos, out object v)
        {
            v = null;
            switch (f)
            {
                case UELib.Core.UFloatProperty _:
                    if (pos + 4 > b.Length) return false;
                    v = BitConverter.ToSingle(b, pos); pos += 4; return true;
                case UELib.Core.UIntProperty _:
                    if (pos + 4 > b.Length) return false;
                    v = BitConverter.ToInt32(b, pos); pos += 4; return true;
                case UELib.Core.UByteProperty _:
                    if (pos + 1 > b.Length) return false;
                    v = b[pos]; pos += 1; return true;
                case UELib.Core.UStrProperty _:
                {
                    if (!ReadAtomicFString(b, ref pos, out var str)) return false;
                    v = str; return true;
                }
                case UELib.Core.UBoolProperty _:
                    // ONE byte (0/1), not a DWORD. Confirmed from the NATIVE ground truth:
                    // UBoolProperty::SerializeItem (Core.dll.c:12653-12672) extracts the bit
                    // to a byte and calls FArchive::Serialize(&val, 1) — one byte — then sets/
                    // clears the in-memory DWORD bit.
                    // (Was a 4-byte DWORD read — dormant in Tut1, no bool-bearing atomic struct,
                    // but it would have mis-parsed any future one.)
                    if (pos + 1 > b.Length) return false;
                    v = b[pos] != 0; pos += 1; return true;
                case UELib.Core.UNameProperty _:
                {
                    int idx = ReadAtomicCompactIndex(b, ref pos);
                    if (idx < 0) return false;
                    var names = pkgCls.Package?.Names;
                    v = (names != null && idx < names.Count) ? new UEName(names[idx].Name) : null;
                    return true;
                }
                case UELib.Core.UStructProperty sp:
                {
                    string sn = sp.Struct?.Name?.ToString()?.ToLowerInvariant();
                    if (sn == "color")
                    {
                        if (pos + 4 > b.Length) return false;
                        v = MakeColorBag(b[pos], b[pos + 1], b[pos + 2], b[pos + 3]); pos += 4; return true;
                    }
                    if (sn == "vector")
                    {
                        if (pos + 12 > b.Length) return false;
                        v = new Vector3(BitConverter.ToSingle(b, pos), BitConverter.ToSingle(b, pos + 4), BitConverter.ToSingle(b, pos + 8));
                        pos += 12; return true;
                    }
                    if (sn == "rotator")
                    {
                        if (pos + 12 > b.Length) return false;
                        v = new UERotator(BitConverter.ToInt32(b, pos), BitConverter.ToInt32(b, pos + 4), BitConverter.ToInt32(b, pos + 8));
                        pos += 12; return true;
                    }
                    if (sp.Struct == null) return false;
                    var nested = ReadAtomicStruct(sp.Struct, pkgCls, b, ref pos);
                    v = nested; return nested != null;
                }
                case UELib.Core.UObjectProperty _:                   // includes UClassProperty
                {
                    int objIdx = ReadAtomicCompactIndexSigned(b, ref pos);
                    v = ResolveAtomicObjRef(pkgCls, objIdx);
                    return true;
                }
                default:
                    return false;
            }
        }

        // FCompactIndex: byte0 bit7=sign, bit6=more, 6 value bits; following
        // bytes bit7=more, 7 value bits (max 5 bytes).
        static int ReadAtomicCompactIndexSigned(byte[] b, ref int pos)
        {
            if (pos >= b.Length) return 0;
            int b0 = b[pos++];
            bool negative = (b0 & 0x80) != 0;
            int value = b0 & 0x3F;
            if ((b0 & 0x40) != 0)
            {
                int shift = 6;
                for (int i = 0; i < 4 && pos < b.Length; i++)
                {
                    int bn = b[pos++];
                    value |= (bn & 0x7F) << shift;
                    shift += 7;
                    if ((bn & 0x80) == 0) break;
                }
            }
            return negative ? -value : value;
        }

        static int ReadAtomicCompactIndex(byte[] b, ref int pos)
            => ReadAtomicCompactIndexSigned(b, ref pos);

        static object ResolveAtomicObjRef(UClass cls, int objIdx)
        {
            if (objIdx == 0) return null;
            var pkg = cls.Package;
            if (pkg == null) return null;
            string name = null, klass = null, path = null;
            if (objIdx > 0 && objIdx - 1 < pkg.Exports.Count)
            {
                var it = pkg.Exports[objIdx - 1];
                name = it.ObjectName.ToString();
#pragma warning disable CS0618
                klass = it.ClassName;
#pragma warning restore CS0618
                path = pkg.PackageName + "." + it.GetPath();
            }
            else if (objIdx < 0 && -objIdx - 1 < pkg.Imports.Count)
            {
                var it = pkg.Imports[-objIdx - 1];
                name = it.ObjectName.ToString();
                klass = it.ClassName;
                // For an import the root of the Outer chain is the owning package; which export
                // answers it is decided by ULinkerLoad::VerifyImport (Core registry).
                var chain = new List<(string Name, string ClassName, string ClassPackage)>();
                for (UELib.UObjectTableItem o = it; o != null; o = o.Outer)
                    chain.Add((o.ObjectName.ToString(),
                        (o as UELib.UImportTableItem)?.ClassName?.ToString(),
                        (o as UELib.UImportTableItem)?.PackageName?.ToString()));
                chain.Reverse();
                path = HP1.Core.UEClassRegistry.Shared?.VerifyImportPath(chain)
                    ?? string.Join(".", chain.ConvertAll(c => c.Name));
            }
            if (string.IsNullOrEmpty(name)) return null;
            if (string.IsNullOrEmpty(klass) || string.Equals(klass, "Class", StringComparison.OrdinalIgnoreCase))
            {
                var rc = HP1.Core.UEClassRegistry.Shared?.Find(name);
                if (rc != null) return rc;
            }
            // FULL PATH REQUIRED: a reference inside a defaults struct
            // (FEBook.SplashScreens[i].texture = HPMenu.Icons.FEEALogoTexture1) must reach the
            // frontend as a full path, since the texture bank resolves by full path. Same
            // ObjectPath as ParseResourceRef for text defaults.
            return new UEResourceRef(klass ?? "Object", name, path);
        }

        static void LogStructDecodeOnce(string key, string msg)
        {
            if (!_loggedStructDecode.Add(key)) return;
            HP1.Core.UETrace.Warn($"[VM] struct default '{key}': {msg}");
        }

        static UEObject MakeFloatParamsBag(float bse, float rnd)
        {
            var bag = new UEObject(UEPropLayout.ForStructName("FloatParams")) { IsStruct = true };
            bag.Props["Base"] = bse;
            bag.Props["Rand"] = rnd;
            return bag;
        }

        static UEObject MakeColorBag(byte r, byte g, byte bl, byte a)
        {
            var bag = new UEObject(UEPropLayout.ForStructName("Color")) { IsStruct = true };
            bag.Props["R"] = (int)r;
            bag.Props["G"] = (int)g;
            bag.Props["B"] = (int)bl;
            bag.Props["A"] = (int)a;
            return bag;
        }

        static UEObject MakeColorParamsBag(byte[] b)
        {
            var bag = new UEObject(UEPropLayout.ForStructName("ColorParams")) { IsStruct = true };
            bag.Props["Base"] = (b != null) ? MakeColorBag(b[0], b[1], b[2], b[3]) : MakeColorBag(0, 0, 0, 0);
            bag.Props["Rand"] = (b != null) ? MakeColorBag(b[4], b[5], b[6], b[7]) : MakeColorBag(0, 0, 0, 0);
            return bag;
        }

        // StrProperty default: strip the outer quote pair AND reverse UELib's
        // FormatLiteral escaping (PropertyDisplay.cs:15-39 escapes inner \" \\ \n \r).
        // Single left-to-right pass so `\\` (literal backslash) isn't re-interpreted.
        // No-op for Tut1 (no string default contains an escape), correct for any
        // escaped default on later levels.
        static string Unquote(string s, char q = '"')
        {
            if (string.IsNullOrEmpty(s)) return s;
            int a = 0, b = s.Length;
            if (s[0] == q) a = 1;
            if (b > a && s[b - 1] == q) b--;
            string body = s.Substring(a, b - a);
            if (body.IndexOf('\\') < 0) return body;                 // fast path: no escapes
            var sb = new System.Text.StringBuilder(body.Length);
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    char n = body[++i];
                    sb.Append(n == 'n' ? '\n' : n == 'r' ? '\r' : n);  // \" \\ → the char; \n \r → newline/CR
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // Strip a matching leading/trailing quote pair of either type ("..." or
        // '...'). UE names/strings never contain quotes, so this is safe.
        static string UnquoteAny(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 2) return s;
            char c = s[0];
            if ((c == '"' || c == '\'') && s[s.Length - 1] == c)
                return s.Substring(1, s.Length - 2);
            return s;
        }

        // UE render-resource classes whose defaultproperty refs we resolve to a
        // UEResourceRef marker (everything else stays None — see ParseDefaultValue).
        static readonly HashSet<string> _resourceClasses = new(UEPropNameComparer.Instance)
        {
            "Texture", "Font", "Sound", "Music", "Palette",
            "FireTexture", "WaterTexture", "WetTexture", "IceTexture",
            "WaveTexture", "ScriptedTexture",
            // Actor.Mesh defaults use these concrete UMesh subclasses. Dropping
            // them to None made source guards such as Harry.PlayWaiting's
            // `if (Mesh==None) return` suppress the original animation logic.
            "Mesh", "LodMesh", "SkeletalMesh", "VertMesh",
        };

        // Resolve a `Class'Pkg.Name'` default literal to the runtime UClass (null
        // if the literal isn't a class ref or the package isn't loaded — stays
        // None, faithful to an unloadable class).
        // Classes named by cls's own defaultproperties class literals, in tag order.
        internal static IEnumerable<UClass> DefaultClassRefs(UClass cls)
        {
            if (cls?.Properties == null) yield break;
            foreach (var dp in cls.Properties)
                if (dp?.Type is UELib.Types.PropertyType.ObjectProperty or UELib.Types.PropertyType.ClassProperty
                    && ResolveClassRef(dp.Value) is UClass c)
                    yield return c;
        }

        static object ResolveClassRef(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length < 8) return null;
            if (!raw.StartsWith("Class'", StringComparison.OrdinalIgnoreCase)
             || raw[raw.Length - 1] != '\'') return null;
            string path = raw.Substring(6, raw.Length - 7);
            int dot = path.LastIndexOf('.');
            string name = dot >= 0 ? path.Substring(dot + 1) : path;
            int first = path.IndexOf('.');
            return HP1.Core.UEClassRegistry.Shared.Find(first > 0 ? path.Substring(0, first) : null, name);
        }

        // A text default is the decompiler-printed IMPORT chain of the class
        // (`Texture'HPEdit.Icons.Icons.station'` in HPBase.u), not an object path: which export
        // answers it is decided by ULinkerLoad::VerifyImport. Intermediate links of a resource
        // import are groups (Core.Package), the leaf is of the declared class; the text does
        // not carry the leaf's class package. If the chain does not resolve, the literal path
        // remains (the consumer refuses loudly).
        static readonly Dictionary<string, string> _textImportPaths = new(StringComparer.OrdinalIgnoreCase);
        static string VerifyTextImportPath(string path, string cls)
        {
            string key = cls + "'" + path;
            if (_textImportPaths.TryGetValue(key, out string done)) return done;
            string[] seg = path.Split('.');
            string result = path;
            if (seg.Length >= 2 && HP1.Core.UEClassRegistry.Shared is { } reg)
            {
                var chain = new List<(string Name, string ClassName, string ClassPackage)>();
                chain.Add((seg[0], "Package", "Core"));
                for (int i = 1; i < seg.Length - 1; i++) chain.Add((seg[i], "Package", "Core"));
                chain.Add((seg[seg.Length - 1], cls, null));
                string verified = reg.VerifyImportPath(chain);
                if (verified is not null) { result = verified; _textImportPaths[key] = result; }
            }
            return result;
        }

        // Resource reference from class defaultproperties (template Mesh/Brush): companion of
        // ClassDefaultScalar for the template's AActor::GetCylinderExtent
        // (ULevel::SpawnActor 0x103a67d8). Only ObjectProperty, only via ParseResourceRef: no
        // bags, no classes, no ObjectId spent.
        public static object ClassDefaultResource(UClass cls, string name)
        {
            for (var c = cls; c != null; c = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(c))
            {
                if (c.Properties == null) continue;
                UELib.Core.UDefaultProperty found = null;
                foreach (var dp in c.Properties)
                {
                    if (dp?.Name == null || dp.ArrayIndex > 0) continue;
                    if (!string.Equals(dp.Name.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (dp.Type == UELib.Types.PropertyType.ObjectProperty) found = dp;
                }
                if (found != null) return ParseResourceRef(found.Value ?? "");
            }
            return null;
        }

        // MESH LITERAL IN A FUNCTION BODY (execObjectConst, Core.dll.c:55595: *Result = pointer
        // from the stream) is the same UObject as a defaultproperties reference. UELib returns
        // its own object, while all Actor.Mesh consumers (frame DrawActorSprite
        // Render.dll.c:12048, PlayAnim, ApplyAnim, collision) read UEResourceRef; without the
        // conversion `Mesh = SkeletalMesh'HProps.PotionBottleBlueMesh'` (Hub4.DummyPotionBottle)
        // left a non-null value nobody understood. Converted by the same ParseResourceRef as
        // defaults: one form per object. Textures/sounds/fonts are left alone: their consumers
        // read both forms (WorldFrame.AssetPath, TexPathOf, SoundNatives).
        public static object MeshLiteralRef(object literal)
        {
            if (literal is not UObject uo) return literal;
            string cls = uo.ImportTable != null ? uo.ImportTable.ClassName.ToString() : uo.Class?.Name;
            if (cls is null || !(cls.Equals("Mesh", StringComparison.OrdinalIgnoreCase)
                || cls.Equals("LodMesh", StringComparison.OrdinalIgnoreCase)
                || cls.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)
                || cls.Equals("VertMesh", StringComparison.OrdinalIgnoreCase)))
                return literal;
            return ParseResourceRef(cls + "'" + HP1.UEScript.Render.ShippedLayout.FullPath(uo) + "'")
                ?? throw new InvalidOperationException($"execObjectConst: mesh literal {cls}'{uo.GetPath()}' not parsed");
        }

        // Parse an ObjectProperty default like `Texture'HPBase.Icons.leftPanel'`.
        // Keep the complete object path: UE1 object references are not bare-name
        // lookups, and different packages/groups may legally contain the same leaf.
        static object ParseResourceRef(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw == "None") return null;
            int q = raw.IndexOf('\'');
            if (q <= 0 || raw[raw.Length - 1] != '\'') return null;
            string cls = raw.Substring(0, q);
            if (!_resourceClasses.Contains(cls)) return null;
            string path = raw.Substring(q + 1, raw.Length - q - 2);
            path = VerifyTextImportPath(path, cls);
            int dot = path.LastIndexOf('.');
            string name = dot >= 0 ? path.Substring(dot + 1) : path;
            return new UEResourceRef(cls, name, path);
        }

        // ── Gesture template resolution (spell-lesson scorer) ────────────────
        // spellX.default.Gesture = Gesture'HPBase.FlipPattern' → a native UGesture
        // instance whose Points (array<vector>) is the pattern the player traces.
        // UELib can't auto-parse the native array (the native class declares no script
        // `var`, so the inner type is undetected), so pull the raw value bytes via
        // UObject.GetRawPropertyValueBytes (added to our UELib build) and decode the
        // UE1 layout [FCompactIndex count][count × (x,y,z) floats]. Cached per pattern.
        // Returns null for non-Gesture refs so the caller falls back to ParseResourceRef.
        static readonly Dictionary<string, UEObject> _gestureCache =
            new(UEPropNameComparer.Instance);

        internal static UEObject ResolveGestureRef(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw == "None") return null;
            int q = raw.IndexOf('\'');
            if (q <= 0 || raw[raw.Length - 1] != '\'') return null;
            if (!string.Equals(raw.Substring(0, q), "Gesture", StringComparison.OrdinalIgnoreCase)) return null;
            // The reference names its full object path (Gesture'Package.Group.Name'): resolved by that path only (exact,
            // like UE1 StaticFindObject), cached by it — not by the leaf name, which another package may reuse.
            string path = raw.Substring(q + 1, raw.Length - q - 2);
            if (_gestureCache.TryGetValue(path, out var cached)) return cached;
            var exp = HP1.Core.UEClassRegistry.Shared.FindExport(path);
            if (exp == null) return null;          // packages not ready / missing — retry later (don't cache null)
            exp.BeginDeserializing();
            var bag = new UEObject {
                Class = HP1.Core.UEClassRegistry.Shared.Find("Gesture"),
                Interp = DefaultInterp,
            };
            bag.Props["Points"] = DecodeVectorArray(exp.GetRawPropertyValueBytes("Points"));
            _gestureCache[path] = bag;
            return bag;
        }

        // UE1 array<vector> serialized value: [FCompactIndex count][count × 3 floats].
        static List<object> DecodeVectorArray(byte[] b)
        {
            var list = new List<object>();
            if (b == null || b.Length == 0) return list;
            int pos = 0;
            int count = ReadAtomicCompactIndexSigned(b, ref pos);   // canonical 0x80=sign/0x40=continue (#audit 17)
            if (count < 0 || count > 100000) return list;
            for (int i = 0; i < count && pos + 12 <= b.Length; i++)
            {
                float x = System.BitConverter.ToSingle(b, pos);
                float y = System.BitConverter.ToSingle(b, pos + 4);
                float z = System.BitConverter.ToSingle(b, pos + 8);
                pos += 12;
                list.Add(new Vector3(x, y, z));
            }
            return list;
        }
    }
}
