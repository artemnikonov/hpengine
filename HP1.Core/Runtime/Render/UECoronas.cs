// LIGHT CORONAS — the corona block of URender::DrawFrame (Render.dll 1.53), owned by Core.
//
// Source: disassembly of Render.dll 0x10b26742..0x10b26e0f (DrawFrame tail, decompile Render.dll.c:1582-1803) and
// FUN_10b26f10 (0x10b26f10..0x10b27045), constants read from .rdata:
//   0x10b386ac = 2^-32 (FTime -> seconds), 0x10b385b4 = 3.0, 0x10b385bc = 0.0, 0x10b385b0 = 1.0,
//   0x10b38fd8 = 1/85, 0x10b38fd4 = 1/84, 0x10b3869c = 1/255, 0x10b38fd0 = 0.8, 0x10b385ec = 0.5.
// Field offsets (audit/prop_offsets.py over the Light class dump): +0x28 bit 0x200 bDeleteMe, +0x1f0 bit 4 bCorona,
// +0x160 Skin, +0xfc Location, +0x16c DrawScale, +0x1e3 LightHue, +0x1e4 LightSaturation, +0xa0/+0xa4 Region.Zone/iLeaf,
// +0x80 XLevel. RenDev+0x60 = URenderDevice.Coronas (Engine.dll.c StaticConstructor of URenderDevice, offset 0x60).
//
// Gate (0x10b26742..0x10b26779): Viewport->Actor->Region.iLeaf != -1, Region.Zone != NULL, RenDev->Coronas,
// Frame->Recursion == 0 (only the master frame; the block sits in every DrawFrame call).
// Table: FMemCache item 0x27, 0x180 bytes = 32 slots of {AActor*, actor index, float brightness}, zeroed on creation.
// Clock (0x10b2683d..0x10b269c4): FTime of the previous run (statics 0x10b6ff98/9c, seeded by the first run);
// dec = float(float(now - prev) * 2^-32 * 3); the time is re-read and stored as prev.
// Decay (0x10b269c9..0x10b269f6): every used slot: b -= dec; b < 0 -> slot.actor = 0 (index/brightness stay).
// Candidates: static lights of the viewer leaf (Model->Leaves[iLeaf].iPermeating -> Model->Lights, 0-terminated;
// 0x10b269f8..0x10b26a53), then URender::LeafLights[iLeaf] (0x10b26a58..0x10b26a8a). The dynamic list is EMPTY at
// this point in 1.53: URender::OccludeFrame clears LeafLights[DynLightLeaves[i]] for i < NumDynLightLeaves
// (0x10b25472..0x10b2548f) right after OccludeBsp, before child frames, and DrawWorld calls OccludeFrame(master)
// (0x10b27c7b) then DrawFrame(master) (0x10b27c83); the only writer of LeafLights is the dynamic light filter
// FUN_10b31c70 (0x10b31cfc..0x10b321b3), which registers every leaf it fills in DynLightLeaves. So the port has no
// dynamic candidate pass.
// FUN_10b26f10(Frame, table, &next, actor, dec): bCorona && Skin && !bDeleteMe && XLevel->SingleLineCheck(Hit, NULL,
// End = actor.Location, Start = Frame->Coords.Origin, TraceFlags 6, Extent 0) clear; then: actor already in a slot ->
// b = min(dec + dec + b, 1); else the first free slot from `next` (next is 0 at the start of each run and is advanced
// past used slots, never past the filled one) gets {actor, GetActorIndex(actor), min(dec + dec, 1)}; no free slot ->
// the lamp is dropped.
// Draw loop (0x10b26a8c..0x10b26e07), slots 0..31: index <= Level->Actors.Num && Actors[index] == actor; the
// frontend projects (Coords, Proj.Z / Z + FX2/FY2, Z > 1) and draws UCanvas::DrawIcon (vtable +0x64) of Skin at
// center - size*0.5, size = Frame->X * DrawScale * 0.8, Z = 1, PolyFlags 0x104, colour = HueSat(LightHue,
// LightSaturation) * brightness (thunk_FUN_10b2aab0 0x10b2aab0). All arithmetic sits in the render PC24 window
// (URender::PreRender..PostRender) — every x87 result is a float, as below.
//
// NOT ORIGINAL, decided: improvement (author 2026-11-03, audit/DEFERRED_DEBT_2026-10-04.md "Corona fade clock";
// distance cut-off = leaf permeation + line check, both as shipped: tools/swarc_oracle/RENDER_MEASURE.md): the clock is the Core viewport clock (sum of the engine frame deltas Core receives,
// UEWorldFrameProducer.ViewportDelta); shipped reads wall time (appSeconds/rdtsc FTime) at each run of the block.
// The GCache item can be flushed in shipped (FMemCache flush re-zeroes the table); the port keeps the table for the
// process lifetime, across levels, like an unflushed cache item.
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    public sealed class UECoronas
    {
        public const int Slots = 32;   // 0x180 / 0xc
        struct Slot { public UEObject Actor; public int Index; public float Brightness; }
        readonly Slot[] _table = new Slot[Slots];
        bool _seeded;                  // DAT_10b67500 & 1
        double _prev;                  // DAT_10b6ff98/9c

        public readonly List<CoronaInstance> Draws = new();
        // Branch counters of the last runs (cumulative; the fixture reads them).
        public int Runs, Gated, RecursionSkipped, Inserted, Brightened, Dropped, Rejected, Blocked, Cleared, StaleIndex;

        public UEObject SlotActor(int i) => _table[i].Actor;
        public float SlotBrightness(int i) => _table[i].Brightness;
        public int SlotIndex(int i) => _table[i].Index;

        // Render.dll HueSat block 0x10b26ba1..0x10b26d2b.
        public static (float r, float g, float b) HueSat(int hue, int sat)
        {
            const float k85 = 0.0117647061124444f;     // 0x10b38fd8
            const float k84 = 0.011904762126505375f;   // 0x10b38fd4
            float r, g, b;
            if (hue < 0x56) { r = (0x55 - hue) * k85; g = hue * k85; b = 0f; }
            else if (hue < 0xab) { r = 0f; g = (0xaa - hue) * k85; b = (hue - 0x55) * k85; }
            else { r = (hue - 0xaa) * k85; g = 0f; b = (0xff - hue) * k84; }
            float f = sat * 0.003921568859368563f;     // 0x10b3869c
            return ((1f - r) * f + r, (1f - g) * f + g, (1f - b) * f + b);
        }

        // One DrawFrame(frame) corona block. viewer — Viewport->Actor; levelActors — Level->Actors (Core compacts it like
        // ULevel::CompactActors); level — the viewer level model (LeafPermeating / LightExports = Model->Leaves / Lights as
        // export indices); byExport — export index -> actor; now — Core viewport clock, seconds; lineClear(start, end) —
        // SingleLineCheck(TraceFlags 6, Extent 0) found nothing.
        public void Step(UESceneNode frame, UEObject viewer, IList<UEObject> levelActors, bool deviceCoronas,
            StaticGeometry level, IReadOnlyDictionary<int, UEObject> byExport, double now,
            Func<UEVector, UEVector, bool> lineClear)
        {
            if (frame.Recursion != 0) { RecursionSkipped++; return; }
            Draws.Clear();
            UEObject region = viewer?.GetProp(PK.Region) as UEObject;
            int leaf = region is null ? -1 : UEValue.ToInt(region.GetProp(PK.iLeaf));
            if (leaf == -1 || region.GetProp(PK.Zone) is null || !deviceCoronas) { Gated++; return; }
            Runs++;
            if (!_seeded) { _seeded = true; _prev = now; }
            float dec = (float)(now - _prev) * 3f;   // fild FTime delta; fmul 2^-32; fmul 3.0 (PC24)
            _prev = now;
            for (int i = 0; i < Slots; i++)
            {
                if (_table[i].Actor is null) continue;
                float b = _table[i].Brightness - dec;
                _table[i].Brightness = b;
                if (b < 0f) { _table[i].Actor = null; Cleared++; }   // 0x10b385bc = 0.0
            }

            if (level?.LeafPermeating is null || level.LightExports is null)
                throw new InvalidOperationException("DrawFrame coronas: level model leaf light lists are missing");
            if (leaf < 0 || leaf >= level.LeafPermeating.Length)
                throw new InvalidOperationException($"DrawFrame coronas: viewer iLeaf {leaf} outside Model->Leaves ({level.LeafPermeating.Length})");
            int next = 0;
            int start = level.LeafPermeating[leaf];
            if (start != -1)
                for (int k = start; level.LightExports[k] != 0; k++)
                {
                    if (!byExport.TryGetValue(level.LightExports[k], out UEObject lamp))
                        throw new InvalidOperationException($"DrawFrame coronas: Model->Lights export {level.LightExports[k]} is not a level actor");
                    Candidate(frame, lamp, levelActors, ref next, dec, lineClear);
                }

            for (int i = 0; i < Slots; i++)
            {
                UEObject a = _table[i].Actor;
                if (a is null) continue;
                int idx = _table[i].Index;
                if (idx > levelActors.Count) { StaleIndex++; continue; }
                if (idx == levelActors.Count)
                    throw new NotSupportedException($"DrawFrame coronas: slot index {idx} == Actors.Num — shipped reads the TArray slack Actors.Data[Num], not ported");
                if (!ReferenceEquals(levelActors[idx], a)) { StaleIndex++; continue; }
                var (hr, hg, hb) = HueSat(UEValue.ToInt(a.GetProp(PK.LightHue)), UEValue.ToInt(a.GetProp(PK.LightSaturation)));
                float br = _table[i].Brightness;
                UEVector loc = UEValue.AsVector3(a.GetProp(PK.Location));
                float ds = UEValue.AsFloat(a.GetProp(PK.DrawScale));
                bool on = Project(frame, loc, ds, out float x0, out float y0, out float x1, out float y1);
                if (!on) Behind++;
                Draws.Add(new CoronaInstance(a.ObjectId, WorldFrameProjector.AssetPath(a.GetProp(PK.Skin)), loc, ds,
                    br * hr, br * hg, br * hb, on, x0, y0, x1, y1, frame.X));
            }
        }

        // The DrawIcon tile (DrawFrame, Render.dll 0x10b26b03..0x10b26d71): V = (Location − Coords.Origin) in frame axes
        // (X right, Y down, Z forward); drawn only when V.Z > 1 (fcomp 0x10b385b0); centre = V.XY·(Proj.Z (+0xdc) / V.Z) +
        // (FX2 +0xc8, FY2 +0xcc); size = Frame->X·DrawScale·0.8 (fild Frame->X; fmul DrawScale; fmul 0x10b38fd0), the tile
        // centred (0x10b385ec = 0.5). Moved from the frontend CoronaBuilder (one owner of the projection).
        public int Behind;
        public static bool Project(UESceneNode f, UEVector p, float drawScale, out float x0, out float y0, out float x1, out float y1)
        {
            x0 = y0 = x1 = y1 = 0f;
            BrushCoords c = f.Coords;
            UEVector rel = p - c.Origin;
            float z = UEVector.Dot(rel, c.Z);
            if (!(z > 1f)) return false;
            float k = f.ProjZ / z;
            float sx = UEVector.Dot(rel, c.X) * k + f.FX2;
            float sy = UEVector.Dot(rel, c.Y) * k + f.FY2;
            float size = f.X * drawScale * 0.8f;
            x0 = sx - size * 0.5f; y0 = sy - size * 0.5f; x1 = x0 + size; y1 = y0 + size;
            return true;
        }

        // FUN_10b26f10.
        void Candidate(UESceneNode frame, UEObject a, IList<UEObject> levelActors, ref int next, float dec,
            Func<UEVector, UEVector, bool> lineClear)
        {
            if (!UEValue.ToBool(a.GetProp(PK.bCorona)) || a.GetProp(PK.Skin) is null || a.Destroyed) { Rejected++; return; }
            if (!lineClear(frame.Coords.Origin, UEValue.AsVector3(a.GetProp(PK.Location)))) { Blocked++; return; }
            for (int i = 0; i < Slots; i++)
                if (ReferenceEquals(_table[i].Actor, a))
                {
                    float v = dec + dec + _table[i].Brightness;
                    _table[i].Brightness = 1f > v ? v : 1f;   // fcomp; test ah,0x41: only 1 > v keeps v
                    Brightened++;
                    return;
                }
            while (next < Slots && _table[next].Actor is not null) next++;
            if (next >= Slots) { Dropped++; return; }
            int index = levelActors.IndexOf(a);   // ULevel::GetActorIndex: first match
            if (index < 0) throw new InvalidOperationException($"ULevel::GetActorIndex: actor {a.GetProp(PK.Name)} not found");
            float s = dec + dec;
            _table[next] = new Slot { Actor = a, Index = index, Brightness = 1f > s ? s : 1f };
            Inserted++;
        }
    }
}
