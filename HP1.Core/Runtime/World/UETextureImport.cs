// TEXTURE FROM BMP AT RUNTIME — native 321 CreateTextureFromBMP.
//
// Callers: the menu save-slot pages (FESlotPage.UpdateSlots — "..\Save\SGS <level>[<point>].bmp", then
// "..\Save\DefaultGameSnap.bmp"; FEFilePage.ShowWindowFile — SavePath\SaveGameSnap<i>.bmp). The 128×128×8 files are
// placed into <game>\Save by the installer; they are not on the shipped disc.
//
// Shipped chain (disasm, all Engine.dll unless marked):
//   AActor::execCreateTextureFromBMP 0x10413630:
//     P_GET_STR(TexName), P_GET_STR(FileName), P_FINISH; *Result = NULL;
//     Pkg = UObject::CreatePackage(NULL, FName(L"SavePics", FNAME_Add));
//     T = ImportObject<UTexture>(Pkg, FName(*TexName), RF_Public|RF_Standalone
//         (0x80004), *FileName, NULL, NULL, NULL, GWarn)   // FUN_10417310
//     if (T) *Result = T.
//   UFactory::StaticImportObject (Core.dll 0x10124d40): factories are UFactory classes with SupportedClass == Texture
//     (EXACT, +0x28) and ImportPriority >= 0; the game has one — UEngineTextureFactory (StaticConstructor 0x10425880:
//     SupportedClass = UTexture, Formats "bmp;Bitmap files;pcx;PC Painbrush files", bCreateNew cleared); the editor
//     UTextureFactory lives in Editor.dll, which the game does not load. Non-empty file name, bText = 0 — binary
//     branch: appLoadFileToArray (relative to the process working dir = System), a 0 is appended to the data,
//     FactoryCreateBinary(..., Type = appFExt, Buffer, Buffer+Num-1). No file — no result,
//     Warn->Logf(LocalizeError("NoFindImport")) and NULL.
//   UEngineTextureFactory::FactoryCreateBinary 0x10425a50, BMP branch:
//     Length >= 0x36 and "BM"; biWidth/biHeight powers of two, else "Texture dimensions are not powers of two", NULL;
//     biCompression != 0 — "RLE compression of BMP images not supported", NULL; biPlanes 1 and biBitCount 8 —
//     StaticConstructObject(Texture, Pkg, Name, Flags), UTexture::Init(W,H) (0x10422340: USize=UClamp=W,
//     VSize=VClamp=H, UBits/VBits = appCeilLogTwo, one mip W×H), PostLoad; palette — min(256,
//     biClrUsed ? biClrUsed : 256) entries FColor(R=pal[i*4+2], G=pal[i*4+1], B=pal[i*4], A=255), padded with
//     (0,0,0,255) to 256; BMP rows bottom-up: mip[(H-1-y)*W .. +W] = data[bfOffBits + y*Align(W,4) ..].
//     PCX branch (0x10425e3f): 8-bit one-plane RLE (see Pcx). Tail (0x10426066): name "…_A##" — animation frame
//     linking (see Finish); -COMPRESSDXT — compression; mask: Palette->Colors(0) == FColor(255,0,255,255) and at least
//     one mip pixel 0 -> PolyFlags |= PF_Masked, else &= ~PF_Masked.
//     Judge: se_harness/bsp_diff/texture_factory_oracle.py (the real factory under unicorn, synthetic BMP/PCX files).
//
// NOT PORTED (loud): 24-bit BMP and 3-plane PCX (TEXF_RGBA8: a runtime RGBA8 texture has no frontend path, B30;
// all 30 shipped snapshots are 8-bit), -COMPRESSDXT (a command-line switch the game is not started with).
//
// Result: a VM object of class Texture in package SavePics with the fields scripts read
// (USize/VSize/UClamp/VClamp/UBits/VBits/bMasked), and the pixels in Core's runtime texture registry
// (UERuntimeTextures): the frontend rasterizes them by path "SavePics.<name>" like package textures — it has no BMP
// parser of its own (C5/C7).
using System;
using System.Collections.Generic;
using System.IO;
using UELib.Core;

namespace HP1.UEScript
{
    // Runtime texture pixels (P8 + palette), as FactoryCreateBinary left them.
    public sealed class UERuntimeTexture
    {
        public readonly string Path;          // "SavePics.save0" — the texture's GetPathName
        public readonly int USize, VSize;
        public readonly byte[] Indices;       // mip 0, rows top-down, USize*VSize
        public readonly byte[] PaletteRGBA;   // 256 × (R,G,B,A)
        public readonly bool Masked;          // PF_Masked by the factory rule
        public readonly int Version;          // grows when the object with the same name is recreated
        internal UERuntimeTexture(string path, int u, int v, byte[] idx, byte[] pal, bool masked, int version)
        { Path = path; USize = u; VSize = v; Indices = idx; PaletteRGBA = pal; Masked = masked; Version = version; }
    }

    // Process runtime texture registry: in shipped these are objects of package SavePics living until exit
    // (RF_Standalone). Key — full path.
    public static class UERuntimeTextures
    {
        static readonly Dictionary<string, UERuntimeTexture> _byPath = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, UEObject> _objects = new(StringComparer.OrdinalIgnoreCase);
        static int _version;

        public static bool TryGet(string path, out UERuntimeTexture tex)
        {
            lock (_byPath) return _byPath.TryGetValue(path ?? "", out tex);
        }

        // Path of the VM texture object if it is a runtime texture; otherwise null.
        public static string PathOf(UEObject o)
        {
            if (o is null || o.OuterPath is null) return null;
            string path = o.OuterPath + "." + o.GetProp(PK.Name);
            lock (_byPath) return _byPath.ContainsKey(path) ? path : null;
        }

        // StaticConstructObject with a name already taken in the package recreates THE SAME object in place —
        // pointers to it (menu buttons) remain valid.
        internal static UEObject ObjectFor(string package, string name, Func<UEObject> create)
        {
            string path = package + "." + name;
            lock (_byPath)
            {
                if (!_objects.TryGetValue(path, out UEObject o)) _objects[path] = o = create();
                return o;
            }
        }

        // StaticFindObject(UTexture, package, name) over the textures this registry constructed (FName: case-insensitive).
        internal static UEObject Find(string package, string name)
        {
            lock (_byPath) return _objects.TryGetValue(package + "." + name, out UEObject o) ? o : null;
        }

        internal static UERuntimeTexture Publish(string path, int u, int v, byte[] idx, byte[] pal, bool masked)
        {
            lock (_byPath)
            {
                var t = new UERuntimeTexture(path, u, v, idx, pal, masked, ++_version);
                _byPath[path] = t;
                return t;
            }
        }
    }

    public static class UETextureImport
    {
        public const string SavePicsPackage = "SavePics";   // 0x104db878

        // AActor::execCreateTextureFromBMP (0x10413630). `workingDir` — the shipped process working directory (the
        // installation's System): the file name from the script is relative ("..\Save\…").
        public static UEObject CreateTextureFromBMP(UEInterpreter interp, string texName, string fileName,
            string workingDir, Action<string> log)
            => StaticImportTexture(interp, SavePicsPackage, string.IsNullOrEmpty(texName) ? "None" : texName,
                fileName ?? "", workingDir, log);

        // UFactory::StaticImportObject (Core.dll 0x10124d40) for Class = Texture.
        static UEObject StaticImportTexture(UEInterpreter interp, string package, string name,
            string fileName, string workingDir, Action<string> log)
        {
            // The only factory: bCreateNew cleared, bText = 0, non-empty name -> binary.
            if (fileName.Length == 0)
            {
                log?.Invoke($"import_texture name={name} file= — empty name: no factories for it");
                return Fail(fileName, log);
            }
            log?.Invoke($"FactoryCreateBinary: Texture with EngineTextureFactory (0 0 {fileName})");
            string path = ResolveLikeWindows(workingDir, fileName);
            if (path is null) return Fail(fileName, log);
            byte[] data = File.ReadAllBytes(path);
            UEObject tex = FactoryCreateBinary(interp, package, name, data, log);
            return tex ?? Fail(fileName, log);
        }

        static UEObject Fail(string fileName, Action<string> log)
        {
            log?.Invoke($"import_texture NoFindImport file={fileName}");
            return null;
        }

        // appLoadFileToArray: path relative to the working directory, Windows separator, case-insensitive file
        // system.
        static string ResolveLikeWindows(string workingDir, string fileName)
        {
            string rel = fileName.Replace('\\', '/');
            string full = System.IO.Path.IsPathRooted(rel) ? rel
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(workingDir ?? ".", rel));
            return UEInstallPath.File(full);
        }

        static int I32(byte[] b, int o) => BitConverter.ToInt32(b, o);
        static int U16(byte[] b, int o) => b[o] | (b[o + 1] << 8);

        // UEngineTextureFactory::FactoryCreateBinary (0x10425a50), BMP branch.
        internal static UEObject FactoryCreateBinary(UEInterpreter interp, string package, string name,
            byte[] data, Action<string> log)
        {
            // Length = BufferEnd - Buffer: the zero appended by StaticImportObject is excluded.
            int length = data.Length;
            if (length < 0x36 || data[0] != (byte)'B' || data[1] != (byte)'M')
            {
                // Not BMP (0x10425a88/0x10425a91 -> 0x10425e2b): PCX needs Length >= 0x80 and first byte 0x0a
                // (0x10425e2b/0x10425e36); anything else -> 0x1042649d: Warn->Logf("Bad image format for texture
                // import") (0x104dbd60), NULL. Players hit it with a broken Save\SGS file (issue #4).
                if (length >= 0x80 && data[0] == 0x0a) return Pcx(interp, package, name, data, log);
                log?.Invoke("Bad image format for texture import");
                return null;
            }
            int w = I32(data, 0x12), h = I32(data, 0x16);
            if ((w & (w - 1)) != 0 || (h & (h - 1)) != 0)
            {
                log?.Invoke("Texture dimensions are not powers of two");
                return null;
            }
            if (I32(data, 0x1e) != 0)
            {
                log?.Invoke("RLE compression of BMP images not supported");
                return null;
            }
            int planes = U16(data, 0x1a), bits = U16(data, 0x1c);
            if (planes == 1 && bits == 24)
                throw new NotSupportedException(
                    $"CreateTextureFromBMP {name}: 24-bit BMP (0x10425d06, TEXF_RGBA8) not ported — "
                    + "a runtime RGBA8 texture has no frontend path (B30), §8");
            if (!(planes == 1 && bits == 8))
            {
                log?.Invoke($"BMP uses an unsupported format ({planes}/{bits})");
                return null;
            }

            UEObject tex = Construct(interp, package, name, w, h);
            // Palette: FColor(R=pal[2], G=pal[1], B=pal[0], A=255) × min(256, biClrUsed||256), padded with black.
            int clrUsed = I32(data, 0x2e);
            int count = clrUsed == 0 || clrUsed >= 0x100 ? 0x100 : clrUsed;
            var pal = new byte[256 * 4];
            for (int i = 0; i < 256; i++)
            {
                if (i < count)
                {
                    int o = 0x36 + i * 4;
                    pal[i * 4] = data[o + 2]; pal[i * 4 + 1] = data[o + 1]; pal[i * 4 + 2] = data[o];
                }
                pal[i * 4 + 3] = 0xff;
            }
            // Rows bottom-up: mip[(H-1-y)*W] <- bfOffBits + y*Align(W,4).
            int off = I32(data, 0x0a), stride = (w + 3) & ~3;
            var idx = new byte[w * h];
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(data, off + y * stride, idx, (h - 1 - y) * w, w);
            return Finish(tex, package, name, w, h, idx, pal, log);
        }

        // StaticConstructObject(Texture, Pkg, Name, 0x80004) + UTexture::Init(W,H) (0x10422340) + PostLoad.
        static UEObject Construct(UEInterpreter interp, string package, string name, int w, int h)
        {
            UClass cls = HP1.Core.UEClassRegistry.Shared.Find("Texture")
                ?? throw new InvalidOperationException("CreateTextureFromBMP: class Engine.Texture not found");
            // StaticConstructObject(Texture, SavePics, Name, 0x80004): a taken name — the same object, recreated
            // from defaults.
            UEObject tex = UERuntimeTextures.ObjectFor(package, name, () => new UEObject { Class = cls, Interp = interp });
            tex.Props.Clear();
            tex.Class = cls;
            tex.LoadDefaults();
            tex.OuterPath = package;
            tex.Props["Name"] = new UEName(name);
            tex.Props["USize"] = w; tex.Props["UClamp"] = w;
            tex.Props["VSize"] = h; tex.Props["VClamp"] = h;
            tex.Props["UBits"] = (byte)CeilLogTwo(w);
            tex.Props["VBits"] = (byte)CeilLogTwo(h);
            return tex;
        }

        // Original: Engine.dll ?FactoryCreateBinary@UEngineTextureFactory@@UAEPAVUObject@@PAVUClass@@PAV2@VFName@@K2PBGAAPBEPBEPAVFFeedbackContext@@@Z [HP1 0x10425E3F-0x10425FFF]
        // PCX: W = Xmax - Xmin + 1, H = Ymax - Ymin + 1 (u16 at 8/4, 10/6), powers of two (else 0x10426493: the
        // "not powers of two" log, NULL). One plane of 8 bits: the RLE body from +0x80 fills the mip CONTINUOUSLY
        // (BytesPerLine is not read): a byte with both top bits set is a run (count = low 6 bits, even 0; value =
        // the next byte; the memset is clamped to the mip end, the write pointer advances by the full count), any
        // other byte one pixel; the palette is the 0x300 bytes before BufferEnd, FColor(R,G,B,255) (the 0x0C marker is
        // not checked). Three planes of 8 bits (0x104262bd): TEXF_RGBA8, STOP like the 24-bit BMP. Else NULL.
        static UEObject Pcx(UEInterpreter interp, string package, string name, byte[] data, Action<string> log)
        {
            int w = U16(data, 8) - U16(data, 4) + 1, h = U16(data, 10) - U16(data, 6) + 1;
            if (((w & (w - 1)) | (h & (h - 1))) != 0)
            {
                log?.Invoke("Texture dimensions are not powers of two");
                return null;
            }
            if (data[0x41] == 3 && data[3] == 8)
                throw new NotSupportedException(
                    $"CreateTextureFromBMP {name}: 24-bit PCX (0x104262bd, TEXF_RGBA8) not ported — "
                    + "a runtime RGBA8 texture has no frontend path (B30), §8");
            if (!(data[0x41] == 1 && data[3] == 8))
            {
                log?.Invoke($"PCX uses an unsupported format ({data[0x41]}/{data[3]})");
                return null;
            }
            // A zero-sized side (Xmax < Xmin wraps to a power of two only for W = 0) or a palette before the buffer:
            // the original reads outside the file — STOP.
            if (w <= 0 || h <= 0 || data.Length < 0x300)
                throw new NotSupportedException($"CreateTextureFromBMP {name}: PCX {w}x{h}, {data.Length} bytes — the original reads outside the file, §8");
            UEObject tex = Construct(interp, package, name, w, h);
            var idx = new byte[w * h];
            int src = 0x80;
            // StaticImportObject appends one zero (Core 0x10124d40): the decoder may read it, nothing past it.
            byte At(int i) => i < data.Length ? data[i]
                : i == data.Length ? (byte)0
                : throw new NotSupportedException($"CreateTextureFromBMP {name}: PCX body ends before the {w}x{h} image — the original reads past the file, §8");
            for (int dst = 0; dst < idx.Length;)
            {
                byte b = At(src++);
                if ((b & 0xc0) == 0xc0)
                {
                    int run = b & 0x3f;
                    byte v = At(src++);
                    for (int k = 0, n = Math.Min(run, idx.Length - dst); k < n; k++) idx[dst + k] = v;
                    dst += run;
                }
                else idx[dst++] = b;
            }
            var pal = new byte[256 * 4];
            int p0 = data.Length - 0x300;
            for (int i = 0; i < 256; i++)
            {
                pal[i * 4] = data[p0 + i * 3]; pal[i * 4 + 1] = data[p0 + i * 3 + 1]; pal[i * 4 + 2] = data[p0 + i * 3 + 2];
                pal[i * 4 + 3] = 0xff;
            }
            return Finish(tex, package, name, w, h, idx, pal, log);
        }

        // Original: Engine.dll ?FactoryCreateBinary@UEngineTextureFactory@@UAEPAVUObject@@PAVUClass@@PAV2@VFName@@K2PBGAAPBEPBEPAVFFeedbackContext@@@Z [HP1 0x10426066-0x1042645B]
        // Tail of every format: a name ending "_A" + two ASCII digits (the letter upper-cased only from a-z) is an
        // animation frame N = appAtoi(digits): N > 0 — the texture "<prefix>_A%02i"(N-1) in the same package, if
        // any, gets AnimNext = this (0x104261a4); N < 99 — "<prefix>_A%02i"(N+1), if any, becomes this AnimNext
        // (0x1042620b). -COMPRESSDXT is a command-line switch the game is never started with. Mask: Colors(0) ==
        // (255,0,255,255) and some mip pixel 0 -> PF_Masked, else cleared.
        static UEObject Finish(UEObject tex, string package, string name, int w, int h, byte[] idx, byte[] pal, Action<string> log)
        {
            int len = name.Length;
            if (len >= 4 && name[len - 4] == '_' && (name[len - 3] is 'A' or 'a')
                && name[len - 2] is >= '0' and <= '9' && name[len - 1] is >= '0' and <= '9')
            {
                int n = (name[len - 2] - '0') * 10 + (name[len - 1] - '0');
                string stem = name.Substring(0, len - 2);
                log?.Invoke($"Texture animation frame {n}: {name}");
                if (n > 0 && UERuntimeTextures.Find(package, stem + (n - 1).ToString("00")) is UEObject prev)
                {
                    prev.Props["AnimNext"] = tex;
                    log?.Invoke($"   Linked to previous: {prev.GetProp(PK.Name)}");
                }
                if (n < 99 && UERuntimeTextures.Find(package, stem + (n + 1).ToString("00")) is UEObject next)
                {
                    tex.Props["AnimNext"] = next;
                    log?.Invoke($"   Linked to next: {next.GetProp(PK.Name)}");
                }
            }
            // Mask: Colors(0) == (255,0,255,255) and some pixel is 0 (0x10426261..0x1042644b).
            bool magenta = pal[0] == 0xff && pal[1] == 0 && pal[2] == 0xff && pal[3] == 0xff;
            bool masked = magenta && Array.IndexOf(idx, (byte)0) >= 0;
            tex.Props["bMasked"] = masked;

            UERuntimeTexture rt = UERuntimeTextures.Publish(package + "." + name, w, h, idx, pal, masked);
            log?.Invoke($"import_texture {rt.Path} {w}x{h} masked={masked} v={rt.Version}");
            return tex;
        }

        // appCeilLogTwo (Core): the smallest k with 2^k >= x.
        static int CeilLogTwo(int x)
        {
            int k = 0;
            while ((1 << k) < x) k++;
            return k;
        }
    }
}
