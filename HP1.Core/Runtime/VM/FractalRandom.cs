using System;
using System.Buffers.Binary;

namespace HP1.UEScript
{
    // RANDOM BYTE RING OF PROCEDURAL TEXTURES (Fire.dll: DAT_10518450 x0x200, index DAT_10518650). Seeded ONCE
    // per process by the first UFractalTexture constructor (FUN_10501050, Fire.dll.c:52-55: 0x200 x (BYTE)appRand()),
    // i.e. when the first package with a procedural texture loads, before any gameplay FRand.
    // Step (WaterRedrawDrops, Fire.dll.c:3326-3329):
    //   v = ring32[(idx + 0x80) & 0xfc]; idx = (idx + 4) & 0xfc; ring32[idx] ^= v; -> v.
    // One ring for ALL textures (water and fire): the access order between them shows in the result,
    // so the instance is shared (Shared).
    public sealed class FractalRandom
    {
        public readonly byte[] Ring = new byte[0x200];
        public int Index;
        public bool Seeded;
        public static readonly FractalRandom Shared = new FractalRandom();

        public void Seed(Func<int> appRand)
        {
            for (int i = 0; i < 0x200; i++) Ring[i] = unchecked((byte)appRand());
            Index = 0; Seeded = true;
        }

        public void Set(ReadOnlySpan<byte> ring, int index)
        {
            ring.CopyTo(Ring); Index = index & 0xfc; Seeded = true;
        }

        public uint Next()
        {
            int old = Index;
            Index = (Index + 4) & 0xfc;
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(Ring.AsSpan((old + 0x80) & 0xfc, 4));
            var slot = Ring.AsSpan(Index, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(slot, BinaryPrimitives.ReadUInt32LittleEndian(slot) ^ v);
            return v;
        }
    }
}
