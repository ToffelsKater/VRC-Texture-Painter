using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MeshTexturePainter
{
    /// <summary>What to paint: a texture property and the UV channel the shader reads it with.</summary>
    internal struct PartSpec
    {
        public string textureProperty;
        public int uvChannel;
        public int width;   // 0 = size of the source texture
        public int height;
        public TextureWrapMode? wrapU;
        public TextureWrapMode? wrapV;
    }

    /// <summary>
    /// One painted texture of a session. A material can show different textures
    /// through different UV channels (the main texture on UV0 for the body, a
    /// Poiyomi decal on UV2 for the face); each part owns the triangles laid out in
    /// its channel. All parts share the same layer structure.
    /// </summary>
    internal sealed class PaintPart : IDisposable
    {
        public PaintDocument Document;
        public PaintTarget Target;
        public PartMeta Meta;
        public RenderTexture Preview;
        /// <summary>During a color blend stroke: the layer as it was when the stroke started (owned by the stroke, not the part).</summary>
        public RenderTexture FilterSource;

        public string Label => Meta != null ? $"{Meta.textureProperty} (UV{Meta.uvChannel})" : "Texture";

        public void Dispose()
        {
            Document?.Dispose();
            Target?.Dispose();
            RTUtil.Release(ref Preview);
            Document = null;
            Target = null;
        }
    }

    /// <summary>
    /// A painting session on one renderer: owns the painted textures (parts), the
    /// brush engine and the history, and handles all Scene view input while active.
    /// </summary>
    internal sealed class PaintSession : IDisposable
    {
        static readonly int ControlHash = "MeshTexturePainter.Paint".GetHashCode();

        readonly List<PaintPart> parts;

        public IReadOnlyList<PaintPart> Parts => parts;
        /// <summary>The first texture. Every part has the same layer structure, so it also stands for the layer stack.</summary>
        public PaintDocument Document => parts.Count > 0 ? parts[0].Document : null;
        public PaintTarget Target => parts.Count > 0 ? parts[0].Target : null;
        public PaintHistory History { get; private set; }
        public BrushEngine Engine { get; private set; }
        public BrushSettings Settings { get; }
        public ProjectMeta Meta { get; }

        public string ProjectPath;
        public bool Dirty;
        public bool IsStroking => stroking;

        public event Action Changed;

        bool previewDirty = true;
        double lastPoseCheck;
        bool previousToolsHidden;
        Tool previousTool;
        bool active;

        // stroke state
        bool stroking;
        PaintTool strokeTool;
        PaintLayer[] strokeLayers;
        RenderTexture[] filterSnapshots;
        bool strokeHadDabs;
        Vector2 lastGui;
        float distanceToNextDab;

        // cursor
        Vector2 cursorGui;
        bool cursorInView;
        bool cursorHasHit;
        PaintHit cursorHit;
        int cursorPart;

        PaintSession(List<PaintPart> parts, BrushSettings settings, ProjectMeta meta)
        {
            this.parts = parts;
            Settings = settings;
            Meta = meta;
            Engine = new BrushEngine();
            History = new PaintHistory(parts.Select(p => p.Document));
            History.Changed += OnHistoryChanged;
        }

        // ------------------------------------------------------------------ creation

        public static PaintSession Create(Renderer renderer, int[] slots, int uvChannel, string textureProperty,
            int width, int height, int padding, BrushSettings settings, out string error,
            TextureWrapMode? wrapU = null, TextureWrapMode? wrapV = null)
        {
            var spec = new PartSpec { textureProperty = textureProperty, uvChannel = uvChannel, width = width, height = height, wrapU = wrapU, wrapV = wrapV };
            return Create(renderer, slots, new[] { spec }, padding, settings, out error);
        }

        /// <summary>Starts painting one or more textures of the renderer's material at once.</summary>
        public static PaintSession Create(Renderer renderer, int[] slots, IReadOnlyList<PartSpec> specs, int padding,
            BrushSettings settings, out string error)
        {
            error = null;
            if (specs == null || specs.Count == 0)
            {
                error = "Add at least one texture to paint.";
                return null;
            }
            var materials = renderer.sharedMaterials;
            var material = slots.Select(s => s < materials.Length ? materials[s] : null).FirstOrDefault(m => m != null);

            var parts = new List<PaintPart>();
            var claimed = new List<int>();
            foreach (var spec in specs)
            {
                var source = material != null && material.HasProperty(spec.textureProperty) ? material.GetTexture(spec.textureProperty) : null;
                // the texture's own wrap mode decides where UVs outside 0..1 are painted
                var target = new PaintTarget(renderer, slots, spec.uvChannel, spec.textureProperty,
                    spec.wrapU ?? (source != null ? source.wrapModeU : TextureWrapMode.Repeat),
                    spec.wrapV ?? (source != null ? source.wrapModeV : TextureWrapMode.Repeat),
                    claimed.ToArray());
                if (!target.Bake())
                {
                    error = specs.Count > 1 ? $"{spec.textureProperty} (UV{spec.uvChannel}): {target.Error}" : target.Error;
                    target.Dispose();
                    foreach (var p in parts) p.Dispose();
                    return null;
                }
                claimed.Add(spec.uvChannel);

                var size = spec.width > 0 && spec.height > 0 ? new Vector2Int(spec.width, spec.height) : PaintProjectIO.SourceSize(source);
                var doc = new PaintDocument(Mathf.Clamp(size.x, 16, 8192), Mathf.Clamp(size.y, 16, 8192)) { IsSRGB = PaintProjectIO.IsSRGB(source) };
                doc.Layers.Add(CreateBaseLayer(doc, source));
                doc.Layers.Add(doc.CreateLayer("Layer 1", Color.clear));
                doc.ActiveIndex = 1;

                parts.Add(new PaintPart
                {
                    Document = doc,
                    Target = target,
                    Meta = new PartMeta
                    {
                        textureProperty = spec.textureProperty,
                        uvChannel = spec.uvChannel,
                        width = doc.Width,
                        height = doc.Height,
                        srgb = doc.IsSRGB,
                        sourceTexturePath = source != null ? AssetDatabase.GetAssetPath(source) : null,
                        wrapU = (int)target.WrapU,
                        wrapV = (int)target.WrapV
                    }
                });
            }

            var meta = new ProjectMeta { slots = parts[0].Target.Slots, padding = padding, parts = parts.Select(p => p.Meta).ToArray() };
            return Finish(parts, settings, meta, padding);
        }

        static PaintLayer CreateBaseLayer(PaintDocument doc, Texture source)
        {
            if (source == null) return doc.CreateLayer("Base", Color.white);
            var raw = PaintProjectIO.LoadSourcePixels(source);
            if (raw != null)
            {
                var layer = new PaintLayer("Base", doc.RenderImported(raw, false));
                Object.DestroyImmediate(raw);
                return layer;
            }
            Debug.LogWarning($"VRC Texture Painter: '{source.name}' is not a PNG, JPG or TGA file, the base layer was read from the imported (possibly compressed or downscaled) texture.");
            return new PaintLayer("Base", doc.RenderImported(source, doc.IsSRGB));
        }

        public static PaintSession Open(string path, Renderer renderer, BrushSettings settings, out string error)
        {
            error = null;
            List<PaintDocument> docs;
            ProjectMeta meta;
            try
            {
                docs = PaintProjectIO.Load(path, out meta);
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }

            if (renderer == null) renderer = ResolveRenderer(meta);
            if (renderer == null)
            {
                foreach (var d in docs) d.Dispose();
                error = "Select the renderer this project belongs to, then open it again.";
                return null;
            }

            var parts = new List<PaintPart>();
            var claimed = new List<int>();
            for (int i = 0; i < docs.Count; i++)
            {
                var pm = meta.parts[i];
                var target = new PaintTarget(renderer, meta.slots ?? new[] { 0 }, pm.uvChannel, pm.textureProperty,
                    (TextureWrapMode)pm.wrapU, (TextureWrapMode)pm.wrapV, claimed.ToArray());
                parts.Add(new PaintPart { Document = docs[i], Target = target, Meta = pm });
                claimed.Add(pm.uvChannel);
                if (!target.Bake())
                {
                    error = docs.Count > 1 ? $"{pm.textureProperty} (UV{pm.uvChannel}): {target.Error}" : target.Error;
                    foreach (var p in parts) p.Dispose();
                    for (int j = i + 1; j < docs.Count; j++) docs[j].Dispose();
                    return null;
                }
            }
            var session = Finish(parts, settings, meta, meta.padding);
            session.ProjectPath = path;
            return session;
        }

        static PaintSession Finish(List<PaintPart> parts, BrushSettings settings, ProjectMeta meta, int padding)
        {
            foreach (var part in parts) part.Target.BuildPadMap(part.Document.Width, part.Document.Height, padding);
            var renderer = parts[0].Target.Renderer;
            meta.rendererId = GlobalObjectId.GetGlobalObjectIdSlow(renderer).ToString();
            meta.rendererPath = HierarchyPath(renderer.transform);
            return new PaintSession(parts, settings, meta);
        }

        public static Renderer ResolveRenderer(ProjectMeta meta)
        {
            if (!string.IsNullOrEmpty(meta.rendererId) && GlobalObjectId.TryParse(meta.rendererId, out var id))
            {
                if (GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) is Renderer r) return r;
            }
            if (!string.IsNullOrEmpty(meta.rendererPath))
            {
                var go = GameObject.Find(meta.rendererPath);
                if (go != null && go.TryGetComponent<Renderer>(out var byPath)) return byPath;
            }
            return null;
        }

        static string HierarchyPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return "/" + path;
        }

        // ------------------------------------------------------------------ lifecycle

        public void Activate()
        {
            if (active) return;
            active = true;
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += Update;
            previousToolsHidden = Tools.hidden;
            Tools.hidden = true;
            previousTool = Tools.current;
            Tools.current = Tool.None;
            previewDirty = true;
            SceneView.RepaintAll();
        }

        public void Deactivate()
        {
            if (!active) return;
            active = false;
            if (stroking) CancelStroke();
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= Update;
            Tools.hidden = previousToolsHidden;
            if (Tools.current == Tool.None && previousTool != Tool.None) Tools.current = previousTool;
            Target?.RestorePreview();
            SceneView.RepaintAll();
        }

        public void Dispose()
        {
            Deactivate();
            if (History != null)
            {
                History.Changed -= OnHistoryChanged;
                History.Dispose();
            }
            Target?.RestorePreview();
            foreach (var part in parts) part.Dispose();
            parts.Clear();
            Engine?.Dispose();
            ReleaseSnapshots();
            History = null;
            Engine = null;
        }

        void ReleaseSnapshots()
        {
            if (filterSnapshots == null) return;
            foreach (var rt in filterSnapshots) RTUtil.Release(rt);
            filterSnapshots = null;
        }

        void OnHistoryChanged()
        {
            Dirty = true;
            MarkChanged();
        }

        public void MarkChanged()
        {
            previewDirty = true;
            Changed?.Invoke();
        }

        void Update()
        {
            if (Target == null) return;
            if (Target.Renderer == null)
            {
                Deactivate();
                Changed?.Invoke();
                return;
            }

            EnforceNoTool();

            double now = EditorApplication.timeSinceStartup;
            if (!stroking && now - lastPoseCheck > 0.5)
            {
                lastPoseCheck = now;
                if (Target.PoseChanged())
                {
                    foreach (var part in parts) part.Target.Bake();
                    previewDirty = true;
                }
            }

            if (previewDirty)
            {
                previewDirty = false;
                RefreshPreview();
            }
        }

        public void RefreshPreview()
        {
            var shown = new List<(string, Texture)>(parts.Count);
            foreach (var part in parts)
            {
                var doc = part.Document;
                bool srgb = doc.IsSRGB && RTUtil.LinearProject;
                if (part.Preview == null || part.Preview.width != doc.Width || part.Preview.height != doc.Height || part.Preview.sRGB != srgb)
                {
                    RTUtil.Release(ref part.Preview);
                    part.Preview = RTUtil.Create("MTP Preview " + part.Meta.textureProperty, doc.Width, doc.Height, RenderTextureFormat.ARGB32, srgb, mips: true, filter: FilterMode.Trilinear);
                    part.Preview.anisoLevel = 4;
                }
                part.Preview.wrapModeU = part.Target.WrapU;
                part.Preview.wrapModeV = part.Target.WrapV;
                doc.Present(part.Target.PadMap, part.Target.WrapVector, part.Preview);
                part.Preview.GenerateMips();
                shown.Add((part.Meta.textureProperty, part.Preview));
            }
            Target.ApplyPreview(shown);
            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------ scene input

        void OnSceneGUI(SceneView view)
        {
            if (parts.Count == 0 || parts.Any(p => !p.Target.IsValid) || Document.ActiveLayer == null) return;
            var e = Event.current;
            int id = GUIUtility.GetControlID(ControlHash, FocusType.Passive);

            switch (e.GetTypeForControl(id))
            {
                case EventType.Layout:
                    EnforceNoTool();
                    HandleUtility.AddDefaultControl(id);
                    break;

                case EventType.MouseMove:
                    UpdateCursor(e.mousePosition);
                    view.Repaint();
                    break;

                case EventType.MouseDown:
                    if (e.button == 0 && !e.alt && GUIUtility.hotControl == 0)
                    {
                        GUIUtility.hotControl = id;
                        BeginStroke(view, e);
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        UpdateCursor(e.mousePosition);
                        ContinueStroke(view, e);
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        EndStroke();
                        e.Use();
                    }
                    break;

                case EventType.ScrollWheel:
                    if (e.control)
                    {
                        float step = e.delta.y > 0 ? 1f / 1.1f : 1.1f;
                        if (e.shift) Settings.Current.strength = Mathf.Clamp01(Settings.Current.strength * step);
                        else Settings.Current.radius = Mathf.Clamp(Settings.Current.radius * step, 1f, 2000f);
                        Changed?.Invoke();
                        e.Use();
                    }
                    break;

                case EventType.KeyDown:
                    UpdateCursor(e.mousePosition);
                    if (HandleKeyEvent(e))
                    {
                        e.Use();
                        view.Repaint();
                    }
                    break;

                case EventType.MouseLeaveWindow:
                    cursorInView = false;
                    view.Repaint();
                    break;

                case EventType.Repaint:
                    DrawCursor(view);
                    break;
            }
        }

        /// <summary>
        /// Unity's tools (Q is the View tool) would take the left mouse button away
        /// from painting, so no transform tool stays selected while a session is active.
        /// </summary>
        internal void EnforceNoTool()
        {
            if (active && !Navigating && Tools.current != Tool.None) Tools.current = Tool.None;
        }

        /// <summary>
        /// Right mouse fly mode, Alt orbit or middle mouse pan in progress. Tools.viewToolActive
        /// alone is also true whenever the View tool is merely selected, which must not count.
        /// </summary>
        static bool Navigating => Tools.viewToolActive && Tools.current != Tool.View;

        internal void SetCursor(PaintHit hit, int part = 0)
        {
            cursorInView = true;
            cursorHasHit = true;
            cursorHit = hit;
            cursorPart = part;
        }

        /// <summary>Painting hotkeys. Returns true when the event was handled.</summary>
        internal bool HandleKeyEvent(Event e)
        {
            // Keys are chosen to be unbound in Unity 2022.3's Scene view: fly mode uses
            // WASD / QE, Q-Y switch tools and 2 toggles 2D mode. Never eat keys while navigating.
            if (e.type != EventType.KeyDown || e.control || e.command || e.alt || Navigating) return false;

            var tool = Settings.Current;
            switch (e.keyCode)
            {
                case KeyCode.LeftBracket:
                    if (e.shift) tool.strength = Mathf.Clamp01(tool.strength - 0.05f);
                    else tool.radius = Mathf.Max(1f, tool.radius / 1.15f);
                    break;
                case KeyCode.RightBracket:
                    if (e.shift) tool.strength = Mathf.Clamp01(tool.strength + 0.05f);
                    else tool.radius = Mathf.Min(2000f, tool.radius * 1.15f);
                    break;
                case KeyCode.Alpha3:
                case KeyCode.Keypad1: Settings.tool = PaintTool.HardBrush; break;
                case KeyCode.Alpha4:
                case KeyCode.Keypad2: Settings.tool = PaintTool.SoftBrush; break;
                case KeyCode.Alpha5:
                case KeyCode.Keypad3: Settings.tool = PaintTool.Blur; break;
                case KeyCode.Alpha6:
                case KeyCode.Keypad4: Settings.tool = PaintTool.ColorBlend; break;
                case KeyCode.Alpha7:
                case KeyCode.Keypad5: Settings.tool = PaintTool.Eraser; break;
                case KeyCode.C:
                case KeyCode.Keypad0:
                    if (!PickColorUnderCursor()) return false;
                    break;
                case KeyCode.Escape:
                    if (!stroking) return false;
                    CancelStroke();
                    GUIUtility.hotControl = 0;
                    break;
                default:
                    return false;
            }
            Changed?.Invoke();
            return true;
        }

        /// <summary>Nearest surface under the ray over all painted textures.</summary>
        internal bool Raycast(Ray ray, out PaintHit hit, out int partIndex)
        {
            hit = default;
            partIndex = -1;
            float best = float.MaxValue;
            for (int i = 0; i < parts.Count; i++)
            {
                if (!parts[i].Target.Raycast(ray, out var h) || h.distance >= best) continue;
                best = h.distance;
                hit = h;
                partIndex = i;
            }
            return partIndex >= 0;
        }

        void UpdateCursor(Vector2 guiPoint)
        {
            cursorGui = guiPoint;
            cursorInView = true;
            cursorHasHit = Raycast(HandleUtility.GUIPointToWorldRay(guiPoint), out cursorHit, out cursorPart);
        }

        void DrawCursor(SceneView view)
        {
            if (!cursorInView) return;
            var tool = Settings.Current;
            float radiusPoints = tool.radius / EditorGUIUtility.pixelsPerPoint;
            Color ring = BrushSettings.UsesColor(Settings.tool) ? Settings.color : Color.white;
            ring.a = 1f;

            Handles.BeginGUI();
            var center = new Vector3(cursorGui.x, cursorGui.y, 0f);
            Handles.color = new Color(0f, 0f, 0f, 0.6f);
            Handles.DrawWireDisc(center, Vector3.forward, radiusPoints + 1f);
            Handles.color = ring;
            Handles.DrawWireDisc(center, Vector3.forward, radiusPoints);
            if (Settings.tool == PaintTool.SoftBrush || Settings.tool == PaintTool.Eraser || Settings.tool == PaintTool.ColorBlend)
            {
                Handles.color = new Color(ring.r, ring.g, ring.b, 0.35f);
                Handles.DrawWireDisc(center, Vector3.forward, radiusPoints * Mathf.Max(0.02f, tool.hardness));
            }
            else if (Settings.tool == PaintTool.Blur)
            {
                Handles.color = new Color(1f, 1f, 1f, 0.35f);
                Handles.DrawWireDisc(center, Vector3.forward, radiusPoints * Settings.blurSize);
            }
            Handles.EndGUI();

            if (cursorHasHit && Settings.falloffShape == FalloffShape.Sphere)
            {
                var cam = view.camera;
                float pixelWorld = 2f / (Mathf.Abs(cam.projectionMatrix.m11) * cam.pixelHeight);
                float depth = cam.orthographic ? 1f : Mathf.Max(1e-4f, -cam.worldToCameraMatrix.MultiplyPoint3x4(cursorHit.point).z);
                Handles.color = new Color(ring.r, ring.g, ring.b, 0.5f);
                Handles.DrawWireDisc(cursorHit.point, cursorHit.normal, tool.radius * pixelWorld * depth);
            }
        }

        static float Pressure(Event e) => e.pointerType == PointerType.Pen ? Mathf.Clamp01(e.pressure) : 1f;

        void BeginStroke(SceneView view, Event e)
        {
            if (!Document.ActiveLayer.visible)
            {
                view.ShowNotification(new GUIContent("The active layer is hidden"), 1.5);
                return;
            }
            Engine.PrepareCamera(view.camera, Target);
            BeginStrokeCore(Settings.tool);
            lastGui = e.mousePosition;
            DabAt(view, e.mousePosition, Pressure(e));
            distanceToNextDab = Spacing();
        }

        /// <summary>Starts a stroke on the active layer of every texture.</summary>
        internal void BeginStrokeCore(PaintTool tool)
        {
            ReleaseSnapshots();
            strokeTool = tool;
            strokeHadDabs = false;
            strokeLayers = parts.Select(p => p.Document.ActiveLayer).ToArray();
            if (BrushSettings.IsStrokeBuffered(tool))
            {
                var kind = tool == PaintTool.Eraser ? StrokeKind.Erase : StrokeKind.Paint;
                foreach (var part in parts) part.Document.BeginStroke(kind, Settings.color, Settings.For(tool).strength);
            }
            else
            {
                filterSnapshots = strokeLayers.Select(l => RTUtil.Duplicate(l.texture, l.texture.name)).ToArray();
                if (tool == PaintTool.ColorBlend)
                    for (int i = 0; i < parts.Count; i++) parts[i].FilterSource = filterSnapshots[i];
            }
            stroking = true;
        }

        float Spacing()
        {
            var tool = Settings.For(strokeTool);
            return Mathf.Max(1f, tool.spacing * tool.radius / EditorGUIUtility.pixelsPerPoint);
        }

        void ContinueStroke(SceneView view, Event e)
        {
            if (!stroking) return;
            Vector2 p = e.mousePosition;
            Vector2 delta = p - lastGui;
            float length = delta.magnitude;
            float spacing = Spacing();
            float s = distanceToNextDab;
            float pressure = Pressure(e);
            int guard = 0;
            while (s <= length && guard++ < 4096)
            {
                DabAt(view, lastGui + delta * (s / Mathf.Max(length, 1e-5f)), pressure);
                s += spacing;
            }
            distanceToNextDab = s - length;
            lastGui = p;
        }

        void DabAt(SceneView view, Vector2 guiPoint, float pressure)
        {
            var tool = Settings.For(strokeTool);
            var input = new DabInput
            {
                screenPixel = HandleUtility.GUIPointToScreenPixelCoordinate(guiPoint),
                radius = tool.radius * (Settings.pressureSize ? Mathf.Lerp(0.1f, 1f, pressure) : 1f),
                strength = Settings.pressureStrength ? pressure : 1f
            };
            if (Raycast(HandleUtility.GUIPointToWorldRay(guiPoint), out var hit, out _))
            {
                input.hasHit = true;
                input.hitPoint = hit.point;
                input.hitNormal = hit.normal;
            }
            ApplyDab(view.camera, input);
        }

        /// <summary>One dab of the current stroke into every texture.</summary>
        internal void ApplyDab(Camera cam, DabInput input)
        {
            if (!stroking) return;
            Engine.PrepareCamera(cam, Target);
            if (BrushSettings.IsStrokeBuffered(strokeTool))
            {
                foreach (var part in parts) Engine.StrokeDab(part.Document, part.Target, cam, Settings, strokeTool, input);
            }
            else
            {
                input.strength *= Settings.For(strokeTool).strength;
                Engine.FilterDab(parts, cam, Settings, strokeTool, input);
            }
            strokeHadDabs = true;
            previewDirty = true;
        }

        internal void EndStroke()
        {
            if (!stroking) return;
            stroking = false;
            foreach (var part in parts) part.FilterSource = null;
            string name = ObjectNames.NicifyVariableName(strokeTool.ToString());
            var steps = new List<HistoryStep>();
            if (BrushSettings.IsStrokeBuffered(strokeTool))
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    var doc = parts[i].Document;
                    if (doc.Stroke == StrokeKind.None) continue;
                    var before = doc.CommitStroke();
                    if (before != null) steps.Add(new PixelStep(name, strokeLayers[i], before));
                }
            }
            else if (filterSnapshots != null)
            {
                for (int i = 0; i < filterSnapshots.Length; i++)
                {
                    if (strokeHadDabs) steps.Add(new PixelStep(name, strokeLayers[i], filterSnapshots[i]));
                    else RTUtil.Release(filterSnapshots[i]);
                }
                filterSnapshots = null;
            }
            if (steps.Count == 1) History.Push(steps[0]);
            else if (steps.Count > 1) History.Push(new CompoundStep(name, steps.ToArray()));
            Dirty = true;
            MarkChanged();
        }

        void CancelStroke()
        {
            stroking = false;
            foreach (var part in parts) part.FilterSource = null;
            if (BrushSettings.IsStrokeBuffered(strokeTool))
            {
                foreach (var part in parts) part.Document.CancelStroke();
            }
            else if (filterSnapshots != null)
            {
                for (int i = 0; i < filterSnapshots.Length; i++)
                {
                    RTUtil.Release(ref strokeLayers[i].texture);
                    strokeLayers[i].texture = filterSnapshots[i];
                    strokeLayers[i].MarkDirty();
                }
                filterSnapshots = null;
            }
            MarkChanged();
        }

        /// <summary>Picks the brush colour from the painting under the cursor.</summary>
        public bool PickColorUnderCursor() => cursorInView && cursorHasHit && PickColor(cursorHit, cursorPart);

        public bool PickColor(PaintHit hit) => PickColor(hit, 0);

        public bool PickColor(PaintHit hit, int partIndex)
        {
            if (partIndex < 0 || partIndex >= parts.Count) return false;
            var part = parts[partIndex];
            var doc = part.Document;
            var composite = doc.GetComposite();
            var wrapped = part.Target.WrapUV(hit.uv);
            var c = RTUtil.ReadPixel(composite, Mathf.FloorToInt(wrapped.x * doc.Width), Mathf.FloorToInt(wrapped.y * doc.Height));
            c.a = 1f;
            Settings.color = c;
            Changed?.Invoke();
            return true;
        }

        // ------------------------------------------------------------------ layer operations
        // Every texture has the same layer structure; each operation applies to all of them.

        void Commit()
        {
            foreach (var part in parts) part.Document.InvalidateComposite();
            Dirty = true;
            MarkChanged();
        }

        void PushPixelSteps(string name, List<HistoryStep> steps)
        {
            if (steps.Count == 1) History.Push(steps[0]);
            else if (steps.Count > 1) History.Push(new CompoundStep(name, steps.ToArray()));
        }

        public void SetActiveLayer(int index)
        {
            foreach (var part in parts) part.Document.ActiveIndex = index;
            MarkChanged();
        }

        public void AddLayer()
        {
            var before = History.CaptureStacks();
            int activeBefore = Document.ActiveIndex;
            string name = Document.UniqueLayerName("Layer");
            foreach (var part in parts)
            {
                var layer = part.Document.CreateLayer(name, Color.clear);
                part.Document.Layers.Insert(activeBefore + 1, layer);
                part.Document.ActiveIndex = activeBefore + 1;
                History.RegisterLayer(layer);
            }
            History.PushStack("New Layer", before, activeBefore);
            Commit();
        }

        public void DuplicateLayer()
        {
            var before = History.CaptureStacks();
            int activeBefore = Document.ActiveIndex;
            foreach (var part in parts)
            {
                var source = part.Document.ActiveLayer;
                var copy = new PaintLayer(source.name, RTUtil.Duplicate(source.texture, source.texture.name)) { Props = source.Props };
                copy.name = source.name + " copy";
                part.Document.Layers.Insert(activeBefore + 1, copy);
                part.Document.ActiveIndex = activeBefore + 1;
                History.RegisterLayer(copy);
            }
            History.PushStack("Duplicate Layer", before, activeBefore);
            Commit();
        }

        public void DeleteLayer()
        {
            if (Document.Layers.Count <= 1) return;
            var before = History.CaptureStacks();
            int activeBefore = Document.ActiveIndex;
            foreach (var part in parts)
            {
                part.Document.Layers.RemoveAt(activeBefore);
                part.Document.ActiveIndex = Mathf.Min(activeBefore, part.Document.Layers.Count - 1);
            }
            History.PushStack("Delete Layer", before, activeBefore);
            Commit();
        }

        public void MoveLayer(int from, int to)
        {
            if (from == to || from < 0 || to < 0 || from >= Document.Layers.Count || to >= Document.Layers.Count) return;
            var before = History.CaptureStacks();
            int activeBefore = Document.ActiveIndex;
            foreach (var part in parts)
            {
                var layers = part.Document.Layers;
                var layer = layers[from];
                layers.RemoveAt(from);
                layers.Insert(to, layer);
                part.Document.ActiveIndex = to;
            }
            History.PushStack("Move Layer", before, activeBefore);
            Commit();
        }

        public void MergeDown()
        {
            int index = Document.ActiveIndex;
            if (index <= 0) return;
            var before = History.CaptureStacks();
            var steps = new List<HistoryStep>();
            foreach (var part in parts)
            {
                var doc = part.Document;
                var lower = doc.Layers[index - 1];
                var upper = doc.Layers[index];
                var merged = doc.RenderMerged(lower, upper);
                var oldTexture = lower.texture;
                lower.texture = merged;
                lower.MarkDirty();
                doc.Layers.RemoveAt(index);
                doc.ActiveIndex = index - 1;
                steps.Add(new PixelStep("Merge Down", lower, oldTexture));
            }
            steps.Add(History.CreateStackStep("Merge Down", before, index));
            History.Push(new CompoundStep("Merge Down", steps.ToArray()));
            Commit();
        }

        public void FillLayer(Color color)
        {
            string name = color.a <= 0f ? "Clear Layer" : "Fill Layer";
            var steps = new List<HistoryStep>();
            foreach (var part in parts)
            {
                var layer = part.Document.ActiveLayer;
                var old = layer.texture;
                layer.texture = part.Document.RenderFilled(color);
                layer.MarkDirty();
                steps.Add(new PixelStep(name, layer, old));
            }
            PushPixelSteps(name, steps);
            Commit();
        }

        /// <summary>Adds an image as a new layer of one texture (the image is laid out in that texture's UVs).</summary>
        public void ImportLayer(Texture texture, string name, int partIndex = 0)
        {
            partIndex = Mathf.Clamp(partIndex, 0, parts.Count - 1);
            var before = History.CaptureStacks();
            int activeBefore = Document.ActiveIndex;
            for (int i = 0; i < parts.Count; i++)
            {
                var doc = parts[i].Document;
                PaintLayer layer;
                if (i == partIndex)
                {
                    var raw = PaintProjectIO.LoadSourcePixels(texture);
                    var rt = raw != null ? doc.RenderImported(raw, false) : doc.RenderImported(texture, PaintProjectIO.IsSRGB(texture));
                    if (raw != null) Object.DestroyImmediate(raw);
                    layer = new PaintLayer(name, rt);
                }
                else
                {
                    layer = doc.CreateLayer(name, Color.clear);
                }
                doc.Layers.Insert(activeBefore + 1, layer);
                doc.ActiveIndex = activeBefore + 1;
                History.RegisterLayer(layer);
            }
            History.PushStack("Import Layer", before, activeBefore);
            Commit();
        }

        /// <summary>Applies layer properties to that layer in every texture, with undo; repeated edits of the same property merge.</summary>
        public void SetLayerProps(PaintLayer layer, LayerProps props, string what)
        {
            int index = -1;
            foreach (var part in parts)
            {
                index = part.Document.Layers.IndexOf(layer);
                if (index >= 0) break;
            }
            if (index < 0 || layer.Props.Equals(props)) return;
            var before = History.CaptureStacks();
            foreach (var part in parts) part.Document.Layers[index].Props = props;
            History.PushStack("Layer " + what, before, Document.ActiveIndex, what + index);
            Commit();
        }

        // ------------------------------------------------------------------ files

        public void SaveProject(string path, bool fast = false)
        {
            PaintProjectIO.Save(path, parts.Select(p => p.Document).ToList(), Meta, fast);
            if (!fast)
            {
                ProjectPath = path;
                Dirty = false;
            }
            Changed?.Invoke();
        }

        public Texture2D ExportTexture(string assetPath, bool assignToMaterials) => ExportTexture(0, assetPath, assignToMaterials);

        /// <summary>Exports one flattened texture to an asset path and optionally assigns it to its property on the painted slots.</summary>
        public Texture2D ExportTexture(int partIndex, string assetPath, bool assignToMaterials)
        {
            var part = parts[partIndex];
            var pm = part.Meta;
            string fullPath = Path.GetFullPath(assetPath);
            bool overwriting = File.Exists(fullPath);
            PaintProjectIO.ExportPng(part.Document, part.Target.PadMap, part.Target.WrapVector, fullPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            if (!overwriting || assetPath != pm.sourceTexturePath)
                PaintProjectIO.CopyImporterSettings(pm.sourceTexturePath, assetPath, part.Document.IsSRGB);

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            pm.exportPath = assetPath;
            if (assignToMaterials && texture != null && part.Target.Renderer != null)
            {
                var materials = part.Target.Renderer.sharedMaterials;
                foreach (int slot in part.Target.Slots)
                {
                    if (slot >= materials.Length || materials[slot] == null) continue;
                    var mat = materials[slot];
                    string matPath = AssetDatabase.GetAssetPath(mat);
                    if (!string.IsNullOrEmpty(matPath) && !matPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.LogWarning($"VRC Texture Painter: material '{mat.name}' is embedded in '{matPath}' and cannot be changed. Extract it first to assign the painted texture.");
                        continue;
                    }
                    Undo.RecordObject(mat, "Assign Painted Texture");
                    mat.SetTexture(pm.textureProperty, texture);
                    EditorUtility.SetDirty(mat);
                }
                AssetDatabase.SaveAssets();
            }
            Changed?.Invoke();
            return texture;
        }

        public void Rebake()
        {
            bool ok = true;
            foreach (var part in parts) ok &= part.Target.Bake();
            if (ok) MarkChanged();
        }

        public void RebuildPadding(int padding)
        {
            Meta.padding = padding;
            foreach (var part in parts) part.Target.BuildPadMap(part.Document.Width, part.Document.Height, padding);
            MarkChanged();
        }
    }
}
