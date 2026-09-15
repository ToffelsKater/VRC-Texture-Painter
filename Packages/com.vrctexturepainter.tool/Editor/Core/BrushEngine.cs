using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeshTexturePainter
{
    internal struct DabInput
    {
        /// <summary>Screen pixel position, origin bottom left (HandleUtility.GUIPointToScreenPixelCoordinate).</summary>
        public Vector2 screenPixel;
        public float radius;
        public float strength;
        public bool hasHit;
        public Vector3 hitPoint;
        public Vector3 hitNormal;
    }

    /// <summary>
    /// GPU side of the brushes. Every dab rasterizes the paint mesh in texture
    /// space; each texel is weighted by where its 3D surface point lands under
    /// the brush, so paint is seam, mirror and overlap aware by construction.
    /// Blur and color blend read their colours from a small view space capture
    /// of the model around the brush, which is continuous across UV seams.
    /// </summary>
    internal sealed class BrushEngine : IDisposable
    {
        sealed class CaptureBuffers
        {
            public RenderTexture color, weight, colorTemp, weightTemp, depth;

            public CaptureBuffers(int res)
            {
                color = RTUtil.Create("MTP Capture Color", res, res, RenderTextureFormat.ARGBHalf, mips: true);
                weight = RTUtil.Create("MTP Capture Weight", res, res, RenderTextureFormat.RHalf, mips: true);
                colorTemp = RTUtil.Create("MTP Capture Color Temp", res, res, RenderTextureFormat.ARGBHalf);
                weightTemp = RTUtil.Create("MTP Capture Weight Temp", res, res, RenderTextureFormat.RHalf);
                depth = RTUtil.Create("MTP Capture Depth", res, res, RenderTextureFormat.Depth, depth: 24);
            }

            public void Release()
            {
                RTUtil.Release(ref color);
                RTUtil.Release(ref weight);
                RTUtil.Release(ref colorTemp);
                RTUtil.Release(ref weightTemp);
                RTUtil.Release(ref depth);
            }
        }

        const int MaxCaptureResolution = 1024;

        readonly Dictionary<int, CaptureBuffers> captures = new Dictionary<int, CaptureBuffers>();
        readonly System.Random random = new System.Random();
        RenderTexture depthMap;
        Matrix4x4 depthViewProj;
        int depthMeshVersion = -1;
        PaintTarget depthTarget;

        public RenderTexture DepthMap => depthMap;

        /// <summary>Renders the occlusion depth of the target from the camera if the view or pose changed.</summary>
        public void PrepareCamera(Camera cam, PaintTarget target)
        {
            int w = Math.Max(1, cam.pixelWidth), h = Math.Max(1, cam.pixelHeight);
            var viewProj = cam.projectionMatrix * cam.worldToCameraMatrix;
            if (depthMap != null && depthMap.width == w && depthMap.height == h && viewProj == depthViewProj
                && depthTarget == target && depthMeshVersion == target.MeshVersion)
                return;

            if (depthMap == null || depthMap.width != w || depthMap.height != h)
            {
                RTUtil.Release(ref depthMap);
                depthMap = RTUtil.Create("MTP Occlusion Depth", w, h, RenderTextureFormat.RFloat, filter: FilterMode.Point, depth: 24);
            }

            var mat = PaintResources.ViewSpace;
            mat.SetMatrix(Ids.CaptureVP, GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix);
            mat.SetMatrix(Ids.WorldToView, cam.worldToCameraMatrix);
            mat.SetMatrix(Ids.Mirror, Matrix4x4.identity);
            var cmd = PaintResources.Cmd;
            cmd.SetRenderTarget(depthMap);
            cmd.ClearRenderTarget(true, true, new Color(1e7f, 1e7f, 1e7f, 1e7f));
            cmd.DrawMesh(target.OccluderMesh != null ? target.OccluderMesh : target.PaintMesh, Matrix4x4.identity, mat, 0, PaintResources.ViewDepth);
            PaintResources.Execute(cmd);

            depthViewProj = viewProj;
            depthTarget = target;
            depthMeshVersion = target.MeshVersion;
        }

        public static List<Matrix4x4> Mirrors(BrushSettings settings, PaintTarget target)
        {
            var list = new List<Matrix4x4> { Matrix4x4.identity };
            if (!settings.mirror || target.Renderer == null) return list;
            var root = target.Renderer.transform.root;
            var scale = Vector3.one;
            scale[(int)settings.mirrorAxis] = -1f;
            list.Add(root.localToWorldMatrix * Matrix4x4.Scale(scale) * root.worldToLocalMatrix);
            return list;
        }

        void SetBrushUniforms(Material m, Camera cam, BrushSettings s, in DabInput d, Matrix4x4 mirror, PaintTool tool)
        {
            var proj = cam.projectionMatrix;
            var view = cam.worldToCameraMatrix;
            float w = Math.Max(1, cam.pixelWidth), h = Math.Max(1, cam.pixelHeight);
            float pixelWorld = 2f / (Mathf.Abs(proj.m11) * h);

            m.SetMatrix(Ids.BrushVP, proj * view);
            m.SetMatrix(Ids.WorldToView, view);
            m.SetMatrix(Ids.Mirror, mirror);
            m.SetVector(Ids.ScreenSize, new Vector4(w, h, 1f / w, 1f / h));
            m.SetVector(Ids.BrushCenter, new Vector4(d.screenPixel.x, d.screenPixel.y, Math.Max(0.5f, d.radius), 0f));

            float worldRadius = 0f;
            if (d.hasHit)
            {
                float depth = Math.Max(1e-5f, -view.MultiplyPoint3x4(d.hitPoint).z);
                worldRadius = d.radius * (cam.orthographic ? pixelWorld : pixelWorld * depth);
            }
            m.SetVector(Ids.BrushWorld, new Vector4(d.hitPoint.x, d.hitPoint.y, d.hitPoint.z, worldRadius));

            var camPos = cam.transform.position;
            var camFwd = cam.transform.forward;
            m.SetVector(Ids.CamPos, new Vector4(camPos.x, camPos.y, camPos.z, cam.orthographic ? 1f : 0f));
            m.SetVector(Ids.CamForward, new Vector4(camFwd.x, camFwd.y, camFwd.z, 0f));
            m.SetFloat(Ids.ProjScale, Mathf.Abs(proj.m11) * h * 0.5f);
            m.SetFloat(Ids.PixelWorld, pixelWorld);

            bool sphere = s.falloffShape == FalloffShape.Sphere && d.hasHit;
            m.SetFloat(Ids.BrushShape, sphere ? 1f : 0f);
            m.SetFloat(Ids.HardEdge, tool == PaintTool.HardBrush ? 1f : 0f);
            m.SetFloat(Ids.Hardness, s.For(tool).hardness);
            m.SetFloat(Ids.Occlusion, s.occlusion && depthMap != null ? 1f : 0f);
            m.SetFloat(Ids.BackfaceCull, s.backfaceCulling ? 1f : 0f);
            m.SetFloat(Ids.NormalFade, s.normalFalloff ? Mathf.Cos(s.normalAngle * Mathf.Deg2Rad) : 0f);
            m.SetFloat(Ids.CullTriangles, 1f);
            m.SetFloat(Ids.MixSpace, (float)s.mixSpace);
            m.SetTexture(Ids.DepthMap, depthMap != null ? (Texture)depthMap : Texture2D.whiteTexture);
        }

        /// <summary>Hard, soft and eraser dabs: accumulate the brush into the document's stroke mask.</summary>
        public void StrokeDab(PaintDocument doc, PaintTarget target, Camera cam, BrushSettings s, PaintTool tool, DabInput d)
        {
            if (doc.StrokeMask == null || (s.falloffShape == FalloffShape.Sphere && !d.hasHit)) return;
            var mat = PaintResources.UVSpace;
            foreach (var mirror in Mirrors(s, target))
            {
                SetBrushUniforms(mat, cam, s, d, mirror, tool);
                mat.SetFloat(Ids.DabStrength, d.strength);
                var cmd = PaintResources.Cmd;
                cmd.SetRenderTarget(doc.StrokeMask);
                cmd.DrawMesh(target.PaintMesh, Matrix4x4.identity, mat, 0, PaintResources.UVMask);
                PaintResources.Execute(cmd);
            }
            doc.NotifyStrokeChanged();
        }

        /// <summary>Blur or color blend on a single texture.</summary>
        public void FilterDab(PaintDocument doc, PaintTarget target, Camera cam, BrushSettings s, PaintTool tool, DabInput d)
            => FilterDab(new[] { new PaintPart { Document = doc, Target = target } }, cam, s, tool, d);

        /// <summary>
        /// Blur (average of each texel's neighbourhood) or color blend (one weighted
        /// average of the whole brush). Every texture of the session is captured into
        /// the same view, so blurring over the border between two textures (a body
        /// texture on UV0 next to a face decal on UV2) mixes both sides.
        /// </summary>
        public void FilterDab(IReadOnlyList<PaintPart> parts, Camera cam, BrushSettings s, PaintTool tool, DabInput d)
        {
            var active = new List<PaintPart>();
            foreach (var p in parts)
                if (p.Document.ActiveLayer != null && p.Target.PadMap != null && p.Target.PaintMesh != null) active.Add(p);
            if (active.Count == 0 || (s.falloffShape == FalloffShape.Sphere && !d.hasHit)) return;

            // Flatten: one average of the whole brush. Transition: a gaussian whose 10-90%
            // ramp spans blendWidth of the brush diameter (ramp ~ 2.56 sigma). Blur: blurSize.
            bool average = tool == PaintTool.ColorBlend && s.blendMode == ColorBlendMode.Flatten;
            float radius = Math.Max(1f, d.radius);
            float sigmaPx = average ? 0f
                : tool == PaintTool.ColorBlend ? Math.Max(0.5f, s.blendWidth * radius * 0.78f)
                : Math.Max(0.5f, s.blurSize * radius * 0.5f);
            float margin = average ? 1f : sigmaPx * 3f;
            float side = 2f * (radius + margin);
            int res = Mathf.Clamp(RTUtil.NextPow2(Mathf.CeilToInt(side)), 32, MaxCaptureResolution);
            if (!captures.TryGetValue(res, out var buffers))
            {
                buffers = new CaptureBuffers(res);
                captures[res] = buffers;
            }

            var proj = cam.projectionMatrix;
            var view = cam.worldToCameraMatrix;
            float w = Math.Max(1, cam.pixelWidth), h = Math.Max(1, cam.pixelHeight);
            float hx = side / w, hy = side / h;
            float cx = d.screenPixel.x / w * 2f - 1f, cy = d.screenPixel.y / h * 2f - 1f;
            var crop = Matrix4x4.identity;
            crop.m00 = 1f / hx; crop.m03 = -cx / hx;
            crop.m11 = 1f / hy; crop.m13 = -cy / hy;
            var captureVP = GL.GetGPUProjectionMatrix(crop * proj, true) * view;
            var rect = new Vector4(d.screenPixel.x - side * 0.5f, d.screenPixel.y - side * 0.5f, side, side);

            var viewMat = PaintResources.ViewSpace;
            var uvMat = PaintResources.UVSpace;
            var blit = PaintResources.Blit;

            // Masks are data: mix the stored values, treat alpha as a plain channel
            // and only write the channels of the selected mask colour.
            bool mask = active[0].Document.IsMask;
            var writeMask = mask ? MaskChannels.WriteMask(s.maskChannel) : ColorWriteMask.Red | ColorWriteMask.Green | ColorWriteMask.Blue;

            foreach (var mirror in Mirrors(s, active[0].Target))
            {
                // 1. capture every texture as seen around the brush (shared depth: the nearest surface wins)
                SetBrushUniforms(viewMat, cam, s, d, mirror, tool);
                if (mask) viewMat.SetFloat(Ids.MixSpace, (float)ColorMixSpace.Srgb);
                viewMat.SetFloat(Ids.IgnoreAlpha, mask ? 1f : 0f);
                viewMat.SetMatrix(Ids.CaptureVP, captureVP);
                viewMat.SetFloat(Ids.WeightByBrush, average ? 1f : 0f);
                CaptureParts(active, viewMat, buffers.color, buffers.depth, PaintResources.ViewCaptureColor);
                CaptureParts(active, viewMat, buffers.weight, buffers.depth, PaintResources.ViewCaptureWeight);

                // 2. filter it
                if (average)
                {
                    buffers.color.GenerateMips();
                    buffers.weight.GenerateMips();
                }
                else
                {
                    float sigmaTex = sigmaPx * res / side;
                    float step = Math.Max(1f, sigmaTex * 3f / 12f);
                    blit.SetFloat(Ids.BlurSigma, sigmaTex / step);
                    BlurPair(blit, buffers.color, buffers.colorTemp, step / res);
                    BlurPair(blit, buffers.weight, buffers.weightTemp, step / res);
                }

                // 3. write back through texture space
                SetBrushUniforms(uvMat, cam, s, d, mirror, tool);
                if (mask) uvMat.SetFloat(Ids.MixSpace, (float)ColorMixSpace.Srgb);
                uvMat.SetFloat(Ids.ColorWriteMask, (float)writeMask);
                uvMat.SetFloat(Ids.DabStrength, d.strength);
                uvMat.SetFloat(Ids.Seed, (float)random.NextDouble() * 1000f);
                uvMat.SetFloat(Ids.TargetMode, average ? 1f : 0f);
                uvMat.SetFloat(Ids.CaptureMaxMip, Mathf.Log(res, 2));
                uvMat.SetVector(Ids.CaptureRect, rect);
                uvMat.SetTexture(Ids.CaptureColor, buffers.color);
                uvMat.SetTexture(Ids.CaptureWeight, buffers.weight);

                foreach (var part in active)
                {
                    var layer = part.Document.ActiveLayer;
                    var mesh = part.Target.PaintMesh;
                    var cmd = PaintResources.Cmd;
                    cmd.SetRenderTarget(layer.texture);
                    cmd.DrawMesh(mesh, Matrix4x4.identity, uvMat, 0, PaintResources.UVApplyRGB);
                    if (!mask && s.affectAlpha && !layer.lockAlpha)
                    {
                        cmd.DrawMesh(mesh, Matrix4x4.identity, uvMat, 0, PaintResources.UVApplyAlphaMul);
                        cmd.DrawMesh(mesh, Matrix4x4.identity, uvMat, 0, PaintResources.UVApplyAlphaAdd);
                    }
                    PaintResources.Execute(cmd);
                }
            }
            foreach (var part in active) part.Document.ActiveLayer.MarkDirty();
        }

        static void CaptureParts(List<PaintPart> parts, Material viewMat, RenderTexture target, RenderTexture depth, int pass)
        {
            var cmd = PaintResources.Cmd;
            cmd.SetRenderTarget(target, depth);
            cmd.ClearRenderTarget(true, true, Color.clear);
            PaintResources.Execute(cmd);
            foreach (var part in parts)
            {
                var doc = part.Document;
                // Color blend reads the layer as it was when the stroke started, so dabs never
                // pick up colours the stroke itself moved (which is what makes a smear).
                viewMat.SetTexture(Ids.Layer, part.FilterSource != null ? part.FilterSource : doc.ActiveLayer.texture);
                viewMat.SetTexture(Ids.PadMap, part.Target.PadMap);
                RTUtil.SetTexSize(viewMat, doc.Width, doc.Height);
                viewMat.SetVector(Ids.WrapMode, part.Target.WrapVector);
                cmd = PaintResources.Cmd;
                cmd.SetRenderTarget(target, depth);
                cmd.DrawMesh(part.Target.PaintMesh, Matrix4x4.identity, viewMat, 0, pass);
                PaintResources.Execute(cmd);
            }
        }

        static void BlurPair(Material blit, RenderTexture tex, RenderTexture temp, float texelStep)
        {
            blit.SetVector(Ids.BlurDir, new Vector4(texelStep, 0f, 0f, 0f));
            RTUtil.Blit(tex, temp, blit, PaintResources.BlitBlur);
            blit.SetVector(Ids.BlurDir, new Vector4(0f, texelStep, 0f, 0f));
            RTUtil.Blit(temp, tex, blit, PaintResources.BlitBlur);
        }

        public void Dispose()
        {
            RTUtil.Release(ref depthMap);
            foreach (var c in captures.Values) c.Release();
            captures.Clear();
        }
    }
}
