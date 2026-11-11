#!/bin/sh
# Rebuilds the 2D-frame shader blobs from hud.hlsl (the single source).
#   sh Hud/shaders/build.sh            - write blobs next to the source
#   sh Hud/shaders/build.sh --check    - build into a temp dir and compare with the blobs in the tree
# Requires dxc (DirectXShaderCompiler with libdxil; DXIL is signed at build) and spirv-cross.
# Their paths can be set via DXC=... and SPIRV_CROSS=.... Same convention as shaders/build.sh (classic.hlsl).
set -eu
here=$(cd "$(dirname "$0")" && pwd)
DXC=${DXC:-dxc}
SPIRV_CROSS=${SPIRV_CROSS:-spirv-cross}
out=$here
[ "${1:-}" = "--check" ] && out=$(mktemp -d)

src=$here/hud.hlsl
# vk:: attributes mean nothing for DXIL; their warning is suppressed only in the DXIL build.
"$DXC" -T vs_6_0 -E vs_main -spirv -fspv-target-env=vulkan1.0 -Fo "$out/hud.vert.spv" "$src"
"$DXC" -T ps_6_0 -E fs_main -spirv -fspv-target-env=vulkan1.0 -Fo "$out/hud.frag.spv" "$src"
"$DXC" -T vs_6_0 -E vs_main -Wno-ignored-attributes -Fo "$out/hud.vert.dxil" "$src"
"$DXC" -T ps_6_0 -E fs_main -Wno-ignored-attributes -Fo "$out/hud.frag.dxil" "$src"
"$SPIRV_CROSS" --msl --msl-version 20100 --msl-decoration-binding "$out/hud.vert.spv" --output "$out/hud.vert.msl"
"$SPIRV_CROSS" --msl --msl-version 20100 --msl-decoration-binding "$out/hud.frag.spv" --output "$out/hud.frag.msl"

if [ "$out" != "$here" ]; then
    rc=0
    for f in hud.vert.spv hud.frag.spv hud.vert.dxil hud.frag.dxil hud.vert.msl hud.frag.msl; do
        if ! cmp -s "$out/$f" "$here/$f"; then echo "STALE $f"; rc=1; fi
    done
    rm -rf "$out"
    [ $rc = 0 ] && echo "hud shaders: blobs match hud.hlsl"
    exit $rc
fi
echo "hud shaders: blobs written to $out"
