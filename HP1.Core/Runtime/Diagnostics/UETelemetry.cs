// TARGETED PLAYTHROUGH TELEMETRY: AN OBSERVER, NOT A PARTICIPANT.
//
// WHY. A diagnostic run must map EVERY user-visible symptom to a concrete producer -> WorldFrame -> consumer
// chain: vertical placement of characters and beans, Weasley lighting, fullscreen colour, the lesson panel,
// Peeves' route. The route log (UEPlaythroughLog) answers "how far did it get"; this answers "what exactly was in
// Core state for THESE actors when it changed". Not the whole world and not every frame: only the listed
// families, and only on change.
//
// GUARANTEE. No writes into the simulation: the class only reads properties and writes lines. Disabled
// telemetry costs one bool check per actor per tick (UELevelRuntime.TickActorBody).
//
// HOW TO ENABLE. Environment variable HP1_TELEMETRY (headless). Value: comma-separated list of vertical,
// peeves, flash, light, blackboard, all. Extra actors by name: HP1_WATCH=a,b,c. Without the route log
// (UEPlaythroughLog.Enabled, opened only by the runtime_poc harness) telemetry writes nothing.
using System;
using System.Collections.Generic;
using System.Globalization;
using HP1.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public static class UETelemetry
    {
        [Flags]
        public enum Family
        {
            None = 0, Vertical = 1, Peeves = 2, Flash = 4, Light = 8, Blackboard = 16,
            All = Vertical | Peeves | Flash | Light | Blackboard,
        }

        // DECLARATION ORDER MATTERS: static initializers run top to bottom, and ParseEnv() writes into
        // _watchNames, so the lists must be created BEFORE it (otherwise TypeInitializationException with HP1_WATCH).
        static readonly List<string> _watchNames = new();
        static readonly Dictionary<int, bool> _watchCache = new();
        static Family _families = ParseEnv();

        public static bool Active => _families != Family.None;
        public static bool Has(Family f) => (_families & f) != 0;

        // Class families: substrings of the class name (case-insensitive). Tut1 "generic children" are
        // gen_fem_N / gen_male_N spawned by TriggerSpwnBsChrOnPPnt; beans are *JellyBean / *Bean.
        static readonly string[] ClassMatches =
        {
            "Dumbledore", "Fred", "George", "Ron", "Hermione", "gen_fem", "gen_male",
            "JellyBean", "CheckerBean", "StripeBean", "Peeves", "SpellBlackboard",
        };

        static Family ParseEnv()
        {
            string v = Environment.GetEnvironmentVariable("HP1_TELEMETRY");
            Family f = ParseFamilies(v);
            string w = Environment.GetEnvironmentVariable("HP1_WATCH");
            if (!string.IsNullOrEmpty(w))
                foreach (string n in w.Split(',')) if (n.Trim().Length > 0) _watchNames.Add(n.Trim());
            return f;
        }

        static Family ParseFamilies(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return Family.None;
            Family f = Family.None;
            foreach (string raw in v.Split(','))
            {
                string s = raw.Trim().ToLowerInvariant();
                switch (s)
                {
                    case "all": f |= Family.All; break;
                    case "vertical": f |= Family.Vertical; break;
                    case "peeves": f |= Family.Peeves; break;
                    case "flash": f |= Family.Flash; break;
                    case "light": f |= Family.Light; break;
                    case "blackboard": f |= Family.Blackboard; break;
                    case "": break;
                    default:
                        // Unknown family: reported loudly, not silently ignored.
                        UETrace.Log($"[telemetry] unknown family '{s}' (expected vertical,peeves,flash,light,blackboard,all)");
                        break;
                }
            }
            return f;
        }

        public static bool Watches(UEObject a)
        {
            if (a is null) return false;
            if (_watchCache.TryGetValue(a.ObjectId, out bool w)) return w;
            string cls = a.Class?.Name?.ToString() ?? "";
            string name = a.GetProp(PK.Name)?.ToString() ?? "";
            w = false;
            foreach (string m in ClassMatches)
                if (cls.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) { w = true; break; }
            if (!w)
                foreach (string n in _watchNames)
                    if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(n, cls, StringComparison.OrdinalIgnoreCase)) { w = true; break; }
            _watchCache[a.ObjectId] = w;
            return w;
        }

        // ── PER-ACTOR SNAPSHOTS ──────────────────────────────────────────────
        struct Prev
        {
            public string State, Latent, Base, MoveTarget;
            public int Physics;
            public float Z;
            public int LastTick;
        }
        static readonly Dictionary<int, Prev> _prev = new();

        static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => $"({F(v.x)},{F(v.y)},{F(v.z)})";
        static string P(object o)
        {
            UEPlane p = UEValue.AsPlane(o);
            return $"({F(p.X)},{F(p.Y)},{F(p.Z)},{F(p.W)})";
        }
        static string N(object o) => o is UEObject u ? (u.GetProp(PK.Name)?.ToString() ?? u.Class?.Name?.ToString() ?? "?") : (o?.ToString() ?? "None");

        // Called from UELevelRuntime.TickActorBody after each actor's tick.
        public static void AfterActorTick(UELevelRuntime rt, UEInterpreter interp, UEObject actor, float dt)
        {
            if (actor is null || actor.Destroyed) return;
            // Local player: the same object that drives the tick (UELevelRuntime.LocalPlayer);
            // interp.PlayerHarry may be unset in headless.
            UEObject player = rt?.LocalPlayer ?? interp?.PlayerHarry;
            bool isPlayer = player is not null && ReferenceEquals(actor, player);
            if (isPlayer && Has(Family.Flash)) FlashLine(rt, actor);
            if (!Watches(actor)) return;

            string cls = actor.Class?.Name?.ToString() ?? "?";
            string name = actor.GetProp(PK.Name)?.ToString() ?? "?";
            Vector3 loc = UEValue.AsVector3(actor.GetProp(PK.Location));
            string state = actor.CurrentStateName ?? "None";
            string latent = actor.StateFrame?.LatentState.ToString() ?? "-";
            int phys = UEValue.ToInt(actor.GetProp(PK.Physics));
            string @base = N(actor.GetProp(PK.Base));
            string moveTarget = N(actor.GetDeclared("Pawn", "MoveTarget"));

            _prev.TryGetValue(actor.ObjectId, out Prev pv);
            bool first = pv.LastTick == 0 && pv.State == null;
            bool changed = first || pv.State != state || pv.Latent != latent || pv.Physics != phys
                || pv.Base != @base || pv.MoveTarget != moveTarget || Math.Abs(pv.Z - loc.z) > 0.5f;
            if (!changed) return;
            _prev[actor.ObjectId] = new Prev
            {
                State = state, Latent = latent, Base = @base, MoveTarget = moveTarget,
                Physics = phys, Z = loc.z, LastTick = UEPlaythroughLog.Tick,
            };

            bool isPeeves = cls.IndexOf("Peeves", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isPeeves && Has(Family.Peeves))
            {
                UEPlaythroughLog.Always("peeves",
                    $"{name}({cls}) state={state} latent={latent} loc={V(loc)} rot={actor.GetProp(PK.Rotation)} "
                    + $"phys={phys} accel={V(UEValue.AsVector3(actor.GetProp(PK.Acceleration)))} "
                    + $"vel={V(UEValue.AsVector3(actor.GetProp(PK.Velocity)))} "
                    + $"moveTimer={F(UEValue.AsFloat(actor.GetProp(PK.MoveTimer)))} "
                    + $"navP={N(actor.GetProp(PK.navP))} destP={N(actor.GetProp(PK.destP))} "
                    + $"moveTarget={moveTarget} dest={V(UEValue.AsVector3(actor.GetProp(PK.Destination)))} "
                    + $"stationDest={actor.GetProp(PK.stationDestination)} firstPath={actor.GetProp(PK.firstPath)}");
            }
            if (Has(Family.Vertical) && actor.GetProp(PK.Mesh) is UEResourceRef mesh)
            {
                float hgt = UEValue.AsFloat(actor.GetProp(PK.CollisionHeight));
                Vector3 pre = UEValue.AsVector3(actor.GetProp(PK.PrePivot));
                float shift = 0f;
                try { shift = rt.MeshAlignShiftZOf(actor); } catch (Exception e) { shift = float.NaN; UETrace.Log($"[telemetry] MeshAlignShiftZOf({name}): {e.Message}"); }
                string floor = "n/a";
                if (rt.Bsp != null)
                {
                    BspHit hit = rt.Bsp.TraceWorldLine(loc, loc - new Vector3(0f, 0f, 4096f));
                    floor = hit.Blocked ? F(loc.z - 4096f * hit.Fraction) : "none";
                }
                UEPlaythroughLog.Always("vert",
                    $"{name}({cls}) mesh={mesh.Name} loc={V(loc)} phys={phys} colH={F(hgt)} "
                    + $"alignB={(UEValue.ToBool(actor.GetProp(PK.bAlignBottom)) ? 1 : 0)} "
                    + $"colW={(UEValue.ToBool(actor.GetProp(PK.bCollideWorld)) ? 1 : 0)} "
                    + $"ct={UEValue.ToInt(actor.GetProp(PK.CollideType))} pre={V(pre)} shift={F(shift)} "
                    + $"floorZ={floor} cylBottom={F(loc.z - hgt)} base={@base} state={state} latent={latent}");
            }
        }

        // ── FULLSCREEN COLOUR: EVERY TICK WHILE NOT IDENTITY ─────────────────
        //
        // Printed when any plane changes (string comparison) or every 30 ticks while the quad is not identity.
        static string _lastFlashKey;
        static int _lastFlashTick = int.MinValue;

        static void FlashLine(UELevelRuntime rt, UEObject player)
        {
            UEPlane ff = UEValue.AsPlane(player.GetProp(PK.FlashFog));
            bool identity = ff.X == 0f && ff.Y == 0f && ff.Z == 0f && ff.W >= 1f;
            string key = $"{P(ff)}{P(player.GetProp(PK.DesiredFlashFog))}{P(player.GetProp(PK.ConstantGlowFog))}"
                + $"{P(player.GetProp(PK.InstantFog))}{F(UEValue.AsFloat(player.GetProp(PK.FadeRate)))}";
            int tick = UEPlaythroughLog.Tick;
            if (key == _lastFlashKey && (identity || tick - _lastFlashTick < 30)) return;
            _lastFlashKey = key; _lastFlashTick = tick;
            object zone = (player.GetProp(PK.HeadRegion) as UEObject)?.GetProp(PK.Zone);
            string viewFog = zone is UEObject z ? P(z.GetProp(PK.ViewFog)) : "no-zone";
            UEPlaythroughLog.Always("flash",
                $"FlashFog={P(ff)} Desired={P(player.GetProp(PK.DesiredFlashFog))} "
                + $"Constant={P(player.GetProp(PK.ConstantGlowFog))} Instant={P(player.GetProp(PK.InstantFog))} "
                + $"FadeRate={F(UEValue.AsFloat(player.GetProp(PK.FadeRate)))} bNoFlash={(UEValue.ToBool(player.GetProp(PK.bNoFlash)) ? 1 : 0)} "
                + $"ViewFog={viewFog} identity={(identity ? 1 : 0)} "
                + $"state={player.CurrentStateName ?? "None"}");
        }
    }
}
