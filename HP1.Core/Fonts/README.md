# Bundled font (NOT ORIGINAL)

`Philosopher-Regular.ttf` — Philosopher 2.001 Regular by Jovanny Lemonad (https://github.com/alexeiva/philosopher; file
taken from https://github.com/google/fonts `ofl/philosopher/Philosopher-Regular.ttf`), unmodified, SHA-256
`e1cba0b5003b5694f1d6cc0fbe629f78362ad38a9ffd4d0cb7abfd9698a575ed`. License: SIL Open Font License 1.1
(`Philosopher-OFL.txt`; no Reserved Font Name).

Use: the RU (EA) edition's `HPConsole.SetupLanguage` asks for OS fonts — `CreateNativeFont('MS Sans Serif', 24/16/10/12)`
(`System/SAPFont.rus`). The original draws them with GDI (WinDrv `UWindowsViewport::DrawString`); MS Sans Serif is a
Windows system font that is not part of any install. Author decision 2026-11-03 ("a free font with Cyrillic"):
a free font with Cyrillic, bundled with the port — an improvement-class substitution, not original. Author decision
2026-11-20: Philosopher (a book-style face with Cyrillic, the face the 25 Years HD mod asks for) replaces Liberation Sans
(Arial metrics, used 2026-11-03..2026-11-20), whose plain sans text did not suit the storybook menus. Core rasterises it
(`UENativeFont.cs`), antialiased and at the interface scale (author 2026-11-16, not original: GDI draws
NONANTIALIASED_QUALITY pixels at the canvas resolution).

Every other face a script asks for (mods, e.g. 25 Years HD's `CreateNativeFont('Philosopher', 42)`) gets the same
font: author decision 2026-11-15, not original (the original's GDI font mapper substitutes on the user's Windows).
