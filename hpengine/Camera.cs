// Frame camera of the SDL renderer: UE-world position and rotation (or Core's explicit axes) and the clip-matrix rows built from them.
using HP1.UEScript;

namespace HPEngine
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
        // FovAngle is not clamped: FSceneNode::ComputeRenderSize takes Proj.Z = FX2/appTan(FovAngle·π/360) for any value
        // (HP2 body 0x1038ada0..0x1038ae69: fld [Actor+0x3c8], fmul, appTan, fdiv, no compare). Above 180 Proj.Z is negative
        // and the frame is drawn turned by 180° (HP2 14050Riddle: "BaseCam FOV Angle=200", then to 90 over 4 s). Only a
        // FovAngle whose Proj.Z is not finite (tan = 0) is refused in Rows.
        public static Camera From(CameraSnapshot c)
        {
            BrushCoords m = UESceneNode.MasterCoords(c.Location, c.Rotation);
            return new Camera
            {
                X = c.Location.x, Y = c.Location.y, Z = c.Location.z,
                Pitch = c.Rotation.Pitch * 360f / 65536f, Yaw = c.Rotation.Yaw * 360f / 65536f, Roll = c.Rotation.Roll * 360f / 65536f,
                Fov = c.FovAngle,
                HasAxes = true, FX = m.Z.x, FY = m.Z.y, FZ = m.Z.z, RX = m.X.x, RY = m.X.y, RZ = m.X.z, UX = -m.Y.x, UY = -m.Y.y, UZ = -m.Y.z,
            };
        }

        // The camera of the canvas mesh pass (Core HudKind.Actor): this camera at Core's canvas FovAngle when Hor+ widened the
        // world's (WorldFrame.CanvasFovAngle > 0), else this camera.
        public Camera AtCanvasFov(float canvasFov)
        {
            Camera c = this;
            if (canvasFov > 0f) c.Fov = canvasFov;
            return c;
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
        // Vulkan, verified by execution: --selftest-gpu). Screen = V.xy·Proj.Z/V.z + (FX2, FY2) with
        // Core's Proj.Z (UESceneNode.ProjZOf, the master frame's PC64 value), i.e. clip x = V.x·2·Proj.Z/W, y = V.y·2·Proj.Z/H.
        // frameW/frameH (canvas pass): the projection of a frame of that size anchored at the target's top-left (Proj.Z and
        // centre from it), as the HUD places canvas pixels; 0 — the target's own size.
        // m: 20 floats — rows 0..3 act on the CAMERA-RELATIVE point p − eye, m[16..18] = eye (the camera location), m[19] = 0.
        // Render.dll subtracts Coords.Origin before the axes (URender::Project, UERenderVisibility.Project); folding −axis·Cam into the rows
        // and dotting them with absolute world positions (~3000 uu) in float32 put ~2e-4 uu of noise on clip z and w
        // separately, ±20 units of 2^-24 in depth — HP2 EntryHall_Hub candle bases (1–6 units in front of the table top)
        // flipped behind it by view angle (HP1_archive night_2026-11-15/hp2_candle_base.md).
        public void Rows(int w, int h, float[] m, int frameW = 0, int frameH = 0)
        {
            Axes(out var fx, out var fy, out var fz, out var rx, out var ry, out var rz, out var ux, out var uy, out var uz);
            // Depth as D3DDrv DrawTriangles writes it (see classic.hlsl vs_main): sz = (1 − 1/Z)·(1 + 2^-15); with n = 1,
            // far = 32769 row 2 gives a = far/(far − n) = 1 + 2^-15 and b = −a, the same mapping (sz > 1 beyond Z = 32769).
            // The shader derives clip z from clip w (z = a·(w − 1)); row 2 is that same mapping for CPU users (DrawLog).
            // Campaign census (--depthcensus, 181 drawlog camera points): deepest drawn vertex 9783.
            float projZ = UESceneNode.ProjZOf(frameW > 0 ? frameW : w, Fov, pc24: false), n = 1f, far = 32769f;
            if (!float.IsFinite(projZ)) throw new System.InvalidOperationException($"Camera: FovAngle {Fov} has no projection (Proj.Z {projZ})");
            float sx = 2f * projZ / w, sy = 2f * projZ / h, a = far / (far - n), b = -n * far / (far - n);
            void Row(int i, float x, float y, float z, float k, float w0)
            { m[i * 4] = x * k; m[i * 4 + 1] = y * k; m[i * 4 + 2] = z * k; m[i * 4 + 3] = w0; }
            Row(0, rx, ry, rz, sx, 0);
            Row(1, ux, uy, uz, sy, 0);
            Row(2, fx, fy, fz, a, b);
            Row(3, fx, fy, fz, 1, 0);
            m[16] = X; m[17] = Y; m[18] = Z; m[19] = 0f;
            if (frameW <= 0) return;
            // clip x += (frameW / w − 1)·w_clip, y += (1 − frameH / h)·w_clip: the frame centre (frameW/2, frameH/2) in pixels.
            float ox = (float)frameW / w - 1f, oy = 1f - (float)frameH / h;
            for (int k = 0; k < 4; k++) { m[k] += ox * m[12 + k]; m[4 + k] += oy * m[12 + k]; }
        }
    }
}
