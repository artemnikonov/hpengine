# Meshes: LOD, skinning, skeletal pose and animation

HP1 uses two mesh kinds: vertex meshes (`ULodMesh`, frames of vertices interpolated by `GetFrame`) and skeletal meshes (`USkeletalMesh`, a bone hierarchy posed by `ApplyAnim` and skinned in `GetFrame`). Both are drawn by `URender::DrawLodMesh` (Render.dll 1.53, body 0x10b0ff00), which first picks a level of detail from the mesh's screen size. Bone positions are game values, not just presentation: native `BonePos` (257), trailer attachment by `AnimBone`, the particle owner-mesh branch and the shadow route all read the pose. So Core computes the pose and the frontend draws it.

## Level of detail (`DrawLodMesh`)

Selection (Render.dll 0x10b0ffcd..0x10b1011d), all in the 24-bit x87 render window, so each step is a float operation. Port: [`HP1.Core/Runtime/Render/UEMeshLod.cs`](../HP1.Core/Runtime/Render/UEMeshLod.cs).

- Gate: `Mesh->LODStrength` (+0x18c) is non-zero and `Mesh->CollapsePointThus.Num` (+0x130) is non-zero; otherwise no LOD.
- `lod = Owner->LODBias (+0x78) / (GlobalShapeLODAdjust (+0x50) * GlobalShapeLOD (+0x4c) * LODStrength)`.
- `area = (Y2 - Y1 + 1) * (X2 - X1 + 1)` of the actor's `FDynamicSprite` rectangle (int).
- `r1 = area * 0.04 / ModelVerts` (+0x180; constant 0x10b387e0); `r2 = sqrt(area) / Frame->FX * 4.0` (+0xb8; constant 0x10b38600).
- `verts = (ModelVerts - LODMinVerts) * (max(r1, r2) * lod) + LODMinVerts` (+0x190). With `ShapeLODMode` (+0x54) equal to 0, `verts` is `ModelVerts` if `ShapeLODFix` (+0x58) is 0.0, else `ModelVerts * ShapeLODFix`.
- `n = min(round_to_even(verts), ModelVerts)`; `n >= ModelVerts` means no LOD.
- `URender::Init` sets `GlobalShapeLOD`, `GlobalShapeLODAdjust` and `ShapeLODFix` to 1.0 and `ShapeLODMode` to 1; no viewport or client detail setting is read. Other writers: the console commands MLOD, MLMODE, MLFIX (not ported) and, for `GlobalShapeLODAdjust`, `URender::PostRender` (see below). All 385 shipped meshes have LODStrength 1; `FrameVerts >= ModelVerts`; `LODBias` is 1 everywhere (class default).
- `GetFrame` then clamps the request through its `LODRequest` reference (0x10b10255): `USkeletalMesh::GetFrame` does `n = min(n, FrameVerts)`; `ULodMesh::GetFrame` does `n = min(SpecialVerts + n, FrameVerts) - SpecialVerts`. A mesh with `SpecialVerts > 0` is refused in the port.
- Collapse (face loop from 0x10b10a00): a face is drawn only when `FaceLevel[face] <= n`. With LOD, each wedge `w` is replaced by `CollapseWedgeThus[w]` while `Wedges[w].iVertex >= n`; the final wedge gives both the point and the UV. Without LOD a face's own wedges are drawn. Example: Dumbledore on Lev5_Final at the Trigger9 camera, 568 faces in the full mesh, 118 drawn at n=70 (the original sends fewer triangles still after its own culling; this is consistency evidence, not proof).
- An actor seen by two frames of the tree may get two different levels; `GetFrame` runs once per draw with that draw's request (D-71; not yet compared with the running original).
- Not original (improvement, author 2026-10-30): `URender::PostRender` (Render.dll 0x10b1ef80) raises `GlobalShapeLODAdjust` every frame when the frame rate falls below `MinDesiredFrameRate`; the port keeps the Init value 1.0.
- Not original (improvement, D-62, author 2026-10-15): in the enhanced visual mode the full mesh is drawn instead of the collapsed one. `VisualMode=Original` collapses it, as the original does (author 2026-10-16); Enhanced, the default, draws the full mesh.

## Skinning is a weighted sum

`USkeletalMesh::GetFrame` in Engine.dll skins vertices from bone frames (port: `SkinPoints` in [`HP1.Core/Runtime/World/UEMeshCatalog.cs`](../HP1.Core/Runtime/World/UEMeshCatalog.cs)).

- If there is exactly one influence group (+0x1d8 equals 1), a point is the bone frame applied to its local point, and the point count is `min(count, NumPoints)`.
- Otherwise a point is the sum, over every bone that influences it, of `w * (bone frame applied to the point's local point)`, with `w = (float)ushort * 1.5259022e-05` (1/65535). The loop iterates bone groups and their weights and leaves a group at a point index >= NumPoints.
- Assigning instead of summing (last bone wins) hides on meshes with 1 to 3 influences but crushes the chest of the Fred and George meshes (Neville's skeleton, up to 13 influences per point).

## Skeletal pose (`ApplyAnim`, `GetFrame`)

Port: [`HP1.Core/Runtime/World/UELevelRuntime.SkeletalPose.cs`](../HP1.Core/Runtime/World/UELevelRuntime.SkeletalPose.cs).

- Animation source: the actor's `Animation` (+0x68), or the mesh's `DefaultAnimation` (+0x24c) when null. The sequence is found by the actor's `AnimSequence` name (+0x50); tracks are matched to mesh bones by bone name against the animation's `RefBones`, and a miss logs "Warning: missing bone animation".
- Track time: `t = min(AnimFrame, 1.0) * TrackTime`. Keys are searched by accumulating `KeyTimes[k] * TimeScale` while the sum is at most t. An exact match takes the key; past the last key the pose wraps to key 0 with upper bound `TrackTime`; otherwise `alpha = (t - lo) / (hi - lo)` blends quaternions with `SlerpQuat` and positions linearly (`(1 - a) * lo + a * hi`). A bone without a track takes the reference skeleton pose.
- Key decoding: `FAnimVec::Quat` (Engine.dll 0x1041a120) computes the quaternion from three sines, with w from a square root; positions are `key * PositionScale * (1/32767)`.
- Tween: with a non-zero `TweenRate` and a previous pose in the cache, the new pose is blended with the old one: quaternion `SlerpQuat(new, old, 1 - TweenAlpha)`, position `new * TweenAlpha + old * (1 - TweenAlpha)`.
- Chain composition (`GetFrame`): each bone's `FCoords` comes from its place and is divided by the parent's down the hierarchy; the reference pose composition (conjugate quaternion, `M_local * M_parent`) was matched on 492 of 492 vertices of a 75-bone mesh.
- The render-box path (`GetRenderBoundingBox`, `ApplyAnim(rootOnly)`) runs in the 24-bit window, so the whole chain is float-by-float: `operator<<(FBox, FCoords)` (Core 0x1014f3a0), the mesh frame index, `FAnimVec::Quat`, the root block (0x1041d11c..0x1041d33b). It was compared bit for bit with the original's body (286 steps, each distinct between the two precisions).
- Open (loud STOP in the port): the pose of a channel actor (`AnimBone != 0` and Physics not Trailer) has no pose of its own (overlaying channels onto the owner's pose is ported); the animation `StartBone` offset when the animation root's parent is not itself; `bAnimByOwner` in `GetFrame` has no carriers in the campaign. An animation with an unported feature stops loudly rather than falling back to a reference pose.
- A full `ApplyAnim` is run for every DT_Mesh actor drawn by `DrawActorSprite`, in `DrawFrame` order. A vertex mesh's `GetFrame` writes no actor fields.

## Vertex mesh frames (`ULodMesh::GetFrame`, Engine.dll 0x1035e460)

Port: [`HP1.Core/Runtime/World/UELevelRuntime.VertexMeshFrame.cs`](../HP1.Core/Runtime/World/UELevelRuntime.VertexMeshFrame.cs). The body shape is ported without a bit-exact x87 check (presentation only).

- Coordinates: `C.Origin -= PrePivot + Location`; `C *= actor Rotation`; `C *= mesh RotOrigin`; `C *= FScale(DrawScale * mesh Scale)`; `C.Origin += mesh Origin`; the vertex is `(v - C.Origin)` along the axes of C.
- Frame branch (AnimFrame at least 0, or no cache yet): `f = NumFrames * max(AnimFrame, 0)`, `i = round_to_even(f - 0.5)`, `t = f - i`; the frames `(i mod N) + Start` and `((i+1) mod N) + Start` blend as `(b - a) * t + a` when `t > 0`. No sequence means frame 0.
- Tween branch (AnimFrame below 0 with a live cache): `t = 1 - AnimFrame / previous AnimFrame`; a sequence change zeroes the accumulator; a change or `t` outside [0, 1] sets `t = 0` and the previous AnimFrame to `-1/NumFrames`; `acc = (1 - acc) * t + acc`; for `acc <= 0.97` and `t > 0` the cache is blended with the sequence's Start frame, otherwise the cache is returned as is (0x1035e888..0x1035e900 fills vertices [count, full) from the Start frame at `acc > 0.97`).
- LOD request: only `min(SpecialVerts + request, FrameVerts)` vertices are computed into the cache (0x1035e4ac..0x1035e4c7) and the count is kept at cache+0xc; the draw gets back the level `GetFrame` returns. Points at or above it are stale and no drawn face reaches them.
- The cache is a `GCache` item: `GCache.Flush` (SaveGame 0x103a26b9, `UEngine::Flush`) drops it. Open: LRU eviction under memory pressure is not modelled.

## Animation logic and natives

Animation is gameplay (`AnimEnd` switches states, the Cast notify launches a spell), so the whole loop is in Core. Ports: [`HP1.Core/Runtime/World/UELevelRuntime.Animation.cs`](../HP1.Core/Runtime/World/UELevelRuntime.Animation.cs), [`HP1.Core/Runtime/World/UELevelRuntime.AnimNatives.cs`](../HP1.Core/Runtime/World/UELevelRuntime.AnimNatives.cs).

- `AActor::TickAnimation` (loop 0x103b39e4..0x103b3ac2): at most 4 passes per tick. `TweenAlpha` advances at the head of every pass and does not consume dt, so a pure tween gains up to `4 * dt * TweenRate` per tick; this is a quirk of the original, kept. Frame advance consumes dt and returns the remainder to the notify, finish and wrap passes. The animator (velocity source and receiver of AnimEnd and notifies) is the Owner when `AnimBone != 0 && bAnimTransient`.
- Frame advance (0x103b3ad8..0x103b3b43): with `AnimRate >= 0`, `AnimFrame + elapsed * AnimRate`; with a negative rate the rate is `-(|Velocity| * AnimRate)`, raised to `AnimMinRate` if below it. These run at 64-bit x87 precision in the game tick.
- Defaults of the natives: `PlayAnim` (259) and `LoopAnim` (260) use Rate 1.0 and TweenTime -1.0 (a 0.5 s tween, TweenRate 2, not an instant one). The name `None` passes through the writes and stops the animation (AnimLast 0). `TweenAnim` (294) delegates to `PlayAnim` with replace semantics and Rate 0, so AnimLast stays `1 - 1/NumFrames` and `FinishAnim` stays armed until the tween ends; the shared path also clears `bAnimMove` and removes auxiliary channels.
- Name lookup is exact on the pair (actor mesh, sequence name) and case-insensitive like a UE1 `FName` (the comparison uses `appStricmp`). A name with no sequence is a legal no-op: the original data is self-inconsistent, for example `skronMesh` carries `Breathe` while `Tut1Ron.uc` asks for `breath`. Script comparisons of `AnimSequence` (36 reads in the game's scripts) are case-blind.
- Open (the port stops loudly): the original writes the argument's FName into +0x50; two spellings with the same FName index resolve in the original to the first registered spelling in the global name table, which depends on package load order. The port writes the mesh's sequence name. Only the string form (save record, log) can see the difference.
- For a skeletal mesh `AActor::GetAnim` takes sequences from `Actor.SkelAnim`, or the mesh's `DefaultAnimation` when that is empty; for a vertex mesh, from the mesh itself. `SkelAnim` is written only by `ApplyAnim`.
