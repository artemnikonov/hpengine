#!/bin/sh
# Rebuilds shader blobs from classic.hlsl (the only source).
#   sh shaders/build.sh            — write blobs next to the source
#   sh shaders/build.sh --check    — build into a temp dir and compare with the blobs in the tree
# Requires dxc (DirectXShaderCompiler with libdxil — DXIL is signed at build) and spirv-cross.
# Their paths can be set via DXC=... and SPIRV_CROSS=....
set -eu
here=$(cd "$(dirname "$0")" && pwd)
DXC=${DXC:-dxc}
SPIRV_CROSS=${SPIRV_CROSS:-spirv-cross}
out=$here
[ "${1:-}" = "--check" ] && out=$(mktemp -d)

src=$here/classic.hlsl
# vk:: attributes mean nothing for DXIL; their warning is suppressed only in the DXIL build.
"$DXC" -T vs_6_0 -E vs_main -spirv -fspv-target-env=vulkan1.0 -Fo "$out/classic.vert.spv" "$src"
"$DXC" -T ps_6_0 -E fs_main -spirv -fspv-target-env=vulkan1.0 -Fo "$out/classic.frag.spv" "$src"
"$DXC" -T vs_6_0 -E vs_main -Wno-ignored-attributes -Fo "$out/classic.vert.dxil" "$src"
"$DXC" -T ps_6_0 -E fs_main -Wno-ignored-attributes -Fo "$out/classic.frag.dxil" "$src"
"$SPIRV_CROSS" --msl --msl-version 20100 --msl-decoration-binding "$out/classic.vert.spv" --output "$out/classic.vert.msl"
"$SPIRV_CROSS" --msl --msl-version 20100 --msl-decoration-binding "$out/classic.frag.spv" --output "$out/classic.frag.msl"

if [ "$out" != "$here" ]; then
    rc=0
    for f in classic.vert.spv classic.frag.spv classic.vert.dxil classic.frag.dxil classic.vert.msl classic.frag.msl; do
        if ! cmp -s "$out/$f" "$here/$f"; then echo "STALE $f"; rc=1; fi
    done
    rm -rf "$out"
    [ $rc = 0 ] && echo "shaders: blobs match classic.hlsl"
    exit $rc
fi
echo "shaders: blobs written to $out"
