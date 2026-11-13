// Frame occlusion world (UEOcclusion — URender::OccludeBsp, D-56) from the Core world: live level model (map nodes
// and tracker mover-brush fragments), zone actors Model.Zones[z].ZoneActor, LevelInfo, viewer flags, device flags
// (SpanBased, ShinySurfaces — from the GameRenderDevice config, as for translation), the surface texture flag word
// and the warp-zone Generate event.
//
// Generate (Render.dll 1.53, URender::OccludeBsp, PF_Portal branch, disasm 0x10b23e17..0x10b23eb2): for a warp zone
// behind a visible portal with OtherSideLevel (+0x3fc) == None or OtherSideActor (+0x3f8) == None —
// ProcessEvent(FindFunctionChecked(ENGINE_Generate)), then the check repeats. Bytecode (Engine.u, --dumpfunc
// WarpZoneInfo): Generate — `if (OtherSideLevel != None) return; ForceGenerate();`; ForceGenerate — with "/" in
// OtherSideURL both None, else OtherSideLevel = XLevel and foreach AllActors(WarpZoneInfo) -> OtherSideActor with
// ThisTag == OtherSideURL. Both fields are transient: after loading a save (no BeginPlay) they are None, and the
// renderer is the first to fill them again on seeing the portal.
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        public int WarpGenerateCalls;   // count of Generate events from the renderer (probe/test)
        readonly UEOcclusionWorld _occlusionWorld = new();
        readonly Dictionary<int, int> _surfTexFlags = new();
        HeadlessBspModel _surfTexModel;

        // Coplanar chain pieces of the current level model (CoplanarOcclusion: the span-buffer outcome of a node chain, with
        // Core's surface | texture flags); computed once per world. The frontend draws them instead of whole polygons.
        Dictionary<int, List<HP1.UEScript.UEVector[]>> _coplanar, _coplanarBack, _coplanarNearBack; StaticGeometry _coplanarFor;
        public IReadOnlyDictionary<int, List<HP1.UEScript.UEVector[]>> LevelCoplanarPieces
        {
            get
            {
                StaticGeometry g = LatestWorldStatic?.Geometry;
                if (g is null) return null;
                if (!ReferenceEquals(g, _coplanarFor))
                {
                    _coplanarBack = new Dictionary<int, List<HP1.UEScript.UEVector[]>>();
                    _coplanarNearBack = new Dictionary<int, List<HP1.UEScript.UEVector[]>>();
                    _coplanar = CoplanarOcclusion.Compute(g, s => g.Surfaces[s].PolyFlags | SurfTexturePolyFlags(s), new CoplanarOcclusion.Stats(), _coplanarBack, _coplanarNearBack);
                    _coplanarFor = g;
                }
                return _coplanar;
            }
        }

        // The same cut for a viewer behind a node OccludeBsp draws from behind (two-sided or portal): the frontend's back runs.
        public IReadOnlyDictionary<int, List<HP1.UEScript.UEVector[]>> LevelCoplanarBackPieces
            => LevelCoplanarPieces is null ? null : _coplanarBack;

        // A one-sided node seen from behind with the eye within 1 unit of its plane (CoplanarOcclusion nearBack).
        public IReadOnlyDictionary<int, List<HP1.UEScript.UEVector[]>> LevelCoplanarNearBackPieces
            => LevelCoplanarPieces is null ? null : _coplanarNearBack;

        // Surf->Texture->PolyFlags (+0x80) of a level or tracker surface; no texture — 0.
        internal int SurfTexturePolyFlags(int surf)
        {
            HeadlessBspModel bsp = Bsp;
            if (!ReferenceEquals(bsp, _surfTexModel)) { _surfTexModel = bsp; _surfTexFlags.Clear(); }
            if (_surfTexFlags.TryGetValue(surf, out int f)) return f;
            SurfMaterialRef m = bsp.MaterialOfSurf(surf);
            f = m is null ? 0 : (TextureFacts.Read(m)?.PolyFlags ?? 0);
            _surfTexFlags[surf] = f;
            return f;
        }

        // interp — executor of the Generate event; null is allowed while no warp zone needs Generate.
        internal UEOcclusionWorld OcclusionWorld(UEInterpreter interp, UEObject player)
        {
            UEOcclusionWorld w = _occlusionWorld;
            HeadlessBspModel bsp = Bsp;
            w.Bsp = bsp;
            w.ZoneActor = z =>
            {
                int[] za = bsp?.RawZoneActors;
                return za is not null && z >= 0 && z < za.Length && za[z] > 0 && ActorsByExport.TryGetValue(za[z], out UEObject a) ? a : null;
            };
            // Level->Actors(0), shipped assert IsA ALevelInfo (UnLevel.h 0xf7/0xf8).
            UEObject li = Actors.Count > 0 ? Actors[0] : null;
            w.LevelInfo = li is not null && UEInterpreter.ClassIsA(li.Class, "LevelInfo") ? li : null;
            w.Level = interp?.XLevel;
            w.ShowFlags = UEValue.ToInt(player.GetProp(PK.ShowFlags));
            w.RendMap = UEValue.ToInt(player.GetProp(PK.RendMap));
            w.SpanBased = ReadSpanBased;
            w.Shiny = ReadShinySurfaces;
            // Surf->Texture->PolyFlags (+0x80) — the surface texture word (for the tracker, the brush FPoly texture).
            w.TexturePolyFlags = SurfTexturePolyFlags;
            w.SourceOf = surf =>
            {
                if (bsp is null || !bsp.SurfIsDynamic(surf)) return new ViewSource(surf, 0, 0);
                if (_trackerSurfShared.Contains(surf))
                    throw new NotSupportedException($"tracker surface {surf}: brush shared by several movers (FPoly.iLink overwritten) — the child frame's mover is undefined");
                if (!_trackerSurfOwner.TryGetValue(surf, out (UEObject Mover, int Poly) o))
                    throw new InvalidOperationException($"tracker surface {surf} without an owning mover");
                return new ViewSource(-1, o.Mover.ObjectId, o.Poly);
            };
            // A drawn tracker fragment names its own mover (FMovingBrushTracker node owner), so a brush shared by several
            // movers (FPoly.iLink overwritten, SourceOf refuses) still names the right one; the FPoly number is the brush's.
            w.PolyOf = (surf, node) =>
            {
                if (bsp is null || !bsp.SurfIsDynamic(surf)) return new ViewSource(surf, 0, 0);
                if (bsp.Tracker.NodeOwner(node) is not UEObject mover || !_trackerSurfOwner.TryGetValue(surf, out (UEObject Mover, int Poly) o))
                    throw new InvalidOperationException($"tracker node {node} (surface {surf}) without an owning mover");
                return new ViewSource(-1, mover.ObjectId, o.Poly);
            };
            w.Generate = za =>
            {
                if (interp is null) throw new InvalidOperationException($"Generate: warp zone {za.GetProp(PK.Name)} needs the other side, but the event executor was not passed");
                WarpGenerateCalls++;
                UELifecycle.CallEvent(interp, za, "Generate");
            };
            w.TimeSeconds = w.LevelInfo is UEObject lv ? UEValue.AsFloat(lv.GetProp(PK.TimeSeconds))
                : throw new InvalidOperationException("OccludeBsp: assert Actors(0)->IsA(ALevelInfo) — Level->Actors[0] is not a LevelInfo");
            return w;
        }

        // URenderDevice +0x48 (SpanBased): set by the driver — SoftDrv Init (SoftDrv.dll.c:31218: 1), D3DDrv
        // StaticConstructor (D3DDrv.dll.c:278: 0). Device = GameRenderDevice of the config.
        internal bool ReadSpanBased()
        {
            string device = Config?.ResolveSection(UEConfig.ScopeSystem, RenderDeviceRef);
            return device switch
            {
                "SoftDrv.SoftwareRenderDevice" => true,
                "D3DDrv.D3DRenderDevice" => false,
                _ => throw new System.IO.InvalidDataException($"RenDev.SpanBased: device flags of '{device}' not read from its DLL"),
            };
        }

        // RenDev.ShinySurfaces (URenderDevice +0x5c) of the GameRenderDevice — same GConfig order as
        // ReadHighDetailActors (overlay, then shipped ini). Missing key — loud failure.
        internal bool ReadShinySurfaces()
        {
            string device = Config?.ResolveSection(UEConfig.ScopeSystem, RenderDeviceRef);
            if (string.IsNullOrEmpty(device) || device == RenderDeviceRef)
                throw new System.IO.InvalidDataException("RenDev.ShinySurfaces: the configuration does not name GameRenderDevice");
            string raw = Config.Get(UEConfig.ScopeSystem, device, "ShinySurfaces");
            if (string.IsNullOrEmpty(raw))
                throw new System.IO.InvalidDataException($"RenDev.ShinySurfaces: the device section '{device}' has no ShinySurfaces key");
            return string.Equals(raw.Trim(), "True", StringComparison.OrdinalIgnoreCase);
        }
    }
}
