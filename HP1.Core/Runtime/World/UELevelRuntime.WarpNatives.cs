// WarpZoneInfo coordinate transforms. Both natives mutate three out/ref
// arguments in place: location, velocity and rotation.
//
// Source of truth: shipped Engine.dll
//   AWarpZoneInfo::execUnWarp  Engine.dll.c:83627-83822
//   AWarpZoneInfo::execWarp    Engine.dll.c:153884-154073
// and the Core.dll FCoords bodies already ported by BrushCoords:
//   operator*=, operator/= Rotator, OrthoRotation.
using System;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        static BrushCoords WarpCoordsOf(UEObject self)
        {
            if (self?.GetProp(PK.WarpCoords) is not UEObject value
                || !value.IsStruct)
                throw new InvalidOperationException(
                    $"WarpZoneInfo has no decoded WarpCoords actor={self?.GetProp(PK.Name)}");
            return new BrushCoords
            {
                Origin = UEInterpreter.AsVector3(value.GetProp(PK.Origin)),
                X = UEInterpreter.AsVector3(value.GetProp(PK.XAxis)),
                Y = UEInterpreter.AsVector3(value.GetProp(PK.YAxis)),
                Z = UEInterpreter.AsVector3(value.GetProp(PK.ZAxis)),
            };
        }

        // Matrix transpose, axes only. Warp's location formula adds the
        // authored Origin after this vector transform; FCoords rotation
        // composition does not consume the origin.
        static BrushCoords InverseAxes(in BrushCoords c) => new()
        {
            Origin = Vector3.zero,
            X = new Vector3(c.X.x, c.Y.x, c.Z.x),
            Y = new Vector3(c.X.y, c.Y.y, c.Z.y),
            Z = new Vector3(c.X.z, c.Y.z, c.Z.z),
        };

        // execWarp location/velocity (disasm Engine.asm 0x1040ce8a..0x1040ceea and
        // 0x1040cf90..0x1040cfe4): WarpCoords.Transpose() — Origin' =
        // -(Origin.TransformVectorBy(WarpCoords)) (Core calls 0x1040ce48 /
        // 0x1040ce50, result stored to float memory), axes transposed;
        // then TransformPointBy: d = Loc - Origin' in x87 registers (fsub without
        // store), component = (d.x*A.x + d.y*A.y) + d.z*A.z, one fstp to float.
        // Velocity is the same row without the subtraction. Intermediates are
        // kept in double, like BrushCoords (x87 64-bit precision).
        static Vector3 WarpTransposeOrigin(in BrushCoords c)
            => -c.TransformVector(c.Origin);

        static float WarpRow(double dx, double dy, double dz, float ax, float ay, float az)
            => (float)((dx * ax + dy * ay) + dz * az);

        static Vector3 WarpDirect(in BrushCoords c, double dx, double dy, double dz) => new(
            WarpRow(dx, dy, dz, c.X.x, c.X.y, c.X.z),
            WarpRow(dx, dy, dz, c.Y.x, c.Y.y, c.Y.z),
            WarpRow(dx, dy, dz, c.Z.x, c.Z.y, c.Z.z));

        static Vector3 WarpTransposed(in BrushCoords c, double dx, double dy, double dz) => new(
            WarpRow(dx, dy, dz, c.X.x, c.Y.x, c.Z.x),
            WarpRow(dx, dy, dz, c.X.y, c.Y.y, c.Z.y),
            WarpRow(dx, dy, dz, c.X.z, c.Y.z, c.Z.z));

        // FCoords::OrthoRotation (Core 0x1014ed10): `fmul qword [0x1017fa50]` and a call to MSVCRT
        // `_ftol` (0x10176f72 -> IAT 0x102504f4) — TRUNCATION toward zero; constant 0x40c45f306446f9b4
        // (65536/2π, computed in float).
        internal static readonly double OrthoRadToUnr = BitConverter.Int64BitsToDouble(0x40c45f306446f9b4);
        // appAtan2 (0x101260f0, bare fpatan: UEX87Transcendental, correctly rounded, D-71) times the qword constant in a
        // PC64 register (one rounding), then _ftol: truncation of that register value (low dword of the int64).
        internal static int FtolAngle(HP1.Core.X87Ext atan)
        {
            HP1.Core.X87Ext v = HP1.Core.X87Ext.Round64(atan * HP1.Core.X87Ext.From(OrthoRadToUnr));
            if (!v.IsFinite) return 0;
            System.Numerics.BigInteger t = v.TruncateToInteger();
            return t >= long.MinValue && t <= long.MaxValue ? unchecked((int)(long)t) : 0;
        }
        internal static HP1.Core.X87Ext AppAtan2(double y, double x) =>
            HP1.Core.UEX87Transcendental.Fpatan(HP1.Core.X87Ext.From(y), HP1.Core.X87Ext.From(x));

        // FCoords::OrthoRotation at PC64 (game logic), disasm Core 0x1014ed21..0x1014ee14: yaw = appAtan2(X.y, X.x);
        // |X.xy| = appSqrt (bare fsqrt, result kept in the register, fstp qword as the argument) of X.y·X.y + X.x·X.x in the
        // register, fstp qword; pitch = appAtan2(X.z, |X.xy|); the identity coords /= (pitch, yaw, 0) give S = their Y axis;
        // roll = appAtan2(S·Z, S·Y), each dot (S.z·A.z + S.y·A.y) + S.x·A.x in the register, fstp qword (0x1014edca..0x1014ee03).
        // Judge: se_harness/bsp_diff/ortho_rotation_oracle.py (the real body at FPCW 0x037F), SelfTest ortho_rotation_pc64.
        internal static UERotator OrthoRotation(in BrushCoords c)
        {
            static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            static double Dot(Vector3 s, Vector3 a) => R(R(X(s.z) * X(a.z) + X(s.y) * X(a.y)) + X(s.x) * X(a.x)).ToDouble();
            int yaw = FtolAngle(AppAtan2(c.X.y, c.X.x));
            double horizontalSquared = R(X(c.X.y) * X(c.X.y) + X(c.X.x) * X(c.X.x)).ToDouble();
            double horizontal = HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(horizontalSquared)).ToDouble();
            int pitch = FtolAngle(AppAtan2(c.X.z, horizontal));
            BrushCoords withoutRoll = BrushCoords.Identity;
            withoutRoll.DivRotator(pitch, yaw, 0);
            int roll = FtolAngle(AppAtan2(Dot(withoutRoll.Y, c.Z), Dot(withoutRoll.Y, c.Y)));
            return new UERotator(pitch, yaw, roll);
        }

        static UERotator TransformWarpRotation(
            UERotator rotation, in BrushCoords coords, bool inverse)
        {
            BrushCoords frame = BrushCoords.Identity;
            frame.DivRotator(rotation.Pitch, rotation.Yaw, rotation.Roll);
            BrushCoords axes = inverse ? InverseAxes(coords) : coords;
            axes.Origin = Vector3.zero;
            frame.Mul(axes);
            return OrthoRotation(frame);
        }

        public object WarpNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (args.Length < 3)
                throw new InvalidOperationException("Warp requires Loc, Vel and R");
            BrushCoords coords = WarpCoordsOf(self);
            Vector3 location = UEInterpreter.AsVector3(args[0]);
            Vector3 velocity = UEInterpreter.AsVector3(args[1]);
            UERotator rotation = UEInterpreter.AsRotator(args[2]);

            Vector3 originT = WarpTransposeOrigin(coords);
            args[0] = WarpTransposed(coords, (double)location.x - originT.x,
                (double)location.y - originT.y, (double)location.z - originT.z);
            args[1] = WarpTransposed(coords, velocity.x, velocity.y, velocity.z);
            args[2] = TransformWarpRotation(rotation, coords, inverse: true);
            return null;
        }

        public object UnWarpNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (args.Length < 3)
                throw new InvalidOperationException("UnWarp requires Loc, Vel and R");
            BrushCoords coords = WarpCoordsOf(self);
            Vector3 location = UEInterpreter.AsVector3(args[0]);
            Vector3 velocity = UEInterpreter.AsVector3(args[1]);
            UERotator rotation = UEInterpreter.AsRotator(args[2]);

            // The body is inlined (disasm 0x1040d2d5..0x1040d335, 0x1040d35e..0x1040d3b2):
            // d = Loc - Origin without a float store, component =
            // (d.x*A.x + d.y*A.y) + d.z*A.z over the ORIGINAL axes, one fstp.
            // Core's out-of-line FVector::TransformVectorBy sums z, y, x
            // (BrushCoords.TransformVector) — that is a different body.
            args[0] = WarpDirect(coords, (double)location.x - coords.Origin.x,
                (double)location.y - coords.Origin.y, (double)location.z - coords.Origin.z);
            args[1] = WarpDirect(coords, velocity.x, velocity.y, velocity.z);
            args[2] = TransformWarpRotation(rotation, coords, inverse: false);
            return null;
        }
    }
}
