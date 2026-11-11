using HP1.UEScript;

namespace OpenHP1
{
    public partial struct Camera
    {
        public float X, Y, Z, Pitch, Yaw, Roll, Fov;   // UE world, angles in degrees
        public bool HasAxes; public float FX, FY, FZ, RX, RY, RZ, UX, UY, UZ;   // explicit axes (Core frame camera)

        // The frame camera takes the master frame's axes from Core: URender::CreateMasterFrame Coords = GMath.ViewCoords /
        // Rotation (Engine.dll.c:28477-28500, UESceneNode.MasterCoords) through the SinTab table, i.e. angles quantised to
        // multiples of 4 (SinTab[(a>>2)&0x3fff]). Exact sin/cos of the rotator shifted every frame by up to ~0.15 px
        // (Lev_Tut3 M1, pitch −5825 yaw −12379: Light99/103 tiles vs the original drawlog). Coords: X right, Y down, Z forward.
        // Pitch/Yaw/Roll stay for the free camera, which clears HasAxes when it turns.
        // FovAngle outside (0, 180) has no projection (ComputeRenderSize would give an infinite or negative Proj.Z): a loud
        // refusal, not an invented 90.
        public static Camera From(CameraSnapshot c)
        {
            if (!(c.FovAngle > 0f && c.FovAngle < 180f))
                throw new System.InvalidOperationException($"Camera: FovAngle {c.FovAngle} has no projection (FSceneNode::ComputeRenderSize)");
            BrushCoords m = UESceneNode.MasterCoords(c.Location, c.Rotation);
            return new Camera
            {
                X = c.Location.x, Y = c.Location.y, Z = c.Location.z,
                Pitch = c.Rotation.Pitch * 360f / 65536f, Yaw = c.Rotation.Yaw * 360f / 65536f, Roll = c.Rotation.Roll * 360f / 65536f,
                Fov = c.FovAngle,
                HasAxes = true, FX = m.Z.x, FY = m.Z.y, FZ = m.Z.z, RX = m.X.x, RY = m.X.y, RZ = m.X.z, UX = -m.Y.x, UY = -m.Y.y, UZ = -m.Y.z,
            };
        }

        // UE axes: X forward, Y right, Z up. The free camera (no HasAxes) turns in degrees; its axes come from the same Core
        // body as the frame camera, at the rotator nearest to its angles.
        public void Axes(out float fx, out float fy, out float fz, out float rx, out float ry, out float rz,
            out float ux, out float uy, out float uz)
        {
            if (HasAxes) { fx = FX; fy = FY; fz = FZ; rx = RX; ry = RY; rz = RZ; ux = UX; uy = UY; uz = UZ; return; }
            static int U(float deg) => (int)System.MathF.Round(deg * 65536f / 360f);
            BrushCoords m = UESceneNode.MasterCoords(default, new UERotator(U(Pitch), U(Yaw), U(Roll)));
            fx = m.Z.x; fy = m.Z.y; fz = m.Z.z; rx = m.X.x; ry = m.X.y; rz = m.X.z; ux = -m.Y.x; uy = -m.Y.y; uz = -m.Y.z;
        }

        // Clip matrix rows (SDL_GPU: z 0..1, clip +Y is the frame top; the same on Metal and
        // Vulkan, verified by execution: --selftest-gpu + clip-x/y mutations). Screen = V.xy·Proj.Z/V.z + (FX2, FY2) with
        // Core's Proj.Z (UESceneNode.ProjZOf, the master frame's PC64 value), i.e. clip x = V.x·2·Proj.Z/W, y = V.y·2·Proj.Z/H.
        public void Rows(int w, int h, float[] m)
        {
            Axes(out var fx, out var fy, out var fz, out var rx, out var ry, out var rz, out var ux, out var uy, out var uz);
            if (!(Fov > 0f && Fov < 180f)) throw new System.InvalidOperationException($"Camera: FovAngle {Fov} has no projection");
            // Depth as D3DDrv DrawTriangles writes it (D3DDrv.dll.c:985-986, 16248/16250): sz = (1 − 1/Z)·(1 + 2^-15); with n = 1,
            // far = 32769 the rows give a = far/(far − n) = 1 + 2^-15 and b = −a, the same mapping (sz > 1 beyond Z = 32769).
            // Campaign census (--depthcensus, 181 drawlog camera points): deepest drawn vertex 9783.
            float projZ = UESceneNode.ProjZOf(w, Fov, pc24: false), n = 1f, far = 32769f;
            float sx = 2f * projZ / w, sy = 2f * projZ / h, a = far / (far - n), b = -n * far / (far - n);
            float X = this.X, Y = this.Y, Z = this.Z;
            void Row(int i, float x, float y, float z, float k, float w0)
            { m[i * 4] = x * k; m[i * 4 + 1] = y * k; m[i * 4 + 2] = z * k; m[i * 4 + 3] = -(x * X + y * Y + z * Z) * k + w0; }
            Row(0, rx, ry, rz, sx, 0);
            Row(1, ux, uy, uz, sy, 0);
            Row(2, fx, fy, fz, a, b);
            Row(3, fx, fy, fz, 1, 0);
        }
    }
}
