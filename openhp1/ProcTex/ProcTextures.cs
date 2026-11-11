// ENGINE PROCEDURAL TEXTURES: fire (FireTexture) and water (WetTexture) over the Core simulation.
//
//   * fire: palette -> RGBA, alpha = max(r,g,b); a reference resolves by full path or by an unambiguous leaf,
//     an ambiguous leaf is a STOP; created with a first Tick(0); use marking; only drawn textures tick;
//   * water: carrier by full path and by leaf, STOP on a missing source / oversized source / unseeded ring,
//     source palette with alpha 0 at index 0, only drawn textures tick, journal;
//   * the live texture is substituted by material path at frame build (Wet(...)) and marked used (MarkWetUsed(...)).
// Simulation is Core's: HP1.UEScript.FireTexture / WaterTexture; the RNG ring is FractalRandom (by default
// FractalRandom.Shared, shared by fire and water, as in Fire.dll).
// The ring is NOT the game rand() stream: OpenHP1Program seeds it once from its own presentation stream (render
// random numbers); fire/water steps do not touch the game stream.
//
// Notes (each with its reason in the README):
//   * clock: the caller's `nowSeconds` (double, viewport wall clock, one value per frame); dt = (float)(now − last);
//   * no second tick in a frame where the texture already ticked (creation + TickAll in one frame): the original
//     UTexture::Lock calls Update only when Time != LastUpdateTime;
//   * RGBA rows in UE order (top to bottom), as the engine's LoadTexture;
//   * a short fire palette is a STOP, not a grey ramp: the Core record always carries 768 bytes, a ramp would be
//     a silent substitute;
//   * an unseeded ring is flagged loudly for fire too.
using System;
using System.Collections.Generic;
using HP1.UEScript;

namespace OpenHP1.ProcTex
{
    public sealed class ProcTextures
    {
        // Live texture: RGBA (USize × VSize × 4, UE rows top to bottom) + content version.
        public sealed class Entry
        {
            public string Key;
            public int Width, Height;
            public byte[] Rgba;
            public int Version;              // grows when RGBA is recomputed with DIFFERENT content
            public int UploadedVersion;      // set by the renderer after upload (MarkUploaded)
            public int Ticks;                // simulation Tick calls
            public bool Changed => Version != UploadedVersion;
            public void MarkUploaded() => UploadedVersion = Version;

            internal int UsedFrame, TickedFrame;
            internal double LastTime;
            internal RenderCadence Cadence;  // 60 Hz step rate at any render rate (Core RenderCadence)
            internal byte[] LastMip;
            public FireTexture Fire { get; internal set; }       // Core simulation (read-only for journal/checks)
            public WaterTexture Water { get; internal set; }
            internal byte[] PaletteRgba;      // 256 × RGBA
            internal byte[] Mip => Fire != null ? Fire.Mip0 : Water.Mip0;
        }

        readonly FractalRandom _ring;
        // fire: full path -> record; leaf -> all records (case-sensitive keys)
        readonly Dictionary<string, UEProceduralTextureData.Fire> _fireDefs = new();
        readonly Dictionary<string, List<UEProceduralTextureData.Fire>> _fireByLeaf = new();
        readonly Dictionary<string, Entry> _fireLive = new();
        // water: case-insensitive
        readonly Dictionary<string, UEProceduralTextureData.Wet> _wetDefs = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<UEProceduralTextureData.Wet>> _wetByLeaf = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Entry> _wetLive = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _reported = new(StringComparer.Ordinal);

        public readonly List<string> Problems = new();   // STOP lines, each once
        public int WetTicksTotal, FireTicksTotal;
        public int FireLive => _fireLive.Count;
        public int WetLive => _wetLive.Count;

        public ProcTextures(IEnumerable<UEProceduralTextureData.Fire> fires,
                            IEnumerable<UEProceduralTextureData.Wet> wets,
                            FractalRandom ring = null)
        {
            _ring = ring ?? FractalRandom.Shared;
            if (fires != null)
                foreach (var t in fires)                                   // EnsureDefs
                {
                    if (t == null || t.Name == null) continue;
                    string key = string.IsNullOrEmpty(t.Path) ? t.Name : t.Path;
                    _fireDefs[key] = t;
                    if (!_fireByLeaf.TryGetValue(t.Name, out var list)) { list = new(); _fireByLeaf[t.Name] = list; }
                    list.Add(t);
                }
            if (wets != null)
                foreach (var r in wets)                                    // EnsureDefs
                {
                    if (r == null || string.IsNullOrEmpty(r.Path)) continue;
                    _wetDefs[r.Path] = r;
                    if (r.SourceMip0 == null || r.SourceMip0.Length == 0) continue;
                    int dot = r.Path.LastIndexOf('.');
                    string leaf = dot >= 0 ? r.Path.Substring(dot + 1) : r.Path;
                    if (!_wetByLeaf.TryGetValue(leaf, out var list)) { list = new(); _wetByLeaf[leaf] = list; }
                    list.Add(r);
                }
        }

        void Stop(string once, string message)
        {
            if (_reported.Add(once)) Problems.Add(message);
        }

        // ── fire ───────────────────────────────────────────────────────────────

        // SameContent: one texture if everything that enters the simulation matches.
        static bool SameContent(UEProceduralTextureData.Fire a, UEProceduralTextureData.Fire b)
        {
            if (a.USize != b.USize || a.VSize != b.VSize || a.RenderHeat != b.RenderHeat
                || a.SparksLimit != b.SparksLimit || a.PrimeCount != b.PrimeCount
                || a.Rising != b.Rising || a.SparkCount != b.SparkCount
                || a.FxFrequency != b.FxFrequency || a.MinFrameRate != b.MinFrameRate || a.MaxFrameRate != b.MaxFrameRate
                || a.MipZeroR != b.MipZeroR || a.MipZeroG != b.MipZeroG || a.MipZeroB != b.MipZeroB)
                return false;
            if ((a.Sparks?.Length ?? 0) != (b.Sparks?.Length ?? 0)) return false;
            if (a.Sparks != null)
                for (int i = 0; i < a.Sparks.Length; i++) if (a.Sparks[i] != b.Sparks[i]) return false;
            if ((a.Palette?.Length ?? 0) != (b.Palette?.Length ?? 0)) return false;
            if (a.Palette != null)
                for (int i = 0; i < a.Palette.Length; i++) if (a.Palette[i] != b.Palette[i]) return false;
            return true;
        }

        // Resolve: full path, otherwise the leaf if all candidates are indistinguishable.
        UEProceduralTextureData.Fire ResolveFire(string refName, out string key)
        {
            key = null;
            if (_fireDefs.TryGetValue(refName, out var exact)) { key = refName; return exact; }
            int dot = refName.LastIndexOf('.');
            string leaf = dot >= 0 ? refName.Substring(dot + 1) : refName;
            if (!_fireByLeaf.TryGetValue(leaf, out var list) || list.Count == 0) return null;
            for (int i = 1; i < list.Count; i++)
                if (!SameContent(list[0], list[i]))
                {
                    Stop("fireleaf:" + leaf, $"[FireTex] STOP: leaf '{leaf}' belongs to {list.Count} different textures "
                        + "with DIFFERENT content. A full-path reference is required — choosing the «first» would set the effect's look "
                        + "by package load order.");
                    return null;
                }
            key = list[0].Path ?? leaf;
            return list[0];
        }

        // GetTexture: live fire texture by reference; null means unresolved/STOP.
        public Entry Fire(string refName, int frame, double nowSeconds)
        {
            if (string.IsNullOrEmpty(refName)) return null;
            var d = ResolveFire(refName, out string key);
            if (d == null) return null;
            if (_fireLive.TryGetValue(key, out var e)) { e.UsedFrame = frame; return e; }
            if (d.Palette == null || d.Palette.Length < 768)
            {
                Stop("firepal:" + key, $"[FireTex] STOP: '{key}' — palette {d.Palette?.Length ?? 0} bytes < 768; no fire");
                return null;
            }
            FireTexture core;
            try
            {
                core = new FireTexture(d.USize, d.VSize, (byte)d.RenderHeat, d.Rising, d.SparksLimit,
                                       d.Sparks ?? Array.Empty<byte>(), (byte)d.FxFrequency)
                {
                    MinFrameRate = d.MinFrameRate, MaxFrameRate = d.MaxFrameRate,
                    PrimeCount = (byte)Math.Clamp(d.PrimeCount, 0, 255),
                };
            }
            catch (ArgumentException ex)
            {
                Stop("firector:" + key, $"[FireTex] STOP: '{key}' — Core FireTexture refused: {ex.Message}");
                return null;
            }
            if (!_ring.Seeded)                                     // fire
                Stop("ring", "[ProcTex] STOP: the procedural-texture RNG ring is not seeded (OpenHP1Program seeds FractalRandom.Shared) — sparks/drops would run on a zero ring");
            var pal = new byte[256 * 4];
            for (int i = 0; i < 256; i++)
            {
                byte r = d.Palette[i * 3], g = d.Palette[i * 3 + 1], b = d.Palette[i * 3 + 2];
                pal[i * 4] = r; pal[i * 4 + 1] = g; pal[i * 4 + 2] = b;
                pal[i * 4 + 3] = Math.Max(r, Math.Max(g, b));   // brightness -> alpha
            }
            e = new Entry { Key = key, Width = d.USize, Height = d.VSize, Fire = core, PaletteRgba = pal,
                            Rgba = new byte[d.USize * d.VSize * 4] };
            // The first Tick catches up PrimeCount and takes a step.
            e.LastTime = nowSeconds;
            Step(e, 0f, frame);                                   // fire: first Tick(0)
            e.UsedFrame = frame;
            _fireLive[key] = e;
            return e;
        }

        // HP1FireTexture.MarkUsed (:185-190)
        public void MarkFireUsed(string refName, int frame)
        {
            if (string.IsNullOrEmpty(refName)) return;
            if (ResolveFire(refName, out string key) == null || key == null) return;
            if (_fireLive.TryGetValue(key, out var e)) e.UsedFrame = frame;
        }

        // ── water ──────────────────────────────────────────────────────────────

        // Fire by FULL path (surfaces/meshes know the path; only sprites via Fire() use the leaf).
        public bool IsFire(string uePath) => !string.IsNullOrEmpty(uePath) && _fireDefs.ContainsKey(uePath);
        public bool IsWetCarrier(string uePath) => !string.IsNullOrEmpty(uePath) && _wetDefs.ContainsKey(uePath);

        // CarrierByLeaf: exactly one carrier with a source under the leaf.
        public string WetCarrierByLeaf(string leaf)
        {
            if (string.IsNullOrEmpty(leaf) || !_wetByLeaf.TryGetValue(leaf, out var list)) return null;
            if (list.Count == 1) return list[0].Path;
            Stop("leaf:" + leaf, $"[Wet] STOP: leaf '{leaf}' belongs to {list.Count} WetTexture carriers — a full path is required");
            return null;
        }

        public bool WetSize(string uePath, out int w, out int h)
        {
            w = h = 0;
            if (string.IsNullOrEmpty(uePath) || !_wetDefs.TryGetValue(uePath, out var r)) return false;
            w = r.W; h = r.H; return true;
        }

        // GetTexture: the carrier's live texture; null means not a carrier, or STOP.
        public Entry Wet(string uePath, int frame, double nowSeconds)
        {
            if (string.IsNullOrEmpty(uePath) || !_wetDefs.TryGetValue(uePath, out var r)) return null;
            if (_wetLive.TryGetValue(uePath, out var e)) { e.UsedFrame = frame; return e; }
            if (r.SourceMip0 == null || r.SourceMip0.Length == 0)
            {
                Stop(uePath, $"[Wet] STOP: '{uePath}' — source '{r.SourceMissing}' is missing from the shipped data; no water (not used in the campaign)");
                return null;
            }
            if (r.SourcePalette == null || r.SourcePalette.Length < 768)
            {
                Stop(uePath, $"[Wet] STOP: '{uePath}' — source palette {r.SourcePalette?.Length ?? 0} bytes < 768; no water");
                return null;
            }
            var water = new WaterTexture(r.W, r.H)
            {
                DropType = (byte)r.DropType, WaveAmp = (byte)r.WaveAmp, FX_Frequency = (byte)r.FxFrequency, FX_Phase = (byte)r.FxPhase,
                FX_Amplitude = (byte)r.FxAmplitude, FX_Speed = (byte)r.FxSpeed, FX_Radius = (byte)r.FxRadius, FX_Size = (byte)r.FxSize,
                FX_Depth = (byte)r.FxDepth, FX_Time = (byte)r.FxTime, NumDrops = r.NumDrops,
                MinFrameRate = r.MinFrameRate, MaxFrameRate = r.MaxFrameRate, PrimeCount = (byte)r.PrimeCount,
                SourceMip0 = r.SourceMip0, SourceUBits = (byte)r.SourceUBits, SourceVBits = (byte)r.SourceVBits,
            };
            byte[] drops = r.Drops ?? Array.Empty<byte>();
            Array.Copy(drops, water.Drops, Math.Min(drops.Length, water.Drops.Length));
            water.PostLoad();
            if (water.SourceMip0 == null)
            {
                Stop(uePath, $"[Wet] STOP: '{uePath}' {r.W}x{r.H} — source '{r.SourcePath}' {r.SourceW}x{r.SourceH} is larger; shipped clears SourceTexture (UWetTexture::PostLoad), no water");
                return null;
            }
            if (!_ring.Seeded)                                     // water
                Stop("ring", "[ProcTex] STOP: the procedural-texture RNG ring is not seeded (OpenHP1Program seeds FractalRandom.Shared) — sparks/drops would run on a zero ring");
            var pal = new byte[256 * 4];
            for (int i = 0; i < 256; i++)
            {
                int k = i * 3;
                pal[i * 4] = r.SourcePalette[k]; pal[i * 4 + 1] = r.SourcePalette[k + 1]; pal[i * 4 + 2] = r.SourcePalette[k + 2];
                pal[i * 4 + 3] = (byte)(i == 0 ? 0 : 255);                 // index 0 transparent
            }
            e = new Entry { Key = uePath, Width = r.W, Height = r.H, Water = water, PaletteRgba = pal,
                            Rgba = new byte[r.W * r.H * 4] };
            // The first draw already goes through Update -> Tick.
            e.LastTime = nowSeconds;
            Step(e, 0f, frame);                                   // water: first Tick(0)
            e.UsedFrame = frame;
            _wetLive[uePath] = e;
            return e;
        }

        // HP1WetTextures.MarkUsed (:180-183)
        public void MarkWetUsed(string uePath, int frame)
        {
            if (!string.IsNullOrEmpty(uePath) && _wetLive.TryGetValue(uePath, out var e)) e.UsedFrame = frame;
        }

        // ── frame ──────────────────────────────────────────────────────────────

        // TickAll for fire and water: once per frame (Entry.TickedFrame guards the frame, a repeated call in the frame
        // ticks nothing); only textures drawn in this or the previous frame tick; dt is from their last tick.
        // Order fire -> water: the original order is the Lock order at draw time, unknown here (README).
        public void TickAll(int frame, double nowSeconds)
        {
            foreach (var e in _fireLive.Values) TickOne(e, frame, nowSeconds);
            foreach (var e in _wetLive.Values) TickOne(e, frame, nowSeconds);
        }

        void TickOne(Entry e, int frame, double nowSeconds)
        {
            if (frame - e.UsedFrame > 1) return;        // not drawn -> not Locked -> no tick
            if (e.TickedFrame == frame || nowSeconds == e.LastTime) return;   // Time == LastUpdateTime
            // 60 Hz rate at any render rate (D-35): in the original, one step per drawn frame.
            if (!e.Cadence.Due(nowSeconds)) return;
            float dt = (float)(nowSeconds - e.LastTime); e.LastTime = nowSeconds;
            Step(e, dt, frame);
        }

        void Step(Entry e, float dt, int frame)
        {
            if (e.Fire != null) { e.Fire.Tick(dt, _ring); FireTicksTotal++; }
            else { e.Water.Tick(dt, _ring); WetTicksTotal++; }
            e.Ticks++;
            e.TickedFrame = frame;
            Upload(e);
        }

        // Palette -> RGBA (Upload); the version grows only if the mip indices changed.
        static void Upload(Entry e)
        {
            byte[] m = e.Mip;
            if (e.LastMip != null && m.AsSpan().SequenceEqual(e.LastMip)) return;
            e.LastMip ??= new byte[m.Length];
            Buffer.BlockCopy(m, 0, e.LastMip, 0, m.Length);
            byte[] pal = e.PaletteRgba, rgba = e.Rgba;
            for (int i = 0; i < m.Length; i++)
            {
                int c = m[i] * 4, o = i * 4;
                rgba[o] = pal[c]; rgba[o + 1] = pal[c + 1]; rgba[o + 2] = pal[c + 2]; rgba[o + 3] = pal[c + 3];
            }
            e.Version++;
        }

        // Journal + fire count.
        public string Journal()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("fire live=").Append(_fireLive.Count).Append(" ticks=").Append(FireTicksTotal);
            sb.Append(" | wet live=").Append(_wetLive.Count).Append(" ticks=").Append(WetTicksTotal);
            foreach (var e in _wetLive.Values)
                sb.Append(' ').Append(e.Key).Append('=').Append(e.Ticks).Append("/ph").Append(e.Water.GlobalPhase);
            return sb.ToString();
        }
    }
}
