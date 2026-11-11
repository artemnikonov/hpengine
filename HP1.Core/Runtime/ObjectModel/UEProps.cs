// VM OBJECT PROPERTIES: CELLS BY SLOT, NOT A DICTIONARY BY NAME.
//
// In UE1 a property is a UProperty offset into object memory: bytecode
// (InstanceVariable) carries the UProperty and a read is one indexing. A
// Dictionary<string,object> with a key computed per bytecode read (PropKey: class
// ShadowMap under a lock) plus string hashing cost 21% (ShadowMap) and 12% (PropKey)
// of headless Tut1 time.
//
// Layout:
//   * UEPropKeys: global key ids (property name, case-insensitive like FName). A string
//     key becomes an id via a memoised string REFERENCE: literals and names from the
//     package name table are the same objects, so chars are never read;
//   * UEPropLayout: class (or struct) layout, key -> slot. Keys are the same strings as
//     the former dictionary keys (simple name; "Class.Name" for a shadowed declaration,
//     see UEObject.PropKey), so the string API answers exactly like a dictionary.
//     Bytecode addresses a slot by UProperty id: a shadowed declaration gets its own
//     slot, like its own offset in shipped;
//   * UEProps: values of one object: an array by slot plus insertion order
//     (enumeration follows it, like a dictionary without removals).
//
// Dictionary semantics are kept: a missing key is distinct from a key holding null
// (ContainsKey/TryGetValue), indexer-read of a missing key throws KeyNotFoundException,
// a null key throws ArgumentNullException, modification during enumeration throws
// InvalidOperationException.
//
// Threads: layouts and the key table are shared by all objects, so reads are lock-free
// (arrays are published whole) and writes take a lock. Values of one object are
// single-threaded.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using UELib.Core;

namespace HP1.UEScript
{
    public static class UEPropKeys
    {
        sealed class Entry
        {
            public readonly string Key; public readonly int Hash; public readonly int Id;
            public Entry(string k, int h, int id) { Key = k; Hash = h; Id = id; }
        }
        sealed class Memo
        {
            public readonly string S; public readonly int Id;
            public Memo(string s, int id) { S = s; Id = id; }
        }

        static readonly object _lock = new object();
        static Entry[] _table = new Entry[4096];          // open addressing, power of two
        static int _used;
        static string[] _names = new string[1024];         // id -> first spelling
        static int _count;
        static readonly Memo[] _memo = new Memo[8192];     // string reference -> id

        public static int Count => Volatile.Read(ref _count);
        public static string Name(int id) => Volatile.Read(ref _names)[id];

        // Key id; a new key is registered.
        public static int Id(string key)
        {
            if (key is null) throw new ArgumentNullException(nameof(key));
            int ms = RuntimeHelpers.GetHashCode(key) & (8192 - 1);
            Memo m = Volatile.Read(ref _memo[ms]);
            if (m is not null && ReferenceEquals(m.S, key)) return m.Id;
            int id = Probe(key, true);
            Volatile.Write(ref _memo[ms], new Memo(key, id));
            return id;
        }

        // Key id, or -1 if no object has had this key yet.
        public static int Find(string key)
        {
            if (key is null) throw new ArgumentNullException(nameof(key));
            int ms = RuntimeHelpers.GetHashCode(key) & (8192 - 1);
            Memo m = Volatile.Read(ref _memo[ms]);
            if (m is not null && ReferenceEquals(m.S, key)) return m.Id;
            int id = Probe(key, false);
            if (id >= 0) Volatile.Write(ref _memo[ms], new Memo(key, id));
            return id;
        }

        static int Probe(string key, bool create)
        {
            int h = UEPropNameComparer.Compute(key);
            int id = Lookup(Volatile.Read(ref _table), key, h);
            if (id >= 0 || !create) return id;
            lock (_lock)
            {
                Entry[] t = _table;
                id = Lookup(t, key, h);
                if (id >= 0) return id;
                if ((_used + 1) * 2 > t.Length)
                {
                    var nt = new Entry[t.Length * 2];
                    foreach (Entry e in t) if (e is not null) Insert(nt, e);
                    t = nt;
                }
                id = _count;
                if (id == _names.Length)
                {
                    var nn = new string[id * 2];
                    Array.Copy(_names, nn, id);
                    Volatile.Write(ref _names, nn);
                }
                _names[id] = key;
                Volatile.Write(ref _count, id + 1);
                Insert(t, new Entry(key, h, id));
                _used++;
                Volatile.Write(ref _table, t);
                return id;
            }
        }

        static int Lookup(Entry[] t, string key, int h)
        {
            int mask = t.Length - 1;
            for (int i = h & mask; ; i = (i + 1) & mask)
            {
                Entry e = Volatile.Read(ref t[i]);
                if (e is null) return -1;
                if (e.Hash == h && UEPropNameComparer.Instance.Equals(e.Key, key)) return e.Id;
            }
        }

        static void Insert(Entry[] t, Entry e)
        {
            int mask = t.Length - 1;
            int i = e.Hash & mask;
            while (t[i] is not null) i = (i + 1) & mask;
            Volatile.Write(ref t[i], e);
        }

        // UProperty id (bytecode addresses a slot by declaration, not by name).
        static readonly Dictionary<UProperty, int> _propIds = new(UERefComparer<UProperty>.Instance);
        static UProperty[] _props = new UProperty[1024];
        public static int PropId(UProperty p)
        {
            lock (_propIds)
            {
                if (_propIds.TryGetValue(p, out int id)) return id;
                id = _propIds.Count;
                if (id == _props.Length)
                {
                    var np = new UProperty[id * 2];
                    Array.Copy(_props, np, id);
                    Volatile.Write(ref _props, np);
                }
                _props[id] = p;
                _propIds[p] = id;
                return id;
            }
        }
        public static UProperty Prop(int propId) => Volatile.Read(ref _props)[propId];
    }

    public sealed class UEPropLayout
    {
        public readonly UClass Class;      // class layout; null for a struct
        public readonly string Label;      // for diagnostics

        readonly object _lock = new object();
        string[] _keys = new string[16];   // slot -> key
        int[] _keyIds = new int[16];       // slot -> key id
        int _count;
        int[] _slotByKey = Array.Empty<int>();    // key id -> slot+1
        int[] _slotByProp = Array.Empty<int>();   // UProperty id -> slot+1

        public int SlotCount => Volatile.Read(ref _count);
        public string KeyAt(int slot) => Volatile.Read(ref _keys)[slot];
        public int KeyIdAt(int slot) => Volatile.Read(ref _keyIds)[slot];

        // Static arrays of a struct layout (function locals `local T X[N]`): slot and
        // declaration. UE1 zeroes the whole frame memory, which for an array means ArrayDim
        // elements; UEProps.MaterializeStaticArrays creates them.
        public (int Slot, UProperty Prop)[] StaticArrays = Array.Empty<(int, UProperty)>();

        // Shadowed-name map of the class (UEObject.ShadowMap); null if nothing is shadowed.
        public readonly System.Collections.Generic.Dictionary<string, string> Shadow;

        UEPropLayout(UClass cls, string label) { Class = cls; Label = label; Shadow = UEObject.ShadowMap(cls); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int FindSlot(int keyId)
        {
            int[] a = Volatile.Read(ref _slotByKey);
            return (uint)keyId < (uint)a.Length ? a[keyId] - 1 : -1;
        }

        public int SlotForKey(int keyId)
        {
            int s = FindSlot(keyId);
            if (s >= 0) return s;
            lock (_lock) return AddKeyLocked(keyId);
        }

        int AddKeyLocked(int keyId)
        {
            int[] a = _slotByKey;
            if ((uint)keyId < (uint)a.Length && a[keyId] != 0) return a[keyId] - 1;
            int slot = _count;
            if (slot == _keys.Length)
            {
                var nk = new string[slot * 2]; Array.Copy(_keys, nk, slot);
                var ni = new int[slot * 2];    Array.Copy(_keyIds, ni, slot);
                Volatile.Write(ref _keys, nk);
                Volatile.Write(ref _keyIds, ni);
            }
            _keys[slot] = UEPropKeys.Name(keyId);
            _keyIds[slot] = keyId;
            Volatile.Write(ref _count, slot + 1);
            if (keyId >= a.Length)
            {
                var na = new int[Math.Max(UEPropKeys.Count, keyId + 1) + 64];
                Array.Copy(a, na, a.Length);
                a = na;
            }
            a[keyId] = slot + 1;
            Volatile.Write(ref _slotByKey, a);
            return slot;
        }

        // Slot of the property declared by UProperty `p` (id propId). The key is the same
        // one UEObject.PropKey would give for an object of this class.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SlotForProp(int propId)
        {
            int[] a = Volatile.Read(ref _slotByProp);
            if ((uint)propId < (uint)a.Length && a[propId] != 0) return a[propId] - 1;
            return ResolveProp(propId);
        }

        int ResolveProp(int propId)
        {
            UProperty p = UEPropKeys.Prop(propId);
            string name = p.Name.ToString();
            string key = UEObject.ShadowKey(Shadow, UEObject.PropDeclClassName(p), name);
            int keyId = UEPropKeys.Id(key);
            lock (_lock)
            {
                int slot = AddKeyLocked(keyId);
                int[] a = _slotByProp;
                if (propId >= a.Length)
                {
                    var na = new int[propId + 256];
                    Array.Copy(a, na, a.Length);
                    a = na;
                }
                a[propId] = slot + 1;
                Volatile.Write(ref _slotByProp, a);
                return slot;
            }
        }

        // ── Layout registry ───────────────────────────────────────────────
        static readonly object _regLock = new object();
        static readonly Dictionary<UClass, UEPropLayout> _byClass = new(UERefComparer<UClass>.Instance);
        static readonly Dictionary<UStruct, UEPropLayout> _byStruct = new(UERefComparer<UStruct>.Instance);
        static readonly Dictionary<string, UEPropLayout> _byName = new(StringComparer.OrdinalIgnoreCase);

        // Shared layout for objects with no class and no known struct.
        public static readonly UEPropLayout Loose = new UEPropLayout(null, "<loose>");

        public static UEPropLayout ForClass(UClass cls)
        {
            if (cls is null) return Loose;
            lock (_regLock)
            {
                if (_byClass.TryGetValue(cls, out var l)) return l;
                l = new UEPropLayout(cls, cls.Name);
                // Declarations become slots in chain order from the root, like offsets in
                // object memory; a slot's key is what the string API sees.
                var chain = new List<UClass>();
                for (var c = cls; c != null; c = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(c)) chain.Add(c);
                for (int i = chain.Count - 1; i >= 0; i--)
                    foreach (var p in chain[i].EnumerateFields<UProperty>())
                        if (p?.Name != null) l.SlotForProp(UEPropKeys.PropId(p));
                _byClass[cls] = l;
                return l;
            }
        }

        // Read on every function call (locals layout): a lock-free snapshot; a new
        // struct publishes a copy.
        static Dictionary<UStruct, UEPropLayout> _byStructSnap = new(UERefComparer<UStruct>.Instance);

        public static UEPropLayout ForStruct(UStruct st)
        {
            if (st is null) return Loose;
            if (Volatile.Read(ref _byStructSnap).TryGetValue(st, out var hit)) return hit;
            lock (_regLock)
            {
                int had = _byStruct.Count;
                UEPropLayout l = BuildStructLocked(st);
                if (_byStruct.Count != had)
                    Volatile.Write(ref _byStructSnap, new Dictionary<UStruct, UEPropLayout>(_byStruct, UERefComparer<UStruct>.Instance));
                return l;
            }
        }

        // ForStruct of many structs with ONE snapshot copy (UELevelRuntime.WarmFirstUseCaches: function locals at level
        // load; a copy per new struct made the first calls of a level quadratic in the number of known structs).
        public static void WarmStructs(IEnumerable<UStruct> structs)
        {
            lock (_regLock)
            {
                int had = _byStruct.Count;
                foreach (UStruct st in structs) if (st is not null) BuildStructLocked(st);
                if (_byStruct.Count != had)
                    Volatile.Write(ref _byStructSnap, new Dictionary<UStruct, UEPropLayout>(_byStruct, UERefComparer<UStruct>.Instance));
            }
        }

        static UEPropLayout BuildStructLocked(UStruct st)
        {
            if (_byStruct.TryGetValue(st, out var l)) return l;
            l = new UEPropLayout(null, st.Name?.ToString());
            var chain = new List<UStruct>();
            for (UStruct s = st; s != null; s = s.Super as UStruct) chain.Add(s);
            var arrays = new List<(int, UProperty)>();
            for (int i = chain.Count - 1; i >= 0; i--)
                foreach (var p in chain[i].EnumerateFields<UProperty>())
                    if (p?.Name != null)
                    {
                        int slot = l.SlotForProp(UEPropKeys.PropId(p));
                        if (p.ArrayDim > 1 && !p.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Parm))
                            arrays.Add((slot, p));
                    }
            if (arrays.Count > 0) l.StaticArrays = arrays.ToArray();
            _byStruct[st] = l;
            return l;
        }

        // Struct known only by name (bags the port builds itself: Color, FloatParams,
        // PointRegion...). Members are registered on write.
        public static UEPropLayout ForStructName(string name)
        {
            lock (_regLock)
            {
                if (_byName.TryGetValue(name, out var l)) return l;
                l = new UEPropLayout(null, name);
                _byName[name] = l;
                return l;
            }
        }
    }

    public sealed class UEProps : IEnumerable<KeyValuePair<string, object>>
    {
        // A null value that is PRESENT (the key exists). An empty array cell means the key
        // is absent.
        static readonly object PresentNull = new object();
        static readonly object[] NoValues = Array.Empty<object>();
        static readonly int[] NoOrder = Array.Empty<int>();

        UEPropLayout _layout;
        object[] _v = NoValues;    // slot -> value
        int[] _order = NoOrder;    // present slots in insertion order
        int _n;
        int _version;        // every write: the enumerator's modification check
        int _valueVersion;   // writes that change a value (see Version)

        public UEProps(UEPropLayout layout) { _layout = layout ?? UEPropLayout.Loose; }

        public UEPropLayout Layout => _layout;

        // Frame-local static arrays (`local float L[3]`): zeroed lists of ArrayDim elements,
        // like object fields (UEObject.ZeroStaticArray). Without them a write `L[i] = x`
        // found no array and was lost (Fluffy.PostBeginPlay: heads got ListenTime = 0).
        internal void MaterializeStaticArrays()
        {
            var arrays = _layout.StaticArrays;
            for (int i = 0; i < arrays.Length; i++)
                SetSlot(arrays[i].Slot, UEObject.ZeroStaticArray(arrays[i].Prop));
        }
        public int Count => _n;
        // Bumped by every write that changes a value (SetSlot unless it stores the same value, see SameValue; Remove,
        // Clear, CopyFrom, Rebind always): a cache keyed by it is exact for anything computed from this bag's own values,
        // except SetUnversioned slots. What a value refers to (another bag, an array) is not covered.
        internal int Version => _valueVersion;

        // Layout change (the object was assigned a class): values move over by key in the
        // previous insertion order.
        internal void Rebind(UEPropLayout layout)
        {
            layout ??= UEPropLayout.Loose;
            if (ReferenceEquals(layout, _layout)) return;
            if (_n == 0) { _layout = layout; _v = NoValues; return; }
            var old = _layout; var ov = _v; var oo = _order; int on = _n;
            _layout = layout; _v = NoValues; _order = NoOrder; _n = 0; _version++; _valueVersion++;
            for (int i = 0; i < on; i++)
            {
                int s = oo[i];
                object v = ov[s];
                SetSlot(layout.SlotForKey(old.KeyIdAt(s)), ReferenceEquals(v, PresentNull) ? null : v);
            }
        }

        // ── By slot of the current layout ─────────────────────────────────
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetSlot(int slot, out object value)
        {
            object[] v = _v;
            object o = (uint)slot < (uint)v.Length ? v[slot] : null;
            if (o is null) { value = null; return false; }
            value = ReferenceEquals(o, PresentNull) ? null : o;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object GetSlot(int slot)
        {
            object[] v = _v;
            object o = (uint)slot < (uint)v.Length ? v[slot] : null;
            return ReferenceEquals(o, PresentNull) ? null : o;
        }

        public void SetSlot(int slot, object value) => SetSlot(slot, value, versioned: true);

        void SetSlot(int slot, object value, bool versioned)
        {
            object[] v = _v;
            if ((uint)slot >= (uint)v.Length)
            {
                var nv = new object[Math.Max(slot + 1, _layout.SlotCount)];
                Array.Copy(v, nv, v.Length);
                _v = v = nv;
            }
            if (v[slot] is null)
            {
                if (_n == _order.Length)
                {
                    var no = new int[_n == 0 ? Math.Max(8, _layout.SlotCount) : _n * 2];   // first growth: the layout's size (allocation only)
                    Array.Copy(_order, no, _n);
                    _order = no;
                }
                _order[_n++] = slot;
            }
            object old = v[slot], stored = value ?? PresentNull;
            v[slot] = stored;
            _version++;
            if (versioned && !SameValue(old, stored)) _valueVersion++;
        }

        // A rewrite of the same value: the same reference (not an array or list: one re-stored after an in-place change
        // is a change), or a boxed scalar/vector/rotator with the same bits (so 0 and -0, or two NaN payloads, are
        // different values). An absent slot (null) never equals a stored one.
        static bool SameValue(object a, object b)
        {
            if (ReferenceEquals(a, b)) return a is not IList;
            if (a is null) return false;
            switch (a)
            {
                case float f: return b is float g && BitConverter.SingleToInt32Bits(f) == BitConverter.SingleToInt32Bits(g);
                case int i: return b is int j && i == j;
                case bool x: return b is bool y && x == y;
                case byte p: return b is byte q && p == q;
                case UEVector u:
                    return b is UEVector w && BitConverter.SingleToInt32Bits(u.x) == BitConverter.SingleToInt32Bits(w.x)
                        && BitConverter.SingleToInt32Bits(u.y) == BitConverter.SingleToInt32Bits(w.y)
                        && BitConverter.SingleToInt32Bits(u.z) == BitConverter.SingleToInt32Bits(w.z);
                case UERotator r: return b is UERotator t && r.Pitch == t.Pitch && r.Yaw == t.Yaw && r.Roll == t.Roll;
                default: return false;
            }
        }

        // ── By key id (C# hot paths keep the id in a static) ──────────────
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object Get(int keyId)
        {
            int s = _layout.FindSlot(keyId);
            return s < 0 ? null : GetSlot(s);
        }
        public bool TryGetValue(int keyId, out object value)
        {
            int s = _layout.FindSlot(keyId);
            if (s < 0) { value = null; return false; }
            return TryGetSlot(s, out value);
        }
        public void Set(int keyId, object value) => SetSlot(_layout.SlotForKey(keyId), value);
        // A write that does not move Version: only for a value no Version-keyed cache reads (AActor.bTicked, the tick
        // pass guard, which the frame projection does not read; HP1_FRAME_CACHE_VERIFY=1 re-projects every memo hit).
        internal void SetUnversioned(int keyId, object value) => SetSlot(_layout.SlotForKey(keyId), value, versioned: false);

        // ── String API (dictionary-compatible) ────────────────────────────
        public bool TryGetValue(string key, out object value)
        {
            int id = UEPropKeys.Find(key);
            if (id < 0) { value = null; return false; }
            return TryGetValue(id, out value);
        }

        public object Get(string key)
        {
            int id = UEPropKeys.Find(key);
            return id < 0 ? null : Get(id);
        }

        public object this[string key]
        {
            get
            {
                if (!TryGetValue(key, out object v)) throw new KeyNotFoundException($"The given key '{key}' was not present in the dictionary.");
                return v;
            }
            set => SetSlot(_layout.SlotForKey(UEPropKeys.Id(key)), value);
        }

        public bool ContainsKey(string key) => TryGetValue(key, out _);

        public bool Remove(string key)
        {
            int id = UEPropKeys.Find(key);
            if (id < 0) return false;
            int s = _layout.FindSlot(id);
            object[] v = _v;
            if (s < 0 || (uint)s >= (uint)v.Length || v[s] is null) return false;
            v[s] = null;
            int i = Array.IndexOf(_order, s, 0, _n);
            Array.Copy(_order, i + 1, _order, i, _n - i - 1);
            _n--;
            _version++; _valueVersion++;
            return true;
        }

        // Call-frame pool (UEVmPool): the locals bag of a returned call, emptied for the next call's layout. Same
        // observable state as `new UEProps(layout)` (every slot absent, insertion order empty); the arrays are kept,
        // their lengths only decide when SetSlot allocates. Present slots are exactly the first _n of _order.
        internal void ResetForReuse(UEPropLayout layout)
        {
            object[] v = _v;
            for (int i = 0; i < _n; i++) v[_order[i]] = null;
            _n = 0;
            _layout = layout ?? UEPropLayout.Loose;
            _version++; _valueVersion++;
        }

        public void Clear()
        {
            if (_n == 0) return;
            Array.Clear(_v, 0, _v.Length);
            _n = 0;
            _version++; _valueVersion++;
        }

        // Copy of values from another object with the same layout (CloneStruct).
        internal void CopyFrom(UEProps src, Func<object, object> map)
        {
            _layout = src._layout;
            _v = src._v.Length == 0 ? NoValues : new object[src._v.Length];
            _order = src._n == 0 ? NoOrder : new int[src._n];
            _n = src._n;
            for (int i = 0; i < _n; i++)
            {
                int s = src._order[i];
                _order[i] = s;
                object o = src._v[s];
                _v[s] = ReferenceEquals(o, PresentNull) ? o : (map(o) ?? PresentNull);
            }
            _version++; _valueVersion++;
        }

        public KeyCollection Keys => new KeyCollection(this);

        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<KeyValuePair<string, object>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<KeyValuePair<string, object>>
        {
            readonly UEProps _p; readonly int _ver; int _i;
            KeyValuePair<string, object> _cur;
            internal Enumerator(UEProps p) { _p = p; _ver = p._version; _i = 0; _cur = default; }
            public bool MoveNext()
            {
                if (_ver != _p._version) throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                if (_i >= _p._n) { _cur = default; return false; }
                int s = _p._order[_i++];
                object o = _p._v[s];
                _cur = new KeyValuePair<string, object>(_p._layout.KeyAt(s), ReferenceEquals(o, PresentNull) ? null : o);
                return true;
            }
            public KeyValuePair<string, object> Current => _cur;
            object IEnumerator.Current => _cur;
            public void Reset() { _i = 0; }
            public void Dispose() { }
        }

        public readonly struct KeyCollection : IEnumerable<string>
        {
            readonly UEProps _p;
            internal KeyCollection(UEProps p) { _p = p; }
            public int Count => _p._n;
            public IEnumerator<string> GetEnumerator()
            {
                foreach (var kv in _p) yield return kv.Key;
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
