using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // DEFAULTS OF NATIVE ENGINE CLASSES.
    //
    // WHY. In shipped, `get ini:<ClassRef> <Property>` is NOT answered from the ini.
    // UObject::ResolveName (Core.dll.c:52103-52131) replaces the name with the ini value —
    // `ini:Engine.Engine.ViewportManager` becomes the class name `WinDrv.WindowsClient` — and GET
    // takes the value from THAT CLASS'S DEFAULTS. An ini key only OVERRIDES the default:
    // UClass::Register (Core.dll.c:27436) builds the CDO and calls UObject::LoadConfig only at the
    // end. No key — the default is the answer; that is normal, not a data defect.
    //
    // WHY A TABLE, NOT A PACKAGE READ. Classes like `WinDrv.WindowsClient` are registered by the
    // DLL itself: shipped data has no `WinDrv.u` package, so the default cannot come from data —
    // it must be taken from the binary.
    //
    // FILL RULE. A row appears here ONLY when both hold:
    //   1) the campaign asks for the value (there is a live call in `.uc`);
    //   2) the value is PROVEN from shipped code, and the proof is written in full in the Source
    //      field — link by link, not as a "see the binary" reference.
    // Anything unproven does NOT go here: `get ini:` still raises a loud STOP for it. A guess
    // passed off as a default is indistinguishable from a real answer.
    //
    // DEMAND. Across the whole campaign `get ini:` asks exactly five keys:
    //   Engine.Engine.AudioDevice     MusicVolume     — present in shipped ini
    //   Engine.Engine.AudioDevice     SoundVolume     — present in shipped ini
    //   Engine.Engine.AudioDevice     Use3dHardware   — present in shipped ini
    //   Engine.Engine.ViewportManager Brightness      — present in shipped ini
    //   Engine.Engine.ViewportManager TextureDetail   — ABSENT from shipped ini
    // So the table covers exactly one key. `SkinDetail` (same declaration, offset 0x80, same enum)
    // is NEVER asked by the campaign and is deliberately absent.
    public static class UENativeClassDefaults
    {
        public readonly struct Entry
        {
            public Entry(string cls, string property, string value, string source)
            {
                Class = cls;
                Property = property;
                Value = value;
                Source = source;
            }

            // Class name — what `ResolveSection` already expanded the reference to,
            // i.e. the ini section name (`WinDrv.WindowsClient`).
            public readonly string Class;
            public readonly string Property;
            public readonly string Value;

            // The full proof. Cannot be empty — a fixture checks it: a row without a
            // source is invention.
            public readonly string Source;
        }

        static readonly Entry[] Table =
        {
            // ── UClient.TextureDetail = High ────────────────────────────────
            //
            // The chain, link by link, each checkable:
            //
            // 1. TYPE AND PLACE. UClient::StaticConstructor declares `TextureDetail` as a
            //    UByteProperty at offset 0x7c, category Display, flags 0x4000 (config), typed by
            //    an enum created right there (Engine.dll.c:121917-121924).
            //
            // 2. ENUM ORDER. The same StaticConstructor fills enum `Details` with names in
            //    ascending index: High(0), Medium(1), Low(2), UltraLow(3)
            //    (Engine.dll.c:121814-121845). So byte 0 reads as High.
            //
            // 3. CDO ZEROED. UClass::Register (Core.dll.c:27436) allocates the defaults buffer
            //    via TArray::Add — which does NOT zero (:41371) — and immediately calls
            //    UObject::InitClassDefaultObject (:4576), which does `appMemset(this,0,0x28)` and
            //    `InitProperties(this, PropertiesSize, SuperClass, NULL, 0)`. InitProperties (:145)
            //    with Defaults=NULL copies the SUPERCLASS defaults up to its size and ZEROES THE
            //    WHOLE REST up to PropertiesSize (:192-205). Properties declared by UClient itself —
            //    including 0x7c — lie exactly in that rest.
            //
            // 4. CONSTRUCTORS DO NOT TOUCH 0x7c. UClient::UClient (Engine.dll.c:123397) writes only
            //    0x2c/0x30/0x34; UWindowsClient::UWindowsClient (WinDrv.dll.c:2735) — only
            //    0xa0/0x108/0x174/0x178/0x17c/0x180/0x182/0x184/0x186/0xfc. Neither writes 0x7c.
            //    (Checking only the UWindowsClient constructor would not suffice: the property is
            //    declared by the BASE Engine.UClient and inherited by WinDrv.WindowsClient.)
            //
            // 5. INI DOES NOT OVERRIDE. The shipped [WinDrv.WindowsClient] section exists and is
            //    complete (WindowedViewportX/Y, Brightness, MipFactor, etc.), but has no
            //    TextureDetail key, nor does DefUser.ini anywhere. So LoadConfig at the end of
            //    UClass::Register does not change the default. (`TextureDetailBias=Near` in
            //    Default.ini:283 is a DIFFERENT key in another section, unrelated.)
            //
            // 6. INDEPENDENT CONFIRMATION. SurrealEngine reached the same value on its own:
            //    USubsystem.h:198 `TextureDetail = "High"`.
            //
            // Result: byte 0 -> Details(0) -> "High".
            new Entry("WinDrv.WindowsClient", "TextureDetail", "High",
                "Engine.dll.c:121814-121845 (enum Details: High,Medium,Low,UltraLow); "
                + "Engine.dll.c:121917-121924 (UByteProperty TextureDetail @0x7c, Display, config); "
                + "Core.dll.c:27436 UClass::Register -> :4576 InitClassDefaultObject -> "
                + ":145 InitProperties(Defaults=NULL) zeroes the remainder past the superclass size; "
                + "Engine.dll.c:123397 UClient::UClient and WinDrv.dll.c:2735 "
                + "UWindowsClient::UWindowsClient do not write 0x7c; the key is absent from the shipped ini files; "
                + "independently: SurrealEngine USubsystem.h:198 TextureDetail=\"High\""),
        };

        public static IReadOnlyList<Entry> All => Table;

        // The class default is the LOWEST value layer, exactly as in shipped:
        // the saved overlay overrides ini, ini overrides the default.
        public static bool TryGet(string cls, string property, out string value)
        {
            foreach (Entry e in Table)
                if (string.Equals(e.Class, cls, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Property, property, StringComparison.OrdinalIgnoreCase))
                {
                    value = e.Value;
                    return true;
                }
            value = null;
            return false;
        }
    }
}
