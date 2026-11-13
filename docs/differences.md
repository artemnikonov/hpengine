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
  readable key names. The resolution list is also filled for scripts without the original's resolution filter (UK 1.1,
  and mods such as the Widescreen Mod, whose list is empty in the original).
- Menu: Options → Video has Display Mode (Fullscreen or Windowed) in place of Color Depth and Interface Size (Smaller,
  Standard, Larger; only the sizes that differ on the screen) in place of Object Detail.
- Russian release: menu text is drawn with Liberation Sans in place of Windows' MS Sans Serif, so its shapes and line
  breaks differ from the original.
- Menu: an open drop-down list closes when the page changes (Esc); the original left it drawn over the next page.

**Sound**
- 44.1 kHz mixer (the original mixes at 22 kHz).
- Music resumes where it was after Alt-Tab or pausing in the menu; songs play to their end instead of being cut.
- The music level does not follow the Sound Volume slider down to silence.

**Input and platform**
- Gamepad (the original has no gamepad bindings): the left stick moves and turns (analog), the right stick moves the
  camera and the menu cursor, the buttons have default actions (layout in [settings](settings.md)); B, Start and the
  right stick are fixed, the other buttons, the triggers, the d-pad and the left stick can be rebound, and a binding
  replaces the default. The d-pad has no default action in the game.
- Gamepad: while a gamepad is in use the Controls page lists its buttons and left-stick directions (defaults
  included) under the connected controller type's names ("Pad A", "Pad Cross", "D-pad Up") and rebinds them like keys;
  with keyboard and mouse it is the original page. The Launcher's More menu has "Reset Gamepad Bindings". In menus the
  d-pad moves the cursor between controls, changes sliders with Left/Right and steps through open drop-down lists
  (A picks, B closes).
- Alt+Enter does not toggle again on key repeat. In a window the mouse is captured without an
  extra click.
- No developer console and no cheat codes.
- Start-up failures are shown: started without a launcher, the engine shows a dialog when it finds no game folder
  (exit code 4), a mod is broken (5), the start map does not exist (6) or it crashes; the launchers show the same line.
- Linux: X11 (XWayland) is preferred to native Wayland; `SDL_VIDEO_DRIVER=wayland` chooses Wayland.
- Own save format (see [Install](../README.md#where-things-are)); the original's saves are not shown.
- Mods are switched on and off from a `Mods` folder without changing the game files (the original has no mod switch).
- A save made with different mods (turned on or off since) restarts its level from the beginning with the progress you
  had when you entered that level (house points, beans, wizard cards, potions) instead of failing to load; the Launcher
  names such slots. A save made by 0.1.0-beta does not load while mods are on (turn the mods off to load it). In both
  cases the game says so after the load, at the top of the screen, the way it shows the tutorial hints.
- An original bug is fixed: at a steady frame rate some Bertie Bott's beans flew away instead of to Harry.
- Mrs Norris climbs the library bookcases (Lev4_Sneak2) at any frame rate; in the original her jump only reaches the
  top at about 20-30 fps and she gets stuck against the bookcase at 60.

