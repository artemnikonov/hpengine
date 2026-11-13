using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HP1.UEScript;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.Core
{
    // FULL LEVEL SNAPSHOT AT LOAD.
    //
    // The route log only answers "how far did the run get"; it says nothing about lighting, materials,
    // placement or resources, so visual divergences (dark bridge, characters above the floor) had nothing to
    // read from.
    //
    // WHY A SNAPSHOT, NOT A STREAM. Churn measurement (--probe-churn, 300 ticks): of 1218 Tut1 actors 64 change
    // per frame, of 2149 Tut1b actors 34. Those are `AnimFrame` (33.6 and 12.0 per frame) and `Rotation`
    // (30.7 and 11.6); everything else together is under three writes per frame. Placement, lights, collision,
    // meshes and flags are level STATICS: one capture costs ~400 KB and nothing per frame.
    //
    // ONE BODY FOR ALL FRONTENDS (C7): a second copy would drift, and a diagnostics divergence reads as a port
    // divergence.
    public static class UELevelSnapshot
    {
        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => $"{F(v.x)},{F(v.y)},{F(v.z)}";

        static string S(UEObject a, string prop)
        {
            object v = a.GetProp(prop);
            if (v is null) return "-";
            string s = v is UEObject o ? (o.GetProp(PK.Name)?.ToString() ?? o.Class?.Name?.ToString())
                                       : v.ToString();
            if (string.IsNullOrEmpty(s) || s == "None") return "-";
            return s.Replace(' ', '_');
        }

        static float Fl(UEObject a, string prop) => UEInterpreter.AsFloat(a.GetProp(prop));
        static int In(UEObject a, string prop) => UEInterpreter.ToInt(a.GetProp(prop));
        static bool Bo(UEObject a, string prop) => UEInterpreter.ToBool(a.GetProp(prop));
        static string B(bool v) => v ? "1" : "0";

        // ── THE LOG MAY OPEN LATER THAN THE WORLD IS BUILT ──────────────────
        //
        // A frontend may open the log after the actor graph is built (for example after all
        // frontend initialisation). With the log closed `Emit` would return, and the first map's initial snapshot would be lost while
        // later travel snapshots worked, so the loss would be easy to miss.
        //
        // WHY A LATCH, NOT A SECOND FRONTEND CALL. A second call is a second owner of the capture moment and
        // would drift silently. Here `Emit` stores the request and the log takes it when it opens, so frontend
        // init order decides nothing.
        static IReadOnlyList<UEObject> _pendingActors;
        static string _pendingMap;

        // Called by the log RIGHT AFTER it opens. The snapshot is marked late: it is taken when the log opens,
        // not when the world was built, so it is not a pure "state at load".
        public static void FlushPending()
        {
            if (_pendingActors is null) return;
            IReadOnlyList<UEObject> actors = _pendingActors;
            string map = _pendingMap;
            _pendingActors = null;
            _pendingMap = null;
            // The log's map column is global and the caller sets it AFTER Open. The snapshot knows its own map;
            // otherwise its rows would carry a foreign column and not be found by map name.
            //
            // THE ASSIGNMENT IS UNCONDITIONAL ON PURPOSE. Both earlier conditions were wrong, differently:
            //
            //   1. `IsNullOrEmpty` never fired: the field is initialised
            //      not to an empty string but to `UEPlaythroughLog.UnknownMap`,
            //      (i.e. to `UnknownMap`, not empty), so the snapshot got the "?" column;
            //   2. comparing against `UnknownMap` fixed only the first
            //      process start. `Map` is a static field, and `Close` did not
            //      reset it, so a later session in the same process found the PREVIOUS map's name there and filed
            //      the new session's initial snapshot under it.
            //
            // No condition is needed: `FlushPending` is called from INSIDE `Open`, strictly BEFORE the caller
            // names this session's map (callers set `Map` after `Open`). Whatever the field holds now is stale by
            // definition, and the snapshot knows its own map.
            //
            // WHY NOT RESET IN `Close`. `Open` calls `Close` first, so such a reset would mask the defect. It adds nothing observable: after `Close` the log is
            // off and nobody but this place reads `Map`.
            UEPlaythroughLog.Map = map;
            UEPlaythroughLog.AlwaysDistinct("snap",
                $"LATE snapshot of map {map}: the log opened AFTER the world was built,"
                + " taken at the moment of opening");
            EmitNow(actors, map);
        }

        // Call AFTER the world is built and states are assigned; otherwise every `state` is empty and the
        // snapshot misreports auto-states.
        public static void Emit(IReadOnlyList<UEObject> actors, string map)
        {
            if (actors is null) return;
            if (!UEPlaythroughLog.Enabled)
            {
                _pendingActors = actors;
                _pendingMap = map;
                return;
            }
            EmitNow(actors, map);
        }

        static void EmitNow(IReadOnlyList<UEObject> actors, string map)
        {

            int nMover = 0, nLight = 0, nPawn = 0, nHidden = 0, nNoMesh = 0;
            var byClass = new Dictionary<string, int>(UEPropNameComparer.Instance);

            foreach (UEObject a in actors)
            {
                if (a is null || a.Destroyed || a.Class is null) continue;
                string cls = a.Class.Name?.ToString() ?? "?";
                byClass.TryGetValue(cls, out int c); byClass[cls] = c + 1;

                bool isMover = UEInterpreter.ClassIsA(a.Class, "Mover");
                bool isLight = UEInterpreter.ClassIsA(a.Class, "Light");
                bool isPawn = UEInterpreter.ClassIsA(a.Class, "Pawn");
                if (isMover) nMover++;
                if (isLight) nLight++;
                if (isPawn) nPawn++;
                if (Bo(a, "bHidden")) nHidden++;

                string name = S(a, "Name");
                var sb = new StringBuilder(256);
                sb.Append("actor ").Append(name).Append(' ').Append(cls)
                  .Append(" loc=").Append(V(UEInterpreter.AsVector3(a.GetProp(PK.Location))));

                object rotObj = a.GetProp(PK.Rotation);
                if (rotObj is not null) sb.Append(" rot=").Append(rotObj);

                sb.Append(" phys=").Append(In(a, "Physics"))
                  .Append(" rad=").Append(F(Fl(a, "CollisionRadius")))
                  .Append(" hgt=").Append(F(Fl(a, "CollisionHeight")))
                  .Append(" drawtype=").Append(In(a, "DrawType"))
                  .Append(" style=").Append(In(a, "Style"))
                  .Append(" scale=").Append(F(Fl(a, "DrawScale")));

                string mesh = S(a, "Mesh");
                if (mesh == "-" && In(a, "DrawType") == 2) nNoMesh++;
                sb.Append(" mesh=").Append(mesh)
                  .Append(" tex=").Append(S(a, "Texture"))
                  .Append(" skin=").Append(S(a, "Skin"))
                  .Append(" unlit=").Append(B(Bo(a, "bUnlit")))
                  .Append(" hidden=").Append(B(Bo(a, "bHidden")))
                  .Append(" glow=").Append(In(a, "AmbientGlow"))
                  .Append(" fatness=").Append(In(a, "Fatness"))
                  .Append(" colact=").Append(B(Bo(a, "bCollideActors")))
                  .Append(" colworld=").Append(B(Bo(a, "bCollideWorld")))
                  .Append(" blockact=").Append(B(Bo(a, "bBlockActors")))
                  .Append(" blockplay=").Append(B(Bo(a, "bBlockPlayers")))
                  .Append(" state=").Append(string.IsNullOrEmpty(a.CurrentStateName)
                        ? "-" : a.CurrentStateName)
                  .Append(" tag=").Append(S(a, "Tag"))
                  .Append(" event=").Append(S(a, "Event"))
                  .Append(" base=").Append(S(a, "Base"))
                  .Append(" owner=").Append(S(a, "Owner"));

                // LIGHTS WITH THEIR OWN FIELDS. Without them "dark bridge" cannot be told apart from "light exists
                // but does not reach the surface": a source may have zero brightness, zero radius or no bSpecialLit.
                if (isLight)
                    sb.Append(" LightType=").Append(In(a, "LightType"))
                      .Append(" LightEffect=").Append(In(a, "LightEffect"))
                      .Append(" bright=").Append(In(a, "LightBrightness"))
                      .Append(" hue=").Append(In(a, "LightHue"))
                      .Append(" sat=").Append(In(a, "LightSaturation"))
                      .Append(" radius=").Append(In(a, "LightRadius"))
                      .Append(" period=").Append(In(a, "LightPeriod"))
                      .Append(" dyn=").Append(B(Bo(a, "bDynamicLight")))
                      .Append(" special=").Append(B(Bo(a, "bSpecialLit")))
                      .Append(" corona=").Append(B(Bo(a, "bCorona")));

                // MOVER KEYS AND TIMES. "The column did not go down" can only be checked against its own keys:
                // without them it is unknown where it was supposed to move at all.
                if (isMover)
                    sb.Append(" keys=").Append(In(a, "NumKeys"))
                      .Append(" movetime=").Append(F(Fl(a, "MoveTime")))
                      .Append(" stayopen=").Append(F(Fl(a, "StayOpenTime")))
                      .Append(" movertype=").Append(In(a, "MoverEncroachType"))
                      .Append(" glide=").Append(In(a, "MoverGlideType"))
                      .Append(" key=").Append(In(a, "KeyNum"))
                      .Append(" basepos=").Append(V(UEInterpreter.AsVector3(a.GetProp(PK.BasePos))))
                      .Append(" trigger=").Append(In(a, "BumpType"));

                UEPlaythroughLog.AlwaysDistinct("snap", sb.ToString());
            }

            // THE SUMMARY IS PRINTED AFTER, NOT INSTEAD. It catches a whole class going missing, but cannot
            // replace the per-actor rows: "bridges 1" says nothing about whether it is lit.
            var top = new List<KeyValuePair<string, int>>(byClass);
            top.Sort((x, y) => y.Value.CompareTo(x.Value));
            var counts = new StringBuilder();
            for (int i = 0; i < top.Count && i < 30; i++)
                counts.Append(' ').Append(top[i].Key).Append('=').Append(top[i].Value);
            UEPlaythroughLog.AlwaysDistinct("snap",
                $"total map={map} actors={actors.Count} movers={nMover} lights={nLight}"
                + $" pawns={nPawn} hidden={nHidden} classes={byClass.Count}");
            UEPlaythroughLog.AlwaysDistinct("snap", "classes:" + counts);
            if (nNoMesh > 0)
                UEPlaythroughLog.AlwaysDistinct("snap",
                    $"WARNING: actors with DrawType=Mesh and NO mesh: {nNoMesh}");
        }
    }
}
