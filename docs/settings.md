# Controls and settings

- **Keyboard and mouse**: exactly as the original — the default layout comes from the game's own `DefUser.ini` (arrows
  move, Ctrl jumps, Alt or the left mouse button casts, Enter toggles look mode, Escape opens the menu). Rebind keys in
  **Options → Controls**.
- **Alt+Enter** toggles fullscreen.
- **Options → Video → Display Mode** (in place of Color Depth; not original): Fullscreen or Windowed.
- **Gamepad** (not original: the 2001 game has no gamepad bindings). Any standard controller works, also plugged in
  while the game runs. Defaults:

  | control | action |
  |---|---|
  | left stick | move and turn (on the broom: steer) |
  | right stick | camera; in menus the cursor |
  | A (South) | jump; in menus select |
  | X (West), right trigger | cast |
  | Y (North) | look mode |
  | RB / LB | broom: speed up / slow down |
  | B (East) | skip, close; in menus back |
  | Start / Menu | the book (pause menu) |
  | d-pad | in menus: move between controls; free to bind in the game |

  With a gamepad in use, **Options → Controls** shows the gamepad's buttons under their own names (Xbox, PlayStation
  or Nintendo) and binds gamepad buttons; with the keyboard it is the original page. A button you bind stops doing
  what it did before. B, Start and the right stick are fixed. Camera and menu cursor speed follow the mouse
  sensitivity slider in Options. **More → Reset Gamepad Bindings** in the Launcher brings the defaults back.
- **Options → Video → Interface Size** (in place of Object Detail; not original): **Smaller**, **Standard** or
  **Larger**. Menus, HUD and text are scaled by whole pixels so they stay sharp; only sizes that differ on your screen
  are offered (below 960 pixels of height, e.g. Steam Deck or 720p, Smaller would equal Standard and is not shown).
  Texture Detail works as in the original.

Optional keys in the `[HPEngine]` section of `<game folder>/HPEngineData/Default.ini`:

| key | default | meaning |
|---|---|---|
| `VisualMode` | `Enhanced` | `Original` turns the visual improvements below off and draws the 3D world as the 2001 renderer did |
| `UIScaleStep` | `0` | the Interface Size choice above (`-1`, `0`, `1`; saved by the menu) |
| `SmoothMotion` | `True` | `False`: no interpolated frames between game ticks, one frame per tick |
| `Anisotropy` | `16` | anisotropic texture filtering, `0` or `1` turns it off |
| `GamepadLookSpeed` | `8` | right stick speed (camera and menu cursor), before the Sensitivity slider |
| `GamepadDefaults` | `True` | `False`: no default gamepad bindings, only the ones you bind |

Linux: the game prefers X11 (XWayland) to native Wayland; set the environment variable `SDL_VIDEO_DRIVER=wayland` to
choose Wayland.

