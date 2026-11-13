#!/bin/sh
# Third-party tools HP1Setup runs to unpack a game installer: innoextract 1.9 (zlib license; Inno Setup installers,
# Setup.exe + Setup-1.bin). Used by hpengine/packaging/make_launcher.sh; licenses in public/THIRD_PARTY_NOTICES.md.
#
#   sh launcher/third_party.sh <RID> <package dir>      RID: osx-arm64 | osx-x64 | win-x64 | linux-x64
#
# Puts <package dir>/tools/innoextract (win: innoextract.exe) and the license texts of everything inside it in
# <package dir>/tools/licenses/. Every download is pinned below by SHA-256 and checked before use; a mismatch stops.
#   win-x64, linux-x64: the official innoextract 1.9 release binaries (Linux: needs only glibc; signed by the author, GPG key
#     ADE9653703D4ADE0E997758128555A66D7E1DEC9 — the pins are of the files whose signatures verified).
#   osx-arm64, osx-x64 (no official macOS binary): built here for the RID's architecture from the official sources — innoextract 1.9 with the three
#     upstream commits Homebrew applies for current CMake/Boost, Boost 1.86.0 (iostreams filesystem date_time
#     program_options, static) and xz 5.6.3 (liblzma, static); zlib and bzip2 from macOS. Needs cmake and Xcode
#     Command Line Tools. Deployment target macOS 13.0, like the Launcher.
# Downloads and builds are cached in HPENGINE_TOOLS_CACHE (default <repo>/.artifacts/tools-cache).
# The installer itself is never run: innoextract only reads it.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/.." && pwd)
fail() { echo "third_party: FAILED: $*" >&2; exit 1; }
[ $# -eq 2 ] || fail "usage: third_party.sh <RID> <package dir>"
RID=$1 PKG=$2
[ -d "$PKG" ] || fail "no package directory $PKG"
CACHE=${HPENGINE_TOOLS_CACHE:-$REPO/.artifacts/tools-cache}
mkdir -p "$CACHE"; CACHE=$(cd "$CACHE" && pwd)

IE=https://constexpr.org/innoextract/files
GH=https://github.com/dscharrer/innoextract/commit
# get <file name> <url> <sha256>: into the cache, checked
get() {
    f="$CACHE/$1"
    if [ ! -f "$f" ]; then curl -sSfL -o "$f.part" "$2" || fail "download $2"; mv "$f.part" "$f"; fi
    echo "$3  $f" | shasum -a 256 -c --status || { rm -f "$f"; fail "$1: SHA-256 differs from the pin $3"; }
}

T="$PKG/tools"; rm -rf "$T"; mkdir -p "$T/licenses"
case "$RID" in
    win-x64)
        get innoextract-1.9-windows.zip "$IE/innoextract-1.9-windows.zip" \
            6989342c9b026a00a72a38f23b62a8e6a22cc5de69805cf47d68ac2fec993065
        W="$CACHE/win"; rm -rf "$W"; mkdir -p "$W"; unzip -q "$CACHE/innoextract-1.9-windows.zip" -d "$W"
        cp "$W/innoextract.exe" "$T/innoextract.exe"
        for l in "$W"/license/*; do cp "$l" "$T/licenses/$(basename "$l" .txt).txt"; done ;;
    linux-x64)
        get innoextract-1.9-linux.tar.xz "$IE/innoextract-1.9-linux.tar.xz" \
            008efe5011476ccc4aae17c3e22038b5a1bc5c7aad2b9d4d869537bf3874d21f
        L="$CACHE/linux"; rm -rf "$L"; mkdir -p "$L"; tar -xJf "$CACHE/innoextract-1.9-linux.tar.xz" -C "$L"
        cp "$L/innoextract-1.9-linux/bin/amd64/innoextract" "$T/innoextract"; chmod 755 "$T/innoextract"
        for l in "$L"/innoextract-1.9-linux/license/*; do cp "$l" "$T/licenses/$(basename "$l").txt"; done ;;
    osx-arm64|osx-x64)
        case "$RID" in osx-arm64) ARCH=arm64 BARCH=arm ;; *) ARCH=x86_64 BARCH=x86 ;; esac
        get innoextract-1.9.tar.gz "$IE/innoextract-1.9.tar.gz" \
            6344a69fc1ed847d4ed3e272e0da5998948c6b828cb7af39c6321aba6cf88126
        get ie-cmake4.patch "$GH/83d0bf4365b09ddd17dddb400ba5d262ddf16fb8.patch?full_index=1" \
            fe5299d1fdea5c66287aef2f70fee41d86aedc460c5b165da621d699353db07d
        get ie-boost185.patch "$GH/264c2fe6b84f90f6290c670e5f676660ec7b2387.patch?full_index=1" \
            f968a9c0521083dd4076ce5eed56127099a9c9888113fc50f476b914396045cc
        get ie-boost189.patch "$GH/882796e0e9b134b02deeaae4bbfe92920adb6fe2.patch?full_index=1" \
            d5af3e86eb2b74bff559885440d330678e5dbb028ce165bb836ddb14224af201
        get boost_1_86_0.tar.bz2 https://archives.boost.io/release/1.86.0/source/boost_1_86_0.tar.bz2 \
            1bed88e40401b2cb7a1f76d4bab499e352fa4d0c5f31c0dbae64e24d34d7513b
        get xz-5.6.3.tar.gz https://github.com/tukaani-project/xz/releases/download/v5.6.3/xz-5.6.3.tar.gz \
            b1d45295d3f71f25a4c9101bd7c8d16cb56348bbef3bbc738da0351e17c73317
        B="$CACHE/$RID"; P="$B/prefix"; BIN="$B/innoextract-1.9/build/innoextract"
        export MACOSX_DEPLOYMENT_TARGET=13.0
        if [ ! -x "$BIN" ]; then
            mkdir -p "$B" "$P"
            echo "== third_party: building xz, Boost, innoextract for $RID (once; cached in $B)"
            [ -f "$P/lib/liblzma.a" ] || { rm -rf "$B/xz-5.6.3"; tar -xzf "$CACHE/xz-5.6.3.tar.gz" -C "$B"
            (cd "$B/xz-5.6.3" && ./configure -q --prefix="$P" --disable-shared --enable-static --disable-xz \
                --disable-xzdec --disable-lzmadec --disable-lzmainfo --disable-scripts --disable-doc --disable-nls \
                CFLAGS="-O2 -arch $ARCH" && make -s -j8 && make -s install) > "$B/xz.log" 2>&1 || fail "xz (see $B/xz.log)"; }
            [ -f "$P/lib/libboost_iostreams.a" ] || { rm -rf "$B/boost_1_86_0"; tar -xjf "$CACHE/boost_1_86_0.tar.bz2" -C "$B"
            (cd "$B/boost_1_86_0" && ./bootstrap.sh --with-libraries=iostreams,filesystem,date_time,program_options \
                && ./b2 -q -j8 --prefix="$P" link=static variant=release threading=multi architecture=$BARCH \
                    address-model=64 -sNO_LZMA=1 -sNO_ZSTD=1 cxxflags="-arch $ARCH -mmacosx-version-min=13.0" \
                    cflags="-arch $ARCH -mmacosx-version-min=13.0" linkflags="-arch $ARCH" install) > "$B/boost.log" 2>&1 || fail "Boost (see $B/boost.log)"; }
            rm -rf "$B/innoextract-1.9"; tar -xzf "$CACHE/innoextract-1.9.tar.gz" -C "$B"
            for p in ie-cmake4 ie-boost185 ie-boost189; do
                (cd "$B/innoextract-1.9" && patch -s -p1 < "$CACHE/$p.patch") || fail "patch $p"
            done
            (cd "$B/innoextract-1.9" && PKG_CONFIG_LIBDIR="$P/lib/pkgconfig" cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=$ARCH \
                -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0 -DCMAKE_PREFIX_PATH="$P" \
                -DCMAKE_IGNORE_PREFIX_PATH=/opt/homebrew\;/usr/local -DBoost_NO_SYSTEM_PATHS=ON -DBOOST_ROOT="$P" \
                -DBoost_USE_STATIC_LIBS=ON -DLZMA_USE_STATIC_LIBS=ON -DZLIB_USE_STATIC_LIBS=OFF \
                -DBZip2_USE_STATIC_LIBS=OFF -Diconv_USE_STATIC_LIBS=OFF \
                && cmake --build build -j8) > "$B/innoextract.log" 2>&1 || fail "innoextract (see $B/innoextract.log)"
        fi
        # The binary is of the RID's architecture only (a host-architecture build would pass every other check).
        [ "$(lipo -archs "$BIN")" = "$ARCH" ] || fail "innoextract is $(lipo -archs "$BIN"), expected $ARCH"
        # Only macOS system libraries may remain dynamic.
        other=$(otool -L "$BIN" | tail -n +2 | awk '{print $1}' | grep -vE '^/usr/lib/|^/System/' || true)
        [ -z "$other" ] || fail "innoextract links outside macOS: $other"
        cp "$BIN" "$T/innoextract"; chmod 755 "$T/innoextract"
        # The build tree's LC_RPATH names the cache under the builder's home (publish.sh --check refuses it) and is not
        # needed: every non-system library is static. Removing it breaks the linker's signature, so sign again.
        for r in $(otool -l "$T/innoextract" | awk '/cmd LC_RPATH/ { getline; getline; print $2 }'); do
            install_name_tool -delete_rpath "$r" "$T/innoextract" 2> /dev/null || fail "innoextract: delete rpath $r"
        done
        codesign --force -s - "$T/innoextract" > /dev/null 2>&1 || fail "innoextract: codesign"
        cp "$B/innoextract-1.9/LICENSE" "$T/licenses/innoextract.LICENSE.txt"
        cp "$B/boost_1_86_0/LICENSE_1_0.txt" "$T/licenses/Boost.LICENSE_1_0.txt"
        cp "$B/xz-5.6.3/COPYING" "$T/licenses/liblzma.COPYING.txt"
        cp "$B/xz-5.6.3/COPYING.0BSD" "$T/licenses/liblzma.COPYING.0BSD.txt" ;;
    *) fail "unknown RID $RID" ;;
esac
echo "third_party $RID: $(ls "$T" | tr '\n' ' ')-> $T"
