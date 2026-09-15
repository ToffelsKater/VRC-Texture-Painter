using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MeshTexturePainter.Tests
{
    public static class MTPTests
    {
        static int failures;
        const int Size = 256;

        static void Check(bool ok, string message)
        {
            Debug.Log("MTPTEST " + (ok ? "PASS " : "FAIL ") + message);
            if (!ok) failures++;
        }

        public static void Run()
        {
            try
            {
                Debug.Log("MTPTEST colorspace=" + QualitySettings.activeColorSpace + " device=" + SystemInfo.graphicsDeviceType);
                Primitives();
                OrientationAndPaint();
                SeamAndPadding();
                BlurAndBlend();
                Occlusion();
                CompositeModes();
                SessionAndFiles();
                SkinnedScaled();
                PreviewAndRestore();
                WrappedUVs();
                SecondUVChannel();
                MultiTexturePaint();
                BlendTransition();
                MaskPaint();
            }
            catch (Exception e)
            {
                Debug.LogError("MTPTEST EXCEPTION " + e);
                failures++;
            }
            Debug.Log("MTPTEST DONE failures=" + failures);
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        // ------------------------------------------------------------------ helpers

        static Camera MakeCamera(bool ortho)
        {
            var go = new GameObject("TestCam");
            var cam = go.AddComponent<Camera>();
            cam.orthographic = ortho;
            cam.orthographicSize = 1.25f;
            cam.fieldOfView = 40f;
            cam.transform.position = new Vector3(0, 0, -5);
            cam.transform.rotation = Quaternion.identity;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.targetTexture = new RenderTexture(512, 512, 24);
            cam.aspect = 1f;
            return cam;
        }

        /// <summary>Quads: each entry = world rect (xMin, yMin, xMax, yMax), z, uv rect.</summary>
        static MeshRenderer MakeQuads(params (Rect world, float z, Rect uv)[] quads)
        {
            var verts = new System.Collections.Generic.List<Vector3>();
            var uvs = new System.Collections.Generic.List<Vector2>();
            var normals = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();
            foreach (var q in quads)
            {
                int b = verts.Count;
                verts.Add(new Vector3(q.world.xMin, q.world.yMin, q.z)); uvs.Add(new Vector2(q.uv.xMin, q.uv.yMin));
                verts.Add(new Vector3(q.world.xMax, q.world.yMin, q.z)); uvs.Add(new Vector2(q.uv.xMax, q.uv.yMin));
                verts.Add(new Vector3(q.world.xMax, q.world.yMax, q.z)); uvs.Add(new Vector2(q.uv.xMax, q.uv.yMax));
                verts.Add(new Vector3(q.world.xMin, q.world.yMax, q.z)); uvs.Add(new Vector2(q.uv.xMin, q.uv.yMax));
                for (int i = 0; i < 4; i++) normals.Add(new Vector3(0, 0, -1));
                tris.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }
            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetNormals(normals);
            mesh.SetTriangles(tris, 0);
            var go = new GameObject("TestQuads");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(Shader.Find("Standard"));
            return mr;
        }

        static Rect R(float xMin, float yMin, float xMax, float yMax) => Rect.MinMaxRect(xMin, yMin, xMax, yMax);

        static DabInput Dab(Camera cam, PaintTarget target, Vector3 world, float radius, float strength = 1f)
        {
            var sp = cam.WorldToScreenPoint(world);
            var d = new DabInput { screenPixel = new Vector2(sp.x, sp.y), radius = radius, strength = strength };
            if (target.Raycast(new Ray(cam.transform.position, (world - cam.transform.position).normalized), out var hit))
            {
                d.hasHit = true;
                d.hitPoint = hit.point;
                d.hitNormal = hit.normal;
            }
            return d;
        }

        static Color Pixel(RenderTexture rt, float u, float v)
        {
            return RTUtil.ReadPixel(rt, Mathf.FloorToInt(u * rt.width), Mathf.FloorToInt(v * rt.height));
        }

        static string Str(Color c) => $"({c.r:F3},{c.g:F3},{c.b:F3},{c.a:F3})";

        /// <summary>Minimal writer for a solid colour 16 bit RGB PNG.</summary>
        static byte[] Png16(int width, int height, float r, float g, float b)
        {
            ushort[] rgb = { (ushort)Mathf.RoundToInt(r * 65535f), (ushort)Mathf.RoundToInt(g * 65535f), (ushort)Mathf.RoundToInt(b * 65535f) };
            var raw = new System.IO.MemoryStream();
            for (int y = 0; y < height; y++)
            {
                raw.WriteByte(0); // filter: none
                for (int x = 0; x < width; x++)
                    foreach (ushort v in rgb)
                    {
                        raw.WriteByte((byte)(v >> 8));
                        raw.WriteByte((byte)v);
                    }
            }
            byte[] scanlines = raw.ToArray();

            var zlib = new System.IO.MemoryStream();
            zlib.WriteByte(0x78);
            zlib.WriteByte(0x01);
            using (var deflate = new System.IO.Compression.DeflateStream(zlib, System.IO.Compression.CompressionLevel.Optimal, true))
                deflate.Write(scanlines, 0, scanlines.Length);
            uint s1 = 1, s2 = 0;
            foreach (byte bt in scanlines)
            {
                s1 = (s1 + bt) % 65521;
                s2 = (s2 + s1) % 65521;
            }
            WriteBE(zlib, (s2 << 16) | s1);

            var ihdr = new System.IO.MemoryStream();
            WriteBE(ihdr, (uint)width);
            WriteBE(ihdr, (uint)height);
            ihdr.WriteByte(16); // bit depth
            ihdr.WriteByte(2);  // colour type: RGB
            ihdr.WriteByte(0);
            ihdr.WriteByte(0);
            ihdr.WriteByte(0);

            var png = new System.IO.MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            WriteChunk(png, "IHDR", ihdr.ToArray());
            WriteChunk(png, "IDAT", zlib.ToArray());
            WriteChunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }

        static void WriteBE(System.IO.Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        static void WriteChunk(System.IO.Stream s, string type, byte[] data)
        {
            WriteBE(s, (uint)data.Length);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = 0xFFFFFFFF;
            void Add(byte bt)
            {
                crc ^= bt;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            foreach (byte bt in typeBytes) Add(bt);
            foreach (byte bt in data) Add(bt);
            WriteBE(s, crc ^ 0xFFFFFFFF);
        }

        static void Cleanup(params UnityEngine.Object[] objects)
        {
            foreach (var o in objects)
            {
                if (o is Component c) UnityEngine.Object.DestroyImmediate(c.gameObject);
                else if (o != null) UnityEngine.Object.DestroyImmediate(o);
            }
        }

        // ------------------------------------------------------------------ tests

        static void Primitives()
        {
            var rt = RTUtil.CreateLayerTexture(4, 4);
            RTUtil.Clear(rt, new Color(0.25f, 0.5f, 0.75f, 1f));
            var c = RTUtil.ReadPixel(rt, 1, 1);
            Check(Mathf.Abs(c.r - 0.25f) < 0.01f && Mathf.Abs(c.g - 0.5f) < 0.01f && Mathf.Abs(c.b - 0.75f) < 0.01f && c.a > 0.99f,
                "clear writes raw values " + Str(c));

            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                tex.SetPixel(x, y, y < 2 ? Color.red : Color.blue);
            tex.Apply();
            RTUtil.Upload(tex, rt);
            var bottom = RTUtil.ReadPixel(rt, 0, 0);
            Check(bottom.r > 0.9f && bottom.b < 0.1f, "readback row 0 matches texture row 0 " + Str(bottom));
            Debug.Log("MTPTEST uvflip=" + PaintResources.UVSpace.GetFloat("_UVFlip"));

            // 16 bit RGB PNG sources (like many avatar body and face textures) must load with raw values.
            // Unity's EncodeToPNG writes 8 bit files, so this one is written by hand.
            string png16 = System.IO.Path.GetFullPath("Temp/mtp_16bit.png");
            System.IO.File.WriteAllBytes(png16, Png16(4, 4, 0.5f, 0.25f, 0.75f));
            var header = System.IO.File.ReadAllBytes(png16);
            var loaded16 = PaintProjectIO.LoadImageFile(png16);
            Check(loaded16 != null, "16 bit PNG loads");
            if (loaded16 != null)
            {
                var doc16 = new PaintDocument(4, 4);
                var rt16 = doc16.RenderImported(loaded16, false);
                var c16 = RTUtil.ReadPixel(rt16, 1, 1);
                Check(header[24] == 16 && Mathf.Abs(c16.r - 0.5f) < 0.01f && Mathf.Abs(c16.g - 0.25f) < 0.01f && Mathf.Abs(c16.b - 0.75f) < 0.01f && c16.a > 0.99f,
                    $"16 bit RGB PNG keeps raw values bitdepth={header[24]} format={loaded16.format} value={Str(c16)}");
                RTUtil.Release(ref rt16);
                doc16.Dispose();
                UnityEngine.Object.DestroyImmediate(loaded16);
            }
            UnityEngine.Object.DestroyImmediate(tex);
            RTUtil.Release(ref rt);
        }

        static void OrientationAndPaint()
        {
            var cam = MakeCamera(true);
            var mr = MakeQuads((R(-1, -1, 1, 1), 0f, R(0, 0, 1, 1)));
            var target = new PaintTarget(mr, new[] { 0 }, 0, "_MainTex");
            Check(target.Bake(), "bake quad " + target.Error);
            target.BuildPadMap(Size, Size, 8);

            var doc = new PaintDocument(Size, Size);
            var baseLayer = doc.CreateLayer("Base", Color.white);
            var paint = doc.CreateLayer("Paint", Color.clear);
            doc.Layers.Add(baseLayer);
            doc.Layers.Add(paint);
            doc.ActiveIndex = 1;

            var engine = new BrushEngine();
            var s = new BrushSettings { occlusion = true, mirror = false };
            engine.PrepareCamera(cam, target);

            doc.BeginStroke(StrokeKind.Paint, Color.red, 1f);
            engine.StrokeDab(doc, target, cam, s, PaintTool.HardBrush, Dab(cam, target, new Vector3(0.5f, 0.5f, 0), 20f));

            // preview composite includes the pending stroke
            var comp = doc.GetComposite();
            var pc = Pixel(comp, 0.75f, 0.75f);
            Check(pc.r > 0.95f && pc.g < 0.05f, "composite shows pending stroke at uv(0.75,0.75) " + Str(pc));

            var before = doc.CommitStroke();
            var hit = Pixel(paint.texture, 0.75f, 0.75f);
            var missY = Pixel(paint.texture, 0.75f, 0.25f);
            var missX = Pixel(paint.texture, 0.25f, 0.75f);
            Check(hit.r > 0.95f && hit.a > 0.95f, "hard dab lands at uv(0.75,0.75) " + Str(hit));
            Check(missY.a < 0.01f, "no paint at uv(0.75,0.25) (v not flipped) " + Str(missY));
            Check(missX.a < 0.01f, "no paint at uv(0.25,0.75) (u not flipped) " + Str(missX));

            // edge of a 20px dab: 20px = 0.0977 world = 0.0488 uv = 12.5 texels
            var inside = Pixel(paint.texture, 0.75f + 10f / Size, 0.75f);
            var outside = Pixel(paint.texture, 0.75f + 15f / Size, 0.75f);
            Check(inside.a > 0.9f && outside.a < 0.1f, $"dab radius matches screen radius inside={Str(inside)} outside={Str(outside)}");

            // ray cast uv
            Check(target.Raycast(new Ray(new Vector3(0.5f, -0.5f, -5), Vector3.forward), out var rh) && Vector2.Distance(rh.uv, new Vector2(0.75f, 0.25f)) < 1e-3f,
                "raycast uv " + rh.uv);

            // undo swap
            var step = new PixelStep("Paint", paint, before);
            step.Undo();
            Check(Pixel(paint.texture, 0.75f, 0.75f).a < 0.01f, "undo restores layer");
            step.Redo();
            Check(Pixel(paint.texture, 0.75f, 0.75f).r > 0.95f, "redo re-applies layer");
            step.Dispose();

            // soft brush strength cap: two passes at opacity 0.5 never exceed 0.5
            doc.BeginStroke(StrokeKind.Paint, Color.blue, 0.5f);
            var d = Dab(cam, target, new Vector3(-0.5f, -0.5f, 0), 30f);
            engine.StrokeDab(doc, target, cam, s, PaintTool.SoftBrush, d);
            engine.StrokeDab(doc, target, cam, s, PaintTool.SoftBrush, d);
            RTUtil.Release(doc.CommitStroke());
            var soft = Pixel(paint.texture, 0.25f, 0.25f);
            Check(soft.a > 0.4f && soft.a < 0.6f, "soft stroke respects opacity " + Str(soft));
            var softEdge = Pixel(paint.texture, 0.25f + 14f / Size, 0.25f);
            Check(softEdge.a > 0.01f && softEdge.a < soft.a, "soft brush falls off " + Str(softEdge));

            // eraser
            doc.BeginStroke(StrokeKind.Erase, Color.white, 1f);
            engine.StrokeDab(doc, target, cam, s, PaintTool.Eraser, Dab(cam, target, new Vector3(0.5f, 0.5f, 0), 20f));
            RTUtil.Release(doc.CommitStroke());
            Check(Pixel(paint.texture, 0.75f, 0.75f).a < 0.05f, "eraser clears alpha");

            // mirror X across the root: paint at +x lands at -x
            s.mirror = true;
            doc.BeginStroke(StrokeKind.Paint, Color.green, 1f);
            engine.StrokeDab(doc, target, cam, s, PaintTool.HardBrush, Dab(cam, target, new Vector3(0.5f, 0.2f, 0), 10f));
            RTUtil.Release(doc.CommitStroke());
            Check(Pixel(paint.texture, 0.25f, 0.6f).g > 0.9f && Pixel(paint.texture, 0.75f, 0.6f).g > 0.9f, "mirror paints both sides");
            s.mirror = false;

            // sphere falloff shape
            s.falloffShape = FalloffShape.Sphere;
            doc.BeginStroke(StrokeKind.Paint, Color.black, 1f);
            engine.StrokeDab(doc, target, cam, s, PaintTool.HardBrush, Dab(cam, target, new Vector3(0.5f, -0.6f, 0), 10f));
            RTUtil.Release(doc.CommitStroke());
            var sph = Pixel(paint.texture, 0.75f, 0.2f);
            Check(sph.a > 0.9f && sph.r < 0.05f, "sphere brush paints " + Str(sph));

            engine.Dispose();
            doc.Dispose();
            target.Dispose();
            Cleanup(mr, cam);
        }

        static void SeamAndPadding()
        {
            var cam = MakeCamera(true);
            // two quads touching at world x=0, mapped to separate islands
            var mr = MakeQuads(
                (R(-1, -1, 0, 1), 0f, R(0.05f, 0.05f, 0.45f, 0.95f)),
                (R(0, -1, 1, 1), 0f, R(0.55f, 0.05f, 0.95f, 0.95f)));
            var target = new PaintTarget(mr, new[] { 0 }, 0, "_MainTex");
            Check(target.Bake(), "bake split quads");
            target.BuildPadMap(Size, Size, 8);

            var doc = new PaintDocument(Size, Size);
            var layer = doc.CreateLayer("Base", Color.white);
            doc.Layers.Add(layer);
            var engine = new BrushEngine();
            var s = new BrushSettings();
            engine.PrepareCamera(cam, target);

            doc.BeginStroke(StrokeKind.Paint, Color.red, 1f);
            engine.StrokeDab(doc, target, cam, s, PaintTool.HardBrush, Dab(cam, target, new Vector3(0f, 0f, 0), 30f));
            RTUtil.Release(doc.CommitStroke());

            var left = Pixel(layer.texture, 0.445f, 0.5f);
            var right = Pixel(layer.texture, 0.555f, 0.5f);
            Check(left.g < 0.05f && right.g < 0.05f, $"stroke across seam paints both islands left={Str(left)} right={Str(right)}");
            var gutterRaw = Pixel(layer.texture, 0.47f, 0.5f);
            Check(gutterRaw.g > 0.95f, "gutter untouched in layer " + Str(gutterRaw));

            var present = RTUtil.Create("present", Size, Size, RenderTextureFormat.ARGB32);
            doc.Present(target.PadMap, target.WrapVector, present);
            var gutter = Pixel(present, 0.47f, 0.5f);
            var far = Pixel(present, 0.5f, 0.5f);
            Check(gutter.g < 0.05f, "seam padding extends paint into gutter " + Str(gutter));
            Check(far.g > 0.95f, "padding limited to distance " + Str(far));

            RTUtil.Release(ref present);
            engine.Dispose();
            doc.Dispose();
            target.Dispose();
            Cleanup(mr, cam);
        }

        static void BlurAndBlend()
        {
            var cam = MakeCamera(true);
            var mr = MakeQuads((R(-1, -1, 1, 1), 0f, R(0, 0, 1, 1)));
            var target = new PaintTarget(mr, new[] { 0 }, 0, "_MainTex");
            target.Bake();
            target.BuildPadMap(Size, Size, 8);

            var split = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true);
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                px[y * Size + x] = x < Size / 2 ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);
            split.SetPixels32(px);
            split.Apply();

            var engine = new BrushEngine();
            var s = new BrushSettings { blurSize = 0.5f };
            engine.PrepareCamera(cam, target);

            foreach (var tool in new[] { PaintTool.Blur, PaintTool.ColorBlend })
            {
                var doc = new PaintDocument(Size, Size);
                var layer = new PaintLayer("Base", doc.RenderImported(split, false));
                doc.Layers.Add(layer);
                var edge0 = Pixel(layer.texture, 0.5f - 2f / Size, 0.5f);
                Check(edge0.r < 0.01f, $"{tool}: import raw values " + Str(edge0));

                s.For(tool).strength = 1f;
                s.For(tool).hardness = 0.8f;
                s.blendMode = ColorBlendMode.Flatten; // the transition mode has its own test
                for (int i = 0; i < 4; i++)
                    engine.FilterDab(doc, target, cam, s, tool, Dab(cam, target, new Vector3(0, 0, 0), 40f));

                var edge = Pixel(layer.texture, 0.5f - 2f / Size, 0.5f);
                var farLeft = Pixel(layer.texture, 0.1f, 0.5f);
                var alpha = Pixel(layer.texture, 0.5f, 0.5f).a;
                Check(edge.r > 0.15f && edge.r < 0.85f, $"{tool}: softens the edge " + Str(edge));
                Check(farLeft.r < 0.01f, $"{tool}: leaves far texels alone " + Str(farLeft));
                Check(Mathf.Abs(alpha - 1f) < 0.01f, $"{tool}: alpha untouched a={alpha:F3}");
                Check(!float.IsNaN(edge.r), $"{tool}: no NaN");
                if (tool == PaintTool.ColorBlend)
                {
                    var l = Pixel(layer.texture, 0.5f - 6f / Size, 0.5f);
                    var r = Pixel(layer.texture, 0.5f + 6f / Size, 0.5f);
                    Check(Mathf.Abs(l.r - r.r) < 0.2f, $"ColorBlend: both sides move to one average l={Str(l)} r={Str(r)}");
                }
                doc.Dispose();
            }

            UnityEngine.Object.DestroyImmediate(split);
            engine.Dispose();
            target.Dispose();
            Cleanup(mr, cam);
        }

        static void Occlusion()
        {
            var cam = MakeCamera(false);
            var mr = MakeQuads(
                (R(-1, -1, 1, 1), 0f, R(0.05f, 0.05f, 0.45f, 0.95f)),
                (R(-1, -1, 1, 1), 1f, R(0.55f, 0.05f, 0.95f, 0.95f)));
            var target = new PaintTarget(mr, new[] { 0 }, 0, "_MainTex");
            target.Bake();
            target.BuildPadMap(Size, Size, 8);
            var engine = new BrushEngine();
            engine.PrepareCamera(cam, target);

            foreach (bool occlusion in new[] { true, false })
            {
                var doc = new PaintDocument(Size, Size);
                var layer = doc.CreateLayer("Base", Color.white);
                doc.Layers.Add(layer);
                var s = new BrushSettings { occlusion = occlusion };
                doc.BeginStroke(StrokeKind.Paint, Color.red, 1f);
                engine.StrokeDab(doc, target, cam, s, PaintTool.HardBrush, Dab(cam, target, Vector3.zero, 25f));
                RTUtil.Release(doc.CommitStroke());
                var front = Pixel(layer.texture, 0.25f, 0.5f);
                var back = Pixel(layer.texture, 0.75f, 0.5f);
                Check(front.g < 0.05f, $"occlusion={occlusion}: front painted " + Str(front));
                if (occlusion) Check(back.g > 0.95f, "occlusion=true: hidden surface untouched " + Str(back));
                else Check(back.g < 0.05f, "occlusion=false: hidden surface painted " + Str(back));
                doc.Dispose();
            }
            engine.Dispose();
            target.Dispose();
            Cleanup(mr, cam);
        }

        static void SessionAndFiles()
        {
            const int n = 64;
            // source texture asset: left half red, right half blue, bottom rows white
            var src = new Texture2D(n, n, TextureFormat.RGBA32, false, true);
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                src.SetPixel(x, y, y < 8 ? Color.white : (x < n / 2 ? Color.red : Color.blue));
            src.Apply();
            System.IO.File.WriteAllBytes("Assets/MTPTestTex.png", src.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(src);
            AssetDatabase.ImportAsset("Assets/MTPTestTex.png", ImportAssetOptions.ForceUpdate);
            var srcAsset = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/MTPTestTex.png");

            var mat = new Material(Shader.Find("Standard")) { mainTexture = srcAsset };
            AssetDatabase.CreateAsset(mat, "Assets/MTPTestMat.mat");

            var mr = MakeQuads((R(-1, -1, 1, 1), 0f, R(0, 0, 1, 1)));
            mr.sharedMaterial = mat;

            var session = PaintSession.Create(mr, new[] { 0 }, 0, "_MainTex", n, n, 4, new BrushSettings(), out string error);
            Check(session != null, "session created " + error);
            if (session == null) return;
            var doc = session.Document;
            Check(doc.Layers.Count == 2 && doc.ActiveIndex == 1, "session has base + empty layer");
            var baseRed = Pixel(doc.Layers[0].texture, 0.25f, 0.5f);
            var baseWhite = Pixel(doc.Layers[0].texture, 0.75f, 2f / n);
            Check(baseRed.r > 0.99f && baseRed.g < 0.01f, "base layer loaded from PNG at full precision " + Str(baseRed));
            Check(baseWhite.g > 0.99f, "base layer orientation (bottom rows white) " + Str(baseWhite));

            session.FillLayer(new Color(0f, 1f, 0f, 0.5f));
            var comp = Pixel(doc.GetComposite(), 0.25f, 0.5f);
            Check(Mathf.Abs(comp.r - 0.5f) < 0.02f && Mathf.Abs(comp.g - 0.5f) < 0.02f, "fill layer composites " + Str(comp));

            // colour picking (Q) reads the composite at the hit UV
            if (session.Target.Raycast(new Ray(new Vector3(-0.5f, 0f, -5f), Vector3.forward), out var pickHit) && session.PickColor(pickHit))
            {
                var picked = session.Settings.color;
                Check(Mathf.Abs(picked.r - 0.5f) < 0.02f && Mathf.Abs(picked.g - 0.5f) < 0.02f && picked.b < 0.02f, "pick colour from the composite " + Str(picked));
            }
            else
            {
                Check(false, "pick colour ray hit");
            }

            // hotkeys: C / numpad 0 pick colour, 3-7 / numpad 1-5 select brushes, Unity's own keys stay free
            session.SetCursor(pickHit);
            foreach (var key in new[] { KeyCode.C, KeyCode.Keypad0 })
            {
                session.Settings.color = Color.black;
                Check(session.HandleKeyEvent(new Event { type = EventType.KeyDown, keyCode = key }) && session.Settings.color.r > 0.4f, $"{key} picks colour");
            }
            var brushKeys = new[]
            {
                (KeyCode.Alpha3, KeyCode.Keypad1, PaintTool.HardBrush),
                (KeyCode.Alpha4, KeyCode.Keypad2, PaintTool.SoftBrush),
                (KeyCode.Alpha5, KeyCode.Keypad3, PaintTool.Blur),
                (KeyCode.Alpha6, KeyCode.Keypad4, PaintTool.ColorBlend),
                (KeyCode.Alpha7, KeyCode.Keypad5, PaintTool.Eraser)
            };
            foreach (var (main, pad, brush) in brushKeys)
            {
                var other = brush == PaintTool.Blur ? PaintTool.HardBrush : PaintTool.Blur;
                session.Settings.tool = other;
                bool mainOk = session.HandleKeyEvent(new Event { type = EventType.KeyDown, keyCode = main }) && session.Settings.tool == brush;
                session.Settings.tool = other;
                bool padOk = session.HandleKeyEvent(new Event { type = EventType.KeyDown, keyCode = pad }) && session.Settings.tool == brush;
                Check(mainOk && padOk, $"{main} / {pad} select {brush}");
            }
            foreach (var key in new[] { KeyCode.Q, KeyCode.W, KeyCode.E, KeyCode.A, KeyCode.S, KeyCode.D, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.KeypadPeriod })
                Check(!session.HandleKeyEvent(new Event { type = EventType.KeyDown, keyCode = key }), $"{key} is left to Unity");

            // painting keeps Unity's tools off so Q (View tool) cannot take over the left mouse button
            var toolBefore = Tools.current;
            session.Activate();
            Tools.current = Tool.View;
            session.EnforceNoTool();
            Check(Tools.current == Tool.None, "View tool is switched off while painting");
            session.Deactivate();
            Check(toolBefore == Tool.None || Tools.current == toolBefore, $"tool restored after painting {toolBefore} -> {Tools.current}");

            // layer stack operations with undo through Unity's undo system
            session.AddLayer();
            Check(doc.Layers.Count == 3, "add layer");
            session.History.PerformUndo();
            Check(doc.Layers.Count == 2, "unity undo removes added layer (count " + doc.Layers.Count + ")");
            session.History.PerformRedo();
            Check(doc.Layers.Count == 3, "unity redo re-adds layer (count " + doc.Layers.Count + ")");
            session.DeleteLayer();
            session.SetActiveLayer(1);
            session.History.PerformUndo();
            session.History.PerformUndo();
            session.History.PerformUndo();
            Check(Pixel(doc.Layers[1].texture, 0.25f, 0.5f).a < 0.01f, "undo fill restores empty layer");
            session.History.PerformRedo();
            session.History.PerformRedo();
            session.History.PerformRedo();
            Check(Pixel(doc.Layers[1].texture, 0.25f, 0.5f).a > 0.45f, "redo fill");
            Check(doc.Layers.Count == 2, "stack after redo (count " + doc.Layers.Count + ")");

            // save / load both encodings
            foreach (bool fast in new[] { false, true })
            {
                string path = System.IO.Path.GetFullPath($"Temp/mtp_test_{fast}.mtpaint");
                session.SaveProject(path, fast);
                var reopened = PaintSession.Open(path, mr, new BrushSettings(), out string openError);
                Check(reopened != null, $"reopen project fast={fast} {openError}");
                if (reopened == null) continue;
                var l1 = Pixel(reopened.Document.Layers[1].texture, 0.25f, 0.5f);
                var l0 = Pixel(reopened.Document.Layers[0].texture, 0.75f, 2f / n);
                Check(reopened.Document.Layers.Count == 2 && Mathf.Abs(l1.a - 0.5f) < 0.02f && l1.g > 0.99f, $"round trip layer pixels fast={fast} " + Str(l1));
                Check(l0.b > 0.99f && l0.r > 0.99f, $"round trip orientation fast={fast} " + Str(l0));
                reopened.Dispose();
            }

            // export + assign
            var exported = session.ExportTexture("Assets/MTPTestExport.png", true);
            Check(exported != null && mat.mainTexture == exported, "export assigns texture to material");
            var file = PaintProjectIO.LoadImageFile(System.IO.Path.GetFullPath("Assets/MTPTestExport.png"));
            var eBottom = file.GetPixel(48, 2);
            var eTop = file.GetPixel(48, 60);
            Check(Mathf.Abs(eBottom.r - 0.5f) < 0.02f && eBottom.g > 0.98f, "export bottom rows (white + green) " + Str(eBottom));
            Check(eTop.r < 0.02f && Mathf.Abs(eTop.b - 0.5f) < 0.02f, "export top rows (blue + green) " + Str(eTop));
            var importer = (TextureImporter)AssetImporter.GetAtPath("Assets/MTPTestExport.png");
            Check(importer.sRGBTexture, "export keeps sRGB import setting");
            UnityEngine.Object.DestroyImmediate(file);

            // TGA: 2x2 uncompressed 32 bit, bottom-left origin
            var tga = new byte[18 + 16];
            tga[2] = 2; tga[12] = 2; tga[14] = 2; tga[16] = 32; tga[17] = 8;
            byte[][] bgra = { new byte[] { 0, 0, 255, 255 }, new byte[] { 0, 255, 0, 255 }, new byte[] { 255, 0, 0, 255 }, new byte[] { 255, 255, 255, 128 } };
            for (int i = 0; i < 4; i++) Array.Copy(bgra[i], 0, tga, 18 + i * 4, 4);
            var t = TgaLoader.Load(tga);
            Check(t != null && t.GetPixel(0, 0).r > 0.99f && t.GetPixel(1, 0).g > 0.99f && t.GetPixel(0, 1).b > 0.99f && Mathf.Abs(t.GetPixel(1, 1).a - 0.5f) < 0.01f, "tga loader");

            session.Dispose();
            UnityEngine.Object.DestroyImmediate(mr.gameObject);
            AssetDatabase.DeleteAsset("Assets/MTPTestExport.png");
            AssetDatabase.DeleteAsset("Assets/MTPTestMat.mat");
            AssetDatabase.DeleteAsset("Assets/MTPTestTex.png");
        }

        /// <summary>Skinned mesh on a scaled object with a posed bone: the baked paint mesh must sit where Unity renders it.</summary>
        static void SkinnedScaled()
        {
            var quad = MakeQuads((R(-1, -1, 1, 1), 0f, R(0, 0, 1, 1)));
            var mesh = UnityEngine.Object.Instantiate(quad.GetComponent<MeshFilter>().sharedMesh);
            UnityEngine.Object.DestroyImmediate(quad.gameObject);

            var root = new GameObject("SkinnedRoot");
            root.transform.localScale = Vector3.one * 2f;
            var bone = new GameObject("Bone").transform;
            bone.SetParent(root.transform, false);

            var weights = new BoneWeight[mesh.vertexCount];
            for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            mesh.boneWeights = weights;
            mesh.bindposes = new[] { bone.worldToLocalMatrix * root.transform.localToWorldMatrix };

            var smr = root.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = new[] { bone };
            smr.rootBone = bone;
            smr.sharedMaterial = new Material(Shader.Find("Standard"));

            // pose: move the bone up by 0.5 world units
            bone.position += new Vector3(0f, 0.5f, 0f);

            var target = new PaintTarget(smr, new[] { 0 }, 0, "_MainTex");
            Check(target.Bake(), "bake skinned " + target.Error);
            // world quad spans x [-2,2], y [-1.5,2.5]; point (1, 1.5) -> uv (0.75, 0.75)
            bool hit = target.Raycast(new Ray(new Vector3(1f, 1.5f, -5f), Vector3.forward), out var h);
            Check(hit && Vector2.Distance(h.uv, new Vector2(0.75f, 0.75f)) < 0.01f && Mathf.Abs(h.point.z) < 1e-3f,
                $"skinned bake matches pose and scale hit={hit} uv={h.uv} point={h.point}");

            bone.position += new Vector3(0f, 0.5f, 0f);
            Check(target.PoseChanged(), "pose change detected");
            target.Bake();
            hit = target.Raycast(new Ray(new Vector3(1f, 2f, -5f), Vector3.forward), out h);
            Check(hit && Vector2.Distance(h.uv, new Vector2(0.75f, 0.75f)) < 0.01f, "rebake follows new pose uv=" + h.uv);

            target.Dispose();
            UnityEngine.Object.DestroyImmediate(root);
        }

        static void PreviewAndRestore()
        {
            Check(EditorGUIUtility.FindTexture("d_Grid.PaintTool") != null, "window icon exists");

            var mr = MakeQuads((R(-1, -1, 1, 1), 0f, R(0, 0, 1, 1)));
            var mat = mr.sharedMaterial;
            var session = PaintSession.Create(mr, new[] { 0 }, 0, "_MainTex", 32, 32, 2, new BrushSettings(), out string error);
            Check(session != null, "session without source texture " + error);
            if (session == null) return;

            session.RefreshPreview();
            var block = new MaterialPropertyBlock();
            mr.GetPropertyBlock(block, 0);
            var shown = block.GetTexture("_MainTex") as RenderTexture;
            Check(shown != null && shown.sRGB == RTUtil.LinearProject, "preview shown through property block (sRGB in linear projects)");
            Check(mat.mainTexture == null, "material asset untouched while painting");
            Check(PaintSession.ResolveRenderer(session.Meta) == mr, "project finds its renderer again");

            session.Dispose();
            block = new MaterialPropertyBlock();
            mr.GetPropertyBlock(block, 0);
            Check(block.GetTexture("_MainTex") == null, "preview removed when the session ends");
            UnityEngine.Object.DestroyImmediate(mr.gameObject);
        }

        /// <summary>UVs outside 0..1 are painted where the texture's wrap mode samples them.</summary>
        static void WrappedUVs()
        {
            var cam = MakeCamera(true);
            var engine = new BrushEngine();
            var s = new BrushSettings();

            // Repeat: quad A lives entirely in tile 1 (u 1.3..1.6 -> 0.3..0.6),
            // quad B straddles u = 1 (u 0.8..1.2 -> 0.8..1.0 and 0.0..0.2)
            var mr = MakeQuads(
                (R(-1, -1, -0.1f, 1), 0f, R(1.3f, 0.1f, 1.6f, 0.9f)),
                (R(0.1f, -1, 1, 1), 0f, R(0.8f, 0.1f, 1.2f, 0.9f)));
            var target = new PaintTarget(mr, new[] { 0 }, 0, "_MainTex", TextureWrapMode.Repeat, TextureWrapMode.Repeat);
            Check(target.Bake(), "bake quads with UVs outside 0..1 " + target.Error);
            target.BuildPadMap(Size, Size, 8);
            engine.PrepareCamera(cam, target);

            var doc = new PaintDocument(Size, Size);
            var layer = doc.CreateLayer("Base", Color.white);
            doc.Layers.Add(layer);
            doc.BeginStroke(StrokeKind.Paint, Color.red, 1f);
            engine.StrokeDab(doc, target, cam, s, PaintTool.HardBrush, Dab(cam, target, new Vector3(-0.55f, 0f, 0f), 20f)); // A centre, u 1.45
            engine.StrokeDab(doc, target, cam, s, PaintTool.HardBrush, Dab(cam, target, new Vector3(0.55f, 0f, 0f), 20f));  // B at u = 1.0
            RTUtil.Release(doc.CommitStroke());

            // A: 2 triangles in one tile, B: 2 triangles crossing u = 1 -> 2 copies each
            Check(target.PaintMesh.vertexCount == 18, "tile copies: " + target.PaintMesh.vertexCount + " paint vertices");

            var tile1 = Pixel(layer.texture, 0.45f, 0.5f);
            Check(tile1.g < 0.05f, "repeat: island in tile 1 is painted " + Str(tile1));
            var edgeRight = Pixel(layer.texture, 0.985f, 0.5f);
            var edgeLeft = Pixel(layer.texture, 0.015f, 0.5f);
            Check(edgeRight.g < 0.05f && edgeLeft.g < 0.05f, $"repeat: stroke over u = 1 paints both sides right={Str(edgeRight)} left={Str(edgeLeft)}");
            var awayRight = Pixel(layer.texture, 0.9f, 0.5f);
            var awayLeft = Pixel(layer.texture, 0.1f, 0.5f);
            Check(awayRight.g > 0.95f && awayLeft.g > 0.95f, "repeat: paint stays under the brush");

            var wrapped = target.WrapUV(new Vector2(1.25f, -0.25f));
            Check(Vector2.Distance(wrapped, new Vector2(0.25f, 0.75f)) < 1e-4f, "WrapUV repeat " + wrapped);

            // Blur across the u = 1 edge: texture black on the left half, white on the right
            var split = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true);
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                px[y * Size + x] = x < Size / 2 ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);
            split.SetPixels32(px);
            split.Apply();
            var blurDoc = new PaintDocument(Size, Size);
            var blurLayer = new PaintLayer("Base", blurDoc.RenderImported(split, false));
            blurDoc.Layers.Add(blurLayer);
            s.blur.strength = 1f;
            s.blur.hardness = 0.8f;
            s.blurSize = 0.5f;
            for (int i = 0; i < 4; i++)
                engine.FilterDab(blurDoc, target, cam, s, PaintTool.Blur, Dab(cam, target, new Vector3(0.55f, 0f, 0f), 40f));
            var blurRight = Pixel(blurLayer.texture, 1f - 2f / Size, 0.5f);
            var blurLeft = Pixel(blurLayer.texture, 2f / Size, 0.5f);
            Check(blurRight.r < 0.9f && blurLeft.r > 0.1f, $"repeat: blur crosses the texture edge right={Str(blurRight)} left={Str(blurLeft)}");
            blurDoc.Dispose();
            UnityEngine.Object.DestroyImmediate(split);
            doc.Dispose();
            target.Dispose();
            UnityEngine.Object.DestroyImmediate(mr.gameObject);

            // Seam padding wraps around the texture edge only for Repeat
            foreach (var mode in new[] { TextureWrapMode.Repeat, TextureWrapMode.Clamp })
            {
                var edgeQuad = MakeQuads((R(-1, -1, 1, 1), 0f, R(0f, 0.2f, 0.3f, 0.8f)));
                var edgeTarget = new PaintTarget(edgeQuad, new[] { 0 }, 0, "_MainTex", mode, mode);
                edgeTarget.Bake();
                edgeTarget.BuildPadMap(Size, Size, 8);
                engine.PrepareCamera(cam, edgeTarget);
                var edgeDoc = new PaintDocument(Size, Size);
                var edgeLayer = edgeDoc.CreateLayer("Base", Color.white);
                edgeDoc.Layers.Add(edgeLayer);
                edgeDoc.BeginStroke(StrokeKind.Paint, Color.red, 1f);
                engine.StrokeDab(edgeDoc, edgeTarget, cam, s, PaintTool.HardBrush, Dab(cam, edgeTarget, Vector3.zero, 400f));
                RTUtil.Release(edgeDoc.CommitStroke());
                var present = RTUtil.Create("present", Size, Size, RenderTextureFormat.ARGB32);
                edgeDoc.Present(edgeTarget.PadMap, edgeTarget.WrapVector, present);
                var across = Pixel(present, 1f - 3f / Size, 0.5f);
                if (mode == TextureWrapMode.Repeat) Check(across.g < 0.05f, "repeat: padding wraps across the texture edge " + Str(across));
                else Check(across.g > 0.95f, "clamp: padding does not wrap " + Str(across));
                RTUtil.Release(ref present);
                edgeDoc.Dispose();
                edgeTarget.Dispose();
                UnityEngine.Object.DestroyImmediate(edgeQuad.gameObject);
            }

            // Mirror: u 1.25 samples texel 0.75 (Repeat would sample 0.25); Clamp paints nothing outside 0..1
            foreach (var mode in new[] { TextureWrapMode.Mirror, TextureWrapMode.Clamp })
            {
                var quad = MakeQuads((R(-1, -1, 1, 1), 0f, mode == TextureWrapMode.Mirror ? R(0.5f, 0.1f, 1.5f, 0.9f) : R(1.1f, 0.1f, 1.4f, 0.9f)));
                var t = new PaintTarget(quad, new[] { 0 }, 0, "_MainTex", mode, TextureWrapMode.Clamp);
                Check(t.Bake(), $"bake {mode}");
                t.BuildPadMap(Size, Size, 8);
                engine.PrepareCamera(cam, t);
                var d = new PaintDocument(Size, Size);
                var l = d.CreateLayer("Base", Color.white);
                d.Layers.Add(l);
                d.BeginStroke(StrokeKind.Paint, Color.red, 1f);
                var world = mode == TextureWrapMode.Mirror ? new Vector3(0.5f, 0f, 0f) : Vector3.zero; // both u = 1.25
                engine.StrokeDab(d, t, cam, s, PaintTool.HardBrush, Dab(cam, t, world, 10f));
                RTUtil.Release(d.CommitStroke());
                var mirrored = Pixel(l.texture, 0.75f, 0.5f);
                var repeated = Pixel(l.texture, 0.25f, 0.5f);
                if (mode == TextureWrapMode.Mirror)
                {
                    Check(mirrored.g < 0.05f && repeated.g > 0.95f, $"mirror: u 1.25 paints texel 0.75 mirrored={Str(mirrored)} repeated={Str(repeated)}");
                    var mw = t.WrapUV(new Vector2(1.25f, 0.5f));
                    Check(Mathf.Abs(mw.x - 0.75f) < 1e-4f, "WrapUV mirror " + mw);
                }
                else
                {
                    Check(repeated.g > 0.95f && mirrored.g > 0.95f, "clamp: UVs outside 0..1 are not painted");
                }
                d.Dispose();
                t.Dispose();
                UnityEngine.Object.DestroyImmediate(quad.gameObject);
            }

            engine.Dispose();
            UnityEngine.Object.DestroyImmediate(cam.gameObject);
        }

        /// <summary>Painting with UV1 selected must follow the UV1 layout, not UV0.</summary>
        static void SecondUVChannel()
        {
            var cam = MakeCamera(true);
            var mr = MakeQuads((R(-1, -1, 1, 1), 0f, R(0, 0, 1, 1)));
            var mesh = mr.GetComponent<MeshFilter>().sharedMesh;
            // UV1: the same quad squeezed into the lower right quarter
            var uv1 = new System.Collections.Generic.List<Vector2>();
            mesh.GetUVs(0, uv1);
            for (int i = 0; i < uv1.Count; i++) uv1[i] = new Vector2(0.5f + uv1[i].x * 0.5f, uv1[i].y * 0.5f);
            mesh.SetUVs(1, uv1);

            var engine = new BrushEngine();
            var s = new BrushSettings();
            var inUV0 = new Vector2(0.75f, 0.75f);   // world (0.5, 0.5) in UV0
            var inUV1 = new Vector2(0.875f, 0.375f); // the same point in UV1
            foreach (int channel in new[] { 0, 1 })
            {
                var target = new PaintTarget(mr, new[] { 0 }, channel, "_MainTex");
                Check(target.Bake(), $"bake UV{channel} {target.Error}");
                target.BuildPadMap(Size, Size, 8);
                engine.PrepareCamera(cam, target);
                var doc = new PaintDocument(Size, Size);
                var layer = doc.CreateLayer("Base", Color.white);
                doc.Layers.Add(layer);
                doc.BeginStroke(StrokeKind.Paint, Color.red, 1f);
                engine.StrokeDab(doc, target, cam, s, PaintTool.HardBrush, Dab(cam, target, new Vector3(0.5f, 0.5f, 0f), 15f));
                RTUtil.Release(doc.CommitStroke());

                var a = Pixel(layer.texture, inUV0.x, inUV0.y);
                var b = Pixel(layer.texture, inUV1.x, inUV1.y);
                if (channel == 0) Check(a.g < 0.05f && b.g > 0.95f, $"UV0 selected paints the UV0 layout uv0={Str(a)} uv1={Str(b)}");
                else Check(b.g < 0.05f && a.g > 0.95f, $"UV1 selected paints the UV1 layout uv0={Str(a)} uv1={Str(b)}");
                bool hit = target.Raycast(new Ray(new Vector3(0.5f, 0.5f, -5f), Vector3.forward), out var h);
                Check(hit && Vector2.Distance(h.uv, channel == 0 ? inUV0 : inUV1) < 1e-3f, $"UV{channel} colour picking reads the same layout {h.uv}");

                doc.Dispose();
                target.Dispose();
            }

            var session = PaintSession.Create(mr, new[] { 0 }, 1, "_MainTex", 32, 32, 2, new BrushSettings(), out string error);
            Check(session != null && session.Target.UVChannel == 1 && session.Meta.parts[0].uvChannel == 1, "session keeps the chosen UV channel " + error);
            session?.Dispose();

            engine.Dispose();
            Cleanup(mr, cam);
        }

        /// <summary>
        /// Body on UV0 with no UV2 layout next to a head on UV2 with no UV0 layout (a
        /// Poiyomi main texture + face decal setup): one session paints both textures.
        /// </summary>
        static void MultiTexturePaint()
        {
            var cam = MakeCamera(true);
            var mr = MakeQuads(
                (R(-1, -1, 0, 1), 0f, R(0.1f, 0.1f, 0.4f, 0.9f)),
                (R(0, -1, 1, 1), 0f, R(0.1f, 0.1f, 0.4f, 0.9f)));
            var mesh = mr.GetComponent<MeshFilter>().sharedMesh;
            var uv0 = new System.Collections.Generic.List<Vector2>();
            mesh.GetUVs(0, uv0);
            var uv2 = new System.Collections.Generic.List<Vector2>(uv0);
            for (int i = 0; i < 4; i++) uv2[i] = Vector2.zero; // body: no UV2 layout
            for (int i = 4; i < 8; i++)
            {
                uv2[i] = new Vector2(0.5f + (uv0[i].x - 0.1f), uv0[i].y); // head: UV2 at u 0.5..0.8
                uv0[i] = Vector2.zero;                                     // head: no UV0 layout
            }
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(2, uv2);

            var specs = new[]
            {
                new PartSpec { textureProperty = "_MainTex", uvChannel = 0, width = Size, height = Size },
                new PartSpec { textureProperty = "_DetailAlbedoMap", uvChannel = 2, width = Size, height = Size }
            };
            var session = PaintSession.Create(mr, new[] { 0 }, specs, 8, new BrushSettings(), out string error);
            Check(session != null && session.Parts.Count == 2, "multi texture session " + error);
            if (session == null)
            {
                Cleanup(mr, cam);
                return;
            }
            var body = session.Parts[0];
            var head = session.Parts[1];
            Check(body.Target.TriangleCount == 2 && head.Target.TriangleCount == 2, $"triangles split by UV layout body={body.Target.TriangleCount} head={head.Target.TriangleCount}");

            // a stroke over the neck seam paints both textures, each in its own layout
            Check(session.Raycast(new Ray(new Vector3(0.5f, 0f, -5f), Vector3.forward), out var headHit, out int headPart) && headPart == 1, "raycast finds the head texture");
            session.BeginStrokeCore(PaintTool.HardBrush);
            session.ApplyDab(cam, Dab(cam, body.Target, Vector3.zero, 30f));
            session.EndStroke();
            var bodyEdge = Pixel(body.Document.ActiveLayer.texture, 0.39f, 0.5f);
            var headEdge = Pixel(head.Document.ActiveLayer.texture, 0.51f, 0.5f);
            Check(bodyEdge.a > 0.9f && headEdge.a > 0.9f, $"stroke over the seam paints body UV0 and head UV2 body={Str(bodyEdge)} head={Str(headEdge)}");
            var bodyCollapsed = Pixel(body.Document.ActiveLayer.texture, 0.001f, 0.001f);
            var headCollapsed = Pixel(head.Document.ActiveLayer.texture, 0.001f, 0.001f);
            var bodyAtHeadLayout = Pixel(body.Document.ActiveLayer.texture, 0.51f, 0.5f);
            Check(bodyCollapsed.a < 0.01f && headCollapsed.a < 0.01f && bodyAtHeadLayout.a < 0.01f,
                $"collapsed UVs receive no paint body00={Str(bodyCollapsed)} head00={Str(headCollapsed)} bodyAtHead={Str(bodyAtHeadLayout)}");

            session.History.PerformUndo();
            Check(Pixel(body.Document.ActiveLayer.texture, 0.39f, 0.5f).a < 0.01f && Pixel(head.Document.ActiveLayer.texture, 0.51f, 0.5f).a < 0.01f, "one undo reverts both textures");
            session.History.PerformRedo();
            Check(Pixel(head.Document.ActiveLayer.texture, 0.51f, 0.5f).a > 0.9f, "redo re-applies both textures");

            // colour picking reads the texture under the cursor
            session.Settings.color = Color.black;
            Check(session.PickColor(headHit, headPart) && session.Settings.color.r > 0.5f, "pick colour from the head texture " + Str(session.Settings.color));

            // blur over the seam mixes the two textures
            RTUtil.Clear(body.Document.Layers[0].texture, Color.black);
            body.Document.Layers[0].MarkDirty();
            session.SetActiveLayer(0);
            session.Settings.blur.strength = 1f;
            session.Settings.blur.hardness = 0.8f;
            session.Settings.blurSize = 0.5f;
            session.BeginStrokeCore(PaintTool.Blur);
            for (int i = 0; i < 4; i++) session.ApplyDab(cam, Dab(cam, body.Target, Vector3.zero, 40f));
            session.EndStroke();
            var bodyBlur = Pixel(body.Document.Layers[0].texture, 0.395f, 0.5f);
            var headBlur = Pixel(head.Document.Layers[0].texture, 0.505f, 0.5f);
            Check(bodyBlur.r > 0.1f && headBlur.r < 0.9f, $"blur mixes colours across textures body={Str(bodyBlur)} head={Str(headBlur)}");

            // linked layer stack
            session.AddLayer();
            Check(body.Document.Layers.Count == 3 && head.Document.Layers.Count == 3, "new layer is added to every texture");
            var props = body.Document.ActiveLayer.Props;
            props.opacity = 0.5f;
            session.SetLayerProps(body.Document.ActiveLayer, props, "Opacity");
            Check(Mathf.Approximately(head.Document.ActiveLayer.opacity, 0.5f), "layer properties apply to every texture");
            session.History.PerformUndo();
            session.History.PerformUndo();
            Check(body.Document.Layers.Count == 2 && head.Document.Layers.Count == 2, "undo removes the layer from every texture");

            // preview shows both textures
            session.RefreshPreview();
            var block = new MaterialPropertyBlock();
            mr.GetPropertyBlock(block, 0);
            Check(block.GetTexture("_MainTex") is RenderTexture && block.GetTexture("_DetailAlbedoMap") is RenderTexture, "preview shows both textures");

            // save and reopen
            string path = System.IO.Path.GetFullPath("Temp/mtp_multi.mtpaint");
            session.SaveProject(path);
            var reopened = PaintSession.Open(path, mr, new BrushSettings(), out string openError);
            Check(reopened != null && reopened.Parts.Count == 2 && reopened.Parts[1].Meta.uvChannel == 2 && reopened.Parts[1].Target.TriangleCount == 2,
                "multi texture project reopens " + openError);
            if (reopened != null)
            {
                var reHead = Pixel(reopened.Parts[1].Document.Layers[1].texture, 0.51f, 0.5f);
                Check(reHead.a > 0.9f, "reopened head texture keeps paint " + Str(reHead));
                reopened.Dispose();
            }

            // a texture whose channel has no triangles left is reported
            var fullUV = MakeQuads((R(-1, -1, 1, 1), 0f, R(0, 0, 1, 1)));
            var fullMesh = fullUV.GetComponent<MeshFilter>().sharedMesh;
            var copy = new System.Collections.Generic.List<Vector2>();
            fullMesh.GetUVs(0, copy);
            fullMesh.SetUVs(2, copy);
            var empty = PaintSession.Create(fullUV, new[] { 0 }, specs, 8, new BrushSettings(), out string emptyError);
            Check(empty == null && emptyError != null && emptyError.Contains("UV2"), "texture without own triangles is reported: " + emptyError);
            empty?.Dispose();
            UnityEngine.Object.DestroyImmediate(fullUV.gameObject);

            session.Dispose();
            block = new MaterialPropertyBlock();
            mr.GetPropertyBlock(block, 0);
            Check(block.GetTexture("_MainTex") == null && block.GetTexture("_DetailAlbedoMap") == null, "both previews removed when the session ends");
            Cleanup(mr, cam);
        }

        static Texture2D SplitTexture()
        {
            var split = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true);
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                px[y * Size + x] = x < Size / 2 ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);
            split.SetPixels32(px);
            split.Apply();
            return split;
        }

        /// <summary>Color Blend turns a hard border into a smooth gradient and never drags colours along the stroke.</summary>
        static void BlendTransition()
        {
            var cam = MakeCamera(true);
            var mr = MakeQuads((R(-1, -1, 1, 1), 0f, R(0, 0, 1, 1)));
            var session = PaintSession.Create(mr, new[] { 0 }, 0, "_MainTex", Size, Size, 8, new BrushSettings(), out string error);
            Check(session != null, "blend session " + error);
            if (session == null)
            {
                Cleanup(mr, cam);
                return;
            }
            var doc = session.Document;
            var baseLayer = doc.Layers[0];
            session.SetActiveLayer(0);

            void LoadSplit()
            {
                var split = SplitTexture();
                RTUtil.Release(ref baseLayer.texture);
                baseLayer.texture = doc.RenderImported(split, false);
                baseLayer.MarkDirty();
                UnityEngine.Object.DestroyImmediate(split);
            }
            float Value(float u) => Pixel(baseLayer.texture, u, 0.5f).r;

            var s = session.Settings;
            s.blendMode = ColorBlendMode.Transition;
            s.blend.strength = 1f;
            s.blend.hardness = 1f;
            s.blendWidth = 1f;

            // a hard black / white border becomes a steady ramp in every mixing space
            foreach (var space in new[] { ColorMixSpace.Perceptual, ColorMixSpace.Linear, ColorMixSpace.Srgb })
            {
                LoadSplit();
                s.mixSpace = space;
                session.BeginStrokeCore(PaintTool.ColorBlend);
                for (int i = 0; i < 6; i++) session.ApplyDab(cam, Dab(cam, session.Target, Vector3.zero, 60f));
                session.EndStroke();

                var samples = new float[9];
                bool monotonic = true;
                float maxStep = 0f;
                string text = "";
                for (int k = 0; k < samples.Length; k++)
                {
                    samples[k] = Value(0.5f + (k - 4) * 8f / Size);
                    text += samples[k].ToString("F2") + " ";
                    if (k > 0)
                    {
                        if (samples[k] < samples[k - 1] - 0.01f) monotonic = false;
                        maxStep = Mathf.Max(maxStep, Mathf.Abs(samples[k] - samples[k - 1]));
                    }
                }
                Check(monotonic && samples[0] < 0.5f && samples[8] > 0.5f && samples[8] - samples[0] > 0.4f && maxStep < 0.25f,
                    $"{space}: hard border becomes a smooth transition [{text}] max step {maxStep:F2}");
            }

            session.History.PerformUndo();
            Check(Value(0.5f - 2f / Size) < 0.01f && Value(0.5f + 2f / Size) > 0.99f, "undo restores the hard border");

            // dragging from black into white must not carry black along the stroke
            LoadSplit();
            s.mixSpace = ColorMixSpace.Perceptual;
            s.blendWidth = 0.6f;
            s.blend.strength = 0.5f;
            s.blend.hardness = 0.5f;
            session.BeginStrokeCore(PaintTool.ColorBlend);
            for (float x = -0.3f; x <= 0.61f; x += 0.02f) session.ApplyDab(cam, Dab(cam, session.Target, new Vector3(x, 0f, 0f), 30f));
            session.EndStroke();
            float deepWhite = Value(0.78f), deepBlack = Value(0.37f), border = Value(0.5f);
            Check(deepWhite > 0.95f, $"no smear: white stays white after the brush passed from black ({deepWhite:F3})");
            Check(deepBlack < 0.05f, $"no smear: black far from the border stays black ({deepBlack:F3})");
            Check(border > 0.1f && border < 0.9f, $"the border itself is blended ({border:F3})");

            // colours pass through the mixing spaces unchanged
            var flat = new Color(0.8f, 0.3f, 0.2f, 1f);
            foreach (var space in new[] { ColorMixSpace.Perceptual, ColorMixSpace.Linear, ColorMixSpace.Srgb })
            {
                RTUtil.Clear(baseLayer.texture, flat);
                baseLayer.MarkDirty();
                s.mixSpace = space;
                session.BeginStrokeCore(PaintTool.ColorBlend);
                session.ApplyDab(cam, Dab(cam, session.Target, Vector3.zero, 40f));
                session.EndStroke();
                var c = Pixel(baseLayer.texture, 0.5f, 0.5f);
                Check(Mathf.Abs(c.r - flat.r) < 0.015f && Mathf.Abs(c.g - flat.g) < 0.015f && Mathf.Abs(c.b - flat.b) < 0.015f,
                    $"{space}: a flat colour stays unchanged {Str(c)}");
            }

            session.Dispose();
            Cleanup(mr, cam);
        }

        /// <summary>
        /// Mask painting: R, G and B strokes only move their own channel and add up in the
        /// same spot, blur only touches the selected channel, colour survives zero alpha.
        /// </summary>
        static void MaskPaint()
        {
            var cam = MakeCamera(true);
            var mr = MakeQuads((R(-1, -1, 1, 1), 0f, R(0, 0, 1, 1)));
            var specs = new[] { new PartSpec { textureProperty = "_DetailMask", uvChannel = 0, width = Size, height = Size } };
            var session = PaintSession.Create(mr, new[] { 0 }, specs, 8, new BrushSettings(), out string error, PaintMode.Mask);
            Check(session != null && session.IsMask, "mask session " + error);
            if (session == null)
            {
                Cleanup(mr, cam);
                return;
            }
            // each painter lists its own texture slots
            var masks = MeshTexturePainterWindow.MaskProperties(mr.sharedMaterial);
            var colors = MeshTexturePainterWindow.ColorTextureProperties(mr.sharedMaterial);
            Check(masks.Contains("_DetailMask") && !masks.Contains("_MainTex") && colors.Contains("_MainTex") && !colors.Contains("_DetailMask") && !colors.Intersect(masks).Any(),
                $"mask painter lists only masks [{string.Join(", ", masks)}], texture painter no masks [{string.Join(", ", colors)}]");

            var doc = session.Document;
            var s = session.Settings;
            Check(doc.IsMask && doc.Layers.Count == 1 && !doc.IsSRGB, $"mask starts as one linear layer layers={doc.Layers.Count} srgb={doc.IsSRGB}");
            var start = Pixel(doc.ActiveLayer.texture, 0.5f, 0.5f);
            Check(start.r < 0.01f && start.g < 0.01f && start.b < 0.01f && start.a > 0.99f, "new mask starts black " + Str(start));

            void Stroke(PaintTool tool, MaskChannel channel, Vector3 world, float radius = 30f)
            {
                s.maskChannel = channel;
                session.BeginStrokeCore(tool);
                session.ApplyDab(cam, Dab(cam, session.Target, world, radius));
                session.EndStroke();
            }
            Color At(float u) => Pixel(doc.ActiveLayer.texture, u, 0.5f);

            // red and green over the same spot add up instead of replacing each other
            Stroke(PaintTool.HardBrush, MaskChannel.Red, Vector3.zero);
            var red = At(0.5f);
            Check(red.r > 0.99f && red.g < 0.01f && red.b < 0.01f, "red paints only the red channel " + Str(red));
            Stroke(PaintTool.HardBrush, MaskChannel.Green, Vector3.zero);
            var both = At(0.5f);
            Check(both.r > 0.99f && both.g > 0.99f && both.b < 0.01f && both.a > 0.99f, "green over red keeps red " + Str(both));

            // the pending stroke shows in the preview composite before it is committed
            s.maskChannel = MaskChannel.Blue;
            session.BeginStrokeCore(PaintTool.HardBrush);
            session.ApplyDab(cam, Dab(cam, session.Target, Vector3.zero, 30f));
            var pending = Pixel(doc.GetComposite(), 0.5f, 0.5f);
            Check(pending.r > 0.99f && pending.g > 0.99f && pending.b > 0.99f, "pending blue stroke adds to the composite " + Str(pending));
            session.EndStroke();

            // opacity moves the channel part of the way
            s.hard.strength = 0.5f;
            Stroke(PaintTool.HardBrush, MaskChannel.Red, new Vector3(-0.5f, 0f, 0f), 20f);
            s.hard.strength = 1f;
            var half = At(0.25f);
            Check(Mathf.Abs(half.r - 0.5f) < 0.02f && half.g < 0.01f, "opacity scales the mask " + Str(half));

            // the eraser clears only the selected channel, black clears all, white sets all
            Stroke(PaintTool.Eraser, MaskChannel.Green, Vector3.zero);
            var erased = At(0.5f);
            Check(erased.r > 0.99f && erased.g < 0.01f && erased.b > 0.99f, "eraser clears only green " + Str(erased));
            Stroke(PaintTool.HardBrush, MaskChannel.Black, Vector3.zero);
            var black = At(0.5f);
            Check(black.r < 0.01f && black.g < 0.01f && black.b < 0.01f && black.a > 0.99f, "black clears every channel and keeps alpha " + Str(black));
            Stroke(PaintTool.HardBrush, MaskChannel.White, Vector3.zero);
            var white = At(0.5f);
            Check(white.r > 0.99f && white.g > 0.99f && white.b > 0.99f, "white sets every channel " + Str(white));
            session.History.PerformUndo();
            Check(At(0.5f).r < 0.01f, "undo reverts the mask stroke " + Str(At(0.5f)));

            // whole mask operations on the selected channel
            s.maskChannel = MaskChannel.Green;
            session.ApplyMaskOperation(MaskOperation.Invert);
            var inverted = At(0.9f);
            Check(inverted.g > 0.99f && inverted.r < 0.01f, "invert green " + Str(inverted));
            s.maskChannel = MaskChannel.Red;
            session.ApplyMaskOperation(MaskOperation.Fill);
            var filled = At(0.9f);
            Check(filled.r > 0.99f && filled.g > 0.99f && filled.b < 0.01f, "fill red keeps green " + Str(filled));
            session.History.PerformUndo();
            session.History.PerformUndo();
            Check(At(0.9f).r < 0.01f && At(0.9f).g < 0.01f, "undo mask operations " + Str(At(0.9f)));

            // blur only touches the selected channel: black / white split in every channel
            var split = SplitTexture();
            var layer = doc.ActiveLayer;
            RTUtil.Release(ref layer.texture);
            layer.texture = doc.RenderImported(split, false);
            layer.MarkDirty();
            UnityEngine.Object.DestroyImmediate(split);
            s.blur.strength = 1f;
            s.blur.hardness = 0.8f;
            s.blurSize = 0.5f;
            s.maskChannel = MaskChannel.Red;
            session.BeginStrokeCore(PaintTool.Blur);
            for (int i = 0; i < 4; i++) session.ApplyDab(cam, Dab(cam, session.Target, Vector3.zero, 40f));
            session.EndStroke();
            var right = At(0.5f + 2f / Size);
            var left = At(0.5f - 2f / Size);
            Check(right.r < 0.9f && left.r > 0.1f && right.g > 0.99f && left.g < 0.01f, $"blur softens only red right={Str(right)} left={Str(left)}");

            // colour channels survive where the mask's alpha is 0
            RTUtil.Clear(layer.texture, new Color(0f, 0f, 0f, 0f));
            layer.MarkDirty();
            Stroke(PaintTool.HardBrush, MaskChannel.Blue, Vector3.zero);
            var clearAlpha = Pixel(doc.GetComposite(), 0.5f, 0.5f);
            Check(clearAlpha.b > 0.99f && clearAlpha.a < 0.01f, "mask colour survives zero alpha " + Str(clearAlpha));
            var present = RTUtil.Create("present", Size, Size, RenderTextureFormat.ARGB32);
            doc.Present(session.Target.PadMap, session.Target.WrapVector, present);
            var presented = Pixel(present, 0.5f, 0.5f);
            Check(presented.b > 0.99f, "presented mask keeps colour at zero alpha " + Str(presented));
            RTUtil.Release(ref present);

            // no colour blend brush and no colour picking on masks
            Check(!session.HandleKeyEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Alpha6 }) && s.tool != PaintTool.ColorBlend, "6 selects no blend brush in the mask painter");
            Check(!session.PickColor(default), "no colour picking on masks");

            // preview: on the mask slot, optionally in place of the main texture
            MaterialPropertyBlock Block()
            {
                var block = new MaterialPropertyBlock();
                mr.GetPropertyBlock(block, 0);
                return block;
            }
            session.RefreshPreview();
            Check(Block().GetTexture("_DetailMask") is RenderTexture && Block().GetTexture("_MainTex") == null, "mask preview shows on its slot");
            session.ShowMaskOnModel = true;
            session.RefreshPreview();
            Check(Block().GetTexture("_MainTex") != null && Block().GetTexture("_MainTex") == Block().GetTexture("_DetailMask"), "show mask on model");
            session.ShowMaskOnModel = false;
            session.RefreshPreview();
            Check(Block().GetTexture("_MainTex") == null && Block().GetTexture("_DetailMask") is RenderTexture, "mask hidden from the model again");

            // save and reopen as a mask
            string path = System.IO.Path.GetFullPath("Temp/mtp_mask.mtpaint");
            session.SaveProject(path);
            Check(PaintProjectIO.ReadMeta(path).mode == (int)PaintMode.Mask, "project header records the mask mode");
            var reopened = PaintSession.Open(path, mr, new BrushSettings(), out string openError);
            Check(reopened != null && reopened.IsMask && reopened.Document.IsMask && reopened.Document.Layers.Count == 1, "mask project reopens as a mask " + openError);
            if (reopened != null)
            {
                var c = Pixel(reopened.Document.GetComposite(), 0.5f, 0.5f);
                Check(c.b > 0.99f && c.a < 0.01f, "reopened mask keeps colour at zero alpha " + Str(c));
                reopened.Dispose();
            }

            session.Dispose();
            Cleanup(mr, cam);
        }

        static void CompositeModes()
        {
            var doc = new PaintDocument(8, 8);
            var bottom = doc.CreateLayer("Bottom", new Color(0.5f, 0.5f, 0.5f, 1f));
            var top = doc.CreateLayer("Top", new Color(0.5f, 0.5f, 0.5f, 1f));
            doc.Layers.Add(bottom);
            doc.Layers.Add(top);
            doc.ActiveIndex = 1;

            void Expect(LayerBlendMode mode, float opacity, float expected)
            {
                top.blendMode = mode;
                top.opacity = opacity;
                var c = Pixel(doc.GetComposite(), 0.5f, 0.5f);
                Check(Mathf.Abs(c.r - expected) < 0.02f && c.a > 0.99f, $"blend {mode} @{opacity}: {c.r:F3} expected {expected:F3}");
            }

            Expect(LayerBlendMode.Normal, 1f, 0.5f);
            Expect(LayerBlendMode.Multiply, 1f, 0.25f);
            Expect(LayerBlendMode.Screen, 1f, 0.75f);
            Expect(LayerBlendMode.Add, 1f, 1f);
            Expect(LayerBlendMode.Multiply, 0.5f, 0.375f);
            Expect(LayerBlendMode.Overlay, 1f, 0.5f);

            // transparent top layer over opaque bottom
            RTUtil.Clear(top.texture, new Color(1f, 0f, 0f, 0.5f));
            top.MarkDirty();
            Expect(LayerBlendMode.Normal, 1f, 0.75f);

            // merged result equals composite
            var merged = doc.RenderMerged(bottom, top);
            var m = Pixel(merged, 0.5f, 0.5f);
            Check(Mathf.Abs(m.r - 0.75f) < 0.02f && Mathf.Abs(m.g - 0.25f) < 0.02f, "merge down " + Str(m));
            RTUtil.Release(ref merged);
            doc.Dispose();
        }
    }
}
