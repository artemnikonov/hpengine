# Differences from the original

Game logic, physics, timing, progress and saves follow the original code. These differences are deliberate improvements,
not original behaviour:

**Picture**
- Widescreen without stretching: the 3D world is Hor+ (wider view on wide screens); menus, loading screens and 2D
  pictures keep 4:3 with black side bars. Interface size is an integer multiple of 480p, so text stays sharp.
- Enhanced mode: 16× anisotropic and trilinear texture filtering, full mesh detail at any distance and frame rate (no
  LOD collapse, e.g. Dumbledore in the final level), fire without the start-up flash.
- Masked textures cut palette colour 0 everywhere, so glasses and similar skins no longer show a dark band.
- Light flares (coronas) fade on the game's own frame clock instead of the wall clock.
- Light flares are hidden behind characters standing between the camera and the lamp (the original lets them shine
  through Harry); see-through characters (Harry under the Invisibility Cloak, the ghosts) still let them through.
- The loading screen is drawn at the same brightness as the menus.
- Game logic always runs at 60 Hz, as the game was designed (several original scripts behave differently at other
  frame rates), so there is no speed-up at very high frame rates and no frozen torch flames above 128 fps (both
  happen in the original); on faster displays the frames between game ticks are interpolated.
- Menu: scrolling combo lists (also with the mouse wheel), all display resolutions offered in every window mode,
  readable key names.

**Sound**
- 44.1 kHz mixer (the original mixes at 22 kHz).
- Music resumes where it was after Alt-Tab or pausing in the menu; songs play to their end instead of being cut.
- The music level does not follow the Sound Volume slider down to silence.

**Input and platform**
- Alt+Enter does not toggle again on key repeat. In a window the mouse is captured without an
  extra click.
- No developer console and no cheat codes.
- Own save format (see Install); the original's saves are not shown.
- An original bug is fixed: at a steady frame rate some Bertie Bott's beans flew away instead of to Harry.
- Mrs Norris climbs the library bookcases (Lev4_Sneak2) at any frame rate; in the original her jump only reaches the
  top at about 20-30 fps and she gets stuck against the bookcase at 60.

