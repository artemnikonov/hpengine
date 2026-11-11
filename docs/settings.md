# Controls and settings

- **Keyboard and mouse**: exactly as the original — the default layout comes from the game's own `DefUser.ini` (arrows
  move, Ctrl jumps, Alt or the left mouse button casts, Enter toggles look mode, Escape opens the menu). Rebind keys in
  **Options → Controls**.
- **Alt+Enter** toggles fullscreen.
- **Options → Video → Interface Size** (in place of Object Detail; not original): **Smaller**, **Standard** or
  **Larger**. Menus, HUD and text are scaled by whole pixels so they stay sharp. Texture Detail works as in the
  original.

Optional keys in the `[OpenHP1]` section of `<game folder>/OpenHP1Data/Default.ini`:

| key | default | meaning |
|---|---|---|
| `VisualMode` | `Enhanced` | `Original` turns the visual improvements below off and draws the 3D world as the 2001 renderer did |
| `UIScaleStep` | `0` | the Interface Size choice above (`-1`, `0`, `1`; saved by the menu) |
| `SmoothMotion` | `True` | `False`: no interpolated frames between game ticks, one frame per tick |
| `Anisotropy` | `16` | anisotropic texture filtering, `0` or `1` turns it off |

