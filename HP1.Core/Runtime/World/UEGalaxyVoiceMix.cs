// GALAXY VOICE VOLUME, PAN AND FREQUENCY — ONE BODY IN CORE.
//
// Source: UGalaxyAudioSubsystem::Update (export
// ?Update@UGalaxyAudioSubsystem@@UAEXUFPointRegion@@AAVFCoords@@@Z -> body
// FUN_106081f0), Galaxy.dll disasm:
//
//   0x106084f7..0x10608679  ambient update: channel Volume/Radius/Pitch are
//                           rewritten from the live actor EVERY Update
//   0x10608750..0x10608c12  one-voice computation (third loop): Location from
//                           the actor, Priority, listener axes, angle, pan,
//                           volume, Doppler
//   0x1060bab4..0x1060bae3  mixer pan table (filled at init)
//   0x10612250.. (Galaxy.dll.c:12887-12909)  mixer: L = Table[0x7fff-Pan],
//                           R = Table[Pan], each *Gain >> 15
//
// Caller: UGameEngine::Draw (Engine.dll.c:117935-117942) —
// `Audio->Update(Viewport->Actor->Region, Frame->Coords)`, so the LISTENER
// AXES are the frame's FSceneNode::Coords (camera from PlayerCalcView:
// GMath.ViewCoords / ViewRotation, Origin = ViewLocation), while the VIEWER for
// priority and Doppler is Viewport->Actor->ViewTarget (+0x4f4) or the actor itself.
//
// TWO DIFFERENT DISTANCES, AND THIS IS NOT A PORTING TYPO:
//   Priority (+0x2c) = (1 - |Loc - Viewer| / Radius) * Volume   (0x10608893)
//   mix volume        = clamp01(1 - |Loc - Coords.Origin| / Radius)  (0x10608a14)
// Priority drives channel eviction; volume is what is heard.
//
// Arithmetic is x87 PC64 (Galaxy runs outside the PreRender..PostRender window,
// where appEnableFastMath(1)): intermediates are double, float stores where
// shipped does `fstp dword`. Reference: these instructions executed under
// unicorn (se_harness/bsp_diff/galaxy_voice_oracle.py, corpus
// data/galaxy_voice_corpus.txt, fixture galaxy_voice_mix_matches_shipped).
//
// STATED BOUNDARY. Everything Update computes is here. Not ported, left to the
// frontend device: the mixer gain chain other than voice volume (master volume,
// 15-bit `>> 15` truncations) and mixer pan smoothing (step at most 0x200 per
// mix block, Galaxy.dll.c:12888-12897) — tied to the device output block, not
// to the game frame.
using System;
using System.Globalization;
using System.IO;
using Vector3 = HP1.UEScript.UEVector;

using HP1.Core;

namespace HP1.UEScript
{
    // [Galaxy.GalaxyAudioSubsystem] from the shipped Default.ini. Property offsets:
    // UGalaxyAudioSubsystem::StaticConstructor (Galaxy.dll.c:1254-1440):
    // UseSurround +0x34, ReverseStereo +0x4c, AmbientFactor +0x58,
    // DopplerSpeed +0x5c. A missing key is a loud failure, not a default.
    public sealed class UEGalaxyConfig
    {
        public readonly bool UseSurround, ReverseStereo;
        public readonly float AmbientFactor, DopplerSpeed;
        // Latency: UIntProperty at +0x60, CPF_Config (Galaxy.dll.c:1402-1407); the music fade thresholds read it live
        // (0x10608fdb/0x1060900d/0x1060903f `fild dword [esi+0x60]`).
        public readonly int Latency;
        // OutputRate (Hz, from the OutputRates list "8000Hz".."48000Hz", Galaxy.dll.c:1288-1322): the mixer frame clock
        // of the music feeder model (UEGalaxyAudio, D-72) — script-visible song end, so the shipped value, not the
        // frontend's output rate.
        public readonly int OutputRate;

        public UEGalaxyConfig(bool useSurround, bool reverseStereo, float ambientFactor, float dopplerSpeed, int latency,
                              int outputRate)
        {
            UseSurround = useSurround; ReverseStereo = reverseStereo;
            AmbientFactor = ambientFactor; DopplerSpeed = dopplerSpeed;
            Latency = latency;
            OutputRate = outputRate;
        }

        static readonly int[] OutputRates = { 8000, 11025, 16000, 22050, 32000, 44100, 48000 };

        public static UEGalaxyConfig Load(string systemDir)
        {
            string ini = UELevelRuntime.DefaultIniPath(systemDir);
            string section = null;
            string surround = null, reverse = null, factor = null, doppler = null, latency = null, rate = null;
            foreach (string raw in File.ReadAllLines(ini))
            {
                string line = raw.Trim();
                if (line.StartsWith("[")) { section = line; continue; }
                if (!string.Equals(section, "[Galaxy.GalaxyAudioSubsystem]", StringComparison.OrdinalIgnoreCase)) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                if (k.Equals("UseSurround", StringComparison.OrdinalIgnoreCase)) surround = v;
                else if (k.Equals("ReverseStereo", StringComparison.OrdinalIgnoreCase)) reverse = v;
                else if (k.Equals("AmbientFactor", StringComparison.OrdinalIgnoreCase)) factor = v;
                else if (k.Equals("DopplerSpeed", StringComparison.OrdinalIgnoreCase)) doppler = v;
                else if (k.Equals("Latency", StringComparison.OrdinalIgnoreCase)) latency = v;
                else if (k.Equals("OutputRate", StringComparison.OrdinalIgnoreCase)) rate = v;
            }
            if (surround is null || reverse is null || factor is null || doppler is null || latency is null || rate is null)
                throw new InvalidDataException(
                    $"{ini}: [Galaxy.GalaxyAudioSubsystem] has no UseSurround/ReverseStereo/AmbientFactor/DopplerSpeed/Latency/OutputRate");
            string hz = rate.Trim();
            if (!hz.EndsWith("Hz", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(hz[..^2], NumberStyles.None, CultureInfo.InvariantCulture, out int outputRate)
                || Array.IndexOf(OutputRates, outputRate) < 0)
                throw new InvalidDataException($"{ini}: [Galaxy.GalaxyAudioSubsystem] OutputRate={rate} is not in the Galaxy OutputRates list");
            int lat = int.Parse(latency, CultureInfo.InvariantCulture);
            // Shipped clamps Latency to [10, 250] in PostEditChange (0x10605e14..0x10605e2f); whether a load path runs
            // that clamp is not established, so a value outside the range is refused instead of guessed.
            if (lat < 10 || lat > 250)
                throw new InvalidDataException($"{ini}: [Galaxy.GalaxyAudioSubsystem] Latency={lat} outside [10, 250] — clamp path not ported");
            return new UEGalaxyConfig(
                bool.Parse(surround), bool.Parse(reverse),
                float.Parse(factor, CultureInfo.InvariantCulture),
                float.Parse(doppler, CultureInfo.InvariantCulture), lat, outputRate);
        }
    }

    // Listener of one Update: frame axes and viewer.
    // Valid — a frame camera is set (Update's mixing loop); HasViewer — a viewport actor exists (PlaySoundW needs only
    // the viewer for priority, 0x106077d5-0x10607812, no frame).
    public readonly struct UEAudioListener
    {
        public readonly bool Valid, HasViewer;
        public readonly BrushCoords Coords;   // Frame->Coords (Origin = ViewLocation)
        public readonly Vector3 Viewer;       // Location of Viewport.Actor.ViewTarget ?? Viewport.Actor
        public UEAudioListener(BrushCoords coords, Vector3 viewer)
        { Valid = true; HasViewer = true; Coords = coords; Viewer = viewer; }
        public UEAudioListener(Vector3 viewer)
        { Valid = false; HasViewer = true; Coords = default; Viewer = viewer; }
    }

    // Voice computation result — what Update passes to the mixer.
    public readonly struct UEGalaxyVoice
    {
        public readonly float Priority;   // channel +0x2c
        public readonly int Volume;       // 0..0x7fff, argument of FUN_1060aae0
        public readonly int Pan;          // 0..0x7fff; 0xc000 — UseSurround branch
        public readonly float Doppler;    // frequency multiplier ([ebp-0x34])
        public UEGalaxyVoice(float priority, int volume, int pan, float doppler)
        { Priority = priority; Volume = volume; Pan = pan; Doppler = doppler; }
    }

    public static class UEGalaxyVoiceMix
    {
        // Galaxy .rdata (read from the image): 0.1 (0x1063a518, double),
        // 9126.302734375 (0x1063a510, float), 16383 (0x1063a50c, float),
        // 32767 (0x1063a524, float), 0.5/2.0 (0x1063a500/0x1063a4f0, double),
        // 1/255 (0x1063a400, double), 25 (0x1063a53c, float), 1/64 (0x1063a530).
        const double NearFraction = 0.1;
        const float PanScale = 9126.302734375f, PanCenter = 16383f, Full = 32767f;
        const double InvByte = 0.00392156862745098;

        // Frame listener. Axes are those of the render master frame
        // (UESceneNode.MasterCoords); the viewer is the viewport actor's ViewTarget or
        // the actor itself (Galaxy 0x10608872-0x10608891 and 0x10608328-0x10608336).
        // Without a frame camera (before the session's first frame) the listener carries the viewer only.
        public static UEAudioListener Listener(UEObject viewportActor, in CameraSnapshot camera)
        {
            if (viewportActor is null) return default;
            UEObject viewer = viewportActor.GetProp(PK.ViewTarget) as UEObject ?? viewportActor;
            Vector3 at = UEValue.AsVector3(viewer.GetProp(PK.Location));
            if (!camera.Valid) return new UEAudioListener(at);
            return new UEAudioListener(UESceneNode.MasterCoords(camera.Location, camera.Rotation), at);
        }

        // Channel priority: (1 - |Loc - Viewer| / Radius) * Volume, unclamped.
        // Update 0x10608893-0x106088f4 — proven by execution (corpus). PlaySoundW
        // (disasm 0x106077e4-0x10607833) rates a NEW sound against stored channel priorities
        // with the same chain: (dx·dx + dy·dy) + dz·dz, appSqrt, / Radius, 1 −, · Volume.
        // x87 chain (Galaxy runs from the game tick at PC64 and sets no control word): 0x10608893..0x106088f4 fld z; fsub;
        // fld y; fsub; fld x; fsub (st0 = dx); dx·dx + dy·dy, + dz·dz, fstp qword, appSqrt (fsqrt), fdiv dword Radius,
        // fsubr qword 1.0, fmul dword Volume, fstp dword — every step rounded to 64 bits.
        public static float Priority(in UEAudioListener l, Vector3 loc, float volume, float radius)
        {
            X87Ext dx = R(E(loc.x) - E(l.Viewer.x)), dy = R(E(loc.y) - E(l.Viewer.y)), dz = R(E(loc.z) - E(l.Viewer.z));
            X87Ext pd = X87Ext.Sqrt64(X87Ext.From(R(R(R(dx * dx) + R(dy * dy)) + R(dz * dz)).ToDouble()));
            return R(R(X87Ext.From(1.0) - X87Ext.Div64(pd, E(radius))) * E(volume)).ToFloat();
        }

        static X87Ext E(float v) => X87Ext.From(v);
        static X87Ext R(X87Ext v) => X87Ext.Round64(v);
        // _ftol (0x1062db06): truncation; NaN or outside int64 -> 0x8000000000000000, low dword 0.
        static int Ftol(X87Ext v)
        {
            if (!v.IsFinite) return 0;
            System.Numerics.BigInteger t = v.TruncateToInteger();
            return t >= long.MinValue && t <= long.MaxValue ? unchecked((int)(long)t) : 0;
        }
        // (loc − Origin)·A (0x106088fa..0x10608960): the three differences stay in the register, ((dx·Ax + dy·Ay) + dz·Az)
        // at 64 bits, fstp dword.
        static float Axis(X87Ext dx, X87Ext dy, X87Ext dz, Vector3 a) => R(R(R(dx * E(a.x)) + R(dy * E(a.y))) + R(dz * E(a.z))).ToFloat();

        // Galaxy 0x10608856..0x10608bb1. loc is the voice's already-updated Location
        // (0x10608750: for a voice with an actor, Actor.Location). velocity is read
        // only in the Doppler branch (voice with an actor in the ambient slot).
        public static UEGalaxyVoice Update(in UEAudioListener l, UEGalaxyConfig cfg, Vector3 loc,
            float volume, float radius, bool stereoSound, bool ambientSlot, bool hasActor, Vector3 velocity)
        {
            if (!l.Valid) throw new InvalidOperationException("Galaxy voice: frame listener not set");
            float priority = Priority(in l, loc, volume, radius);

            // Listener axes (0x106088fa-0x10608960): (Loc - Origin) · X/Y/Z.
            BrushCoords c = l.Coords;
            X87Ext dx = R(E(loc.x) - E(c.Origin.x)), dy = R(E(loc.y) - E(c.Origin.y)), dz = R(E(loc.z) - E(c.Origin.z));
            float X = Axis(dx, dy, dz, c.X), Y = Axis(dx, dy, dz, c.Y), Z = Axis(dx, dy, dz, c.Z);

            // Angle atan2(X, |Z|) (appAtan2 = fpatan, 0x10608963-0x10608990): `fcomp 0.0; test ah,1; je` — fchs only when
            // Z < 0 or unordered, so Z = −0 stays −0 and fpatan(±0, −0) is ±π. fst qword, fstp dword.
            X87Ext zq = E(Z);
            if ((X87Ext.Fcom(zq, E(0f)) & X87Ext.C0) != 0) zq = -zq;
            float angle = HP1.Core.UEX87Transcendental.Fpatan(E(X), zq).ToFloat();   // fpatan (D-71)
            // near zone 0.1*R (0x10608993-0x106089a2): fld Radius; fmul qword 0.1; fstp dword.
            float near = R(E(radius) * X87Ext.From(NearFraction)).ToFloat();
            // dist² = (Y·Y + X·X) + Z·Z: fst dword (dist2), fstp qword, appSqrt -> dist stays in the register.
            X87Ext sum = R(R(E(Y) * E(Y) + E(X) * E(X)) + E(Z) * E(Z));
            float dist2 = sum.ToFloat();
            X87Ext dist = X87Ext.Sqrt64(X87Ext.From(sum.ToDouble()));
            // `fld near; fmul near; fcomp dist2; test ah,0x41; jne` (0x106089cd-0x106089de): near² > dist² — near branch.
            X87Ext nearSq = E(near) * E(near);
            X87Ext a = (X87Ext.Fcom(nearSq, E(dist2)) & (X87Ext.C0 | X87Ext.C3)) == 0
                ? R(X87Ext.Div64(dist, E(near)) * E(angle)) : E(angle);
            int pan = Clamp15(Ftol(R(R(a * E(PanScale)) + E(PanCenter))));

            // Volume: distance from the LISTENER AXES (0x10608a14-0x10608a78): fdiv Radius; fsubr 1.0; `fcom 0.0;
            // test ah,1` (below or unordered) -> 0.0; `fcom 1.0; test ah,1; jne` (not below) -> 1.0; fmul Volume; fmul 32767.
            X87Ext t = R(X87Ext.From(1.0) - X87Ext.Div64(dist, E(radius)));
            if ((X87Ext.Fcom(t, X87Ext.From(0.0)) & X87Ext.C0) != 0) t = X87Ext.From(0.0);
            else if ((X87Ext.Fcom(t, X87Ext.From(1.0)) & X87Ext.C0) == 0) t = X87Ext.From(1.0);
            int vol = Clamp15(Ftol(R(R(t * E(volume)) * E(Full))));

            if (stereoSound) pan = 0x4000;                          // 0x10608a86 (USound+0x50)
            if (cfg.ReverseStereo) pan = 0x7fff - pan;              // 0x10608a9c
            if (Z < 0.0f && cfg.UseSurround) pan = 0xc000;          // 0x10608abd

            float doppler = 1.0f;
            if (hasActor && ambientSlot)                            // 0x10608ac4-0x10608adf
                doppler = Doppler(loc, l.Viewer, velocity, cfg.DopplerSpeed);
            return new UEGalaxyVoice(priority, vol, pan, doppler);
        }

        static int Clamp15(int v) => v < 0 ? 0 : v > 0x7ffe ? 0x7fff : v;

        // 0x10608ae5-0x10608ba1: 1 - Velocity·SafeNormal(Actor - Viewer)/DopplerSpeed,
        // clamp [0.5, 2]. SafeNormal — Core 0x1014f4e0: |v|² < 1e-8f -> zero,
        // otherwise v * (1/sqrt(|v|²)).
        static float Doppler(Vector3 actor, Vector3 viewer, Vector3 vel, float speed)
        {
            float rx = actor.x - viewer.x, ry = actor.y - viewer.y, rz = actor.z - viewer.z;
            double sq = ((double)rx * rx + (double)ry * ry) + (double)rz * rz;
            float nx = 0f, ny = 0f, nz = 0f;
            if (!(sq < (double)1e-8f))
            {
                double s = 1.0 / Math.Sqrt(sq);
                nx = (float)(s * rx); ny = (float)(s * ry); nz = (float)(s * rz);
            }
            double dot = ((double)nz * vel.z + (double)ny * vel.y) + (double)nx * vel.x;
            double f = 1.0 - dot / speed;
            if (f < 0.5) f = 0.5;
            else if (!(f < 2.0)) f = 2.0;
            return (float)f;
        }

        // Mixer pan table (0x1060bab4..0x1060bae3):
        // Table[i] = ftol(sqrt(i * 3.051850947599719e-05) * 32767.0), x87 PC64.
        // Double arithmetic does NOT work here: at i = 0x7fff the double product
        // rounds to exactly 1.0 and gives 32767, while the x87 64-bit mantissa gives 32766
        // (measured with the oracle). For all 32768 inputs shipped equals the EXACT
        // floor(sqrt(i*c)*32767) with c being that very double constant
        // (0x1063b8e8); that is computed here, in integers. The whole-table match is pinned
        // by a hash in fixture galaxy_voice_mix_matches_shipped.
        static ushort[] _panTable;
        public static int PanTable(int i)
        {
            if (_panTable is null)
            {
                long bits = BitConverter.DoubleToInt64Bits(3.051850947599719e-05);
                long mant = (bits & ((1L << 52) - 1)) | (1L << 52);
                int shift = 1075 - (int)((bits >> 52) & 0x7ff);   // c = mant / 2^shift
                var t = new ushort[0x8000];
                for (int k = 0; k < t.Length; k++)
                {
                    // floor(y²), y² = k * 32767² * c; floor(sqrt(y)) = isqrt(floor(y²)).
                    long y2 = (long)(((System.Numerics.BigInteger)k * 32767 * 32767 * mant) >> shift);
                    long n = (long)Math.Sqrt(y2);
                    while ((n + 1) * (n + 1) <= y2) n++;
                    while (n * n > y2) n--;
                    t[k] = (ushort)n;
                }
                _panTable = t;
            }
            return _panTable[i];
        }

        // Voice frequency: ftol(SampleRate * Pitch * Doppler)
        // (0x10608c8c-0x10608ca5: fild qword rate; fmul Pitch; fmul doppler).
        public static int Frequency(int sampleRate, float pitch, float doppler)
            => UELevelRuntime.Ftol((double)sampleRate * pitch * doppler);

        // ── AMBIENT ──────────────────────────────────────────────────────────
        // StartAmbience (0x10608434-0x106084ac): PlaySound(Actor, Index*16+8,
        // AmbientSound, Location, SoundVolume*AmbientFactor/255, (SoundRadius+1)*25,
        // SoundPitch/64).
        // Start volume (0x1060847a-0x1060848c): fild SoundVolume; fmul dword AmbientFactor; fmul qword 1/255; fstp dword —
        // x87 at 64-bit mantissa.
        public static float AmbientStartVolume(UEObject a, UEGalaxyConfig cfg) => AmbientBase(a, cfg).ToFloat();

        static X87Ext AmbientBase(UEObject a, UEGalaxyConfig cfg)
            => R(R(X87Ext.From((int)UEValue.AsByte(a.GetProp(PK.SoundVolume))) * E(cfg.AmbientFactor)) * X87Ext.From(InvByte));

        public static float AmbientRadius(UEObject a)
            => (UEValue.AsByte(a.GetProp(PK.SoundRadius)) + 1) * 25f;

        // Start-loop range test (0x106083a0-0x106083fa): dx, dy, dz = actor − viewer in the register; R = (SoundRadius+1)·25;
        // ((dz·dz + dy·dy) + dx·dx) compared with R·R (fcompp; out of range when R² < d² or unordered) — x87 at 64-bit mantissa.
        public static bool AmbientInRange(Vector3 loc, Vector3 at, float radius)
        {
            X87Ext dx = R(E(loc.x) - E(at.x)), dy = R(E(loc.y) - E(at.y)), dz = R(E(loc.z) - E(at.z));
            X87Ext d2 = R(R(R(dz * dz) + R(dy * dy)) + R(dx * dx));
            X87Ext r = E(radius);
            return (X87Ext.Fcom(R(r * r), d2) & X87Ext.C0) == 0;   // `fcompp; test ah,1; jne` out of range
        }

        public static float AmbientPitch(UEObject a)
            => (float)(UEValue.AsByte(a.GetProp(PK.SoundPitch)) * 0.015625);

        // UpdateAmbience (0x106085cb-0x10608641): volume of the sounding channel —
        //   v = SoundVolume * AmbientFactor * (1/255), v += v;
        //   LightType != 0 -> v = LightBrightness * v * (1/255), then
        //   GRender->GlobalLighting(ShowFlags bit 11, Actor, &v, &color)
        //   (Render.dll FUN_10b06810: type function 0x10b38288 and clamp [0,1]).
        // realtime — (Viewport.Actor.ShowFlags >> 11) & 1; when zero the type is
        // forced to 1 (unchanged), the clamp stays.
        // randomBase — FGlobalRandoms.Base as Update sees it: before this frame's
        // GRandoms->Tick (see LightAnim.ModulatePc64).
        public static float AmbientVolume(UEObject a, UEGalaxyConfig cfg, bool realtime, float[] randomBase)
        {
            // 0x106085d6-0x106085e4: the start-volume chain, fadd st0,st0, fstp dword (x87, 64-bit mantissa).
            X87Ext d = AmbientBase(a, cfg);
            float v = R(d + d).ToFloat();
            int lightType = UEValue.AsByte(a.GetProp(PK.LightType));
            if (lightType == 0) return v;
            // 0x10608606-0x10608617: fild LightBrightness; fmul dword v; fmul qword 1/255; fstp dword.
            float br = R(R(X87Ext.From((int)UEValue.AsByte(a.GetProp(PK.LightBrightness))) * E(v)) * X87Ext.From(InvByte)).ToFloat();
            int type = realtime ? lightType : 1;
            if (type < 10)
                br = WorldFrameProjector.LightAnim.ModulatePc64(type,
                    UEValue.AsByte(a.GetProp(PK.LightPeriod)), UEValue.AsByte(a.GetProp(PK.LightPhase)),
                    a.ObjectId, br,
                    UEValue.AsFloat((a.GetProp(PK.Level) as UEObject)?.GetProp(PK.TimeSeconds)),
                    randomBase, a);
            if (!(br >= 0f)) br = 0f;
            else if (!(br < 1f)) br = 1f;
            return br;
        }
    }
}
