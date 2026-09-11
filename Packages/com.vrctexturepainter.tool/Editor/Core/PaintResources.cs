using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MeshTexturePainter
{
    /// <summary>Shader materials, pass indices and property ids.</summary>
    internal static class PaintResources
    {
        public const int UVCoverage = 0;
        public const int UVMask = 1;
        public const int UVApplyRGB = 2;
        public const int UVApplyAlphaMul = 3;
        public const int UVApplyAlphaAdd = 4;

        public const int ViewDepth = 0;
        public const int ViewCaptureColor = 1;
        public const int ViewCaptureWeight = 2;

        public const int BlitComposite = 0;
        public const int BlitCommit = 1;
        public const int BlitPresent = 2;
        public const int BlitJfaInit = 3;
        public const int BlitJfaStep = 4;
        public const int BlitJfaLimit = 5;
        public const int BlitBlur = 6;
        public const int BlitImport = 7;
        public const int BlitFill = 8;

        static Material uvSpace, viewSpace, blit, gui;
        static CommandBuffer commandBuffer;

        static float uvFlip; // 0 until calibrated

        public static Material UVSpace
        {
            get
            {
                bool created = uvSpace == null;
                var m = Get(ref uvSpace, "Hidden/MeshTexturePainter/UVSpace");
                if (uvFlip == 0f) uvFlip = CalibrateUVFlip(m);
                // a recreated material (asset unload, shader reimport) must get the flip back,
                // otherwise every texture space triangle collapses to a line
                if (created || m.GetFloat(Ids.UVFlip) != uvFlip) m.SetFloat(Ids.UVFlip, uvFlip);
                return m;
            }
        }

        /// <summary>
        /// Texture space rendering writes clip positions directly, bypassing the
        /// projection flip Unity applies for render textures. Instead of relying
        /// on per API conventions, render a known UV rectangle once and compare it
        /// with a blitted reference to find the vertical direction.
        /// </summary>
        static float CalibrateUVFlip(Material m)
        {
            const int size = 4;
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up });
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            mesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 0.5f), new Vector2(0, 0.5f) });
            mesh.SetUVs(1, new Vector4[4]);
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);

            var rendered = RTUtil.Create("MTP UV Calibration", size, size, RenderTextureFormat.ARGB32, filter: FilterMode.Point);
            var reference = RTUtil.Create("MTP UV Reference", size, size, RenderTextureFormat.ARGB32, filter: FilterMode.Point);
            var source = new Texture2D(size, size, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            try
            {
                RTUtil.Clear(rendered, Color.clear);
                m.SetFloat(Ids.UVFlip, 1f);
                m.SetFloat(Ids.CullTriangles, 0f);
                m.SetMatrix(Ids.Mirror, Matrix4x4.identity);
                var cmd = Cmd;
                cmd.SetRenderTarget(rendered);
                cmd.DrawMesh(mesh, Matrix4x4.identity, m, 0, UVCoverage);
                Execute(cmd);

                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    source.SetPixel(x, y, y < size / 2 ? Color.white : Color.clear);
                source.Apply(false);
                RTUtil.Upload(source, reference);

                bool renderedBottom = RTUtil.ReadPixel(rendered, 1, 0).r > 0.5f;
                bool referenceBottom = RTUtil.ReadPixel(reference, 1, 0).r > 0.5f;
                return renderedBottom == referenceBottom ? 1f : -1f;
            }
            finally
            {
                RTUtil.Release(ref rendered);
                RTUtil.Release(ref reference);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(mesh);
            }
        }
        public static Material ViewSpace => Get(ref viewSpace, "Hidden/MeshTexturePainter/ViewSpace");
        public static Material Blit => Get(ref blit, "Hidden/MeshTexturePainter/Blit");
        public static Material Gui => Get(ref gui, "Hidden/MeshTexturePainter/GUI");

        /// <summary>A cleared command buffer for immediate execution.</summary>
        public static CommandBuffer Cmd
        {
            get
            {
                if (commandBuffer == null) commandBuffer = new CommandBuffer { name = "VRC Texture Painter" };
                commandBuffer.Clear();
                return commandBuffer;
            }
        }

        public static void Execute(CommandBuffer cmd)
        {
            var previous = RenderTexture.active;
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            RenderTexture.active = previous;
        }

        static Material Get(ref Material material, string shaderName)
        {
            if (material != null) return material;
            var shader = Shader.Find(shaderName);
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException($"VRC Texture Painter: shader '{shaderName}' is missing or unsupported. Reimport the package.");
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return material;
        }
    }

    internal static class Ids
    {
        public static readonly int MainTex = Shader.PropertyToID("_MainTex");
        public static readonly int Layer = Shader.PropertyToID("_Layer");
        public static readonly int StrokeMask = Shader.PropertyToID("_StrokeMask");
        public static readonly int PadMap = Shader.PropertyToID("_PadMap");
        public static readonly int TexSize = Shader.PropertyToID("_TexSize");
        public static readonly int Opacity = Shader.PropertyToID("_Opacity");
        public static readonly int BlendMode = Shader.PropertyToID("_BlendMode");
        public static readonly int StrokeMode = Shader.PropertyToID("_StrokeMode");
        public static readonly int StrokeOpacity = Shader.PropertyToID("_StrokeOpacity");
        public static readonly int BrushColor = Shader.PropertyToID("_BrushColor");
        public static readonly int LockAlpha = Shader.PropertyToID("_LockAlpha");
        public static readonly int ToLinear = Shader.PropertyToID("_ToLinear");
        public static readonly int ToGamma = Shader.PropertyToID("_ToGamma");
        public static readonly int JumpStep = Shader.PropertyToID("_JumpStep");
        public static readonly int MaxDistSq = Shader.PropertyToID("_MaxDistSq");
        public static readonly int BlurDir = Shader.PropertyToID("_BlurDir");
        public static readonly int BlurSigma = Shader.PropertyToID("_BlurSigma");
        public static readonly int FillColor = Shader.PropertyToID("_FillColor");
        public static readonly int Checker = Shader.PropertyToID("_Checker");
        public static readonly int UVFlip = Shader.PropertyToID("_UVFlip");
        public static readonly int WrapMode = Shader.PropertyToID("_WrapMode");
        public static readonly int MixSpace = Shader.PropertyToID("_MixSpace");

        public static readonly int BrushVP = Shader.PropertyToID("_BrushVP");
        public static readonly int WorldToView = Shader.PropertyToID("_WorldToView");
        public static readonly int Mirror = Shader.PropertyToID("_Mirror");
        public static readonly int ScreenSize = Shader.PropertyToID("_ScreenSize");
        public static readonly int BrushCenter = Shader.PropertyToID("_BrushCenter");
        public static readonly int BrushWorld = Shader.PropertyToID("_BrushWorld");
        public static readonly int CamPos = Shader.PropertyToID("_CamPos");
        public static readonly int CamForward = Shader.PropertyToID("_CamForward");
        public static readonly int ProjScale = Shader.PropertyToID("_ProjScale");
        public static readonly int PixelWorld = Shader.PropertyToID("_PixelWorld");
        public static readonly int BrushShape = Shader.PropertyToID("_BrushShape");
        public static readonly int HardEdge = Shader.PropertyToID("_HardEdge");
        public static readonly int Hardness = Shader.PropertyToID("_Hardness");
        public static readonly int Occlusion = Shader.PropertyToID("_Occlusion");
        public static readonly int BackfaceCull = Shader.PropertyToID("_BackfaceCull");
        public static readonly int NormalFade = Shader.PropertyToID("_NormalFade");
        public static readonly int CullTriangles = Shader.PropertyToID("_CullTriangles");
        public static readonly int DepthMap = Shader.PropertyToID("_DepthMap");
        public static readonly int DabStrength = Shader.PropertyToID("_DabStrength");
        public static readonly int Seed = Shader.PropertyToID("_Seed");
        public static readonly int TargetMode = Shader.PropertyToID("_TargetMode");
        public static readonly int CaptureMaxMip = Shader.PropertyToID("_CaptureMaxMip");
        public static readonly int CaptureRect = Shader.PropertyToID("_CaptureRect");
        public static readonly int CaptureColor = Shader.PropertyToID("_CaptureColor");
        public static readonly int CaptureWeight = Shader.PropertyToID("_CaptureWeight");
        public static readonly int CaptureVP = Shader.PropertyToID("_CaptureVP");
        public static readonly int WeightByBrush = Shader.PropertyToID("_WeightByBrush");
    }

    internal static class RTUtil
    {
        public static bool LinearProject => QualitySettings.activeColorSpace == ColorSpace.Linear;

        public static RenderTexture Create(string name, int width, int height, RenderTextureFormat format,
            bool sRGB = false, bool mips = false, FilterMode filter = FilterMode.Bilinear, int depth = 0)
        {
            var desc = new RenderTextureDescriptor(width, height, format, depth)
            {
                sRGB = sRGB,
                useMipMap = mips,
                autoGenerateMips = false,
                msaaSamples = 1
            };
            var rt = new RenderTexture(desc)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            return rt;
        }

        public static RenderTexture CreateLayerTexture(int width, int height, string name = "MTP Layer")
            => Create(name, width, height, RenderTextureFormat.ARGB32);

        public static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.DestroyImmediate(rt);
            rt = null;
        }

        public static void Release(RenderTexture rt) => Release(ref rt);

        /// <summary>Fills a colour render texture with raw values (no colour space conversion).</summary>
        public static void Clear(RenderTexture rt, Color color)
        {
            var mat = PaintResources.Blit;
            mat.SetVector(Ids.FillColor, new Vector4(color.r, color.g, color.b, color.a));
            Blit(null, rt, mat, PaintResources.BlitFill);
        }

        public static void Blit(Texture source, RenderTexture dest, Material material, int pass)
        {
            var previous = RenderTexture.active;
            if (source != null) material.SetTexture(Ids.MainTex, source);
            Graphics.Blit(source, dest, material, pass);
            RenderTexture.active = previous;
        }

        public static RenderTexture Duplicate(RenderTexture source, string name)
        {
            var rt = Create(name, source.width, source.height, source.format, source.sRGB);
            Graphics.CopyTexture(source, rt);
            return rt;
        }

        // 0 unknown, 1 matches Texture2D convention, -1 reversed
        static int readbackRows;
        static int readbackRectOrigin;

        /// <summary>
        /// Texture2D.ReadPixels conventions differ per graphics API: the order of
        /// the rows it copies and where a partial rect's y offset is measured from
        /// are independent (on D3D11 rows arrive in order but offsets count from
        /// the top). Measure both once against a blitted reference.
        /// </summary>
        static void CalibrateReadback()
        {
            const int size = 4;
            var source = new Texture2D(size, size, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                source.SetPixel(x, y, y < size / 2 ? Color.white : Color.clear);
            source.Apply(false);
            var rt = Create("MTP Readback Calibration", size, size, RenderTextureFormat.ARGB32, filter: FilterMode.Point);
            Upload(source, rt);

            var full = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            var single = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            full.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
            single.ReadPixels(new Rect(1, 0, 1, 1), 0, 0, false);
            RenderTexture.active = previous;
            readbackRows = full.GetPixel(1, 0).r > 0.5f ? 1 : -1;
            readbackRectOrigin = single.GetPixel(0, 0).r > 0.5f ? 1 : -1;

            Object.DestroyImmediate(source);
            Object.DestroyImmediate(full);
            Object.DestroyImmediate(single);
            Release(ref rt);
        }

        static bool ReadbackFlipped
        {
            get
            {
                if (readbackRows == 0) CalibrateReadback();
                return readbackRows < 0;
            }
        }

        static bool ReadRectOriginFlipped
        {
            get
            {
                if (readbackRectOrigin == 0) CalibrateReadback();
                return readbackRectOrigin < 0;
            }
        }

        /// <summary>Reads an 8 bit render texture into a readable, linear (raw value) Texture2D, rows in texture order.</summary>
        public static Texture2D ReadToTexture(RenderTexture rt)
        {
            bool flipped = ReadbackFlipped;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            RenderTexture.active = previous;
            if (flipped)
            {
                var pixels = tex.GetPixels32();
                int w = rt.width, h = rt.height;
                var row = new Color32[w];
                for (int y = 0; y < h / 2; y++)
                {
                    int a = y * w, b = (h - 1 - y) * w;
                    Array.Copy(pixels, a, row, 0, w);
                    Array.Copy(pixels, b, pixels, a, w);
                    Array.Copy(row, 0, pixels, b, w);
                }
                tex.SetPixels32(pixels);
            }
            tex.Apply(false);
            return tex;
        }

        public static Color ReadPixel(RenderTexture rt, int x, int y)
        {
            x = Mathf.Clamp(x, 0, rt.width - 1);
            y = Mathf.Clamp(y, 0, rt.height - 1);
            if (ReadRectOriginFlipped) y = rt.height - 1 - y;
            bool eightBit = rt.format == RenderTextureFormat.ARGB32 || rt.format == RenderTextureFormat.R8;
            var tex = new Texture2D(1, 1, eightBit ? TextureFormat.RGBA32 : TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(x, y, 1, 1), 0, 0, false);
            RenderTexture.active = previous;
            var c = tex.GetPixel(0, 0);
            Object.DestroyImmediate(tex);
            return c;
        }

        /// <summary>Copies raw values of a linear Texture2D into a layer.</summary>
        public static void Upload(Texture2D tex, RenderTexture dest)
        {
            var mat = PaintResources.Blit;
            mat.SetFloat(Ids.ToGamma, 0f);
            Blit(tex, dest, mat, PaintResources.BlitImport);
        }

        public static int NextPow2(int v)
        {
            int p = 1;
            while (p < v) p <<= 1;
            return p;
        }

        public static long EstimateBytes(RenderTexture rt)
        {
            if (rt == null) return 0;
            int bpp;
            switch (rt.format)
            {
                case RenderTextureFormat.ARGBHalf: bpp = 8; break;
                case RenderTextureFormat.ARGBFloat: bpp = 16; break;
                case RenderTextureFormat.RHalf: bpp = 2; break;
                case RenderTextureFormat.RGHalf: bpp = 4; break;
                case RenderTextureFormat.R8: bpp = 1; break;
                default: bpp = 4; break;
            }
            return (long)rt.width * rt.height * bpp;
        }

        public static void SetTexSize(Material m, int width, int height)
            => m.SetVector(Ids.TexSize, new Vector4(width, height, 1f / width, 1f / height));
    }
}
