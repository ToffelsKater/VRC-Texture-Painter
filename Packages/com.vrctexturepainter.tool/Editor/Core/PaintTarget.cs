using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MeshTexturePainter
{
    internal struct PaintHit
    {
        public Vector3 point;
        public Vector3 normal;
        public Vector2 uv;
        public int triangle;
        public float distance;
    }

    /// <summary>
    /// The renderer being painted: a world space, per triangle copy of the posed
    /// mesh for texture space rendering, a BVH for cursor ray casts, and the
    /// seam padding map of the chosen UV channel.
    /// </summary>
    internal sealed class PaintTarget : IDisposable
    {
        public Renderer Renderer { get; }
        public int[] Slots { get; }
        public int UVChannel { get; }
        public string TextureProperty { get; }

        public Mesh PaintMesh { get; private set; }
        public Mesh OccluderMesh { get; private set; }
        public RenderTexture PadMap { get; private set; }
        public int MeshVersion { get; private set; }
        public int TriangleCount { get; private set; }
        public string Error { get; private set; }

        TriangleBVH bvh;
        Vector3[] triNormals;
        Vector2[] triUVs;
        int poseHash;
        MaterialPropertyBlock[] originalBlocks;
        bool previewApplied;

        /// <summary>How the texture wraps outside 0..1; decides where out of range UVs are painted.</summary>
        public TextureWrapMode WrapU { get; }
        public TextureWrapMode WrapV { get; }
        public Vector4 WrapVector => new Vector4((int)WrapU, (int)WrapV, 0f, 0f);

        /// <summary>
        /// UV channels of textures painted before this one in the same session. A
        /// triangle laid out in any of them belongs to that texture and is skipped here.
        /// </summary>
        public int[] ExcludeChannels { get; }

        public PaintTarget(Renderer renderer, int[] slots, int uvChannel, string textureProperty,
            TextureWrapMode wrapU = TextureWrapMode.Repeat, TextureWrapMode wrapV = TextureWrapMode.Repeat,
            int[] excludeChannels = null)
        {
            Renderer = renderer;
            Slots = slots.Distinct().OrderBy(s => s).ToArray();
            UVChannel = uvChannel;
            TextureProperty = textureProperty;
            WrapU = wrapU;
            WrapV = wrapV;
            ExcludeChannels = excludeChannels ?? Array.Empty<int>();
        }

        /// <summary>A triangle without a UV layout in a channel (all corners on one point or line), as Blender leaves unused UV maps.</summary>
        public static bool IsCollapsed(Vector2 a, Vector2 b, Vector2 c) =>
            Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) < 2e-12f;

        const int MaxTileSpan = 16;

        /// <summary>Tiles (integer UV cells) a triangle covers on one axis that the texture actually shows.</summary>
        static void TileRange(float min, float max, TextureWrapMode mode, out int first, out int last)
        {
            const float eps = 1e-5f;
            if (mode == TextureWrapMode.Clamp)
            {
                first = last = 0;
                return;
            }
            first = Mathf.FloorToInt(min + eps);
            last = Mathf.Max(first, Mathf.FloorToInt(max - eps));
            if (mode == TextureWrapMode.MirrorOnce)
            {
                first = Mathf.Max(first, -1);
                last = Mathf.Min(last, 0);
                if (first > last) first = last = 0;
            }
        }

        /// <summary>Maps a UV coordinate from the given tile into the 0..1 tile, as the sampler would.</summary>
        static float MapToTile(float x, int tile, TextureWrapMode mode)
        {
            switch (mode)
            {
                case TextureWrapMode.Repeat: return x - tile;
                case TextureWrapMode.Mirror: return (tile & 1) == 0 ? x - tile : tile + 1 - x;
                case TextureWrapMode.MirrorOnce: return tile == -1 ? -x : x;
                default: return x;
            }
        }

        static float WrapCoord(float x, TextureWrapMode mode)
        {
            switch (mode)
            {
                case TextureWrapMode.Repeat: return Mathf.Repeat(x, 1f);
                case TextureWrapMode.Mirror: return Mathf.PingPong(x, 1f);
                case TextureWrapMode.MirrorOnce: return Mathf.Clamp01(Mathf.Abs(x));
                default: return Mathf.Clamp01(x);
            }
        }

        /// <summary>The 0..1 texture coordinate a mesh UV samples.</summary>
        public Vector2 WrapUV(Vector2 uv) => new Vector2(WrapCoord(uv.x, WrapU), WrapCoord(uv.y, WrapV));

        public bool IsValid => Renderer != null && PaintMesh != null && Error == null;

        public static Mesh GetSourceMesh(Renderer renderer)
        {
            switch (renderer)
            {
                case SkinnedMeshRenderer smr: return smr.sharedMesh;
                case MeshRenderer mr:
                    var filter = mr.GetComponent<MeshFilter>();
                    return filter != null ? filter.sharedMesh : null;
                default: return null;
            }
        }

        public static int SubmeshForSlot(Mesh mesh, int slot) => Mathf.Clamp(slot, 0, Math.Max(0, mesh.subMeshCount - 1));

        // ------------------------------------------------------------------ baking

        public bool Bake()
        {
            Error = null;
            if (Renderer == null) { Error = "The target renderer no longer exists."; return false; }
            var source = GetSourceMesh(Renderer);
            if (source == null) { Error = "The renderer has no mesh."; return false; }

            Mesh baked = null;
            Matrix4x4 toWorld;
            try
            {
                if (Renderer is SkinnedMeshRenderer smr)
                {
                    baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                    smr.BakeMesh(baked, true);
                    toWorld = ResolveBakeMatrix(smr, source, baked);
                }
                else
                {
                    toWorld = Renderer.localToWorldMatrix;
                }
                var geometry = baked != null ? baked : source;

                var positions = new List<Vector3>();
                geometry.GetVertices(positions);
                var normals = new List<Vector3>();
                geometry.GetNormals(normals);
                var uvs = new List<Vector2>();
                source.GetUVs(UVChannel, uvs);
                if (positions.Count == 0) { Error = "The mesh has no vertices."; return false; }
                if (uvs.Count != positions.Count) { Error = $"The mesh has no UV{UVChannel} channel."; return false; }

                int vertexCount = positions.Count;
                var worldPos = new Vector3[vertexCount];
                var worldNormal = new Vector3[vertexCount];
                var normalMatrix = toWorld.inverse.transpose;
                bool hasNormals = normals.Count == vertexCount;
                for (int i = 0; i < vertexCount; i++)
                {
                    worldPos[i] = toWorld.MultiplyPoint3x4(positions[i]);
                    if (hasNormals) worldNormal[i] = normalMatrix.MultiplyVector(normals[i]).normalized;
                }

                var indices = new List<int>();
                var submeshes = new HashSet<int>(Slots.Select(s => SubmeshForSlot(source, s)));
                var allIndices = new List<int>();
                var buffer = new List<int>();
                for (int sub = 0; sub < source.subMeshCount; sub++)
                {
                    if (source.GetTopology(sub) != MeshTopology.Triangles) continue;
                    source.GetTriangles(buffer, sub);
                    allIndices.AddRange(buffer);
                    if (submeshes.Contains(sub)) indices.AddRange(buffer);
                }
                if (indices.Count < 3) { Error = "The selected material slots have no triangles."; return false; }

                // Only triangles laid out in this UV channel can receive paint. When several
                // textures are painted together (body on UV0, face decal on UV2), a triangle
                // belongs to the first texture whose channel has a layout for it.
                var claimedBy = new List<List<Vector2>>();
                foreach (int ch in ExcludeChannels)
                {
                    if (ch == UVChannel) continue;
                    var list = new List<Vector2>();
                    source.GetUVs(ch, list);
                    if (list.Count == positions.Count) claimedBy.Add(list);
                }
                var kept = new List<int>(indices.Count);
                for (int t = 0; t + 2 < indices.Count; t += 3)
                {
                    int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                    if (IsCollapsed(uvs[i0], uvs[i1], uvs[i2])) continue;
                    bool claimed = false;
                    foreach (var other in claimedBy)
                    {
                        if (IsCollapsed(other[i0], other[i1], other[i2])) continue;
                        claimed = true;
                        break;
                    }
                    if (claimed) continue;
                    kept.Add(i0);
                    kept.Add(i1);
                    kept.Add(i2);
                }
                if (kept.Count < 3)
                {
                    Error = claimedBy.Count > 0
                        ? $"No triangles are left for UV{UVChannel}: every triangle with a UV{UVChannel} layout is already painted through an earlier texture."
                        : $"No triangle of the selected material slots has a UV{UVChannel} layout.";
                    return false;
                }
                indices = kept;

                int triCount = indices.Count / 3;
                var triVerts = new Vector3[triCount * 3];
                triNormals = new Vector3[triCount * 3];
                triUVs = new Vector2[triCount * 3];
                var triInfo = new Vector4[triCount * 3];
                for (int t = 0; t < triCount; t++)
                {
                    int b = t * 3;
                    int i0 = indices[b], i1 = indices[b + 1], i2 = indices[b + 2];
                    Vector3 p0 = worldPos[i0], p1 = worldPos[i1], p2 = worldPos[i2];
                    Vector3 c = (p0 + p1 + p2) / 3f;
                    float r = Mathf.Sqrt(Mathf.Max((p0 - c).sqrMagnitude, Mathf.Max((p1 - c).sqrMagnitude, (p2 - c).sqrMagnitude)));
                    Vector3 face = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                    triVerts[b] = p0; triVerts[b + 1] = p1; triVerts[b + 2] = p2;
                    triNormals[b] = hasNormals ? worldNormal[i0] : face;
                    triNormals[b + 1] = hasNormals ? worldNormal[i1] : face;
                    triNormals[b + 2] = hasNormals ? worldNormal[i2] : face;
                    triUVs[b] = uvs[i0]; triUVs[b + 1] = uvs[i1]; triUVs[b + 2] = uvs[i2];
                    var info = new Vector4(c.x, c.y, c.z, r);
                    triInfo[b] = info; triInfo[b + 1] = info; triInfo[b + 2] = info;
                }

                if (PaintMesh == null)
                    PaintMesh = new Mesh { name = "MTP Paint Mesh", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
                // Texture space copies: every tile a triangle's UVs reach is shifted
                // (or mirrored) into 0..1. A triangle crossing a tile edge gets one
                // copy per tile, so each part lands where the sampler reads it.
                var meshVerts = new List<Vector3>(triVerts.Length);
                var meshNormals = new List<Vector3>(triVerts.Length);
                var meshUVs = new List<Vector2>(triVerts.Length);
                var meshInfo = new List<Vector4>(triVerts.Length);
                int limited = 0;
                for (int t = 0; t < triCount; t++)
                {
                    int b = t * 3;
                    Vector2 a0 = triUVs[b], a1 = triUVs[b + 1], a2 = triUVs[b + 2];
                    TileRange(Mathf.Min(a0.x, Mathf.Min(a1.x, a2.x)), Mathf.Max(a0.x, Mathf.Max(a1.x, a2.x)), WrapU, out int u0, out int u1);
                    TileRange(Mathf.Min(a0.y, Mathf.Min(a1.y, a2.y)), Mathf.Max(a0.y, Mathf.Max(a1.y, a2.y)), WrapV, out int v0, out int v1);
                    if (u1 - u0 >= MaxTileSpan) { u1 = u0 + MaxTileSpan - 1; limited++; }
                    if (v1 - v0 >= MaxTileSpan) { v1 = v0 + MaxTileSpan - 1; limited++; }
                    for (int ku = u0; ku <= u1; ku++)
                    for (int kv = v0; kv <= v1; kv++)
                    for (int k = 0; k < 3; k++)
                    {
                        var uv = triUVs[b + k];
                        meshVerts.Add(triVerts[b + k]);
                        meshNormals.Add(triNormals[b + k]);
                        meshInfo.Add(triInfo[b + k]);
                        meshUVs.Add(new Vector2(MapToTile(uv.x, ku, WrapU), MapToTile(uv.y, kv, WrapV)));
                    }
                }
                if (limited > 0)
                    Debug.LogWarning($"VRC Texture Painter: {limited} triangles on '{Renderer.name}' span more than {MaxTileSpan} UV tiles; only the first {MaxTileSpan} tiles are painted.");

                PaintMesh.Clear();
                PaintMesh.SetVertices(meshVerts);
                PaintMesh.SetNormals(meshNormals);
                PaintMesh.SetUVs(0, meshUVs);
                PaintMesh.SetUVs(1, meshInfo);
                var sequential = new int[meshVerts.Count];
                for (int i = 0; i < sequential.Length; i++) sequential[i] = i;
                PaintMesh.SetIndices(sequential, MeshTopology.Triangles, 0, false);
                PaintMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
                PaintMesh.UploadMeshData(false);

                if (OccluderMesh == null)
                    OccluderMesh = new Mesh { name = "MTP Occluder Mesh", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
                OccluderMesh.Clear();
                OccluderMesh.SetVertices(worldPos);
                OccluderMesh.SetIndices(allIndices, MeshTopology.Triangles, 0, false);
                OccluderMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
                OccluderMesh.UploadMeshData(false);

                bvh = new TriangleBVH(triVerts);
                TriangleCount = triCount;
                poseHash = ComputePoseHash();
                MeshVersion++;
                return true;
            }
            finally
            {
                if (baked != null) Object.DestroyImmediate(baked);
            }
        }

        /// <summary>
        /// BakeMesh output is in the renderer's space, with or without its scale
        /// depending on Unity version and flags. Compare a few vertices against
        /// manual skinning and pick the matrix that reproduces the real pose.
        /// </summary>
        static Matrix4x4 ResolveBakeMatrix(SkinnedMeshRenderer smr, Mesh source, Mesh baked)
        {
            var t = smr.transform;
            var withScale = t.localToWorldMatrix;
            var withoutScale = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            if ((t.lossyScale - Vector3.one).sqrMagnitude < 1e-8f) return withScale;

            var srcVerts = source.vertices;
            var bakedVerts = baked.vertices;
            if (srcVerts.Length == 0 || bakedVerts.Length != srcVerts.Length) return withScale;

            var bones = smr.bones;
            var bindposes = source.bindposes;
            var weights = source.boneWeights;
            bool skinned = bones != null && bones.Length > 0 && bindposes.Length > 0 && weights.Length == srcVerts.Length;

            double errWith = 0, errWithout = 0;
            int stride = Math.Max(1, srcVerts.Length / 64);
            for (int i = 0; i < srcVerts.Length; i += stride)
            {
                Vector3 reference;
                if (skinned)
                {
                    var w = weights[i];
                    reference = Vector3.zero;
                    float total = 0f;
                    Accumulate(ref reference, ref total, w.boneIndex0, w.weight0, bones, bindposes, srcVerts[i]);
                    Accumulate(ref reference, ref total, w.boneIndex1, w.weight1, bones, bindposes, srcVerts[i]);
                    Accumulate(ref reference, ref total, w.boneIndex2, w.weight2, bones, bindposes, srcVerts[i]);
                    Accumulate(ref reference, ref total, w.boneIndex3, w.weight3, bones, bindposes, srcVerts[i]);
                    if (total <= 0f) continue;
                    reference /= total;
                }
                else
                {
                    reference = withScale.MultiplyPoint3x4(srcVerts[i]);
                }
                errWith += (withScale.MultiplyPoint3x4(bakedVerts[i]) - reference).sqrMagnitude;
                errWithout += (withoutScale.MultiplyPoint3x4(bakedVerts[i]) - reference).sqrMagnitude;
            }
            return errWithout < errWith ? withoutScale : withScale;
        }

        static void Accumulate(ref Vector3 sum, ref float total, int bone, float weight, Transform[] bones, Matrix4x4[] bindposes, Vector3 v)
        {
            if (weight <= 0f || bone < 0 || bone >= bones.Length || bone >= bindposes.Length || bones[bone] == null) return;
            sum += weight * (bones[bone].localToWorldMatrix * bindposes[bone]).MultiplyPoint3x4(v);
            total += weight;
        }

        int ComputePoseHash()
        {
            if (Renderer == null) return 0;
            unchecked
            {
                int h = Renderer.localToWorldMatrix.GetHashCode();
                if (Renderer is SkinnedMeshRenderer smr)
                {
                    var bones = smr.bones;
                    for (int i = 0; i < bones.Length && i < 1024; i++)
                        if (bones[i] != null) h = h * 31 + bones[i].localToWorldMatrix.GetHashCode();
                    var mesh = smr.sharedMesh;
                    int shapes = mesh != null ? mesh.blendShapeCount : 0;
                    for (int i = 0; i < shapes; i++) h = h * 31 + smr.GetBlendShapeWeight(i).GetHashCode();
                    h = h * 31 + (mesh != null ? mesh.GetInstanceID() : 0);
                }
                else
                {
                    var mesh = GetSourceMesh(Renderer);
                    h = h * 31 + (mesh != null ? mesh.GetInstanceID() : 0);
                }
                return h;
            }
        }

        /// <summary>True when the renderer moved, was posed or had blend shapes changed since the last bake.</summary>
        public bool PoseChanged() => Renderer != null && ComputePoseHash() != poseHash;

        // ------------------------------------------------------------------ seam padding

        /// <summary>Builds the texel offset map used for seam padding with a jump flood.</summary>
        public void BuildPadMap(int width, int height, int maxDistance)
        {
            var old = PadMap;
            RTUtil.Release(ref old);
            PadMap = null;

            var coverage = RTUtil.Create("MTP Coverage", width, height, RenderTextureFormat.R8, filter: FilterMode.Point);
            RTUtil.Clear(coverage, Color.clear);
            DrawCoverage(coverage);

            var a = RTUtil.Create("MTP JFA A", width, height, RenderTextureFormat.RGHalf, filter: FilterMode.Point);
            var b = RTUtil.Create("MTP JFA B", width, height, RenderTextureFormat.RGHalf, filter: FilterMode.Point);
            var mat = PaintResources.Blit;
            RTUtil.SetTexSize(mat, width, height);
            mat.SetVector(Ids.WrapMode, WrapVector);
            RTUtil.Blit(coverage, a, mat, PaintResources.BlitJfaInit);

            maxDistance = Mathf.Clamp(maxDistance, 0, 2048);
            if (maxDistance > 0)
            {
                for (int step = RTUtil.NextPow2(maxDistance); step >= 1; step /= 2)
                {
                    mat.SetFloat(Ids.JumpStep, step);
                    RTUtil.Blit(a, b, mat, PaintResources.BlitJfaStep);
                    (a, b) = (b, a);
                }
                mat.SetFloat(Ids.JumpStep, 1);
                RTUtil.Blit(a, b, mat, PaintResources.BlitJfaStep);
                (a, b) = (b, a);
            }

            PadMap = RTUtil.Create("MTP Pad Map", width, height, RenderTextureFormat.RGHalf, filter: FilterMode.Point);
            mat.SetFloat(Ids.MaxDistSq, (float)maxDistance * maxDistance);
            RTUtil.Blit(a, PadMap, mat, PaintResources.BlitJfaLimit);

            RTUtil.Release(ref coverage);
            RTUtil.Release(ref a);
            RTUtil.Release(ref b);
        }

        public void DrawCoverage(RenderTexture target)
        {
            var mat = PaintResources.UVSpace;
            mat.SetFloat(Ids.CullTriangles, 0f);
            mat.SetMatrix(Ids.Mirror, Matrix4x4.identity);
            var cmd = PaintResources.Cmd;
            cmd.SetRenderTarget(target);
            cmd.DrawMesh(PaintMesh, Matrix4x4.identity, mat, 0, PaintResources.UVCoverage);
            PaintResources.Execute(cmd);
        }

        // ------------------------------------------------------------------ queries

        public bool Raycast(Ray ray, out PaintHit hit)
        {
            hit = default;
            if (bvh == null) return false;
            if (!bvh.Raycast(ray, float.MaxValue, out int tri, out float distance, out float u, out float v)) return false;
            int b = tri * 3;
            float w = 1f - u - v;
            hit.triangle = tri;
            hit.distance = distance;
            hit.point = ray.GetPoint(distance);
            hit.normal = (triNormals[b] * w + triNormals[b + 1] * u + triNormals[b + 2] * v).normalized;
            hit.uv = triUVs[b] * w + triUVs[b + 1] * u + triUVs[b + 2] * v;
            return true;
        }

        // ------------------------------------------------------------------ preview

        /// <summary>Shows a texture on the selected material slots through property blocks (never touches the material asset).</summary>
        public void ApplyPreview(Texture texture) => ApplyPreview(new[] { (TextureProperty, texture) });

        /// <summary>Shows several textures at once (one per painted property) on the selected slots.</summary>
        public void ApplyPreview(IReadOnlyList<(string property, Texture texture)> textures)
        {
            if (Renderer == null) return;
            int slotCount = Renderer.sharedMaterials.Length;
            if (!previewApplied)
            {
                originalBlocks = new MaterialPropertyBlock[slotCount];
                foreach (int slot in Slots)
                {
                    if (slot >= slotCount) continue;
                    originalBlocks[slot] = new MaterialPropertyBlock();
                    Renderer.GetPropertyBlock(originalBlocks[slot], slot);
                }
                previewApplied = true;
            }
            foreach (int slot in Slots)
            {
                if (slot >= slotCount) continue;
                var block = new MaterialPropertyBlock();
                Renderer.GetPropertyBlock(block, slot);
                foreach (var (property, texture) in textures) block.SetTexture(property, texture);
                Renderer.SetPropertyBlock(block, slot);
            }
        }

        public void RestorePreview()
        {
            if (!previewApplied) return;
            previewApplied = false;
            if (Renderer == null || originalBlocks == null) return;
            for (int slot = 0; slot < originalBlocks.Length; slot++)
            {
                if (originalBlocks[slot] == null) continue;
                Renderer.SetPropertyBlock(originalBlocks[slot], slot);
            }
            originalBlocks = null;
        }

        public void Dispose()
        {
            RestorePreview();
            var pad = PadMap;
            RTUtil.Release(ref pad);
            PadMap = null;
            if (PaintMesh != null) Object.DestroyImmediate(PaintMesh);
            if (OccluderMesh != null) Object.DestroyImmediate(OccluderMesh);
            PaintMesh = null;
            OccluderMesh = null;
            bvh = null;
        }
    }
}
