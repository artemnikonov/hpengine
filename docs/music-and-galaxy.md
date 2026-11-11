# Music, sound channels and the Galaxy audio engine

The game's audio runs through `Galaxy.dll` (a Miles-style mixer, `UGalaxyAudioSubsystem`) driven by `Engine.dll`. Almost
everything the scripts can observe about sound lives in Galaxy's per-frame `Update` (body `FUN_106081f0`): the 16-entry
channel table, `bSongFinished`, the music fade and the moment a sound ends. OpenHP1 ports those bodies into Core
(`../HP1.Core/Runtime/World/UEGalaxyAudio.cs`, `UEGalaxyVoiceMix.cs`) and leaves only mixing to the frontend
(`../openhp1/Audio/`). This note records how the original behaves and where the port deliberately differs.

## Formats and catalogs

- Almost all shipped audio is MPEG-1/2 Layer II (mp2): all voice (`AllDialog.uax`), all music (`Music/*.umx`) and nearly
  all effects. The rest is PCM WAV. The port decodes the user's own packages; nothing is shipped with it
  (`../HP1.Core/Runtime/World/UEMpegAudio.cs`).
- The decoder is a port of Engine.dll's `CMpegBase`: on any CPU that runs the game it takes the integer MMX path
  (dequantisation `FUN_10455780`, window `FUN_104579f0`, matrix `FUN_10459070`), which has no x87 instruction, so the
  output does not depend on the FPU control word. It was checked against the original's machine code on every shipped mp2.
- Engine.dll's `MP2SoundStream::FillBuffer` (0x1037e550) is the single source of stream bytes: at each loop start it emits
  a skip (layer II: 0x3c2 bytes), a loop is exactly `iEndPosn` bytes, and a loop restart rewinds the bitstream **without**
  resetting the synthesis filter, so the seam depends on the previous loop's tail (stereo only; mono washes out).
- Music is a `UMusic` played as a temporary `USound` through the same stream (Galaxy `Update`, `USound(UMusic*, bool)`).
  Lookup is `StaticLoadObject` by exact object path (`../HP1.Core/Runtime/World/UEMusicCatalog.cs`); the literal
  `Music'Engine.JS_HP_Title_Screen_v2'` lives in `Engine.u` itself, so that package's Music exports are indexed too.
- The loop flag is sound data, not a name list: `USound::Serialize` reads it from the package tail (version above 0x45),
  and for older packages `SetupVarsForVersion70` derives it from WAV `smpl` loops. It is set on 167 shipped sounds
  (`../HP1.Core/Runtime/World/UESoundCatalog.cs`). `GetSoundDuration` (which scripts use for dialog timing) is
  `FSoundData::GetPeriod`, computed in x87 as bytes times 8 over channels, rate and bits; an empty recording gives 0.
- Open: the mp2 early-end and odd-length branches matter for the Russian edition (1368 sounds); see `editions.md`.

## Sound calls and channels

- `PlaySound` / `PlayOwnedSound` (Engine.dll 0x1040b3a0 / 0x1040b890) do not talk to the device. They walk the listeners
  (`Level.PawnList` pawns with `bIsPlayer`, or client viewports), call `AActor::CheckHearSound` (0x1040ab90) and deliver
  the listener's `ClientHearSound` event, whose native body (`APawn::execClientHearSound`, 0x10407f30) finally calls
  `Audio->PlaySound`. So a sound beyond its radius is culled and a wall muffles it (port:
  `../HP1.Core/Runtime/World/UELevelRuntime.SoundNatives.cs`).
- Defaults: slot 1, volume `TransientSoundVolume`, radius `TransientSoundRadius` (zero means 1600 UU), pitch
  `TransientSoundPitch` times 0.015625. CheckHearSound compares squared distance with the radius-squared times 0.7692308;
  behind geometry the threshold is multiplied by 0.6 (if the instigator is not the hearer) and the X parameter by 0.35.
- A channel is one **(actor, slot)** pair. Its id is `Index*16 + Slot*2 + bNoOverride`; `StopSound` (Galaxy 0x10607ba0) and
  `ModifySound` (0x10607ae0) match on `((ch.Id ^ Id) & ~1) == 0` and the same sound (or `None`), the table is scanned from
  the start and the **first** match wins. With `bNoOverride` a new call is rejected if the channel entry exists, even if
  its voice has finished: an entry lives until the next `Update` removes it (D-10).
- Priority and eviction use a different distance from the heard volume. Priority is `(1 - |Loc - Viewer| / Radius) * Volume`
  with the viewer being the view target; the mixed volume uses the distance to the frame's listener origin
  (`UEGalaxyVoiceMix.cs`, Galaxy 0x10608893 and 0x10608a14). The listener axes are the frame's `Coords`, set by
  `UGameEngine::Draw` calling `Audio->Update(Region, Coords)`.
- Doppler is `1 - Velocity . SafeNormal(Actor - Viewer) / DopplerSpeed`, clamped to 0.5..2 (0x10608ae5). The mixer's pan
  table is `ftol(sqrt(i * 3.0518509e-05) * 32767)`; it must be evaluated with the x87 64-bit mantissa, because plain double
  arithmetic gives 32767 at the top entry where the shipped code gives 32766.
- The voice pool holds `EffectsChannels + 4` (20) voices, stride 0xa0; a stereo sample uses two linked mono voices. A new
  voice steals the last free voice or the one with the lowest tick count, and a channel whose voice is taken is stopped
  (`FUN_1060fbf0`). `UseSurround`, `ReverseStereo`, `AmbientFactor`, `DopplerSpeed`, `Latency` and `OutputRate` come from
  `[Galaxy.GalaxyAudioSubsystem]`; a missing key is a loud failure in the port.
- Ambient sounds are re-acquired by `Update`'s start loop with `SoundVolume * AmbientFactor / 255`, rewritten from the live
  actor every frame (0x106084f7-0x10608679), and stopped on pause (0x10608519). A destroyed actor's ambient channel is
  stopped; any other channel is detached and plays out (`NoteDestroy`).

## End of a sound and of a song

- Every sound streams. The voice is created by `Update` on an empty 0x4000-frame ring and filled by Galaxy's feeder
  thread (0x1060bba0), which is woken by the mixer every OutputRate/96 frames (sequencer counter reload, 0xE5B000 in 16.16
  at 22050 Hz). Galaxy frees the channel when the voice reaches the end the feeder armed. The port models the feeder on
  Core's own audio clock so the channel is freed at the original moment (D-72).
- **A song ends early.** `MP2SoundStream::FillBuffer` makes its short final read ahead of playback; the next `Update`
  sets `bSongFinished`, `Song = None`, `Transition = 1` and stops the song channel. The buffered rest is never played: the
  original cuts the last 0.74-0.98 s at 22050 Hz (measured on six natural ends of the running original). The script-visible
  timing follows the original; the port **plays the buffered tail out** ("not original", D-72).
- `bSongFinished` is written by every `Update` before the music block (0x10608ecc-0x10608f6a): a channel for the song
  exists and its stream is exhausted gives 1; no channel at all gives 1; nothing else writes it. A looping song never
  reports. If `MusicVolume` is 0 the music block does not stop the song, the voice plays to its armed end and the channel
  is freed one `Update` later.

## Music requests, MusicEvent and save loads

- Scripts set `Song`, `SongSection`, `CdTrack` and `Transition` on the player through `PlayerPawn.ClientSetMusic`
  (a `MusicEvent` trigger does this); Galaxy's music block reads them. The start block takes a normal pool channel with
  `PlaySoundW` (volume 100.0, radius 1.0, pitch 0, Actor 0), one `Update` after the request (D-28).
- Transitions: instant and segue change at once; the fade modes (3 and above) count a fade counter down by `dt` and swap
  the song when it falls below `Latency` times a per-mode scale (0x10608fcc-0x10609053). Open: song sections other than 0 or
  255 and CD tracks are refused loudly, because no shipped data uses them.
- The music voice volume is `PercentMusicVolume * MusicVolume * (1/25500) * fade * 32767`, clamped (0x10608797-0x106087e5).
  The voice is "group B": it goes through the zone reverb and under the `SoundVolume` master (D-12).
- **Music after loading a save is the original's PostLogin fade (D-43, D-49 withdrawn 2026-11-09).** Loading a save or
  restarting after death runs `LoadMap`, `SpawnPlayActor` and `GameInfo.PostLogin`, which calls
  `ClientSetMusic(Level.Song = None, MTRAN_Fade)`. The song playing before the load fades out for about a second over the
  loaded level, then there is silence until the next `MusicEvent`. The song and its `USound` survive the map change because
  Galaxy marks both objects `RF_Native|RF_Marked` when it starts a song (0x10609243-0x1060926d), so `LoadMap`'s garbage
  collection never unregisters them. `MusicEvent` does not remove itself after firing. The port briefly restored the save's
  own song (D-49); the level's own `MusicEvent` cut it a moment later, so the improvement was removed. Port:
  `../HP1.Core/Runtime/World/UELevelRuntime.SaveGame.cs`.
- The two `SetViewport(same viewport)` calls inside `LoadMap` stop every non-music channel (0x1039c8af and after
  `GNewBrushTracker`), so the old level's loops go silent during the loading screen.

## Focus loss, pause and the zone reverb

- `SetViewport(NULL)` (window focus loss) stops every channel and unregisters the song; on return Galaxy sets
  `Transition = 1` and restarts the song **from the beginning**. The port resumes it where it was (D-51, not original).
  The mixer clock stops without a viewport (`glxStopOutput`, 0x10616040).
- On menu pause the original silences only ambient channels; other voices keep playing while dialog timers stop.
  The port holds those voices and resumes them (D-52, not original).
- Zone reverb is a send bus: voice send level 0x7f, parameters from `UpdateReverb` (0x106095c4) over the viewer's zone:
  wet gain `MasterGain/255`, cutoff clamped to 0..44100 Hz, six delay/gain pairs (delay = byte * 0.002 s clamped to
  0.001..0.34, gain = byte/255 clamped to 0.001..0.999). The frontend DSP (`../openhp1/Audio/ZoneReverb.cs`) matches the
  original callback `FUN_106206f3` and setup `FUN_1060e2a0` bit for bit at 22050 and 44100 Hz.

## Mixer rate

- The shipped `Default.ini` sets `OutputRate=22050Hz`; the setting is a byte over the list 8000, 11025, 16000, 22050,
  32000, 44100, 48000 Hz and `glxStartOutput` (`FUN_10610c80`) accepts 8000..48000. Voice steps, envelope ticks (every
  1 ms) and sequencer ticks derive from it. The mixer proper is an SSE 16.16 linear-interpolation kernel that cuts calls at
  0x200 frames and at tick boundaries (`../openhp1/Audio/AudioMixer.cs`, `GalaxyMixKernel.cs`).
- OpenHP1 mixes at 44100 Hz by default (D-50, not original: shipped music is 22050/44100 Hz and most sounds too (some are 16000 Hz), so 44100 gives exact steps
  and no unfiltered decimation). Key `OutputRate` in the user ini restores any value of the list. The rate that Core uses
  for the script-visible song end stays the shipped one.
- Open: the mixer's pan smoothing and master-gain truncations are tied to the device block, and the music stream sample
  flags (mono panning) and the file-type name used by music were not read from Galaxy's image (D-12 remainder).
