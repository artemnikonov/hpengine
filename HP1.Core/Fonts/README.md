# Bundled font (NOT ORIGINAL)

`LiberationSans-Regular.ttf` — Liberation Sans 2.1.5 Regular (https://github.com/liberationfonts/liberation-fonts,
release tarball `liberation-fonts-ttf-2.1.5.tar.gz`), unmodified, SHA-256
`76d04c18ea243f426b7de1f3ad208e927008f961dc5945e5aad352d0dfde8ee8`. License: SIL Open Font License 1.1
(`LiberationSans-LICENSE.txt`; Reserved Font Names "Liberation", "Arimo" — the file is shipped unmodified, so the
names are kept).

Use: the RU (EA) edition's `HPConsole.SetupLanguage` asks for OS fonts — `CreateNativeFont('MS Sans Serif', 24/16/10/12)`
(`System/SAPFont.rus`). The original draws them with GDI (WinDrv `UWindowsViewport::DrawString`); MS Sans Serif is a
Windows system font that is not part of any install. Author decision 2026-11-03 ("a free font with Cyrillic"):
a free font with Cyrillic, bundled with the port — an improvement-class substitution, not original. Liberation Sans has
Arial metrics, the closest free match to (Microsoft) Sans Serif. Core rasterises it (`UENativeFont.cs`), antialiased and at the
interface scale (author 2026-11-16, not original: GDI draws NONANTIALIASED_QUALITY pixels at the canvas resolution).

Every other face a script asks for (mods, e.g. 25 Years HD's `CreateNativeFont('Philosopher', 42)`) gets the same
font: author decision 2026-11-15, not original (the original's GDI font mapper substitutes on the user's Windows).
