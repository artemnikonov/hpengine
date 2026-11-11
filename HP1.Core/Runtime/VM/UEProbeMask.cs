using System;
using System.Collections.Generic;
using UELib.Core;

namespace HP1.UEScript
{
    // PROBE MASK — the shipped mechanism by which the engine decides whether
    // to deliver an event to an object at all.
    //
    // Where it lives in the original:
    //   * UObject::ProcessEvent (Core.dll.c:60785) — the CENTRAL gate: every
    //     event dispatch goes through `IsProbing(this, Function->Name)`.
    //     IsProbing calls in AActor::Tick (128551), physWalking, processHitWall
    //     etc. are only early-outs to avoid a pointless function lookup.
    //   * UObject::IsProbing (Core.dll.c:40251) — true if the name is OUTSIDE
    //     the FName range 0xc5..0x104, OR there is no state frame; otherwise bit
    //     (name − 0xc5) of the frame mask.
    //   * UObject::InitExecution (Core.dll.c:45745-45746) — a fresh state frame
    //     gets an ALL-ONES mask.
    //   * UObject::GotoState (Core.dll.c:58352-58360) — the frame mask is
    //     recomputed: (Class.ProbeMask | State.ProbeMask) & State.IgnoreMask.
    //     If the state is not found (including GotoState('Auto') on a class with
    //     no auto state), the CLASS ITSELF becomes the "state".
    //   * execDisable/execEnable (Core.dll.c) — clear and restore ONE bit of the
    //     same mask; for names outside the range they do NOTHING.
    //
    // Without it UnrealScript `ignores` is not modelled at all
    // (Engine.Inventory.Sleeping has no stub Touch function, only IgnoreMask):
    // a sleeping item would get Touch and a dying pawn Bump and Trigger, which
    // the original never delivers.
    //
    // NAME ORDER. Bit i is FName 0xc5+i of the Core.dll hardcoded name table:
    // FName::StaticInit (0x1015786b..0x10157892) registers the static FNameEntry
    // array at 0x101b4230 (stride 0x8c, name at +0xc) with Index = array position,
    // 0x105 entries. The list below equals entries 0xc5..0x104 of that array
    // (selftest probe_name_table_is_core_dll_hardcoded_names reads the DLL).
    // Shipped data agrees: every serialized ProbeMask of every class and state is
    // (super's mask | probe-named functions with FUNC_Defined) under this order.
    internal static class UEProbe
    {
        public const ulong All = ulong.MaxValue;

        static readonly string[] Names =
        {
            "Spawned", "Destroyed", "GainedChild", "LostChild",
            "Probe4", "Probe5", "Trigger", "UnTrigger",
            "Timer", "HitWall", "Falling", "Landed",
            "ZoneChange", "Touch", "UnTouch", "Bump",
            "BeginState", "EndState", "BaseChange", "Attach",
            "Detach", "ActorEntered", "ActorLeaving", "KillCredit",
            "AnimEnd", "EndedRotation", "InterpolateEnd", "EncroachingOn",
            "EncroachedBy", "FootZoneChange", "HeadZoneChange", "PainTimer",
            "SpeechTimer", "MayFall", "Probe34", "Die",
            "Tick", "PlayerTick", "Expired", "Probe39",
            "SeePlayer", "EnemyNotVisible", "HearNoise", "UpdateEyeHeight",
            "SeeMonster", "SeeFriend", "SpecialHandling", "BotDesireability",
            "Probe48", "Probe49", "Probe50", "Probe51",
            "Probe52", "Probe53", "Probe54", "Probe55",
            "Probe56", "Probe57", "Probe58", "Probe59",
            "Probe60", "Probe61", "Probe62", "All",
        };

        static readonly Dictionary<string, int> _bit = Build();

        static Dictionary<string, int> Build()
        {
            var d = new Dictionary<string, int>(UEPropNameComparer.Instance);
            for (int i = 0; i < Names.Length; i++) d[Names[i]] = i;
            return d;
        }

        // Event name → bit number. False for names outside the probe range: for
        // them the original IsProbing is ALWAYS true (Core.dll.c:40262 — the
        // comparison with 0xc4/0x105 precedes any mask work).
        public static bool TryBit(string eventName, out int bit)
        {
            bit = 0;
            return !string.IsNullOrEmpty(eventName) && _bit.TryGetValue(eventName, out bit);
        }

        // State frame mask by the shipped formula. `state` == null means
        // "state not found": the original substitutes the class itself
        // (Core.dll.c:58324-58326).
        //
        // The CANONICAL class is used. A cross-package class reference arrives
        // as an import stub with no body and hence a zero mask; using it would
        // silence ALL probe events of the object. Same canonicalisation as
        // defaults loading, IsA and state lookup.
        public static ulong MaskFor(UClass cls, UState state)
        {
            UClass real = Canonical(cls);
            UState asState = state ?? real;
            if (real is null && asState is null) return All;
            ulong classProbe = real?.ProbeMask ?? 0UL;
            ulong stateProbe = asState?.ProbeMask ?? 0UL;
            ulong stateIgnore = asState?.IgnoreMask ?? All;
            return (classProbe | stateProbe) & stateIgnore;
        }

        static UClass Canonical(UClass cls)
        {
            if (cls is null || !HP1.Core.UEClassRegistry.IsImportStub(cls)) return cls;
            UClass found = HP1.Core.UEClassRegistry.Shared?.Find(cls.Name);
            return found is not null && !HP1.Core.UEClassRegistry.IsImportStub(found) ? found : cls;
        }
    }
}
