// ENGINE.DLL SOUND NATIVES — ONE BODY FOR ALL FRONTENDS.
//
// Shipped hands a sound not to the device but to LISTENERS:
//
//   execPlaySound (Engine.dll.c:63749-63922; disasm 0x1040b3a0-0x1040b5c0)
//   execPlayOwnedSound (13240-13400; disasm 0x1040b890-0x1040bcad)
//     -> for each listener AActor::CheckHearSound (146369-146415;
//        disasm 0x1040ab90-0x1040ad1d; export 0x10304813)
//     -> the listener's ClientHearSound event (FindFunctionChecked+ProcessEvent)
//     -> native APawn::execClientHearSound (143546; disasm 0x10407f30-0x104080ed)
//     -> Audio->PlaySound (vtable +0x78) — here a frame command.
//
// So a sound outside the radius is culled, a wall muffles it, and without a
// bIsPlayer pawn in PawnList shipped is silent. Frontends play sound ONLY from
// the result of this route.
using System;
using System.Collections.Generic;
using UELib.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    // Galaxy channel rules, shared by all frontends.
    public static class UEGalaxyChannels
    {
        // Channel id: `((Slot & 0xff) + Index*8)*2 (+ bNoOverride)`.
        //   execPlaySound    0x1040b3a0-0x1040b3ad (Index*16 + Slot*2 + bNoOverride)
        //   execStopSound    0x1040c0d0-0x1040c0dc
        //   execModifySound  0x1040bfd4-0x1040bfec
        // Index in the port is ObjectId (Galaxy only COMPARES Id, so only
        // uniqueness of the index among live objects is needed).
        public static int ChannelId(int objectIndex, int slot, bool noOverride)
            => ((slot & 0xff) + objectIndex * 8) * 2 + (noOverride ? 1 : 0);

        // Channel match. Galaxy StopSound 0x10607be5-0x10607bf6 and
        // ModifySound 0x10607afa-0x10607b0d use the same condition:
        //   ((ch.Id ^ Id) & ~1) == 0  AND  (ch.Sound == Sound OR Sound == None).
        // The sound is compared BY OBJECT; in the port a sound object is its exact path
        // (FName, case-insensitive).
        public static bool Matches(int channelId, string channelSound, int id, string sound)
            => ((channelId ^ id) & ~1) == 0
               && (sound is null
                   || string.Equals(channelSound, sound, StringComparison.OrdinalIgnoreCase));

        // The table is scanned FROM THE START, FIRST match wins (both functions exit on
        // the first match: StopSound `call StopSound(i); ret`, ModifySound
        // writes the field and `ret`). -1 — no match.
        public static int FindFirst(IReadOnlyList<(int Id, string Sound)> channels, int id, string sound)
        {
            if (channels is null) return -1;
            for (int i = 0; i < channels.Count; i++)
                if (Matches(channels[i].Id, channels[i].Sound, id, sound)) return i;
            return -1;
        }
    }

    public sealed partial class UELevelRuntime
    {
        // Sound path for a value Core does not know itself (e.g. a frontend-supplied
        // object in the Sound property). A null or empty answer is a
        // loud failure: guessing by the name leaf is forbidden.
        public static Func<object, string> ForeignSoundPath;

        // Route branch counters (V8: a fixture must show which branches
        // ran). Measurement only; they do not affect behaviour.
        public int HearChecks, HearCulledByRadius, HearOccluded, HearOccludedCulled, HearDelivered;

        // ── PlaySound / PlayOwnedSound ───────────────────────────────────────
        public object PlaySoundNative(UEInterpreter interp, UEFrame frame, object[] args)
            => PlaySoundShared(interp, frame, args, owned: false);

        public object PlayOwnedSoundNative(UEInterpreter interp, UEFrame frame, object[] args)
            => PlaySoundShared(interp, frame, args, owned: true);

        object PlaySoundShared(UEInterpreter interp, UEFrame frame, object[] args, bool owned)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            // Argument parsing with shipped defaults (both bodies alike):
            //   Sound  = None;  Slot = 1 (byte, SLOT_Misc);
            //   Volume = TransientSoundVolume (+0x1c0);  bNoOverride = 0;
            //   Radius = TransientSoundRadius (+0x1c4);
            //   Pitch  = TransientSoundPitch (+0x1c8) * 0.015625.
            // Disasm PlayOwnedSound 0x1040b89e-0x1040b962 (constant
            // 0x1046e3bc = 0.015625); PlaySound — the same set (63812-63833).
            object sound = args.Length > 0 ? args[0] : null;
            int slot = args.Length > 1 && args[1] is not null ? UEInterpreter.ToInt(args[1]) & 0xff : 1;
            float volume = args.Length > 2 && args[2] is not null
                ? UEInterpreter.AsFloat(args[2])
                : UEInterpreter.AsFloat(self.GetProp(PK.TransientSoundVolume));
            bool noOverride = args.Length > 3 && args[3] is not null && UEInterpreter.ToBool(args[3]);
            float radius = args.Length > 4 && args[4] is not null
                ? UEInterpreter.AsFloat(args[4])
                : UEInterpreter.AsFloat(self.GetProp(PK.TransientSoundRadius));
            float pitch = args.Length > 5 && args[5] is not null
                ? UEInterpreter.AsFloat(args[5])
                : UEInterpreter.AsFloat(self.GetProp(PK.TransientSoundPitch)) * 0.015625f;
            // `if (Sound == 0) return;` — BEFORE any side effects (63838).
            if (sound is null) return null;

            // The DemoPlaySound branch (XLevel->DemoRecDriver != 0) is unreachable
            // in the port: there is no demo recording. Named, not silently omitted.

            int id = UEGalaxyChannels.ChannelId(self.ObjectId, slot, noOverride);
            // RadiusSq = Square(Radius ? Radius : 1600) — fld/fmul/fstp dword
            // (0x1040b3b3-0x1040b3e4): one rounding of the exact product.
            float r = radius == 0f ? 1600f : radius;
            float radiusSq = r * r;
            // Parameters = (Volume*100, Radius, Pitch*100) — constant 100.0f
            // 0x10471b80; Radius goes AS IS (zero stays zero).
            var parameters = new Vector3(volume * 100f, radius, pitch * 100f);

            UEObject level = interp.GetLevelInfo();
            int netMode = UEInterpreter.ToInt(level?.GetProp(PK.NetMode));

            // WHOM TO ASK. PlaySound (0x1040b43b-0x1040b588): NetMode ==
            // NM_Client OR the calling function is simulated (FFrame.Node — UFunction
            // with FUNC_Simulated 0x100 in FunctionFlags +0x70) -> client viewports;
            // otherwise -> Level.PawnList with bIsPlayer. STATE code is not a UFunction
            // (IsA check 0x1040b450-0x1040b4be), its call goes through PawnList.
            // PlayOwnedSound (0x1040baaa) looks ONLY at NetMode == 3.
            bool viaViewports = netMode == 3
                || (!owned && frame.Func is UFunction caller
                    && caller.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Simulated));
            if (viaViewports)
            {
                // Client->Viewports(i)->Actor with Actor->XLevel == XLevel
                // (0x1040b58e-0x1040b65f). The port has one viewport, owned by
                // its level's runtime: the Entry world has none, so an Entry sound
                // on this branch is heard by no one — same as the XLevel check.
                if (Viewport?.GetProp(PK.Actor) is UEObject viewActor && !viewActor.Destroyed)
                    CheckHearSound(interp, self, viewActor, id, sound, parameters, radiusSq);
                return null;
            }

            // PlayOwnedSound excludes a listener only when NetMode != 0
            // (0x1040bb97-0x1040bc4c): an owner player with a Viewport does not hear
            // its own sound. Standalone has no exclusion.
            UEObject excluded = null;
            if (owned && netMode != 0)
            {
                UEObject owner = UEInterpreter.ClassIsA(self.Class, "PlayerPawn") ? self
                    : self.GetProp(PK.Owner) as UEObject;
                if (owner is not null && UEInterpreter.ClassIsA(owner.Class, "PlayerPawn")
                    && owner.GetProp(PK.Player) is UEObject player
                    && UEInterpreter.ClassIsA(player.Class, "Viewport"))
                    excluded = owner;
            }
            for (UEObject pawn = level?.GetProp(PK.PawnList) as UEObject;
                 pawn is not null; pawn = pawn.GetProp(PK.nextPawn) as UEObject)
            {
                if (!PropBool(pawn, "bIsPlayer")) continue;           // +0x25c bit 0x2
                if (ReferenceEquals(pawn, excluded)) continue;
                CheckHearSound(interp, self, pawn, id, sound, parameters, radiusSq);
            }
            return null;
        }

        // ── AActor::CheckHearSound — disasm 0x1040ab90-0x1040ad1d ────────────
        //
        //   HearActor = Hearer; if Hearer IsA PlayerPawn and ViewTarget != None —
        //     ViewTarget (0x1040aba1-0x1040abdd);
        //   thr = float(RadiusSq * 0.7692308)            0x1040abdf-0x1040abf8
        //   d²  = ((dz²+dy²)+dx²) in a PC64 register;    0x1040abfc-0x1040ac2f
        //         float(d²) is written over RadiusSq;
        //   if NOT (d²_ext < thr) — return;             0x1040ac35-0x1040ac3e
        //   FastLineCheck(HearLoc, Location): clear —   0x1040ac44-0x1040ac83
        //     event; otherwise:
        //     t = thr, and ×0.6 if Instigator != Hearer  0x1040ac85-0x1040ac93
        //     Parameters.X = float(X * 0.35)            0x1040ac99-0x1040aca3
        //     if float(d²) > t — return (C0|C3 = 0)     0x1040aca7-0x1040acb4
        //   ProcessEvent(ClientHearSound, {this, Id, Sound, Location, Parameters})
        //                                               0x1040acb6-0x1040ad13
        public void CheckHearSound(UEInterpreter interp, UEObject self, UEObject hearer,
            int id, object sound, Vector3 parameters, float radiusSq)
        {
            HearChecks++;
            UEObject hearActor = hearer;
            if (UEInterpreter.ClassIsA(hearer.Class, "PlayerPawn")
                && hearer.GetProp(PK.ViewTarget) is UEObject viewTarget)
                hearActor = viewTarget;
            Vector3 hear = ActorLocation(hearActor);
            Vector3 src = ActorLocation(self);

            float thr = radiusSq * 0.7692308f;   // 0x1047b59c = 0x3f44ec4f
            HP1.Core.X87Ext Sub(float a, float b)
                => HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.From(a) - HP1.Core.X87Ext.From(b));
            HP1.Core.X87Ext Sq(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v * v);
            HP1.Core.X87Ext dx = Sub(hear.x, src.x), dy = Sub(hear.y, src.y), dz = Sub(hear.z, src.z);
            HP1.Core.X87Ext distSq = HP1.Core.X87Ext.Round64(
                HP1.Core.X87Ext.Round64(Sq(dz) + Sq(dy)) + Sq(dx));
            float distSqF = distSq.ToFloat();
            if (!(distSq.CompareTo(HP1.Core.X87Ext.From(thr)) < 0)) { HearCulledByRadius++; return; }

            if (Bsp is null)
                throw new InvalidOperationException(
                    "CheckHearSound: FastLineCheck before the world BSP is loaded");
            if (!Bsp.FastLineCheckClear(hear, src))
            {
                HearOccluded++;
                HP1.Core.X87Ext t = HP1.Core.X87Ext.From(thr);
                if (!ReferenceEquals(self.GetProp(PK.Instigator) as UEObject, hearer))
                    t = HP1.Core.X87Ext.Round64(t * HP1.Core.X87Ext.From(0.6f));   // 0x1047b598
                parameters = new Vector3(parameters.x * 0.35f, parameters.y, parameters.z); // 0x10473808
                if (HP1.Core.X87Ext.From(distSqF).CompareTo(t) > 0) { HearOccludedCulled++; return; }
            }
            HearDelivered++;
            // FindFunctionChecked + ProcessEvent: the ProcessEvent gate (probe,
            // bDeleteMe, Disable) is the same as for all Core events.
            UELifecycle.CallEventArgs(interp, hearer, "ClientHearSound",
                new object[] { self, id, sound, src, parameters });
        }

        // ── APawn::execClientHearSound — disasm 0x10407f30-0x104080ed ────────
        //
        //   Volume = float(Parameters.X * 0.01), Pitch = float(Parameters.Z * 0.01)
        //     (constant 0x1046d5a0 = 0.01f);
        //   only a PlayerPawn whose Player (+0x4bc) is a Viewport; and with Audio != 0;
        //   Actor with bDeleteMe (+0x28 bit 0x200) -> None;
        //   Radius = Parameters.Y ? Parameters.Y : 1600;
        //   Audio->PlaySound(Actor, Id, S, SoundLocation, Volume, Radius, Pitch).
        public object ClientHearSoundNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            UEObject actor = args.Length > 0 ? args[0] as UEObject : null;
            int id = args.Length > 1 ? UEInterpreter.ToInt(args[1]) : 0;
            object sound = args.Length > 2 ? args[2] : null;
            Vector3 soundLocation = args.Length > 3 ? UEInterpreter.AsVector3(args[3]) : Vector3.zero;
            Vector3 parameters = args.Length > 4 ? UEInterpreter.AsVector3(args[4]) : Vector3.zero;
            float volume = parameters.x * 0.01f;
            float pitch = parameters.z * 0.01f;
            if (!UEInterpreter.ClassIsA(self.Class, "PlayerPawn")) return null;
            if (!(self.GetProp(PK.Player) is UEObject player)
                || !UEInterpreter.ClassIsA(player.Class, "Viewport")) return null;
            // The sound path is recovered from the SOURCE (a pathless marker holds
            // the sounding actor's property, not the listener's) — before Actor is cleared.
            string path = sound is null ? "None" : ResolveSoundPath(actor ?? self, sound);
            if (actor is not null && (actor.Destroyed || PropBool(actor, "bDeleteMe"))) actor = null;
            float radius = parameters.y == 0f ? 1600f : parameters.y;
            // Channel identity from Id, as in Galaxy: Index = Id >> 4,
            // Slot = (Id >> 1) & 7, bNoOverride = Id & 1. ActorId is the Index
            // (channel owner), even if Actor became None through bDeleteMe.
            bool looping = sound is UESoundRef sref ? sref.Looping
                : path != "None" && UESoundCatalog.Shared.Root is not null
                  && (UESoundCatalog.Shared.Load(path, mayFail: true)?.Looping ?? false);
            var cmd = new AudioCommand(AudioKind.Play, id >> 4,
                actor?.GetProp(PK.Name)?.ToString() ?? "None", path, (id >> 1) & 7,
                volume, radius, pitch, (id & 1) != 0, soundLocation, looping: looping);
            EmitDeviceCommand(interp, cmd);
            return null;
        }

        // ── AActor::execStopSound — disasm 0x1040c060-0x1040c0e9 ─────────────
        //   Sound = None; Slot = 1 (prefill 0x1040c092);
        //   Audio->StopSound(this, ((Slot&0xff) + Index*8)*2, Sound) (+0x98).
        // Channel match and "the FIRST one is silenced" — Galaxy 0x10607ba0-0x10607c13,
        // rule UEGalaxyChannels; executed by the Core pool (UEGalaxyAudio).
        public object StopSoundNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            object soundArg = args.Length > 0 ? args[0] : null;
            int slot = args.Length > 1 && args[1] is not null ? UEInterpreter.ToInt(args[1]) & 0xff : 1;
            string path = soundArg is null ? "None" : ResolveSoundPath(self, soundArg);
            var cmd = new AudioCommand(AudioKind.Stop, self.ObjectId,
                self.GetProp(PK.Name)?.ToString(), path, slot, 0f, 0f, 0f, false,
                ActorLocation(self));
            EmitDeviceCommand(interp, cmd);
            return null;
        }

        // ── AActor::execModifySound — disasm 0x1040bf20-0x1040c014 ───────────
        //   Parameter (byte) = 0; Value = 0; Sound = None;
        //   Slot (byte) = 1 — prefill 0x1040bf99;
        //   no device -> *Result = 0 (0x1040c005-0x1040c00b);
        //   else *Result = Audio->ModifySound(this, Id, Sound, Parameter, Value).
        // Galaxy ModifySound 0x10607ae0-0x10607b67: 1 if the channel is found (even
        // for an unknown parameter — 0x10607b32 `jne` to the common `mov eax, 1`),
        // else 0. The pool is Core's (UEGalaxyAudio).
        public object ModifySoundNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return false;
            int param = args.Length > 0 && args[0] is not null ? UEInterpreter.ToInt(args[0]) & 0xff : 0;
            float value = args.Length > 1 && args[1] is not null ? UEInterpreter.AsFloat(args[1]) : 0f;
            object soundArg = args.Length > 2 ? args[2] : null;
            int slot = args.Length > 3 && args[3] is not null ? UEInterpreter.ToInt(args[3]) & 0xff : 1;
            string path = soundArg is null ? null : ResolveSoundPath(self, soundArg);
            var cmd = new AudioCommand(AudioKind.Modify, self.ObjectId,
                self.GetProp(PK.Name)?.ToString(), path ?? "None", slot,
                param == 0 ? value : 0f, param == 1 ? value : 0f, param == 2 ? value : 0f,
                false, ActorLocation(self), param);
            EmitDeviceCommand(interp, cmd);
            // Audio (UGalaxyAudioSubsystem) — the Core pool (UEGalaxyAudio); the command already ran synchronously.
            return ProducerOf(interp).Audio.HasMatchingChannel(UEGalaxyChannels.ChannelId(self.ObjectId, slot, false), path);
        }

        // ── AActor::execGetSoundDuration — disasm 0x1040bdc0-0x1040be5b ──────
        //   None -> 0; else USound+0x60 (FSoundData::GetPeriod cache, filled
        //   when negative). Formula — UESoundCatalog.
        public object GetSoundDurationNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            object sound = args.Length > 0 ? args[0] : null;
            if (sound is null) return 0f;
            if (sound is UESoundRef loaded) return loaded.Duration;
            UEObject self = frame.Context ?? frame.Object;
            return UESoundCatalog.Shared.Load(ResolveSoundPath(self, sound), mayFail: false).Duration;
        }

        // The port's audio device is the FRONTEND frame command stream (the same
        // producer as for native registration — the Entry world has none of its own).
        // Its absence is a binding defect, not the shipped `Audio == NULL` branch.
        static UEWorldFrameProducer ProducerOf(UEInterpreter interp)
            => (interp.Natives as UENativesCore)?.FrameProducer
                ?? throw new InvalidOperationException("audio device (FrameProducer.Audio) not bound");

        void EmitDeviceCommand(UEInterpreter interp, in AudioCommand cmd)
        {
            var producer = ProducerOf(interp);
            producer.EmitAudio(cmd);
            Journal?.Invoke(FormatAudio(cmd));
        }

        // ── SOUND IDENTITY ──────────────────────────────────────────────────
        // Exact path of the Sound object. A pathless marker is recovered from the
        // class defaults (the same object that holds the property); nothing by
        // the name leaf.
        public static string ResolveSoundPath(UEObject self, object sound)
        {
            if (sound is UEResourceRef marker)
            {
                if (!string.Equals(marker.Class, "Sound", StringComparison.OrdinalIgnoreCase))
                    throw new System.IO.InvalidDataException($"audio argument is {marker.Class}, not Sound: {marker}");
                string path = marker.ObjectPath;
                if (string.IsNullOrWhiteSpace(path)) path = RecoverDefaultResourcePath(self, marker);
                if (string.IsNullOrWhiteSpace(path))
                    throw new System.IO.InvalidDataException(
                        $"Sound reference lost its package/group path: {marker}");
                return path;
            }
            if (sound is UObject native) return native.GetPath();
            string foreign = ForeignSoundPath?.Invoke(sound);
            if (string.IsNullOrWhiteSpace(foreign))
                throw new System.IO.InvalidDataException(
                    $"unsupported Sound reference {sound.GetType().FullName}: {sound}");
            return foreign;
        }

        static string RecoverDefaultResourcePath(UEObject self, UEResourceRef marker)
        {
            if (self?.Class is null) return null;
            string propertyName = null;
            foreach (KeyValuePair<string, object> kv in self.Props)
            {
                bool holds = ReferenceEquals(kv.Value, marker);
                if (!holds && kv.Value is System.Collections.IList list)
                    foreach (object x in list) if (ReferenceEquals(x, marker)) { holds = true; break; }
                if (!holds) continue;
                if (propertyName is not null)
                    throw new System.IO.InvalidDataException(
                        $"resource marker {marker} is held by two properties: {propertyName}, {kv.Key}");
                propertyName = kv.Key;
            }
            if (propertyName is null) return null;

            var chain = new List<UClass>();
            for (UClass cls = self.Class; cls is not null; cls = UEInterpreter.CanonicalSuper(cls))
                chain.Add(cls);
            chain.Reverse();
            string resolved = null;
            foreach (UClass cls in chain)
            {
                if (cls.Properties is null) continue;
                foreach (UDefaultProperty tag in cls.Properties)
                {
                    if (tag?.Name is null
                        || !string.Equals(tag.Name.ToString(), propertyName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string raw = tag.Value ?? "";
                    int quote = raw.IndexOf('\'');
                    if (quote <= 0 || raw[raw.Length - 1] != '\'') continue;
                    string resourceClass = raw.Substring(0, quote);
                    string path = raw.Substring(quote + 1, raw.Length - quote - 2);
                    string leaf = path.Substring(path.LastIndexOf('.') + 1);
                    if (!string.Equals(resourceClass, marker.Class, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(leaf, marker.Name, StringComparison.OrdinalIgnoreCase))
                        throw new System.IO.InvalidDataException(
                            $"resource marker/default mismatch property={cls.Name}.{propertyName}: marker={marker} default={raw}");
                    resolved = path; // derived defaults overwrite base defaults
                }
            }
            return resolved;
        }

        // Text form of the command for the headless journal (audio_* lines,
        // pinned by the route SHA). One source with the frame command.
        public static string FormatAudio(in AudioCommand c)
        {
            string ci(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (c.Kind == AudioKind.Stop)
                return $"audio_stop actor={c.ActorName} sound={c.SoundPath} slot={c.Slot}";
            // Modify edits a playing channel: it has no noOverride and starts no
            // voice — rendered as its own line, not as play.
            if (c.Kind == AudioKind.Modify)
                return $"audio_modify actor={c.ActorName} sound={c.SoundPath} slot={c.Slot}"
                    + $" param={c.Param} value={ci(c.Param == 0 ? c.Volume : c.Param == 1 ? c.Radius : c.Pitch)}";
            return $"audio_play actor={c.ActorName}"
                + $" sound={c.SoundPath} slot={c.Slot} volume={ci(c.Volume)} radius={ci(c.Radius)}"
                + $" pitch={ci(c.Pitch)} noOverride={c.NoOverride} location={c.Location}";
        }
    }
}
