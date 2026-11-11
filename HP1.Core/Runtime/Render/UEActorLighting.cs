// ACTOR LIGHTING — LIGHT SOURCE SELECTION.
//
// Rules in UE units / UE world axes:
//   * per-frame light collection (HP1LightSink.Sync): level ambient FGetHSV(Actors(0)); a source when
//     LightType≠0 and LightBrightness>0; colour FGetHSV(Hue, Sat, 255)·Brightness/255·LightState.Scale
//     (URender::GlobalLighting `push 0xff` 0x10b0685d, Render.dll.c:19147; ComputeFromActor :19255,
//     :19274); bDarkLight — sign −1 (:21374-21379); inner radius RadiusInner/256·R (:19219-19225);
//   * per-actor selection (HP1Lighting.GatherActorLights; FLightManager::SetupForActor,
//     Render.dll.c:21286-21463): the BSP leaf sublist (iPermeating, :21286-21300) plus dispatched
//     dynamics — URender::LeafLights of the actor leaf (sprite +0xbc, :690-696, :21346-21372); bSpecialLit pairing (:21301); dist² < R²; initial counter
//     0 for the leaf sublist / 0xff outside it (:21308, :21369); score ROUND((1−d/R)·Brightness·1024);
//     descending sort (UE1 Sort template FUN_10b0d030, SortScoresDescending: equal scores in its order); threshold
//     score/8 after the first accepted (:21383, :21441-21444); cap 3+3 (:21399-21403); LOS from light to
//     actor Location — Model->LineCheck, BSP only (:21421-21425); without a fresh trace — the pair counter bit
//     (:21405, schedule (Index ^ frame) & 0xf); gates bUnlit / iLeaf == -1 before the body (0x10b09b79/86); a fresh
//     actor cache gives 0xff to an accepted light (0x10b0a338); hysteresis rate = ROUND(Δt·768) in [0,255]
//     (:21210-21212), accepted += rate | 1, rejected −= rate (& ~1); export up to 16 entries —
//     accepted always, rejected while counter > 0 (:21391-21398, :21437), colour · counter/255.
// Δt is the VIEWPORT clock: 0x10b09bd2..0x10b09c03 takes Viewport (CurrentTime − LastUpdateTime), FTime int64,
// ·2^-32 (0x10b386ac) ·768 (0x10b386a8), fstp float, fistp — wall time between frames, not paused, no TimeDilation.
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    public sealed class UEActorLighting
    {
        public struct Source
        {
            public UEVector Position;
            public float Radius, RadiusInner;
            public float R, G, B;
            public int ObjectId, ExportIndex, Brightness;
            public bool Ambient, NonIncidence, Static, NoDelete, DynamicLight, SpecialLit, Movable, Dark;
        }

        public struct Exported
        {
            public UEVector Position;
            public float Radius, RadiusInner;   // Radius < 0 — LD_Ambient (the sign is the marker)
            public float R, G, B;
            public int ExportIndex, ObjectId;
            public bool Dark;   // bDarkLight (light actor +0x1f0 bit 0x10): the sum is floored at 0 after this light
        }

        // No count check on the exported records (accept path thunk_FUN_10b097b0, reject-with-counter path
        // 0x10b0a3ea..0x10b0a3f6); the only limit is the end of the 256-record array (0x10b097b0..0x10b097c0,
        // (0x10b5ae48 − 0x10b4ea48) / 0xc4). :21455 is the 16-pair cache limit (CacheCap).
        public const int ExportCap = 256;

        readonly List<Source> _lights = new();
        public IReadOnlyList<Source> Lights => _lights;
        public (float r, float g, float b) LevelAmbient { get; private set; }

        int[] _leafPermeating, _lightExports;
        HashSet<int> _pool;
        readonly HashSet<int> _leafLights = new();
        readonly List<int> _leafChain = new();   // the leaf's Model->Lights exports in chain order (iPermeating)
        int _leafFor = int.MinValue;
        // ACTOR CACHE (FMemCache, key Index·0x100+0x30, Render.dll.c:21222-21283): up to 16 (source, counter) pairs
        // from the previous frame — candidates whose counter is > 1 after selection, in score order (:21452-21462).
        // Its sources are candidates even outside the new leaf sublist (:21284-21312); other counters start at 0
        // for the leaf sublist (:21334), 0xff for dispatched dynamics (:21365). No entry = fresh cache (:21289).
        public const int CacheCap = 16;
        readonly Dictionary<int, List<(int light, int counter)>> _cache = new();
        readonly Dictionary<int, int> _counter = new();
        readonly Dictionary<int, int> _indexByObject = new();
        int _lightFrame, _fadeRate;
        bool _fadeSeeded;
        double _lastViewportSeconds;

        public int LosCalls, LosBlocked, Attached, RejectedCap, RejectedThreshold, RejectedLos, LeafUnknown, RejectedSpecial, Gated, MovableNoLos;
        bool _viewLit = true;
        public bool ViewLitFrame => _viewLit;   // frame view gates (BeginFrame) — for UnlitFlag
        public int FadeRate => _fadeRate;   // frame fade-in rate (checked by validate_actor_light.py)

        public void SetLeafChain(int[] leafPermeating, int[] lightExports)
        {
            _leafPermeating = leafPermeating; _lightExports = lightExports;
            _leafFor = int.MinValue; _leafLights.Clear(); _leafChain.Clear();
            _pool = null;
            if (lightExports is null) return;
            _pool = new HashSet<int>();
            foreach (int e in lightExports) if (e != 0) _pool.Add(e);
        }

        // HP1Lighting.BeginFrame + HP1LightSink.Sync. viewLit — view-level SetupForActor gates (ViewLit). levelBrightness —
        // the light's Level->Brightness (+0x44c): FLightInfo::ComputeFromActor scales every record colour by it
        // (Render.dll.c:19274-19280, read at 0x10b06b0c: fVar32 = Level->Brightness · scalar; +0x8c..+0x98 *= fVar32), actor and surface alike.
        public void BeginFrame(WorldFrame frame, double viewportSeconds, bool viewLit, float levelBrightness)
        {
            _viewLit = viewLit;
            _lightFrame++;
            if (!_fadeSeeded) { _lastViewportSeconds = viewportSeconds; _fadeSeeded = true; }
            _fadeRate = Math.Clamp((int)MathF.Round((float)(Math.Max(0.0, viewportSeconds - _lastViewportSeconds) * 768.0), MidpointRounding.ToEven), 0, 255);
            _lastViewportSeconds = viewportSeconds;
            LosCalls = LosBlocked = Attached = RejectedCap = RejectedThreshold = RejectedLos = LeafUnknown = RejectedSpecial = Gated = MovableNoLos = 0;

            _lights.Clear();
            if (frame.LevelAmbient.Valid)
                LevelAmbient = FGetHSV(frame.LevelAmbient.Hue, frame.LevelAmbient.Saturation, frame.LevelAmbient.Brightness);
            ReadOnlySpan<ActorSnapshot> actors = frame.ActorSpan;
            for (int i = 0; i < actors.Length; i++)
            {
                ref readonly ActorSnapshot a = ref actors[i];
                if (a.Destroyed) continue;
                LightState L = a.Light;
                // LightBrightness 0 stays a source: SetupForActor takes every Model->Lights entry of the leaf chain as a
                // candidate whatever its brightness (score 0, colour 0; shipped array 0x10b4c864 under unicorn,
                // validate_actor_light.py, review C6 2026-11-01). R <= 0 never passes R·R > d2.
                if (L.Type == 0) continue;
                if (!(L.WorldRadius > 0f)) continue;
                var (r, g, b) = FGetHSV(L.Hue, L.Saturation, 255);
                float k = levelBrightness * (L.Brightness * (1f / 255f) * L.Scale);
                r *= k; g *= k; b *= k;
                if (L.Dark) { r = -r; g = -g; b = -b; }
                var src = new Source
                {
                    Position = a.Location, Radius = L.WorldRadius, RadiusInner = L.RadiusInner * (1f / 256f) * L.WorldRadius,
                    R = r, G = g, B = b, ObjectId = a.ObjectId, ExportIndex = a.ExportIndex, Brightness = L.Brightness,
                    Ambient = L.Source == 2, NonIncidence = L.Effect == 13, Static = L.Static, NoDelete = L.NoDelete,
                    DynamicLight = L.DynamicLight, SpecialLit = L.SpecialLit, Movable = L.Movable, Dark = L.Dark,
                };
                _lights.Add(src);
            }
        }

        void EnsureLeaf(int leaf)
        {
            if (leaf == _leafFor) return;
            _leafFor = leaf; _leafLights.Clear(); _leafChain.Clear();
            if (_leafPermeating is null || _lightExports is null || leaf < 0 || leaf >= _leafPermeating.Length) return;
            int start = _leafPermeating[leaf];
            if (start < 0) return;
            for (int k = start; k < _lightExports.Length && _lightExports[k] != 0; k++) { _leafLights.Add(_lightExports[k]); _leafChain.Add(_lightExports[k]); }
        }

        // Candidates in SetupForActor's order (Render.dll.c:21284-21372; disasm 0x10b09dbe..): pass 0 — the actor cache's
        // sources (no limit, at most 16); pass 1 — the leaf sublist, Model->Lights from Leaves[iLeaf].iPermeating to the
        // null entry (:21314-21340); pass 2 — the dispatched list, URender::LeafLights of the actor's leaf after the frame's
        // walk (sprite +0xbc, Render.dll.c:690-696; the dynamic lights FilterLight's leaf half linked there, UEOcclusion,
        // UESprite.LeafLights). Passes 1 and 2 stop at 0x78 candidates (`DAT_10b5f72c < 0x78`). A light enters once: the
        // frame stamp +0xf4 is written only when it is taken (:21310, :21338, :21369). Without a leaf chain (no pool) —
        // every light, index order. The light index is built once per frame.
        readonly Dictionary<int, List<int>> _byExport = new();
        readonly List<(int Index, int Pass)> _order = new();
        int _indexedFrame = -1;

        List<(int Index, int Pass)> CandidateOrder(List<(int light, int counter)> cached, int[] dispatched)
        {
            if (_pool is null) return null;
            if (_indexedFrame != _lightFrame)
            {
                _indexedFrame = _lightFrame;
                foreach (var l in _byExport.Values) l.Clear();
                _indexByObject.Clear();
                for (int i = 0; i < _lights.Count; i++)
                {
                    _indexByObject[_lights[i].ObjectId] = i;
                    int e = _lights[i].ExportIndex;
                    if (!_byExport.TryGetValue(e, out var l)) _byExport[e] = l = new List<int>();
                    l.Add(i);
                }
            }
            _order.Clear();
            foreach (var (id, _) in cached)
                if (_indexByObject.TryGetValue(id, out int ci)) _order.Add((ci, 0));
            foreach (int e in _leafChain)
                if (_byExport.TryGetValue(e, out var l)) foreach (int li in l) _order.Add((li, 1));
            if (dispatched is not null)
                foreach (int id in dispatched)
                    if (_indexByObject.TryGetValue(id, out int di)) _order.Add((di, 2));
            return _order;
        }

        // FUN_10b0d030 (0x10b0d030..0x10b0d1e3): UE1's Sort template (UnTemplate.h) over the candidate pointers with
        // Compare(A, B) = A.score > B.score ? -1 : 1 (`cmp [A+0xec], [B+0xec]; setg; 1 - 2*bl`) - never 0, so equal scores
        // end up where the algorithm puts them: <= 8 items - selection of the "largest" (Compare(item, max) > 0, i.e.
        // item.score <= max.score: the last of equal ones) swapped to the end; more - the middle element swapped to the
        // front as pivot, the two scans (`<= 0` up, `>= 0` down), the smaller half first, the bigger one on the stack.
        static void SortScoresDescending(List<Cand> a)
        {
            static int Cmp(Cand x, Cand y) => x.Score > y.Score ? -1 : 1;
            void Swap(int i, int j) { (a[i], a[j]) = (a[j], a[i]); }
            int n = a.Count;
            if (n < 2) return;
            var stack = new Stack<(int Min, int Max)>();
            stack.Push((0, n - 1));
            while (stack.Count > 0)
            {
                var (min, max) = stack.Pop();
                while (true)
                {
                    int count = max - min + 1;
                    if (count <= 8)
                    {
                        while (max > min)
                        {
                            int m = min;
                            for (int it = min + 1; it <= max; it++) if (Cmp(a[it], a[m]) > 0) m = it;
                            Swap(m, max--);
                        }
                        break;
                    }
                    Swap(min + count / 2, min);
                    int lo = min, hi = max + 1;
                    for (; ; )
                    {
                        while (++lo <= max && Cmp(a[lo], a[min]) <= 0) { }
                        while (--hi > min && Cmp(a[hi], a[min]) >= 0) { }
                        if (lo > hi) break;
                        Swap(lo, hi);
                    }
                    Swap(min, hi);
                    if (hi - 1 - min >= max - lo)
                    {
                        if (min + 1 < hi) stack.Push((min, hi - 1));
                        if (max > lo) { min = lo; continue; }
                    }
                    else
                    {
                        if (max > lo) stack.Push((lo, max));
                        if (min + 1 < hi) { max = hi - 1; continue; }
                    }
                    break;
                }
            }
        }

        struct Cand { public int Index, Score; }
        readonly List<Cand> _cand = new();
        readonly HashSet<int> _taken = new();
        // The last Gather's candidates after the sort (light ExportIndex, score): the shipped array 0x10b4c864 with +0xec
        // (validate_actor_light.py compares them).
        public IEnumerable<(int Export, int Score)> LastCandidates
        { get { foreach (Cand c in _cand) yield return (_lights[c.Index].ExportIndex, c.Score); } }
        bool[] _keep = new bool[64];

        // lineClear(from, to) — LOS from source -> actor Location (level BSP only, TRACE_Level).
        // SetupForActor gates before the actor cache (Render.dll disasm): bUnlit — `test byte [actor+0x198], 1; jne`
        // (0x10b09b79), Region.iLeaf == -1 — `cmp [actor+0xa4], -1; je` (0x10b09b86). Both jump to the epilogue
        // 0x10b0a5d8: no list (for unlit also none for fog), cache and fade counters untouched. The other
        // three gates go to the same epilogue (disasm 0x10b09b93-0x10b09bcc):
        //   * Level->Model->Leaves.Num == 0 — `[[0x10b5ae48]+0x98]+0xdc` (DAT_10b5ae48 = Frame->Level, Render.dll.c:21146;
        //     +0x98 — Model, +0xdc — ArrayNum of the leaf table +0xd8, the same one URender::DrawWorld uses to size
        //     LeafLights, Render.dll.c:654); in the port — length of LeafPermeating (one leaf per entry);
        //   * Viewport->Actor->RendMap != 5 (REN_DynLight) and Viewport->Outer (UClient)->NoLighting — ViewLit.
        // dispatched: the drawing frame's LeafLights[leaf] (UESprite.LeafLights) — light ObjectIds; null — none.
        public int Gather(int actorId, UEVector location, int leaf, bool specialLit, bool unlit,
            Func<UEVector, UEVector, bool> lineClear, Exported[] into, int[] dispatched = null)
        {
            if (_pool != null && leaf < 0) LeafUnknown++;
            if (unlit || leaf == -1 || _leafPermeating is { Length: 0 } || !_viewLit) { Gated++; return 0; }
            EnsureLeaf(leaf);
            bool cold = !_cache.TryGetValue(actorId, out var cached);
            if (cold) _cache[actorId] = cached = new List<(int light, int counter)>();
            _counter.Clear();
            foreach (var (id, ctr) in cached) _counter[id] = ctr;
            _cand.Clear();
            var order = CandidateOrder(cached, dispatched);
            _taken.Clear();
            for (int oi = 0, on = order?.Count ?? _lights.Count; oi < on; oi++)
            {
                int i = order != null ? order[oi].Index : oi;
                int pass = order != null ? order[oi].Pass : -1;
                if (pass > 0 && _cand.Count >= 0x78) break;     // passes 1 and 2: `DAT_10b5f72c < 0x78`
                Source L = _lights[i];
                if (_taken.Contains(L.ObjectId)) continue;      // frame stamp +0xf4 (set below only when taken)
                if (L.SpecialLit != specialLit) { RejectedSpecial++; continue; }
                // 0x10b09e0b..0x10b09e43: d = L - A (fsub of floats), d2 = (dz*dz + dy*dy) + dx*dx (PC24: float per operation);
                // kept when R*R > d2 (`fcomp; test ah, 0x41; jne` skips <= and unordered).
                float dx = L.Position.x - location.x, dy = L.Position.y - location.y, dz = L.Position.z - location.z;
                float d2 = (dz * dz + dy * dy) + dx * dx;
                if (!(L.Radius * L.Radius > d2)) continue;
                // 0x10b09e83..0x10b09ed0: dist = FastSqrt table 0x10b4ca44 (FogMap.FastSqrt); score = fistp of the float
                // ((1 - dist/R)*Brightness)*1024 (fdiv, fsubr 1.0, fimul byte +0x1e2, fmul 1024, fstp dword).
                float dist = FogMap.FastSqrt(d2);
                _taken.Add(L.ObjectId);
                // Initial counter (+0xf8): pass 0 - the cached one; pass 1 - 0; pass 2 - 0xff. No pool: cached, else by
                // leaf membership.
                if (pass == 1) _counter[L.ObjectId] = 0;
                else if (pass == 2) _counter[L.ObjectId] = 0xff;
                else if (pass < 0 && !_counter.ContainsKey(L.ObjectId)) _counter[L.ObjectId] = _leafLights.Contains(L.ExportIndex) ? 0 : 0xff;
                _cand.Add(new Cand { Index = i, Score = (int)MathF.Round((1f - dist / L.Radius) * L.Brightness * 1024f, MidpointRounding.ToEven) });
            }
            SortScoresDescending(_cand);
            if (_keep.Length < _cand.Count) _keep = new bool[_cand.Count];
            int total = 0, statics = 0, threshold = -1;
            for (int c = 0; c < _cand.Count; c++)
            {
                Source L = _lights[_cand[c].Index];
                int counter = _counter[L.ObjectId];
                int score = _cand[c].Score;
                int capCount = L.Static ? statics : total;
                bool attach;
                if (score < threshold) { attach = false; RejectedThreshold++; }
                else if (capCount > 2) { attach = false; RejectedCap++; }
                // A non-static with bMovable is accepted without a trace (0x10b0a21f-0x10b0a22d -> 0x10b0a349): no LOS,
                // no pair counter bit, no full ignition of a fresh cache (the 0xff write 0x10b0a338 is on the trace branch).
                else if (!L.Static && L.Movable) { attach = true; Attached++; MovableNoLos++; }
                else if (!LineOfSight(L, cold, counter, location, lineClear)) { attach = false; RejectedLos++; }
                else { attach = true; Attached++; if (cold) counter = 0xff; }
                if (attach)
                {
                    counter = Math.Min(counter + _fadeRate, 255) | 1;
                    if (threshold == -1) threshold = (score + ((score >> 31) & 7)) >> 3;
                    total++;
                    if (L.Static) statics++;
                }
                else counter = Math.Max(counter - _fadeRate, 0) & ~1;
                _counter[L.ObjectId] = counter;
                _keep[c] = attach || counter > 0;
            }
            // Next frame's cache: candidates with counter > 1 in score order, up to 16 (:21452-21462).
            cached.Clear();
            for (int c = 0; c < _cand.Count && cached.Count < CacheCap; c++)
            {
                int id = _lights[_cand[c].Index].ObjectId;
                if (_counter[id] > 1) cached.Add((id, _counter[id]));
            }
            int count = 0;
            for (int c = 0; c < _cand.Count && count < ExportCap && count < into.Length; c++)
            {
                if (!_keep[c]) continue;
                Source L = _lights[_cand[c].Index];
                // FUN_10b097b0 0x10b097e6..0x10b097ec: the actor's own light is never added as a record (it still took
                // part in the cap and the threshold above).
                if (L.ObjectId == actorId) continue;
                float fade = _counter[L.ObjectId] * (1f / 255f);
                into[count++] = new Exported
                {
                    Position = L.Position, Radius = L.Ambient ? -L.Radius : L.Radius, RadiusInner = L.RadiusInner,
                    R = L.R * fade, G = L.G * fade, B = L.B * fade, ExportIndex = L.ExportIndex, ObjectId = L.ObjectId, Dark = L.Dark,
                };
            }
            return count;
        }

        // Source LOS: shipped calls Level->Model->LineCheck (UModel slot 0x5c, Render.dll.c:21421; ExtraNodeFlags 0 —
        // `push ebx` at 0x10b0a253 after `xor ebx, ebx` at 0x10b0a21d) — the level model without mover actors as
        // primitives; here ULevel::SingleLineCheck with TRACE_Level (4), no extent. Mover fragments from the brush
        // tracker (D-47) live in the same model and occlude as level geometry — same as shipped (checked on the
        // live model with validate_actor_light: Tut1Hermione0/Light1908 rejected by both sides).
        public static Func<UEVector, UEVector, bool> BspLineClear(UELevelRuntime rt, UEInterpreter interp)
            => (from, to) => !rt.SingleLineCheck(interp, null, null, from, to, default, 4).Blocked;

        // Without a fresh trace the previous frame's outcome is the PAIR COUNTER'S LOW BIT (`*(light+0xf8) & 1`,
        // Render.dll.c:21405; accepted writes `| 1`, rejected `& ~1`): a pair never accepted (counter 0
        // in the leaf sublist) is REJECTED without a fresh trace.
        // Fresh-trace schedule: shipped — (source UObject::Index ^ Viewport->FrameCount) & 0xf (disasm
        // 0x10b0a238-0x10b0a243: [light+0x4] ^ [ebp-0x3c], with [ebp-0x3c] = [Viewport+0x7c], 0x10b09c30-0x10b09c3a).
        // The port uses the export index and its own frame counter: UObject::Index is a GObjObjects slot, and the
        // port has no such table (UEObject.ObjectId is its own counter). Not emulated per §4a: it only decides ON WHICH
        // of 16 frames a source's visibility for the actor refreshes; the +0xec/+0xf4/+0xf8 writes are presentation
        // (render ledger: game scripts do not read them). D-41 item 5.
        bool LineOfSight(in Source L, bool cold, int counter, UEVector location, Func<UEVector, UEVector, bool> lineClear)
        {
            int index = L.ExportIndex != 0 ? L.ExportIndex : L.ObjectId;
            if (!cold && ((index ^ _lightFrame) & 0xf) != 0) return (counter & 1) != 0;
            LosCalls++;
            float dx = location.x - L.Position.x, dy = location.y - L.Position.y, dz = location.z - L.Position.z;
            bool clear = dx * dx + dy * dy + dz * dz <= 1e-12f || lineClear(L.Position, location);
            if (!clear) LosBlocked++;
            return clear;
        }

        // ── VERTEX LIGHT ─────────────────────────────────────────────────────────────────────
        // Light manager slot 6, FUN_10b02b10 (disasm 0x10b02b10-0x10b03030), in the render window (PC24: every x87
        // operation rounds to float — C# float in the disasm's operation order). Seed (0x10b4bb78, SetupForActor):
        // FGetHSV(zone)·2·ScaleGlow + glow. Per light (record stride 0xc4):
        //   d = L − P (float stores), d2 = (dx² + dy²) + dz² (fst [ebp-0x20]), dist = FastSqrt table 0x10b4ca44
        //   (0x10b02c8d-0x10b02cab — FogMap.FastSqrt, NOT an exact sqrt);
        //   LD_Ambient (light byte +0x1f4 == 2, 0x10b02cbe): angle 1, light vector N·dist;
        //   otherwise angle = ((dy·Ny + dz·Nz) + dx·Nx) / dist (0x10b02d8a-0x10b02d9f, no guard on dist);
        //   glossy (2·ScaleGlow == 1.4f, 0x10b385c4): (angle + 1)² − 1.5; angle ·= 2·ScaleGlow; angle < 0 -> 0;
        //   K > 0: s = 2·((ly·Ny + lz·Nz) + lx·Nx), r = l − N·s, rp = (rx·Ex + rz·Ez) + ry·Ey;
        //          rp > 0 and t = rp/(d2·|E|²)·rp − C > 0 -> angle += K·t (0x10b02e00-0x10b02e93, no guard);
        //   angle > 0: atten = (R − dist)·inv, inv = 1/max(R − inner, 1.0) stored by the inserter
        //          (0x10b069a3-0x10b069dd: the floor is 1.0, not an epsilon); atten = min(atten, 1) only
        //          (`fcomp` 1.0, NaN -> 1.0; NO lower clamp, 0x10b02eac-0x10b02ec6); colour += atten·angle·RGB;
        //          bDarkLight light (+0x1f0 bit 0x10): each channel floored at 0 after it (0x10b02f68-0x10b02fb8).
        // End: a channel whose BITS are >= 0x3f800000 unsigned (>= 1.0 or negative) becomes 1.0 (0x10b02fc6-0x10b03030).
        // The shipped vertex and light positions are in frame coordinates; here they are world coordinates relative to
        // the eye — the same numbers only for an identity frame at the origin (validate_vertex_light's observer).
        public static (float r, float g, float b) VertexLight(UEVector p, UEVector n, UEVector eye,
            Exported[] lights, int count, (float r, float g, float b) zoneAmbient, float scaleGlow, float glow,
            float specK, float specC, bool glossy)
        {
            float m = 2f * scaleGlow;
            float cr = zoneAmbient.r * m + glow, cg = zoneAmbient.g * m + glow, cb = zoneAmbient.b * m + glow;
            float ex = p.x - eye.x, ey = p.y - eye.y, ez = p.z - eye.z;
            float pe2 = (ex * ex + ey * ey) + ez * ez;
            for (int i = 0; i < count; i++)
            {
                Exported L = lights[i];
                float dx = L.Position.x - p.x, dy = L.Position.y - p.y, dz = L.Position.z - p.z;
                float d2 = (dx * dx + dy * dy) + dz * dz;
                float dist = FogMap.FastSqrt(d2);
                float lx, ly, lz, angle;
                if (L.Radius < 0)
                {
                    angle = 1f;
                    lx = dist * n.x; ly = dist * n.y; lz = dist * n.z;
                }
                else
                {
                    lx = dx; ly = dy; lz = dz;
                    angle = ((dy * n.y + dz * n.z) + dx * n.x) / dist;
                }
                if (glossy) angle = (angle + 1f) * (angle + 1f) - 1.5f;
                angle = m * angle;
                if (angle < 0f) angle = 0f;
                if (specK > 0f)
                {
                    float s2 = (ly * n.y + lz * n.z) + lx * n.x;
                    s2 += s2;
                    float rx = lx - n.x * s2, ry = ly - n.y * s2, rz = lz - n.z * s2;
                    float rp = (rx * ex + rz * ez) + ry * ey;
                    if (rp > 0f)
                    {
                        float t = rp / (d2 * pe2) * rp - specC;
                        if (t > 0f) angle = specK * t + angle;
                    }
                }
                if (!(angle > 0f)) continue;
                float r = Math.Abs(L.Radius);
                float span = r - L.RadiusInner;
                float inv = 1f / (span > 1f ? span : 1f);
                float atten = (r - dist) * inv;
                if (!(1f > atten)) atten = 1f;
                float k = atten * angle;
                cr = k * L.R + cr; cg = k * L.G + cg; cb = k * L.B + cb;
                if (L.Dark)
                {
                    if (cr < 0f) cr = 0f;
                    if (cg < 0f) cg = 0f;
                    if (cb < 0f) cb = 0f;
                }
            }
            return (Saturate(cr), Saturate(cg), Saturate(cb));
        }

        // 0x10b02fc6: `cmp bits, 0x3f800000; jae` — unsigned: >= 1.0 AND every negative value give 1.0.
        static float Saturate(float v) => BitConverter.SingleToUInt32Bits(v) < 0x3f800000u ? v : 1f;

        // Per-actor specular coefficients (HP1ActorLight): C = (1 − Width·1.5/255)², K = 2·Glow/(1−C)²;
        // glossy marker (2·ScaleGlow == 1.4) — K = 6 (Render.dll.c:21178).
        public static (float k, float c) SpecularCoefficients(float scaleGlow, float specularGlow, int specularWidth)
        {
            float c = 1f - specularWidth * 0.0058823530562222f;
            c *= c;
            float k = scaleGlow * 2f == 1.4f ? 6f : specularGlow * 2f / ((1f - c) * (1f - c));
            return (k, c);
        }

        // FGetHSV has ONE body: SurfLightmap.FGetHSV.
        static (float r, float g, float b) FGetHSV(int h, int s, int b)
        {
            UEVector c = SurfLightmap.FGetHSV(h, s, b);
            return (c.x, c.y, c.z);
        }

        // ── ACTOR INPUTS AND VERTICES (C5) ──
        // Core computes character lighting entirely; the frontend only lays out arrays and draws. Checked
        // against shipped by se_harness/bsp_diff/validate_vertex_light.py.
        public struct ActorInputs
        {
            public (float r, float g, float b) Amb;
            public float Glow, K, C;
            public bool Glossy;
        }

        // zone ambient FGetHSV(ZoneAmbientHSB(Region.Zone)); specular and glossy marker — lit actors only.
        // AmbientGlow (Render 0x10b09a72..0x10b09acd): 255 pulses as
        //   (float)(appSin((double)(fild qword Viewport->CurrentTime · 2^-29)) · (double)0.2f + 0.25)
        // (constants 0x10b386cc = 2^-29 float, 0x10b386c0 = 0.2f as double, 0x10b386b0 = 0.25 double); CurrentTime is
        // FTime (seconds·2^32), so the argument is 8·seconds of the VIEWPORT (wall) clock — not paused, no TimeDilation;
        // its phase (seconds since machine start) is not observable. Otherwise AmbientGlow·(1/255) (0x10b3869c).
        // PRECISION: actor lighting runs inside the render window (PC24): fmul 2^-29 rounds the time to 24 bits, fstp
        // qword, appSin (fsin, correctly rounded at any PC, D-71) stays in the register, fmul qword 0.2f and fadd qword
        // 0.25 round to 24 bits, fstp dword (0x10b09aef). Judge: x87_size_oracle.py kind glow24 (SelfTest x87_size_corpus).
        internal static float Glow255(double viewportSeconds)
        {
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round24(v);
            // fild qword CurrentTime (seconds·2^32); fmul 2^-29: viewportSeconds·8 exactly, then the 24-bit rounding
            HP1.Core.X87Ext arg = R(HP1.Core.X87Ext.From(viewportSeconds * 8.0));
            HP1.Core.X87Ext s = HP1.Core.UEX87Transcendental.Fsin(arg);                 // appSin: fsin (D-71)
            return R(R(s * HP1.Core.X87Ext.From((double)0.2f)) + HP1.Core.X87Ext.From(0.25)).ToFloat();
        }

        public static ActorInputs Inputs(in ActorSnapshot a, (int h, int s, int b) zoneAmbientHsb, double viewportSeconds)
        {
            var j = new ActorInputs
            {
                Amb = FGetHSV(zoneAmbientHsb.h, zoneAmbientHsb.s, zoneAmbientHsb.b),
                Glow = a.AmbientGlow == 255 ? Glow255(viewportSeconds) : a.AmbientGlow * (1f / 255f),
            };
            if (!a.Unlit)
            {
                (j.K, j.C) = SpecularCoefficients(a.ScaleGlow, a.SpecularGlow, a.SpecularWidth);
                j.Glossy = a.ScaleGlow == 0.7f;
            }
            return j;
        }

        // Point normals — URender::DrawLodMesh (Render.dll.c:14670-14689, 14843-14850): for each face the
        // UNnormalised cross product (weighted by area) is added to its three points; then
        // N · rsqrt(|N|² + 0.001) via the fast-sqrt tables (0x10b41f0c/0x10b4bc1c, FUN_10b027d0(-0.5);
        // epsilon — .rdata 0x10b387c4 = 0.001f). Shipped works in frame coordinates — (P0−P1)×(P2−P0) with
        // det(GMath.ViewCoords) = −1 is the same normal as (P1−P0)×(P2−P0) in world space; also checked against
        // the culling test of the same body (front face — N·P·Mirror < 0, i.e. outward normal, as in the port —
        // Tut1, 139 actors). Not ported: bMeshCurvy (+0x198 bit 0x10 — normal = point − Location,
        // Render.dll.c:14826-14836; no carriers in the data: the name appears only in Engine.u, never written by its
        // bytecode) and face LOD collapse (iStack_f8) — the port has no skeletal mesh LOD, the full mesh is drawn.
        public static void PointNormals(UERawSkeletalMesh raw, UEVector[] pts, int np, UEVector[] N)
        {
            Array.Clear(N, 0, np);
            for (int t = 0; t < raw.numTris; t++)
            {
                int i0 = raw.wedgeVertex[raw.triWedge[t * 3]], i1 = raw.wedgeVertex[raw.triWedge[t * 3 + 1]], i2 = raw.wedgeVertex[raw.triWedge[t * 3 + 2]];
                UEVector p0 = pts[i0], p1 = pts[i1], p2 = pts[i2];
                float ax = p1.x - p0.x, ay = p1.y - p0.y, az = p1.z - p0.z, bx = p2.x - p0.x, by = p2.y - p0.y, bz = p2.z - p0.z;
                float nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
                N[i0] = new UEVector(N[i0].x + nx, N[i0].y + ny, N[i0].z + nz);
                N[i1] = new UEVector(N[i1].x + nx, N[i1].y + ny, N[i1].z + nz);
                N[i2] = new UEVector(N[i2].x + nx, N[i2].y + ny, N[i2].z + nz);
            }
            for (int i = 0; i < np; i++)
            {
                UEVector n = N[i];
                float k = SurfLightmap.GMathRsqrt(n.z * n.z + n.y * n.y + n.x * n.x + 0.001f);
                N[i] = new UEVector(k * n.x, k * n.y, k * n.z);
            }
        }

        // Mesh PF_Unlit — FUN_10b338f0 (Render.dll.c:39800-39804): bUnlit (+0x198 bit 1) OR Region.ZoneNumber
        // (+0xa8) == 0 OR Viewport->Actor->RendMap != 5 OR UClient.NoLighting (the last two — ViewLit).
        public static bool UnlitFlag(in ActorSnapshot a, bool viewLit) => a.Unlit || a.ZoneNumber == 0 || !viewLit;

        // GUnlitColor — URender::DrawMesh (Render.dll.c:9113-9131; DrawLodMesh the same, :14560-14584):
        // clamp(ScaleGlow·0.5 [0x10b385ec] + AmbientGlow·(1/256) [0x10b3865c], 0 [0x10b385bc], 1 [0x10b385b0]), grey;
        // no zone ambient or AmbientGlow 255 pulse here (editor tint 0x2000000 only under GIsEditor).
        public static float UnlitColor(in ActorSnapshot a)
        {
            float u = a.ScaleGlow * 0.5f + a.AmbientGlow * (1f / 256f);
            return u < 0f ? 0f : u > 1f ? 1f : u;
        }

        // Point RGB (3 floats per point). Unlit (UnlitFlag) — GUnlitColor for all points: FUN_10b0d3c0 under PF_Unlit
        // overwrites each vertex's light (Render.dll.c:22074-22083); D3DDrv multiplies the texel by it in gamma (MODULATE).
        public static void LightPoints(in ActorSnapshot a, in ActorInputs j, UEVector[] pts, UEVector[] N, int np,
            Exported[] lights, int count, UEVector eye, float[] L, bool unlit)
        {
            var amb = j.Amb;
            if (unlit)
            {
                float u = UnlitColor(a);
                for (int i = 0; i < np; i++) { L[i * 3] = u; L[i * 3 + 1] = u; L[i * 3 + 2] = u; }
                return;
            }
            for (int i = 0; i < np; i++)
            {
                var (r, g, b) = VertexLight(pts[i], N[i], eye, lights, count, amb, a.ScaleGlow, j.Glow, j.K, j.C, j.Glossy);
                L[i * 3] = r; L[i * 3 + 1] = g; L[i * 3 + 2] = b;
            }
        }

        // View-level SetupForActor gates (disasm 0x10b09bad-0x10b09bcc, Frame = [0x10b43314], *Frame = Viewport):
        // Viewport->Actor (+0x30) ->RendMap (+0x4e8) == 5 (REN_DynLight) and Viewport->Outer (+0x18, UClient) +0x48 == 0 —
        // UClient.NoLighting (UBoolProperty at offset 0x48, UClient::StaticConstructor Engine.dll.c:121941-121946;
        // Default.ini [WinDrv.WindowsClient] NoLighting=False). Otherwise no actor in the frame gets a list.
        // The original does not null-check Viewport->Actor (0x10b09bb4..0x10b09bb7), and UGameEngine::Draw reads its
        // Location before rendering (0x1039fab6..0x1039fac2): the original never has a world frame without a player;
        // throwing here is its loud equivalent. The LoadMap route (fade before world teardown, loading screen without
        // a world) is held by the mutation
        // loadmap_world_frame_without_viewport_actor (audit/completeness_mutations.txt).
        public static bool ViewLit(UELevelRuntime rt)
        {
            UEObject viewer = rt.LocalPlayer
                ?? throw new InvalidOperationException("actor light: no Viewport->Actor (LocalPlayer) — nothing to read the RendMap gate from");
            return UEValue.ToInt(viewer.GetProp(PK.RendMap)) == 5 && !UEViewportStartup.ClientNoLighting(rt.Config);
        }

    }
}
