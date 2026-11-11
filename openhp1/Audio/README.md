# OpenHP1 audio (`openhp1/Audio/`)

Engine audio over SDL3 audio: voice playback from the Core frame's channel snapshot (`WorldFrame.AudioChannels`),
the Galaxy mixer (kernel, gains, resampling), the mp2 song stream, zone reverb.

**The 16-channel pool, execution of sound calls and music belong to Core (`HP1.Core/Runtime/World/UEGalaxyAudio.cs`).**
PlaySound/StopSound/ModifySound execute synchronously at native-call time, `Update` runs in the engine frame
(`UEWorldFrameProducer.Build`: ambient -> channels -> bSongFinished -> music block), and Core writes `bSongFinished`/`Transition`
itself, with or without a window. One difference: Core computes the end of a voice from a position of `Frequency·dt` frames
(Galaxy uses the real-time Miles sample); the mixer plays with the same step and plays out the tail of a voice that ended
in Core (`VoiceTable`). There is no "live voice + bNoOverride -> return" branch (Galaxy has none); bNoOverride is the
PlaySoundW refusal on an Id match.

## Where things live

| what | where |
|---|---|
| six command kinds, refusal on an unknown kind, a repeated tick is not executed | `UEGalaxyAudio.Execute` (Core) |
| PlaySound: radius 0 -> 1600, pitch <=0 -> 1, volume <0 -> 0, SLOT_None = fresh voice, bNoOverride with a live voice, reuse of the (actor, slot) channel | `UEGalaxyAudio.ExecutePlay` (Core) |
| Modify/Stop: first channel by the Core rule `UEGalaxyChannels` (Id without the bNoOverride bit AND the sound or None) | `UEGalaxyAudio.cs` (Core) |
| ambient: start -> update* -> stop, one voice per actor; Update without a voice re-acquires (counter `AmbientUpdateWithoutVoice`) | `UEGalaxyAudio.cs` (Core) |
| viewport loss silences everything and notifies Core; NoteDestroy (from ULevel::DestroyActor) scans the pool | `UEGalaxyAudio.SetViewport`, `UEGalaxyAudio.NoteDestroy` (Core) |
| ModifySound answer comes from the Core table, the frontend is not asked | `UEGalaxyAudio.HasMatchingChannel` (Core) |
| PlaySoundW loop: Id match, `p <= best` (free channels from the end, a tie takes the last), refusal without a candidate, refusal without a listener | `UEGalaxyAudio.TryAcquire` (Core) |
| MixAll: live actor Location, `UEGalaxyVoiceMix.Update`, priority into the record, refusal on UseSurround; L/R gains are the full integer Galaxy mixer chain (`GalaxyMixGain`), frequency `UEGalaxyVoiceMix.Frequency` | `UEGalaxyAudio.MixAll` (Core) |
| master `m = ftol(SoundVolume/255·127)`, `a = 0x7e02·m²·2 >> 15`, `×0x7fff >> 15` ×4, `×Vol >> 15`, `L/R = Table·a >> 15` (Galaxy.dll.c:12875-12909) | `GalaxyMixGain.cs` |
| music: Instant/Segue immediately; Fade/FastFade/SlowFade at 1/3/0.2 per second, threshold `-0.002/-0.006/-0.0004 · Latency(40)`; the same song is not restarted; a one-shot song gives exactly one `ReportMusicFinished`; refusals on section, CD track, unknown transition; viewport loss/return. State machine over Galaxy fields (`+0x708/+0x70d/+0x780/+0x784`, Galaxy.dll.c:7284-7466): `bSongFinished` every Update, section 255 of the current song without fade, MusicVolume 0 without stop/start, "has a song" = `+0x708` | `UEGalaxyAudio.cs` (Core) |
| master volumes from `UEConfig`; before bootstrap the ini default with one warning | `CoreAudioWorld.MusicMaster/SoundMaster` |
| exact path or unambiguous leaf, PCM cache by path, loud refusal | `SoundBank.cs` |
| streaming `UEMpegStream` reads on the device thread; loop = `Restart()` (new MP2SoundStream pass, continuous synthesis) | `MusicVoice` (`AudioMixer.cs`) |
| reverb DSP as a function over a buffer: zone coefficients, 3 stages, cross feedback, input is the kernel send bus (`(g·0x7f)>>7`), `out = dry + wet·y3`, state reset on a new set, refusal when the cutoff exceeds the rate | `ZoneReverb.cs` |
| reverb key is the zone of the **view actor** (`Camera.ViewActorId`), `UseReverb`, rebuild only when the key changes | `AudioSystem.UpdateReverb` |
| Core: ambient -> channels -> bSongFinished -> music; frontend: snapshot -> voices, reverb | `UEGalaxyAudio.Update` (Core) + `AudioSystem.Update` |

## Platform decisions (not gameplay; named)

The mixer kernel is ported from GENERATED Galaxy code executed under an emulator; it has no remaining differences from
the original.

* **Rate is a Galaxy setting, OpenHP1 default 44100 (author improvement, not original, D-50).**
  The original uses `[Galaxy.GalaxyAudioSubsystem] OutputRate` (list 8000Hz..48000Hz, `Galaxy.dll.c:1276-1323`,
  Init table :5232-5238; glxInit 22050 :8961; shipped Default.ini `OutputRate=22050Hz`). OpenHP1:
  the key comes from the user ini (`AudioMixer.ConfiguredRate`), default 44100; `22050Hz` restores the shipped rate.
  Derived from the rate, as in glxStartOutput (:12050-12073): voice steps, envelope/sequencer ticks, reverb (delays,
  cutoff). **Format as in the original:** int16 stereo via the converter `DAT_1067ca04` (`GalaxyMixKernel.ToS16`:
  cvtps2pi to even + packssdw saturation). SDL receives float32 = exactly `s16/32768`, no processing of its own.
* **Resampling is the generated SSE-path kernel** (`FUN_10620b91`: a verbatim copy of template 0x106200f0): 16.16
  position, fraction carry by truncation, low bits of step/fraction cleared inside the call, **linear interpolation at
  any step** (the template does not read the UseFilter bit / step<1), 15-bit weights, float32 buses `x·(g·2^-30)`. The
  path is chosen by CPU: SSE on any modern one; 3DNow/MMX/x86 (no SSE, no MMX, or UseStereo=False) are not ported.
* **Saturation** happens in the output converter, as in the original.
* **Mix block length** in the original also depends on the DirectSound/WinMM cursor (`FUN_10611fd0`), which is
  platform; here the SDL callback request plays that role. Envelope/sequencer ticks and the 0x200 limit are ported.
* **Sample past the end of data** (the kernel reads `s[p+1]`): the original reads memory past the sample/stream buffer,
  layout not established (STOP). Here: for a loop, from the start of data, otherwise 0.

## Established behaviour

1. **Pan and volume.** Gains are the full integer Galaxy mixer chain `FUN_10612250` (`Galaxy.dll.c:12875-12909`,
   `GalaxyMixGain`): `a = 0x7e02·m²·2 >> 15` (group B master `DAT_1067cedc`; `m = ftol(SoundVolume/255·127)` from
   `glxSetSampleVolume`), `×0x7fff >> 15` four times (game sound +0x18/+0x1a/+0x24/+0x2a), `×Vol >> 15` (+0x66),
   `L = Table[0x7fff-Pan]·a >> 15`, `R = Table[Pan]·a >> 15`, as fractions `/32768`. A linear byte/255 master would be
   ≈2.5 dB louder at SoundVolume 200 (0.784 vs 0.589). The kernel scale is established by executing generated code:
   `x·(g·2^-30)`, `DAT_10648d50` = 2^-30 read from the module (`GalaxyMixKernel`). Field +0x12 = 0x7e02 is voice
   initialisation (`:8913`); that the 2D start `FUN_1060fbf0` and `FUN_1060aae0` do not write it is a decompile-based
   conclusion, **disassembly needed** for 0x1060fbf0..0x10610000, 0x1060aae0..0x1060ac80.
2. **A stereo sound is two voices.** Galaxy starts a stereo sample (flag 0x40, `:15845`) as TWO mono voices
   (`FUN_1060fbf0`, `uVar5 = 2`, `:10936-10937`; link +0x9c `:11114`), and Update writes volume and pan to all voices of
   the chain (`FUN_1060aae0`, `:8354`); for a stereo sound that is `Pan = 0x4000` (`USound+0x50`, `:7110`). The mixer
   receives each voice as mono (call flags `:12938-12939` carry no stereo). So **both data channels sound in both
   outputs**: `L = (sL+sR)·gL`, `R = (sL+sR)·gR`; the original has no channel separation. The data split across the two
   voices is done by the stream `LAB_1060bba0` (not decompiled, **disassembly needed**); the result does not depend on it
   as long as each voice gets its own data channel.
3. **Music: one voice at a time.** The same song is compared by path (`OrdinalIgnoreCase`) with a live stream; no new
   stream is opened for the same song.
4. **A channel record lives until the next Update (D-10).** Original (Galaxy disassembly): ModifySound
   `0x10607ae0-0x10607b67`, StopSound `0x10607ba0-0x10607c13` and the PlaySoundW selection loop `0x1060783e-0x10607883`
   read only the record's Id (+0x88), Sound (+0x90) and priority (+0xac); a finished voice is removed by the third
   Update loop: `FUN_1060a760(voice, 2)` (mode 2 returns voice byte +0xf, `0x1060a7b2 -> 0x1060a880`) equal to 0 ->
   `StopSound(i)` (`0x10608729-0x1060874b`). Between the end of a sound and Update, ModifySound finds the record
   (returns 1), PlaySound with bNoOverride on the same (actor, slot) is refused, and victim selection sees the stored
   priority. Tests: group `[d10]`, mutations `audit/debt_wave1_redtest_audio.sh`.
5. **Music is a group-B voice: through zone reverb and under the SoundVolume master (D-12, Galaxy disassembly).**
   mp2 music is `USound(UMusic*)` (`Engine.dll.c:146466`, +0x64 = 1); the Update branch "Sound+0x64 != 0"
   (`0x10608772-0x10608851`) starts a voice `FUN_1060fbf0(0, sample, -1, vol, -1, 1)`, which writes byte +0x11 = `1|8`
   (`0x1060fd70-0x1060fd7b`), no 0x80 bit; the mixer picks the group by bit 0x80 (`0x10612bf0-0x10612c3e`): without it,
   buses ca64/ca6c/ca74 and master `DAT_1067cedc` (SoundVolume, `m²·2`); the reverb callback `DAT_1066cfe8` receives
   ca64/ca6c (`0x106120bc-0x106120e1`); send +0x1e = 0x7f (initialisation `0x1060b888`; start `FUN_1060fbf0` does not
   write it). Bit 0x80 is set only by `0x10614526`/`0x10614ab9` (modular synthesizer).
   Voice volume (`0x10608797-0x106087e5`): `ftol(byte +0x70e (= ViewActor+0x55b, PercentMusicVolume) · MusicVolume(+0x69)
   · K[0x1063a528] · counter +0x780 · 32767[0x1063a524])`, clamped to [0, 0x7fff], written in the third Update loop BEFORE
   the fade step (one-Update lag). Stereo is two voices with pan 0 and 0x7fff (`0x1060fedf-0x1060ff02`). Here:
   `AudioMixer.Mix` puts music on the group-B buses (dry + send), `MusicVoice.Apply` uses the `GalaxyMixGain` chain,
   `MusicPlayer.VoiceVolume`. K was **read** from Galaxy.dll 1.53: `(float)(1/25500)`, i.e. exactly
   `Percent·0.01·MusicVolume/255`; FName string `DAT_10645ea4` = UTF-16 `"MP2"` (music on this path is an MP2 file).
   **STOP assumptions** (named in code): pan of a mono stream (in the original, inherited from the previous slot owner
   without flag 0x20) is 0x4000; flags 0x40/0x20 of the mp2 stream sample (`glxLoadSample` mode 10) are not read. That HP1
   plays digital music this way is confirmed by the live probe (`tools/origprobe/README.md`: digital music plays through
   the same PlaySound).

## NOT ported (and why)

* **Pan smoothing by the Galaxy mixer** (step ≤ 0x200 per block, `Galaxy.dll.c:12887-12897`): **nothing to port**. For a
  software 2D voice `FUN_1060fbf0` (`:11092-11093`) and `FUN_1060aae0` (`:8345-8348`) write the current (+0x14) and
  target (+0x16) pan with ONE value, the step is always 0: pan jumps per Update, as here. Smoothing works only on the
  hardware 3D path (`FUN_1060ac80` with A3D/EAX and Use3dHardware; Update `:7240` writes only +0x16), outside the
  platform. The block is ≤ 0x200 frames per `FUN_10612250` call (`:12483-12485`). The 15-bit `>> 15` truncations are
  ported (`GalaxyMixGain`).
* **Zone reverb parameter source**: the Core frame `WorldFrame.Reverb` (Galaxy `UpdateReverb`).
* **Inheritance of the reverb send level from a music note** (shared Galaxy pool): the send is 127/128 for all game sounds.
* **`UELevelRuntime.ForeignSoundPath`** is not set in OpenHP1 (Core answers null with a loud refusal).
* **`AmbientFactor`**: Core takes it from `UEGalaxyConfig`.
* **UseSurround** (Pan 0xc000): loud refusal in MixAll.

## Not checked without the game

* Sound on real data (shipped mp2, maps, Tut1): synthetic only.
* Real device: SDL is checked with the `dummy` driver (the callback pulls the mixer); latency, clicks on pan changes and
  saturation cannot be checked by ear.

## Tests

```
cd openhp1/tests/AudioTests && DOTNET_ROLL_FORWARD=Major dotnet run -c Release   # 0 = success
sh openhp1/tests/AudioTests/redtest.sh                                          # red mutations
sh audit/debt_wave1_redtest_audio.sh                                            # mutations D-10/D-12
python3 se_harness/galaxy_mixer/galaxy_mixer_oracle.py                          # oracle (needs bin/Galaxy.dll)
```

The mixer kernel (group `[kernel]`) is compared bit-exactly with the oracle vectors `galaxy_mixer_vectors.txt`, the
output of generated Galaxy.dll code under an emulator (`se_harness/galaxy_mixer/`, regeneration there). Other expected
values are computed independently in the test: pool rules from the PlaySoundW loop; volume/pan by the Core body
`UEGalaxyVoiceMix.Update` (Core owns the value), pan table `floor(sqrt(i·c)·32767)` in the test, the Galaxy mixer gain
chain and stereo as two voices by `ShippedGain` in the test per the decompile expression (integers, master
`floor(byte·127/255)`); channel record lifetime (group `[d10]`) per the Update/ModifySound/PlaySoundW disassembly rule;
music voice volume by `MusicVol` per the disassembly expression `0x10608797-0x106087e5` (K = 1/25500, read from the
module), its gains by the same `ShippedGain` with pan 0/0x7fff; music transitions by a float loop per the Galaxy rule
(volume from the counter BEFORE the step); reverb by a separate double implementation of the formulas; SDL with the
`dummy` driver.

`dotnet run` prints one line per check by group (`[pool]`, `[kernel]`, `[mix]`, `[d10]`, `[music]`, `[frames]`,
`[reverb]`, `[sdl]`; stderr carries the loud refusals the tests trigger on purpose) and ends with the pass line, exit
code 0. `redtest.sh` applies 56 mutations, prints for each which check caught it, and ends with:

```
[redtest] base (sources restored)
AUDIO TESTS PASS (85 checks)
REDTEST PASS: 56 of 56 mutations red, base green
```

`audit/debt_wave1_redtest_audio.sh` (mutations D-10/D-12: D10a-c, D12a-f) runs the base, each mutation, the base after
restore, and ends with:

```
debt_wave1_redtest_audio: red 9 of 9
debt_wave1_redtest_audio: PASS (all mutations red, base PASS)
```

The impulse response does not distinguish Galaxy's 2π `6.282` from `2π` (model difference ≤5e-7 at a peak of 0.57, below
float tolerance), so the stage filter coefficient is checked directly (`ZoneReverb.LowpassBForTest`).

## Status

Checked by local subsystem tests (not the gate); `CLOSED` is not claimed.

| Subsystem | Status | Owner | Activated | Oracle | Dependencies |
|---|---|---|---|---|---|
| Sound command execution, 16-channel pool | ACTIVATED; record lifetime until Update per disassembly (D-10) | Core `UEGalaxyAudio` | yes, tests | no | — |
| Volume/pan/frequency | PORTED (Core body) + ACTIVATED in the mixer | Core `UEGalaxyVoiceMix` | yes | Core: `galaxy_voice_mix_matches_shipped` (not here) | — |
| Mixer gain chain, stereo as two voices (`GalaxyMixGain`, `MixVoice`) | PORTED (decompile) + ACTIVATED; PCM PROVEN by the SwARc Galaxy judge (40 maps × 300 ticks bit-exact, `tools/swarc_oracle/GALAXY_MEASURE.md`) | OpenHP1 frontend | yes | yes (judge PCM) | +0x12 |
| Sound voice ring and stream feeder (`Voice.Message`/`Mix`: ring per Galaxy voice, the sample's shared staging buffer, LAB_1060bba0), pool order (Core `Playback.VoiceSlot`) | PORTED (disassembly) + PROVEN by the judge PCM (as above) | frontend; voice pool Core | yes | yes (judge PCM, voice slots) | — |
| Mixer kernel (`GalaxyMixKernel`: resampler, buses, converter) | PROVEN (bit-exact against executed generated SSE code of Galaxy.dll, 47 sets) | OpenHP1 frontend | yes, `[kernel]` | yes, `se_harness/galaxy_mixer/galaxy_mixer_oracle.py` | — |
| Split into kernel calls (`AudioMixer.Mix/MixVoice`, `MusicVoice.Plan`) | PORTED + ACTIVATED; PCM PROVEN by the judge | OpenHP1 frontend | yes | yes (judge PCM) | — |
| Music (`MusicPlayer`, `MusicVoice`) | PORTED (group-B route and volume expression from Galaxy disassembly) + ACTIVATED | frontend, decisions in Core | yes (synthetic stream) | no | `UEMpegStream` not executed on shipped data here; mp2 sample flags, mono pan: STOP (D-12); K and FName read (1/25500, "MP2") |
| Zone reverb (`ZoneReverb`) | PROVEN: this file bit-exact against Galaxy.dll setup/callback under unicorn, PC64 (`se_harness/galaxy_reverb/galaxy_openhp1_dsp_check.py`, gate [reverbdsp], red mutations `audit/galaxy_reverb_dsp_redtest.sh`) | frontend | yes | yes | zone parameters from the Core frame (ReverbState) |
| SDL3 output (`AudioOutput`) | ACTIVATED (dummy) | platform | yes | — | real device |
