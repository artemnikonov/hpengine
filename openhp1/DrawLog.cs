// Draw-call log of a snapshot (--shot … --drawlog <file>): what the SDL renderer draws, in which pass, with which
// pipeline, where on screen and in which colour. Counterpart of the live-original log tools/origprobe/hp1drawlog.c;
// compared by tools/origprobe/drawlog_compare.py. Passes are Core child frames (WorldFrame.Views): main,
// mirror<view> (MIRROR is the view's NearClip plane in world space), warp<iSurf>, sky; a nested view's pass is the chain
// from the root through '/' (VIEW: kind, iSurf, zone, parent eye — against the original's CHILD). A warp portal surface
// is DRAW kind=WARPREFL tex=warp<iSurf>.
// Call geometry (box/rz/col): batch vertices through THIS view's camera rows: screen bounds in frame pixels,
// RZ = ProjZ / depth (ProjZ = W/2 / tan(fov/2), as the original's FTransform::RZ), colour of the first vertex.
// Blending and Z write come from the CREATED pipeline (CreatePipelines), not from the rule: the log must show what
// went to the GPU even if rule and pipeline diverge. Only the snapshot frame is written.
using System.Globalization;

namespace OpenHP1
{
    static class DrawLog
    {
        public static string Path;              // --drawlog
        static System.IO.StreamWriter _w;
        public static bool On => _w != null;
        public static string Pass = "main";
        static readonly (bool blend, string src, string dst, bool zw, bool cw)[] _pipes = new (bool, string, string, bool, bool)[64];

        // Pipeline pipe: blending (D3D factor names, supplied by the renderer from the created pipeline), Z and colour write.
        public static void Pipe(int i, bool blend, string src, string dst, bool depthWrite, bool colorWrite)
            => _pipes[i] = (blend, src, dst, depthWrite, colorWrite);

        public static void Begin(string map)
        {
            if (Path is null) return;
            _w = new System.IO.StreamWriter(Path, false, new System.Text.UTF8Encoding(false));
            _w.WriteLine($"# openhp1 drawlog map={map}");
            Pass = "main";
        }
        public static void End() { _w?.Dispose(); _w = null; }
        public static void Line(string s) => _w?.WriteLine(s);
        static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

        // Actor stillness at snapshot time: position and rotation as on the first warm-up tick, animation sequence and
        // frame as StillTicks ticks ago, and Base and Owner (effects and shadows follow it) are still too. Original logs
        // are taken at another moment of play, so the judge does not compare place and set of moving actors
        // (STATE still=0: "not covered").
        public const int StillTicks = 60;
        static Dictionary<string, (HP1.UEScript.UEVector, HP1.UEScript.UERotator)> _first;
        static Dictionary<string, (string, float)> _anim;
        public static void MarkFirst(HP1.UEScript.WorldFrame f)
        {
            _first = new();
            if (f != null) foreach (var a in f.Actors) _first[a.Name] = (a.Location, a.Rotation);
        }
        public static void MarkState(HP1.UEScript.WorldFrame f)
        {
            _anim = new();
            if (f != null) foreach (var a in f.Actors) _anim[a.Name] = (a.AnimSequence, a.AnimFrame);
        }
        public static void States(HP1.UEScript.WorldFrame f)
        {
            if (_w is null || f is null) return;
            var by = new Dictionary<string, HP1.UEScript.ActorSnapshot>();
            foreach (var a in f.Actors) by[a.Name] = a;
            var memo = new Dictionary<string, bool>();
            bool Still(string n, int depth)
            {
                if (memo.TryGetValue(n, out bool v)) return v;
                if (depth > 16 || !by.TryGetValue(n, out var a)) return false;
                v = _first != null && _first.TryGetValue(n, out var p) && p.Equals((a.Location, a.Rotation))
                    && _anim != null && _anim.TryGetValue(n, out var m) && m.Equals((a.AnimSequence, a.AnimFrame))
                    && (a.Base == "-" || Still(a.Base, depth + 1)) && (a.Owner == "-" || Still(a.Owner, depth + 1));
                return memo[n] = v;
            }
            // tex= (Actor.Texture) and decalpieces= (pieces of the actor's FDecal records in Core's frame, all views): the
            // original's drawlog does not record a decal's actor (scope=None), so the judge needs the port's actors of that
            // texture to tell "the port lost a decal it had to draw" from "the decal follows a moving owner elsewhere".
            var pieces = new Dictionary<int, int>();
            foreach (var d in f.Decals) pieces[d.DecalId] = pieces.GetValueOrDefault(d.DecalId) + (d.Pieces?.Length ?? 0);
            foreach (var a in f.Actors)
                Line($"STATE\tname={a.Name}\tstill={(Still(a.Name, 0) ? 1 : 0)}\tbase={a.Base}\towner={a.Owner}\tloc={F(a.Location.x)},{F(a.Location.y)},{F(a.Location.z)}"
                    + (a.TexturePath is null ? "" : $"\ttex={a.TexturePath}\tdecalpieces={pieces.GetValueOrDefault(a.ObjectId)}"));
        }

        // LOD level of every mesh draw of the snapshot frame (Core MeshPose.LodParts, D-62), counterpart of the original's
        // hp1drawlog LOD lines: per (pose, frame) verts = the requested vertex count, -1 = the full mesh. view = WorldFrame.Views
        // index, -1 = master. Compared by drawlog_lod_compare.py.
        public static void MeshLods(HP1.UEScript.WorldFrame f)
        {
            if (_w is null || f is null) return;
            var names = new Dictionary<int, string>();
            foreach (var a in f.Actors) names[a.ObjectId] = a.Name;
            foreach (var mp in f.MeshPoses)
            {
                string who = names.GetValueOrDefault(mp.ObjectId, mp.ObjectId.ToString());
                if (mp.LodParts is not { Count: > 0 } parts)
                {
                    if (f.ActorViews.SetOf.TryGetValue(mp.ObjectId, out int s))
                        foreach (int v in f.ActorViews.Sets[s]) Line($"LOD\towner={who}\tmesh={mp.MeshPath}\tweapon={(mp.Weapon ? 1 : 0)}\tview={v}\tverts=-1");
                    continue;
                }
                foreach (var p in parts)
                    foreach (int v in f.ActorViews.Sets[p.ViewSet]) Line($"LOD\towner={who}\tmesh={mp.MeshPath}\tweapon={(mp.Weapon ? 1 : 0)}\tview={v}\tverts={p.Verts}");
            }
        }

        static int _w0, _h0; static float _projZ;
        // Snapshot camera: position, angles in degrees, field of view.
        public static void Camera(float x, float y, float z, float pitch, float yaw, float roll, float fov, int w, int h)
        {
            _w0 = w; _h0 = h; _projZ = HP1.UEScript.UESceneNode.ProjZOf(w, fov, pc24: false);
            Line($"CAMERA\tloc={F(x)},{F(y)},{F(z)}\trot={(int)MathF.Round(pitch * 65536f / 360f)},{(int)MathF.Round(yaw * 65536f / 360f)},{(int)MathF.Round(roll * 65536f / 360f)}\tsize={w},{h}\tfov={F(fov)}");
        }

        // Screen bounds, RZ range and first-vertex colour of a batch through the view camera rows (clip: x, y, z, w = depth).
        // Only triangles the rasterizer keeps are counted: pipeline face culling (cull: 0 none, 1 back, 2 front;
        // front face is clockwise in NDC, SdlRenderer.FrontFace) and intersection with the screen.
        // None left: box=none, a call without pixels (the original mesh renderer never sends such triangles to the device).
        public static string Geom(float[] rows, IReadOnlyList<(float X, float Y, float Z, float R, float G, float B)> verts, int cull)
        {
            if (verts.Count == 0) return "";
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, r0 = float.MaxValue, r1 = float.MinValue;
            Span<float> nx = stackalloc float[3], ny = stackalloc float[3], cw = stackalloc float[3];
            for (int t = 0; t + 2 < verts.Count; t += 3)
            {
                bool front = true;
                for (int k = 0; k < 3; k++)
                {
                    var v = verts[t + k];
                    float cx = rows[0] * v.X + rows[1] * v.Y + rows[2] * v.Z + rows[3], cy = rows[4] * v.X + rows[5] * v.Y + rows[6] * v.Z + rows[7];
                    cw[k] = rows[12] * v.X + rows[13] * v.Y + rows[14] * v.Z + rows[15];
                    if (rows[8] * v.X + rows[9] * v.Y + rows[10] * v.Z + rows[11] < 0f) front = false;   // before the GPU near plane
                    else { nx[k] = cx / cw[k]; ny[k] = cy / cw[k]; }
                }
                if (!front)
                {
                    // Triangle in front of the GPU near plane (crossing the eye plane): clip against the volume the GPU keeps
                    // (clip z ≥ 0 is the near plane, |x|, |y| ≤ w the sides); empty means no pixels. The rest gives bounds and RZ.
                    ClipEye(rows, verts, t, cull, ref x0, ref y0, ref x1, ref y1, ref r0, ref r1);
                    continue;
                }
                float area = (nx[1] - nx[0]) * (ny[2] - ny[0]) - (nx[2] - nx[0]) * (ny[1] - ny[0]);
                if (cull == 1 && area >= 0f || cull == 2 && area <= 0f) continue;
                float tx0 = MathF.Min(nx[0], MathF.Min(nx[1], nx[2])), tx1 = MathF.Max(nx[0], MathF.Max(nx[1], nx[2]));
                float ty0 = MathF.Min(ny[0], MathF.Min(ny[1], ny[2])), ty1 = MathF.Max(ny[0], MathF.Max(ny[1], ny[2]));
                if (tx1 < -1f || tx0 > 1f || ty1 < -1f || ty0 > 1f) continue;
                x0 = MathF.Min(x0, (tx0 + 1f) * 0.5f * _w0); x1 = MathF.Max(x1, (tx1 + 1f) * 0.5f * _w0);
                y0 = MathF.Min(y0, (1f - ty1) * 0.5f * _h0); y1 = MathF.Max(y1, (1f - ty0) * 0.5f * _h0);
                for (int k = 0; k < 3; k++) { float rz = _projZ / cw[k]; r0 = MathF.Min(r0, rz); r1 = MathF.Max(r1, rz); }
            }
            if (x0 > x1) return "\tbox=none";
            var c = verts[0];
            return $"\tbox={F(x0)},{F(y0)},{F(x1)},{F(y1)}\trz={F(r0)},{F(r1)}\tcol={F(c.R)},{F(c.G)},{F(c.B)}";
        }

        // Sutherland-Hodgman in homogeneous clip space against the volume the rasterizer keeps (no far plane:
        // z >= 0, w + x >= 0, w - x >= 0, w + y >= 0, w - y >= 0). Nothing survives or the
        // remaining polygon is culled (leaves the box untouched); otherwise widens the box and RZ range by the clipped vertices.
        static void ClipEye(float[] rows, IReadOnlyList<(float X, float Y, float Z, float R, float G, float B)> verts, int t, int cull,
            ref float x0, ref float y0, ref float x1, ref float y1, ref float r0, ref float r1)
        {
            var poly = new List<System.Numerics.Vector4>(9);
            for (int k = 0; k < 3; k++)
            {
                var v = verts[t + k];
                poly.Add(new System.Numerics.Vector4(
                    rows[0] * v.X + rows[1] * v.Y + rows[2] * v.Z + rows[3], rows[4] * v.X + rows[5] * v.Y + rows[6] * v.Z + rows[7],
                    rows[8] * v.X + rows[9] * v.Y + rows[10] * v.Z + rows[11], rows[12] * v.X + rows[13] * v.Y + rows[14] * v.Z + rows[15]));
            }
            for (int p = 0; p < 5 && poly.Count > 0; p++)
            {
                float D(System.Numerics.Vector4 q) => p switch { 0 => q.Z, 1 => q.W + q.X, 2 => q.W - q.X, 3 => q.W + q.Y, _ => q.W - q.Y };
                var o = new List<System.Numerics.Vector4>(poly.Count + 2);
                for (int i = 0; i < poly.Count; i++)
                {
                    var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                    float da = D(a), db = D(b);
                    if (da >= 0f) o.Add(a);
                    if (da >= 0f != db >= 0f) o.Add(a + (b - a) * (da / (da - db)));
                }
                poly = o;
            }
            if (poly.Count < 3) return;
            float area = 0f;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                area += a.X / a.W * (b.Y / b.W) - b.X / b.W * (a.Y / a.W);
            }
            if (cull == 1 && area >= 0f || cull == 2 && area <= 0f) return;
            foreach (var q in poly)
            {
                float nx = q.X / q.W, ny = q.Y / q.W, rz = _projZ / q.W;
                x0 = MathF.Min(x0, (nx + 1f) * 0.5f * _w0); x1 = MathF.Max(x1, (nx + 1f) * 0.5f * _w0);
                y0 = MathF.Min(y0, (1f - ny) * 0.5f * _h0); y1 = MathF.Max(y1, (1f - ny) * 0.5f * _h0);
                r0 = MathF.Min(r0, rz); r1 = MathF.Max(r1, rz);
            }
        }

        // Core view plane: NearClip n·p + d ≥ 0 -> FPlane n·p = −d.
        public static void MirrorPlane(int idx, string pass, HP1.UEScript.UEVector n, float d)
            => Line($"MIRROR\tidx={idx}\tpass={pass}\towner=core\tplane={F(n.x)},{F(n.y)},{F(n.z)},{F(-d)}");
        public static void View(string pass, string parent, string kind, int isurf, int zone, HP1.UEScript.UEVector parentCam, HP1.UEScript.UEVector cam)
            => Line($"VIEW\tpass={pass}\tparent={parent}\tkind={kind}\tisurf={isurf}\tzone={zone}\tparent_cam={F(parentCam.x)},{F(parentCam.y)},{F(parentCam.z)}\tcam={F(cam.x)},{F(cam.y)},{F(cam.z)}");


        // HUD pipelines as created (SdlHudPass.EnsureHud): colour blend factors per HudBlend; no depth target -> zw=0.
        static readonly (string src, string dst)[] _hudPipes = new (string, string)[8];
        public static void HudPipe(int i, string src, string dst) => _hudPipes[i] = (src, dst);

        // The corona quads the frontend drew from Core's WorldFrame.Coronas (CoronaBuilder -> ScreenEffects.Overlay, HUD
        // pipeline Translucent), counterpart of the original's corona TILE records (DrawFrame corona block DrawIcon,
        // Render.dll 0x10b26dcc..0x10b26e01: Z 1.0, PolyFlags 0x104). box = the quad in frame pixels (not clipped), rz =
        // ProjZ / Z with Z = 1 (the DrawIcon depth), col = vertex colour. Logged after the render: the HUD pipelines exist.
        public static void Coronas(IReadOnlyList<OpenHP1.Sprites.SpriteDraw> draws, HP1.UEScript.WorldFrame f)
        {
            if (_w is null) return;
            var names = new Dictionary<int, string>();
            if (f != null) foreach (var a in f.Actors) names[a.ObjectId] = a.Name;
            var p = _hudPipes[(int)OpenHP1.Hud.HudBlend.Translucent];
            foreach (var d in draws)
            {
                var v0 = d.Vertices[0]; var v2 = d.Vertices[2];
                _w.WriteLine($"DRAW	pass=main	kind=CORONA	group=4	pipe=hud	blend={p.src},{p.dst}	zw=0	cw=1	at=0	flags=0x00000104	tex={d.Texture}"
                    + $"	owner={names.GetValueOrDefault(d.ActorId, d.ActorId.ToString())}	tris=2	box={F(v0.X)},{F(v0.Y)},{F(v2.X)},{F(v2.Y)}	rz={F(_projZ)},{F(_projZ)}	col={F(v0.R)},{F(v0.G)},{F(v0.B)}");
            }
        }

        public static void Draw(string kind, int group, int pipe, bool alphaTest, int flags, string tex, string owner, IEnumerable<int> surfs, uint indices, string geom = "")
        {
            if (_w is null) return;
            var p = _pipes[pipe];
            string blend = p.blend ? p.src + "," + p.dst : "none";
            string s = surfs is null ? "" : "\tsurfs=" + string.Join(",", surfs);
            _w.WriteLine($"DRAW\tpass={Pass}\tkind={kind}\tgroup={group}\tpipe={pipe}\tblend={blend}\tzw={(p.zw ? 1 : 0)}\tcw={(p.cw ? 1 : 0)}\tat={(alphaTest ? 1 : 0)}\tflags=0x{flags:x8}\ttex={tex}\towner={owner ?? ""}\ttris={indices / 3}{s}{geom}");
        }
    }
}
