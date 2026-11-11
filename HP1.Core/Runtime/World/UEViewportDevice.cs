using System;

namespace HP1.UEScript
{
    // VIEWPORT BOUNDARY: WHO OWNS WHAT.
    //
    // The resolution must not live ONLY in the config (`SetRes` writes there, `GetCurrentRes` reads back): two port
    // functions would then only prove consistency with each other, the menu would observe an applied resolution that
    // does not exist, and that breaks the menu's own branch:
    //
    //   FEOptionsPage.uc:539-545
    //     OldSettings = GetCurrentRes $ "x" $ GetCurrentColorDepth;
    //     NewSettings = <combobox selection>;
    //     if (NewSettings != OldSettings) { SetRes NewSettings; ... }
    //
    // i.e. the menu compares the ACTUAL state with its wish and skips applying when they match. If GetCurrentRes
    // echoes what SetRes wrote, the comparison degenerates, and the timeout rollback (:568 `SetRes OldSettings`)
    // rolls back to nowhere.
    //
    // SHIPPED SPLIT (WinDrv.dll.c):
    //   :5096  GetCurrentRes         — prints the size of the LIVE viewport;
    //   :5102  GetCurrentColorDepth  — `(ColorBytes ?: 2) * 8`, the state
    //                                  (client state, not the ini);
    //   :5238  SetRes <W>x<H>[x<D>]  — appAtoi on each piece; with zero width OR height does NOTHING;
    //                                  otherwise requests a real resize;
    //   :15370-15379 — the resize path, and only WHEN THE RESIZE SUCCEEDED:
    //       Client->WindowedViewportX = AppliedX;
    //       Client->WindowedViewportY = AppliedY;
    //       Client->WindowedColorBits = AppliedBytes * 8;
    //       UObject::SaveConfig(Client, 0x4000, NULL);
    //
    // Hence THREE consequences that define this boundary:
    //   * REQUESTED AND APPLIED ARE DIFFERENT VALUES, and the APPLIED one goes to the config. The device may apply
    //     something other than requested;
    //   * saving happens ONLY on success. A failed resize leaves no trace in the config or in GetCurrentRes;
    //   * saving comes AFTER applying, not instead of it. Shipped gets this order for free (synchronous), and so does
    //     the port: every device (OpenHP1 SdlViewportDevice with SDL_SyncWindow, the headless canvas) resizes
    //     synchronously and reports the outcome on return.
    //
    // The boundary follows the same seam: Core owns command parsing, step order and saving; the frontend owns the
    // resize itself and reports the ACTUAL values. GetCurrentRes is answered only by the frontend — it can have no
    // second source.
    // ── HOW A RESIZE REQUEST ENDED ──────────────────────────────────────────
    //
    // Shipped ResizeViewport is SYNCHRONOUS: on return the window is already resized and saving follows in the same
    // call, so it has exactly two outcomes. Only what is already applied and read back from the device is written to
    // the config.
    public enum ViewportResizeOutcome
    {
        // Applied. The device properties already report the new state, and Core takes what goes into the ini from
        // them.
        Applied,
        // Not applied. The shipped failure path: no trace anywhere.
        Rejected,
    }

    // ── WHAT IN A RESIZE IS ARITHMETIC, NOT PLATFORM ────────────────────────
    //
    // Both values below are FACTS FROM THE BINARY, identical for every frontend. A per-frontend copy would create two
    // owners of one shipped rule (C7) — e.g. taking the depth from the ini, which holds the SAVED, not the current,
    // state.
    public static class UEViewport
    {
        // WIDTH IS ROUNDED UP TO EVEN — BEFORE applying and before the fields are written:
        //
        //   WinDrv.dll.c:15152  NewX = (NewX + 1) & ~1
        //   WinDrv.dll.c:15297  Viewport->SizeX = <this value>
        //   WinDrv.dll.c:15299  Viewport->SizeY = NewY        (height is NOT touched)
        //
        // Hence `SetRes 801x600` in shipped gives 802x600, and 802 goes to the ini. Without this rule the port cannot
        // even ASK "was it applied": it would compare the screen with a value shipped never requested.
        //
        // The DEVICE applies it because in shipped it sits inside ResizeViewport, i.e. before the platform call and on
        // all paths — including opening the window at the saved size.
        public static int NormalizeSizeX(int sizeX) => (sizeX + 1) & ~1;

        // INITIAL COLOUR DEPTH — FROM THE VIEWPORT CONSTRUCTOR, NOT FROM THE ini.
        //
        //   :5810-5825  the constructor queries the DESKTOP (GetDeviceCaps BITSPIXEL): 8 -> 2; 24/32 -> 4;
        //               otherwise 2 + flag;
        //   :4907-4919  opening the window passes NewColorBytes = -1;
        //   :15062-15069 with -1 ResizeViewport takes the ALREADY SET Viewport->ColorBytes (this+0x78), i.e. the
        //               constructor's value.
        //
        // So the shipped `WindowedColorBits=16` does NOT mean the live viewport starts at 16 bits: the ini holds the
        // SAVED value, and the start state comes from the desktop. Of the three constructor branches exactly one is
        // reachable on a modern target — 24/32 bits -> 4. Modelling another desktop would be a separate platform
        // capability, not an ini value.
        public const int DesktopColorBytes = 4;
    }

    public interface IViewportDevice
    {
        // The ACTUAL viewport size right now — nothing else. An unfinished request must not be returned here: the
        // menu branch (FEOptionsPage.uc:539) depends on this answer, and a "wished" value degenerates the comparison.
        int SizeX { get; }
        int SizeY { get; }

        // Bytes per pixel right now. Zero means "the device has not named the depth yet" — shipped then treats it as 2
        // (WinDrv.dll.c:5102), and the READER substitutes the two, not the device.
        int ColorBytes { get; }

        // ACTUAL fullscreen state right now — exactly what shipped `UWindowsViewport::IsFullscreen()` answers
        // (WinDrv.dll.c:1619, `BlitFlags & BLIT_Fullscreen`, field this+400). TWO shipped rules depend on it:
        //   * `SetRes` passes it to the resize as is, i.e. the menu command does NOT change the mode
        //     (WinDrv.dll.c:5326-5330);
        //   * the tail of a successful resize saves the applied values into Fullscreen* keys in fullscreen mode and
        //     into Windowed* in windowed mode (:15366-15379).
        // A request has no place here, for the same reason as the size.
        bool Fullscreen { get; }

        // A real resize. `colorBytes` == 0 means "keep the depth" — that is how `SetRes 800x600` without a third
        // piece arrives.
        //
        // `fullscreen` — the mode IN WHICH to apply the size. It is a separate argument because that is what
        // BLIT_Fullscreen is in shipped: the same ResizeViewport opens the initial fullscreen viewport
        // (WinDrv.dll.c:4896-4908) and performs a windowed `SetRes`, and exactly this bit tells them apart — it also
        // selects the pair of keys to save.
        //
        // Applied — the size is ALREADY applied, the properties above report the new state.
        // Rejected — not applied, there must be no trace anywhere.
        ViewportResizeOutcome Resize(int sizeX, int sizeY, int colorBytes, bool fullscreen);

        // Device screen modes, "WxH" space-separated in ascending order — the GetRes answer. Shipped lists the
        // driver's modes in fullscreen mode (WinDrv.dll.c:5126); the port always returns them (author decision:
        // the windowed constants 320x240..800x600 are meaningless on a modern screen).
        string DisplayModes { get; }
    }
}
