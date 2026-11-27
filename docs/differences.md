# Differences from the original

Game logic, physics, timing, progress and saves follow the original code. These differences are deliberate improvements,
not original behaviour. They apply to both games (Harry Potter and the Philosopher's Stone, HP1, and Chamber of Secrets,
HP2) unless a bullet says otherwise; a bullet marked *HP1 only* fixes something HP2's own code already does right or does
not have.

**Picture**
- Widescreen without stretching: the 3D world is Hor+ (wider view on wide screens); menus, loading screens and 2D
  pictures keep 4:3 with black side bars. Interface size is an integer multiple of 480p, so text stays sharp.
- Chamber of Secrets, widescreen: a pickup flying into the HUD (e.g. a bean that hit a wall) is drawn with the game's
  own field of view, placed like the HUD, not with the wider Hor+ view, so it lands on its HUD icon at any aspect.
- Enhanced mode: 16× anisotropic and trilinear texture filtering, full mesh detail at any distance and frame rate (no
  LOD collapse, e.g. Dumbledore in the final level), fire without the start-up flash.
- Masked textures cut palette colour 0 everywhere, so glasses and similar skins no longer show a dark band.
- Light flares (coronas) fade on the game's own frame clock instead of the wall clock.
- Light flares are hidden behind characters standing between the camera and the lamp (the original lets them shine
  through Harry); see-through characters (Harry under the Invisibility Cloak, the ghosts) still let them through.
- The loading screen is drawn at the same brightness as the menus.
- Chamber of Secrets, widescreen: the loading screen is a centred 4:3 picture like the menus, and the shop (vendor) bar
  keeps the world full-width (it is drawn over the playing world, not as a menu).
- Smoothed 2D pictures do not blend in the opposite edge of their texture, so the loading-screen parchment (HP1) has no
  lines at its piece edges (the original shows a faint seam at 800x600 and a stronger one when stretched).
- Two-sided cloth (robes, capes) keeps the depth offset it has at 800 pixels wide on wider screens; the original's offset
  grows with the screen width, and at 4K/5K Harry's robe showed through his neck.
- Game logic always runs at 60 Hz, as the game was designed (several original scripts behave differently at other
  frame rates), so there is no speed-up at very high frame rates and no frozen torch flames above 128 fps (both
  happen in the original); on faster displays the frames between game ticks are interpolated.
- Menu: scrolling combo lists (also with the mouse wheel), all display resolutions offered in every window mode,
  readable key names (HP2: F1-F10, the mouse wheel and the other keys HP2's text files do not name). The resolution list is also filled for scripts without the original's resolution filter (UK 1.1,
  and mods such as the Widescreen Mod, whose list is empty in the original).
- Menu (HP1): Options → Video has Display Mode (Fullscreen or Windowed) in place of Color Depth and Interface Size (Smaller,
  Standard, Larger; only the sizes that differ on the screen) in place of Object Detail.
- Russian releases (both games): menu text is drawn with the book-style font Philosopher in place of Windows' MS Sans Serif, so its shapes and line
  breaks differ from the original. It is drawn smooth (antialiased) and at the screen's resolution, where the original
  draws small pixel text.
- *HP1 only:* a native font with any other face name (mods such as 25 Years HD ask for one) is drawn with the same
  Philosopher, the same way; the original takes whatever substitute font Windows picks for that name.
- Menu (*HP1 only*): an open drop-down list closes when the page changes (Esc); the original left it drawn over the next
  page. HP2's pages close their lists themselves.
- Chamber of Secrets, menu: Options → Sound/Video lists every display resolution (the original offered 640x480,
  800x600 and 1024x768, and never the last mode it found) and has Display Mode (Fullscreen or Windowed) in place of
  Color Depth; the launcher has no Video or Sound page — these settings are in the game's own menu.

**Sound**
- Chamber of Secrets, menu: the options input page shows Difficulty (Easy, Medium, Hard) for every player; the original
  showed it only in its debug mode.
- 44.1 kHz mixer (the original HP1 mixes at 22 kHz).
- Music resumes where it was after Alt-Tab (both games) or pausing in the menu; songs play to their end instead of being
  cut (HP1: HP2's music loops and pauses with the menu in the original).
- *HP1 only:* the music level does not follow the Sound Volume slider down to silence (HP2's never did).

**Input and platform**
- Gamepad (the original has no gamepad bindings): the left stick moves and turns like the arrow keys, the right stick moves the
  camera and the menu cursor, the buttons have default actions (layout in [settings](settings.md)); B, Start and the
  right stick are fixed, the other buttons, the triggers, the d-pad and the left stick can be rebound, and a binding
  replaces the default. The d-pad has no default action in the game. HP2: A is Jump and the broom action, the left stick
  carries HP2's arrow-key commands (spell lessons included), Back opens the map, B skips a cut-scene, and the d-pad picks
  the duel spell (Left Rictusempra, Down Mimblewimble, Right Expelliarmus; Up drinks Wiggenweld and, in a duel, cycles the spell).
- Gamepad: while a gamepad is in use the Controls page lists its buttons and left-stick directions (defaults
  included) under the connected controller type's names ("Pad A", "Pad Cross", "D-pad Up") and rebinds them like keys;
  with keyboard and mouse it is the original page. HP2 shows the pad view; rebinding on HP2's Controls page is not
  ported yet (for keys either). The Launcher's More menu has "Reset Gamepad Bindings". In menus the
  d-pad moves the cursor between controls, changes sliders with Left/Right and steps through open drop-down lists
  (A picks, B closes).
- iOS: on-screen touch controls act as a gamepad (movement and camera sticks, Jump, Cast, Menu) when no
  gamepad is in use; the screen keyboard is not shown (typing needs a hardware keyboard). Development builds can
  download a missing game folder from the developer's Mac (`hpengine/ios/content-server.sh`).
- HP1: any key skips the start-up splash screens, and Space turns a story-book page, the first page and one-page
  stories included, once it has been shown for 2 seconds; the original allows both only in its hidden debug mode.
- Alt+Enter does not toggle again on key repeat. In a window the mouse is captured without an
  extra click.
- No developer console and no cheat codes (HP2: the typed cheats and the menu's Shift+F7 debug switch do nothing).
- Start-up failures are shown: started without a launcher, the engine shows a dialog when it finds no game folder
  (exit code 4), a mod is broken (5), the start map does not exist (6) or it crashes; the launchers show the same line.
- Linux: X11 (XWayland) is preferred to native Wayland; `SDL_VIDEO_DRIVER=wayland` chooses Wayland.
- Own save format (see [Install](../README.md#where-things-are)); the original's saves are not shown.
- Mods are switched on and off from a `Mods` folder without changing the game files (the original has no mod switch).
- The launchers (Windows, Linux, macOS) can select an installed language before starting the game, and work with a
  gamepad. Multilingual distribution
  folders are assembled in the same working copy as mods; original files are not rewritten or replaced.
- A save made with different mods (turned on or off since) restarts its level from the beginning with the progress you
  had when you entered that level (house points, beans, wizard cards, potions) instead of failing to load; the Launcher
  names such slots. A save made by 0.1.0-beta does not load while mods are on (turn the mods off to load it). In both
  cases the game says so after the load, at the top of the screen, the way it shows the tutorial hints.
- *HP1 only:* an original bug is fixed: at a steady frame rate some Bertie Bott's beans flew away instead of to Harry.
- *HP1 only:* Mrs Norris climbs the library bookcases (Lev4_Sneak2) at any frame rate; in the original her jump only reaches the
  top at about 20-30 fps and she gets stuck against the bookcase at 60.

