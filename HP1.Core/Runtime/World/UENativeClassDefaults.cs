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
            // 6. INDEPENDENT CONFIRMATION. The SurrealEngine reimplementation has the same value:
            //    USubsystem.h:198 `TextureDetail = "High"`.
            //
            // Result: byte 0 -> Details(0) -> "High".
            //
            // HP2 (hgame.u asks the same key; [WinDrv.WindowsClient] of HP2 default.ini has none) — the same chain on HP2's
            // own bodies: UClient::StaticConstructor [HP2 Engine 0x10303fdf -> body 0x1038e5f0, Engine.dll.c:245014-245135] fills
            // Details High, Medium, Low, Ultra Low (UTF-16 at 0x104d8e1c / 0x104d8e0c / 0x104d8e00 / 0x104d8de8) and
            // declares TextureDetail UByteProperty +0x7c, Display, 0x4000; UClient::UClient [HP2 0x10304070] writes
            // 0x2c/0x30/0x34, UWindowsClient::UWindowsClient [HP2 WinDrv] 0xa0/0x108/0x174/0x178/0x17c/0xfc (disasm
            // stores; HP2 drops HP1's 0x180..0x186 hotkey atoms, not properties); UWindowsClient::StaticConstructor [HP2
            // WinDrv 0x11101177] declares 0xa4..0xc8 only. Core UClass::Register, InitClassDefaultObject, InitProperties:
            // HP2 = HP1 (tools/hp2/body_diff.py: identical but assert line numbers / the reviewed varargs formatter).
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

        // ── WHAT A DLL-ONLY CLASS DECLARES ──────────────────────────────────
        //
        // UObject::StaticExec GET asks FindField<UProperty>(Class, name) BEFORE any value (Core.dll.c:3389-3419): a
        // property the class chain does not declare answers "Unrecognized property <name>", whatever the ini holds
        // (LoadConfig reads declared properties only). A DLL-only class's own properties are the ones its
        // StaticConstructor creates; a row lists them, with its super class (FindField walks own, then super). A super
        // class without a row is a script class: its fields come from the package (UEClassRegistry).
        public readonly struct DllClass
        {
            public DllClass(UEEdition.Game game, string cls, string super, string[] properties, string source)
            {
                Game = game; Class = cls; Super = super; Properties = properties; Source = source;
            }
            public readonly UEEdition.Game Game;
            public readonly string Class, Super, Source;
            public readonly string[] Properties;
        }

        static readonly DllClass[] DllClasses =
        {
            new(UEEdition.Game.HP2, "ALAudio.ALAudioSubsystem", "Engine.AudioSubsystem",
                new[] { "UseEAX", "CompatibilityMode", "UsePrecache", "ReverseStereo", "Channels", "MusicVolume", "SoundVolume",
                        "DopplerFactor", "RollOff" },
                "Original: ALAudio.dll ?StaticConstructor@UALAudioSubsystem@@QAEXXZ [HP2 0x10002800]: up to its only ret "
                + "(0x10002aeb) nine FName(name, 1) + new(GetClass()) UBool/UInt/UFloatProperty + UProperty::UProperty(0, offset, "
                + "\"ALAudio\", 0x4000): UseEAX bool +0xc0, CompatibilityMode bool +0xc4, UsePrecache bool +0xbc, ReverseStereo "
                + "bool +0xb8, Channels int +0xb4, MusicVolume float +0xa8, SoundVolume float +0xac, DopplerFactor float +0xa4, "
                + "RollOff float +0xb0 (UTF-16 names at 0x1000e0f4..0x1000e23c). Super: the class registration [HP2 ALAudio "
                + "0x100025d0] UClass::UClass(.., 0xf0, 4, Engine ?PrivateStaticClass@UAudioSubsystem (import 0x10009220), .., "
                + "\"ALAudioSubsystem\", .., \"System\", 0x4084004, 0x10001cf0, 0x10002800)"),
            new(UEEdition.Game.HP2, "Engine.AudioSubsystem", "Core.Subsystem", Array.Empty<string>(),
                "Engine.dll's class registration [HP2 0x10389200..0x10389290] UClass::UClass(.., 0x2c, 5, Core "
                + "?PrivateStaticClass@USubsystem (import 0x105ffe3c), .., \"AudioSubsystem\", \"Engine\", \"System\", 0x4084004, "
                + "?InternalConstructor@UObject, ?StaticConstructor@UObject (import 0x105ffb3c)); UObject::StaticConstructor "
                + "[HP2 Core 0x10101041 -> 0x1015c4b0] is a bare ret: no properties"),
        };

        // Whether FindField on `cls` (an ini section name, Package.Class) misses `property`: true only for a tabled DLL-only
        // class whose whole chain is known; false otherwise (not tabled, or the property is declared).
        public static bool Undeclared(string cls, string property)
        {
            bool tabled = false;
            for (string c = cls; c is not null;)
            {
                DllClass row = Array.Find(DllClasses, r => r.Game == UEGameProfile.Current.Game
                                                           && string.Equals(r.Class, c, StringComparison.OrdinalIgnoreCase));
                if (row.Class is null)
                {
                    if (!tabled) return false;
                    int dot = c.IndexOf('.');
                    UELib.Core.UClass script = dot > 0 ? HP1.Core.UEClassRegistry.Shared?.Find(c.Substring(0, dot), c.Substring(dot + 1)) : null;
                    if (script is null)
                        throw new NotSupportedException($"FindField: super class {c} of {cls} is neither tabled nor a loaded script class (STOP)");
                    for (UELib.Core.UClass k = script; k is not null; k = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(k))
                        foreach (UELib.Core.UProperty p in k.EnumerateFields<UELib.Core.UProperty>())
                            if (string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase)) return false;
                    return true;
                }
                tabled = true;
                if (Array.Exists(row.Properties, p => string.Equals(p, property, StringComparison.OrdinalIgnoreCase))) return false;
                c = row.Super;
            }
            return tabled;
        }

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
