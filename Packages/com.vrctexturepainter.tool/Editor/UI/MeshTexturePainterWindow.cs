using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeshTexturePainter
{
    internal sealed class MeshTexturePainterWindow : EditorWindow
    {
        const string RestoreKey = "MeshTexturePainter.RestorePath";
        const string RestoreProjectKey = "MeshTexturePainter.RestoreProjectPath";
        const string RestoreDirtyKey = "MeshTexturePainter.RestoreDirty";
        static readonly int[] Resolutions = { 0, 512, 1024, 2048, 4096, 8192 };
        static readonly string[] ResolutionNames = { "Source size of each texture", "512", "1024", "2048", "4096", "8192" };
        static readonly string[] ToolNames = { "Hard", "Soft", "Blur", "Blend", "Eraser" };
        static readonly TextureWrapMode[] WrapOptions = { TextureWrapMode.Repeat, TextureWrapMode.Repeat, TextureWrapMode.Clamp, TextureWrapMode.Mirror, TextureWrapMode.MirrorOnce };

        [Serializable]
        sealed class PartSetup
        {
            public string property = "_MainTex";
            public int uv;
        }

        PaintSession session;
        BrushSettings settings;

        // setup
        Renderer renderer;
        int slotMask = 1;
        readonly List<PartSetup> partSetups = new List<PartSetup> { new PartSetup() };
        int resolutionIndex;
        int padding = 16;
        int wrapOverride; // 0 = from each texture, otherwise index into WrapOptions
        string coverageKey;
        string coverageHint;

        Vector2 scroll;
        bool strokeFoldout = true;
        bool helpFoldout;
        bool assignOnExport = true;
        ReorderableList layerList;
        readonly List<PaintLayer> displayLayers = new List<PaintLayer>();
        Material thumbnailMaterial;
        string lastError;

        [MenuItem("Tools/VRC Texture Painter")]
        static void OpenWindow() => GetWindow<MeshTexturePainterWindow>();

        [MenuItem("CONTEXT/Renderer/Paint Texture (VRC Texture Painter)")]
        static void OpenFromContext(MenuCommand command)
        {
            var window = GetWindow<MeshTexturePainterWindow>();
            window.SetRenderer(command.context as Renderer);
        }

        // ------------------------------------------------------------------ lifecycle

        void OnEnable()
        {
            titleContent = new GUIContent("Texture Painter", EditorGUIUtility.IconContent("d_Grid.PaintTool").image);
            settings = BrushSettings.Load();
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            if (!EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.delayCall += TryRestore;
        }

        void OnDisable()
        {
            settings?.Save();
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            if (session != null) Autosave();
            if (thumbnailMaterial != null) DestroyImmediate(thumbnailMaterial);
        }

        void OnDestroy()
        {
            // closing the window: the autosave in OnDisable already ran, forget it
            SessionState.EraseString(RestoreKey);
        }

        static string AutosavePath => Path.GetFullPath(Path.Combine("Library", "MeshTexturePainter", "Autosave." + PaintProjectIO.Extension));

        void OnBeforeAssemblyReload()
        {
            if (session != null) Autosave();
        }

        void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode && session != null) Autosave();
            if (change == PlayModeStateChange.EnteredEditMode) TryRestore();
        }

        /// <summary>Unity reloads scripts and enters play mode by throwing away GPU state; keep the work.</summary>
        void Autosave()
        {
            try
            {
                bool dirty = session.Dirty;
                string projectPath = session.ProjectPath;
                session.SaveProject(AutosavePath, fast: true);
                SessionState.SetString(RestoreKey, AutosavePath);
                SessionState.SetString(RestoreProjectKey, projectPath ?? "");
                SessionState.SetBool(RestoreDirtyKey, dirty);
            }
            catch (Exception e)
            {
                Debug.LogError("VRC Texture Painter: autosave failed: " + e);
            }
            CloseSession();
        }

        void TryRestore()
        {
            string path = SessionState.GetString(RestoreKey, null);
            if (string.IsNullOrEmpty(path) || session != null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            SessionState.EraseString(RestoreKey);
            if (!File.Exists(path)) return;
            var restored = PaintSession.Open(path, null, settings, out string error);
            if (restored == null)
            {
                Debug.LogWarning("VRC Texture Painter: could not restore the painting session (" + error + "). It is kept at " + path);
                return;
            }
            string projectPath = SessionState.GetString(RestoreProjectKey, "");
            restored.ProjectPath = string.IsNullOrEmpty(projectPath) ? null : projectPath;
            restored.Dirty = SessionState.GetBool(RestoreDirtyKey, true);
            AttachSession(restored);
        }

        void AttachSession(PaintSession newSession)
        {
            session = newSession;
            renderer = session.Target.Renderer;
            session.Changed += Repaint;
            session.Activate();
            layerList = null;
            Repaint();
        }

        void CloseSession()
        {
            if (session == null) return;
            session.Changed -= Repaint;
            session.Dispose();
            session = null;
            layerList = null;
            Repaint();
        }

        bool ConfirmCloseSession()
        {
            if (session == null || !session.Dirty) return true;
            int choice = EditorUtility.DisplayDialogComplex("VRC Texture Painter",
                "The painting has unsaved changes. Save the project first?", "Save", "Cancel", "Discard");
            if (choice == 1) return false;
            if (choice == 0) return SaveProject(false);
            return true;
        }

        void SetRenderer(Renderer r)
        {
            if (r == renderer) return;
            renderer = r;
            slotMask = 1;
            wrapOverride = 0;
            coverageKey = null;
            var mat = r != null ? r.sharedMaterials.FirstOrDefault(m => m != null) : null;
            var props = TextureProperties(mat);
            string main = props.Contains("_MainTex") ? "_MainTex" : props.FirstOrDefault() ?? "_MainTex";
            partSetups.Clear();
            partSetups.Add(new PartSetup { property = main, uv = ShaderUVChannel(mat, main) });
            Repaint();
        }

        // ------------------------------------------------------------------ GUI

        void OnGUI()
        {
            if (settings == null) settings = BrushSettings.Load();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            try
            {
                if (session == null) DrawSetup();
                else DrawSession();
                if (!string.IsNullOrEmpty(lastError)) EditorGUILayout.HelpBox(lastError, MessageType.Error);
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (Exception e)
            {
                lastError = e.Message;
                Debug.LogException(e);
            }
            EditorGUILayout.EndScrollView();
        }

        static List<string> TextureProperties(Material mat)
        {
            var list = new List<string>();
            if (mat == null || mat.shader == null) return list;
            var shader = mat.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                if (shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) continue;
                if ((shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0 && shader.GetPropertyName(i) != "_MainTex") continue;
                list.Add(shader.GetPropertyName(i));
            }
            return list;
        }

        /// <summary>Poiyomi stores the UV channel of each texture in a "&lt;property&gt;UV" float (0-3 = UV0-UV3).</summary>
        static int ShaderUVChannel(Material mat, string property)
        {
            if (mat == null || !mat.HasProperty(property + "UV")) return 0;
            int uv = Mathf.RoundToInt(mat.GetFloat(property + "UV"));
            return uv >= 0 && uv <= 3 ? uv : 0;
        }

        /// <summary>Poiyomi modes above UV3 (panosphere, world or local position, polar...) are not UV maps.</summary>
        static bool UsesNonUVMapping(Material mat, string property) =>
            mat != null && mat.HasProperty(property + "UV") && Mathf.RoundToInt(mat.GetFloat(property + "UV")) > 3;

        /// <summary>Enabled Poiyomi decals with a texture: typical second textures on another UV channel.</summary>
        static IEnumerable<(string property, int uv, string label)> DecalSuggestions(Material mat)
        {
            if (mat == null) yield break;
            string[] suffixes = { "", "1", "2", "3" };
            for (int n = 0; n < suffixes.Length; n++)
            {
                string property = "_DecalTexture" + suffixes[n];
                if (!mat.HasProperty(property)) continue;
                var texture = mat.GetTexture(property);
                string enabled = "_DecalEnabled" + suffixes[n];
                if (texture == null || (mat.HasProperty(enabled) && mat.GetFloat(enabled) < 0.5f)) continue;
                yield return (property, ShaderUVChannel(mat, property), $"Decal {n}: {texture.name}");
            }
        }

        static string DecalTransformWarning(Material mat, string property)
        {
            if (mat == null || !property.StartsWith("_DecalTexture", StringComparison.Ordinal)) return null;
            string n = property.Substring("_DecalTexture".Length);
            bool moved = mat.HasProperty("_DecalPosition" + n) && (Vector2)mat.GetVector("_DecalPosition" + n) != new Vector2(0.5f, 0.5f);
            bool scaled = mat.HasProperty("_DecalScale" + n) && (Vector2)mat.GetVector("_DecalScale" + n) != Vector2.one;
            bool rotated = mat.HasProperty("_DecalRotation" + n) && Mathf.Abs(mat.GetFloat("_DecalRotation" + n)) > 1e-4f;
            bool offset = mat.HasProperty("_DecalSideOffset" + n) && mat.GetVector("_DecalSideOffset" + n) != Vector4.zero;
            bool tiled = mat.HasProperty(property) && (mat.GetTextureScale(property) != Vector2.one || mat.GetTextureOffset(property) != Vector2.zero);
            return moved || scaled || rotated || offset || tiled
                ? "This decal is moved, scaled, rotated or tiled in the material. The painter paints it on its raw UVs, so strokes would land offset. Reset the decal to position 0.5 / 0.5, scale 1 and rotation 0 to paint it in place."
                : null;
        }

        static string[] UVChannelNames(Mesh mesh)
        {
            var list = new List<Vector2>();
            var names = new string[8];
            for (int ch = 0; ch < 8; ch++)
            {
                mesh.GetUVs(ch, list);
                names[ch] = list.Count > 0 ? $"UV{ch}" : $"UV{ch} (empty)";
            }
            return names;
        }

        /// <summary>Warns when triangles of the selected slots would not receive paint, and which UV channel they use instead.</summary>
        string CoverageHint(Mesh mesh, int[] slots)
        {
            string key = $"{mesh.GetInstanceID()}|{string.Join(",", slots)}|{string.Join(",", partSetups.Select(p => p.uv))}";
            if (key == coverageKey) return coverageHint;
            coverageKey = key;
            coverageHint = null;

            var uvs = new List<Vector2>[8];
            for (int ch = 0; ch < 8; ch++)
            {
                var list = new List<Vector2>();
                mesh.GetUVs(ch, list);
                uvs[ch] = list.Count == mesh.vertexCount ? list : null;
            }
            var painted = partSetups.Select(p => p.uv).Distinct().ToArray();
            int uncovered = 0;
            var elsewhere = new int[8];
            var tris = new List<int>();
            foreach (int sub in slots.Select(s => PaintTarget.SubmeshForSlot(mesh, s)).Distinct())
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                mesh.GetTriangles(tris, sub);
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    bool Valid(int ch) => uvs[ch] != null && !PaintTarget.IsCollapsed(uvs[ch][a], uvs[ch][b], uvs[ch][c]);
                    if (painted.Any(Valid)) continue;
                    uncovered++;
                    for (int ch = 0; ch < 8; ch++)
                        if (!painted.Contains(ch) && Valid(ch)) elsewhere[ch]++;
                }
            }
            if (uncovered == 0) return null;
            int best = Array.IndexOf(elsewhere, elsewhere.Max());
            string channels = string.Join(" / ", painted.Select(c => "UV" + c));
            coverageHint = elsewhere[best] > 0
                ? $"{uncovered:N0} triangles have no {channels} layout, so they would not receive paint. {elsewhere[best]:N0} of them are laid out in UV{best}: add the texture your shader shows through UV{best} (for example a decal) to paint them too."
                : $"{uncovered:N0} triangles have collapsed UVs and cannot be painted.";
            return coverageHint;
        }

        void DrawSetup()
        {
            EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                var r = (Renderer)EditorGUILayout.ObjectField("Renderer", renderer, typeof(Renderer), true);
                if (r != renderer) SetRenderer(r is SkinnedMeshRenderer || r is MeshRenderer ? r : null);
                if (GUILayout.Button("Use Selection", GUILayout.Width(100)))
                {
                    var sel = Selection.activeGameObject;
                    SetRenderer(sel != null ? sel.GetComponent<Renderer>() : null);
                }
            }

            if (renderer == null)
            {
                EditorGUILayout.HelpBox("Pick a Skinned Mesh Renderer or Mesh Renderer to paint on. You can also right click a renderer component and choose Paint Texture.", MessageType.Info);
                DrawOpenProjectButton();
                return;
            }

            var mesh = PaintTarget.GetSourceMesh(renderer);
            if (mesh == null)
            {
                EditorGUILayout.HelpBox("This renderer has no mesh.", MessageType.Warning);
                return;
            }

            var materials = renderer.sharedMaterials;
            var slotNames = materials.Select((m, i) => $"{i}: {(m != null ? m.name : "(none)")}").ToArray();
            EditorGUI.BeginChangeCheck();
            slotMask = EditorGUILayout.MaskField(new GUIContent("Material Slots", "Every selected slot shows the painted textures. Pick the slots that use the material you paint."), slotMask, slotNames);
            if (EditorGUI.EndChangeCheck()) coverageKey = null;
            var slots = Enumerable.Range(0, materials.Length).Where(i => (slotMask & (1 << i)) != 0).ToArray();
            var firstMat = slots.Select(i => materials[i]).FirstOrDefault(m => m != null);
            var props = TextureProperties(firstMat);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(new GUIContent("Textures",
                "Every texture painted at once, with the UV channel the shader reads it with. Each triangle is painted into the first texture whose UV channel has a layout for it, e.g. the body texture on UV0 and a face decal on UV2."), EditorStyles.boldLabel);
            var uvNames = UVChannelNames(mesh);
            int remove = -1;
            for (int i = 0; i < partSetups.Count; i++)
            {
                var ps = partSetups[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (props.Count > 0)
                    {
                        int index = props.IndexOf(ps.property);
                        EditorGUI.BeginChangeCheck();
                        index = EditorGUILayout.Popup(Mathf.Max(0, index), props.ToArray());
                        if (EditorGUI.EndChangeCheck() || !props.Contains(ps.property))
                        {
                            ps.property = props[index];
                            ps.uv = ShaderUVChannel(firstMat, ps.property);
                            coverageKey = null;
                        }
                    }
                    else
                    {
                        ps.property = EditorGUILayout.TextField(ps.property);
                    }
                    EditorGUI.BeginChangeCheck();
                    ps.uv = EditorGUILayout.Popup(ps.uv, uvNames, GUILayout.Width(110));
                    if (EditorGUI.EndChangeCheck()) coverageKey = null;
                    using (new EditorGUI.DisabledScope(partSetups.Count <= 1))
                        if (GUILayout.Button(new GUIContent("✕", "Remove this texture"), GUILayout.Width(24))) remove = i;
                }
                var texture = firstMat != null && firstMat.HasProperty(ps.property) ? firstMat.GetTexture(ps.property) : null;
                var size = PaintProjectIO.SourceSize(texture);
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField(texture != null ? $"{texture.name}  ({size.x}x{size.y})" : "No texture assigned, starts white", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
                if (UsesNonUVMapping(firstMat, ps.property))
                    EditorGUILayout.HelpBox("The material maps this texture without a UV map (panosphere, world position...). It cannot be painted.", MessageType.Warning);
                string decalWarning = DecalTransformWarning(firstMat, ps.property);
                if (decalWarning != null) EditorGUILayout.HelpBox(decalWarning, MessageType.Warning);
            }
            if (remove >= 0)
            {
                partSetups.RemoveAt(remove);
                coverageKey = null;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Add Texture", "Paint another texture of this material at the same time, e.g. one laid out in a different UV channel.")))
                {
                    string next = props.FirstOrDefault(p => partSetups.All(s => s.property != p)) ?? "_MainTex";
                    partSetups.Add(new PartSetup { property = next, uv = ShaderUVChannel(firstMat, next) });
                    coverageKey = null;
                }
                GUILayout.FlexibleSpace();
            }
            foreach (var suggestion in DecalSuggestions(firstMat))
            {
                if (partSetups.Any(s => s.property == suggestion.property)) continue;
                if (GUILayout.Button(new GUIContent($"+ {suggestion.label}  (UV{suggestion.uv})", "Poiyomi decal found on this material. Add it to paint its texture too."), EditorStyles.miniButton))
                {
                    partSetups.Add(new PartSetup { property = suggestion.property, uv = suggestion.uv });
                    coverageKey = null;
                }
            }

            string hint = slots.Length > 0 ? CoverageHint(mesh, slots) : null;
            if (hint != null) EditorGUILayout.HelpBox(hint, MessageType.Info);
            bool duplicate = partSetups.Select(p => p.property).Distinct().Count() != partSetups.Count;
            if (duplicate) EditorGUILayout.HelpBox("Each texture property can only be added once.", MessageType.Warning);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);
            wrapOverride = EditorGUILayout.Popup(
                new GUIContent("UV Wrap", "Where UVs outside 0..1 are painted. Repeat wraps them into the texture (UV tile discard, tiling), Mirror reflects them, Clamp only paints inside 0..1."),
                wrapOverride, new[] { "From each texture", "Repeat", "Clamp", "Mirror", "Mirror Once" });
            resolutionIndex = EditorGUILayout.Popup("Resolution", resolutionIndex, ResolutionNames);
            padding = EditorGUILayout.IntSlider(new GUIContent("Seam Padding", "How many texels painted colour bleeds past UV island borders so seams and mip maps stay clean."), padding, 0, 64);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(slots.Length == 0 || duplicate))
            {
                if (GUILayout.Button("Start Painting", GUILayout.Height(30)))
                {
                    int fixedSize = Resolutions[resolutionIndex];
                    TextureWrapMode? wrap = wrapOverride == 0 ? (TextureWrapMode?)null : WrapOptions[wrapOverride];
                    var specs = partSetups.Select(p => new PartSpec
                    {
                        textureProperty = p.property,
                        uvChannel = p.uv,
                        width = fixedSize,
                        height = fixedSize,
                        wrapU = wrap,
                        wrapV = wrap
                    }).ToList();
                    EditorUtility.DisplayProgressBar("VRC Texture Painter", "Preparing mesh and textures...", 0.5f);
                    try
                    {
                        var created = PaintSession.Create(renderer, slots, specs, padding, settings, out string error);
                        lastError = error;
                        if (created != null) AttachSession(created);
                    }
                    finally
                    {
                        EditorUtility.ClearProgressBar();
                    }
                    GUIUtility.ExitGUI();
                }
            }
            DrawOpenProjectButton();
        }

        void DrawOpenProjectButton()
        {
            if (!GUILayout.Button("Open Project...")) return;
            string path = EditorUtility.OpenFilePanel("Open Paint Project", "Assets", PaintProjectIO.Extension);
            if (string.IsNullOrEmpty(path)) GUIUtility.ExitGUI();
            var opened = PaintSession.Open(path, renderer, settings, out string error);
            lastError = error;
            if (opened != null) AttachSession(opened);
            GUIUtility.ExitGUI();
        }

        void DrawSession()
        {
            var target = session.Target;

            // header
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                string name = target.Renderer != null ? target.Renderer.name : "(missing)";
                int tris = session.Parts.Sum(p => p.Target.TriangleCount);
                string textures = session.Parts.Count == 1 ? $"{session.Document.Width}x{session.Document.Height}" : $"{session.Parts.Count} textures";
                GUILayout.Label($"{name}  ·  {textures}  ·  {tris:N0} tris{(session.Dirty ? "  ·  unsaved" : "")}", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Save", EditorStyles.toolbarButton)) SaveProject(false);
                if (GUILayout.Button("Save As", EditorStyles.toolbarButton)) SaveProject(true);
                if (GUILayout.Button("Stop", EditorStyles.toolbarButton))
                {
                    if (ConfirmCloseSession()) CloseSession();
                    GUIUtility.ExitGUI();
                }
            }

            if (target.Renderer == null)
            {
                EditorGUILayout.HelpBox("The painted renderer is gone (scene closed or object deleted). Save the project to keep your layers.", MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(4);
            DrawTools();
            EditorGUILayout.Space(6);
            DrawLayers();
            EditorGUILayout.Space(6);
            DrawOutput();
            EditorGUILayout.Space(6);
            DrawHelp();
        }

        void DrawTools()
        {
            EditorGUILayout.LabelField("Brush", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            settings.tool = (PaintTool)GUILayout.Toolbar((int)settings.tool, ToolNames, GUILayout.Height(24));
            var tool = settings.Current;

            if (BrushSettings.UsesColor(settings.tool))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    settings.color = EditorGUILayout.ColorField(new GUIContent("Color"), settings.color, true, true, false);
                    settings.secondaryColor = EditorGUILayout.ColorField(GUIContent.none, settings.secondaryColor, true, true, false, GUILayout.Width(50));
                    if (GUILayout.Button(new GUIContent("⇄", "Swap colours"), GUILayout.Width(24)))
                        (settings.color, settings.secondaryColor) = (settings.secondaryColor, settings.color);
                }
            }

            tool.radius = EditorGUILayout.Slider(new GUIContent("Radius (px)", "[ and ] or Ctrl + scroll"), tool.radius, 1f, 500f);
            string strengthLabel = BrushSettings.IsStrokeBuffered(settings.tool) ? "Opacity" : "Strength";
            tool.strength = EditorGUILayout.Slider(new GUIContent(strengthLabel, "Shift + [ and ] or Ctrl + Shift + scroll"), tool.strength, 0f, 1f);
            if (BrushSettings.UsesHardness(settings.tool))
                tool.hardness = EditorGUILayout.Slider("Hardness", tool.hardness, 0f, 1f);
            tool.spacing = EditorGUILayout.Slider(new GUIContent("Spacing", "Distance between dabs as a fraction of the radius"), tool.spacing, 0.02f, 1f);
            if (settings.tool == PaintTool.Blur)
                settings.blurSize = EditorGUILayout.Slider(new GUIContent("Blur Size", "Blur kernel as a fraction of the radius"), settings.blurSize, 0.02f, 1f);
            if (settings.tool == PaintTool.ColorBlend)
            {
                settings.blendMode = (ColorBlendMode)EditorGUILayout.EnumPopup(new GUIContent("Mode",
                    "Transition: blends the colours under the brush into a smooth, seamless gradient.\nFlatten: pulls everything under the brush towards one average colour."), settings.blendMode);
                if (settings.blendMode == ColorBlendMode.Transition)
                    settings.blendWidth = EditorGUILayout.Slider(new GUIContent("Blend Width", "How wide the colour transition is, relative to the brush. Small values only soften hard edges, large values make long gradients."), settings.blendWidth, 0.05f, 1f);
            }
            if (settings.tool == PaintTool.Blur || settings.tool == PaintTool.ColorBlend)
            {
                settings.mixSpace = (ColorMixSpace)EditorGUILayout.EnumPopup(new GUIContent("Mix Colors In",
                    "Perceptual: even, natural transitions without muddy midpoints (OKLab).\nLinear: physically correct light mixing, brighter midpoints.\nSrgb: the texture's stored values, like Photoshop; midpoints between saturated colours look darker."), settings.mixSpace);
                settings.affectAlpha = EditorGUILayout.Toggle("Affect Alpha", settings.affectAlpha);
            }

            strokeFoldout = EditorGUILayout.Foldout(strokeFoldout, "Stroke Options", true);
            if (strokeFoldout)
            {
                EditorGUI.indentLevel++;
                settings.falloffShape = (FalloffShape)EditorGUILayout.EnumPopup(new GUIContent("Falloff Shape", "Projected: a circle on screen, paints everything visible inside it.\nSphere: a ball around the surface under the cursor, never reaches surfaces behind."), settings.falloffShape);
                settings.occlusion = EditorGUILayout.Toggle(new GUIContent("Occlusion", "Only paint surfaces visible from the camera."), settings.occlusion);
                settings.backfaceCulling = EditorGUILayout.Toggle(new GUIContent("Backface Culling", "Do not paint faces pointing away from the camera."), settings.backfaceCulling);
                using (new EditorGUILayout.HorizontalScope())
                {
                    settings.normalFalloff = EditorGUILayout.Toggle(new GUIContent("Normal Falloff", "Fade paint on surfaces seen at a grazing angle to avoid stretched streaks."), settings.normalFalloff);
                    using (new EditorGUI.DisabledScope(!settings.normalFalloff))
                        settings.normalAngle = GUILayout.HorizontalSlider(settings.normalAngle, 10f, 90f);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    settings.mirror = EditorGUILayout.Toggle(new GUIContent("Mirror", "Also paint the mirrored position, mirrored across the avatar root."), settings.mirror);
                    using (new EditorGUI.DisabledScope(!settings.mirror))
                        settings.mirrorAxis = (MirrorAxis)EditorGUILayout.EnumPopup(settings.mirrorAxis);
                }
                settings.pressureSize = EditorGUILayout.Toggle("Pen Pressure → Size", settings.pressureSize);
                settings.pressureStrength = EditorGUILayout.Toggle("Pen Pressure → Strength", settings.pressureStrength);
                EditorGUI.indentLevel--;
            }
            if (EditorGUI.EndChangeCheck())
            {
                settings.Save();
                SceneView.RepaintAll();
            }
        }

        // ------------------------------------------------------------------ layers

        void DrawLayers()
        {
            var doc = session.Document;
            EditorGUILayout.LabelField(new GUIContent("Layers", session.Parts.Count > 1 ? "The layer stack is shared by all painted textures; the thumbnails show each texture." : null), EditorStyles.boldLabel);

            var active = doc.ActiveLayer;
            if (active != null)
            {
                var props = active.Props;
                EditorGUI.BeginChangeCheck();
                var mode = (LayerBlendMode)EditorGUILayout.EnumPopup("Blend Mode", props.blendMode);
                if (EditorGUI.EndChangeCheck())
                {
                    props.blendMode = mode;
                    session.SetLayerProps(active, props, "Blend Mode");
                }
                EditorGUI.BeginChangeCheck();
                float opacity = EditorGUILayout.Slider("Opacity", props.opacity * 100f, 0f, 100f) / 100f;
                if (EditorGUI.EndChangeCheck())
                {
                    props.opacity = opacity;
                    session.SetLayerProps(active, props, "Opacity");
                }
                EditorGUI.BeginChangeCheck();
                bool lockAlpha = EditorGUILayout.Toggle(new GUIContent("Lock Transparency", "Painting only changes colour where the layer already has pixels."), props.lockAlpha);
                if (EditorGUI.EndChangeCheck())
                {
                    props.lockAlpha = lockAlpha;
                    session.SetLayerProps(active, props, "Lock Transparency");
                }
            }

            displayLayers.Clear();
            for (int i = doc.Layers.Count - 1; i >= 0; i--) displayLayers.Add(doc.Layers[i]);
            if (layerList == null) CreateLayerList();
            layerList.list = displayLayers;
            layerList.index = doc.Layers.Count - 1 - doc.ActiveIndex;
            layerList.DoLayoutList();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("New")) session.AddLayer();
                if (GUILayout.Button("Duplicate")) session.DuplicateLayer();
                using (new EditorGUI.DisabledScope(doc.Layers.Count <= 1))
                    if (GUILayout.Button("Delete")) session.DeleteLayer();
                using (new EditorGUI.DisabledScope(doc.ActiveIndex <= 0))
                    if (GUILayout.Button("Merge Down")) session.MergeDown();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Fill", "Fill the layer with the brush colour"))) session.FillLayer(settings.color);
                if (GUILayout.Button("Clear")) session.FillLayer(Color.clear);
                if (GUILayout.Button(new GUIContent("Import Image...", "Add an image as a new layer. It must be laid out like the texture it is imported into.")))
                {
                    if (session.Parts.Count == 1)
                    {
                        PickAndImport(0);
                    }
                    else
                    {
                        var menu = new GenericMenu();
                        for (int i = 0; i < session.Parts.Count; i++)
                        {
                            int part = i;
                            menu.AddItem(new GUIContent("Into " + session.Parts[i].Label), false, () => PickAndImport(part));
                        }
                        menu.ShowAsContext();
                    }
                    GUIUtility.ExitGUI();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                var history = session.History;
                using (new EditorGUI.DisabledScope(!history.CanUndo))
                    if (GUILayout.Button(history.CanUndo ? $"Undo {history.UndoName}" : "Undo")) history.PerformUndo();
                using (new EditorGUI.DisabledScope(!history.CanRedo))
                    if (GUILayout.Button(history.CanRedo ? $"Redo {history.RedoName}" : "Redo")) history.PerformRedo();
            }
        }

        void PickAndImport(int part)
        {
            string path = EditorUtility.OpenFilePanelWithFilters("Import Image as Layer", "Assets", new[] { "Images", "png,jpg,jpeg,tga,psd,tif,tiff,exr" });
            if (!string.IsNullOrEmpty(path)) ImportImageLayer(path, part);
            Repaint();
        }

        void ImportImageLayer(string fullPath, int part)
        {
            if (session == null) return;
            string name = Path.GetFileNameWithoutExtension(fullPath);
            string projectRoot = Path.GetFullPath(".").Replace('\\', '/') + "/";
            string normalized = Path.GetFullPath(fullPath).Replace('\\', '/');
            Texture texture = null;
            Texture2D loaded = null;
            if (normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                texture = AssetDatabase.LoadAssetAtPath<Texture>(normalized.Substring(projectRoot.Length));
            if (texture == null)
            {
                loaded = PaintProjectIO.LoadImageFile(fullPath);
                texture = loaded;
            }
            if (texture == null)
            {
                lastError = "Could not load " + fullPath + ". Files outside the project must be PNG, JPG or TGA.";
                return;
            }
            session.ImportLayer(texture, name, part);
            if (loaded != null) DestroyImmediate(loaded);
        }

        void CreateLayerList()
        {
            layerList = new ReorderableList(displayLayers, typeof(PaintLayer), true, false, false, false)
            {
                elementHeight = 38,
                showDefaultBackground = true,
                drawElementCallback = DrawLayerElement,
                onSelectCallback = list => session.SetActiveLayer(session.Document.Layers.Count - 1 - list.index),
                onReorderCallbackWithDetails = (list, oldIndex, newIndex) =>
                {
                    int count = session.Document.Layers.Count;
                    session.MoveLayer(count - 1 - oldIndex, count - 1 - newIndex);
                }
            };
        }

        void DrawLayerElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (index < 0 || index >= displayLayers.Count) return;
            var layer = displayLayers[index];
            int docIndex = session.Document.Layers.Count - 1 - index;
            rect.y += 2;
            rect.height -= 4;

            var eyeRect = new Rect(rect.x, rect.y + (rect.height - 18) * 0.5f, 20, 18);
            var props = layer.Props;
            EditorGUI.BeginChangeCheck();
            bool visible = GUI.Toggle(eyeRect, props.visible, new GUIContent("", "Visible"));
            if (EditorGUI.EndChangeCheck())
            {
                props.visible = visible;
                session.SetLayerProps(layer, props, "Visibility");
            }

            float x = eyeRect.xMax + 4;
            foreach (var part in session.Parts)
            {
                var thumbRect = new Rect(x, rect.y, rect.height, rect.height);
                x = thumbRect.xMax + 2;
                if (Event.current.type != EventType.Repaint || docIndex >= part.Document.Layers.Count) continue;
                var texture = part.Document.Layers[docIndex].texture;
                if (texture == null) continue;
                if (thumbnailMaterial == null) thumbnailMaterial = new Material(PaintResources.Gui) { hideFlags = HideFlags.HideAndDontSave };
                thumbnailMaterial.SetFloat(Ids.ToLinear, RTUtil.LinearProject ? 1f : 0f);
                thumbnailMaterial.SetFloat(Ids.Checker, 4f);
                EditorGUI.DrawPreviewTexture(thumbRect, texture, thumbnailMaterial, ScaleMode.ScaleToFit);
            }

            var nameRect = new Rect(x + 4, rect.y + 2, Mathf.Max(40f, rect.xMax - x - 4), 18);
            if (isActive)
            {
                EditorGUI.BeginChangeCheck();
                string newName = EditorGUI.DelayedTextField(nameRect, props.name);
                if (EditorGUI.EndChangeCheck() && !string.IsNullOrWhiteSpace(newName))
                {
                    props.name = newName;
                    session.SetLayerProps(layer, props, "Rename");
                }
            }
            else
            {
                EditorGUI.LabelField(nameRect, props.name);
            }

            string info = $"{props.blendMode}  {Mathf.RoundToInt(props.opacity * 100)}%{(props.lockAlpha ? "  🔒" : "")}";
            EditorGUI.LabelField(new Rect(nameRect.x, nameRect.yMax, nameRect.width, 16), info, EditorStyles.miniLabel);
        }

        // ------------------------------------------------------------------ output

        void DrawOutput()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            int newPadding = EditorGUILayout.DelayedIntField(new GUIContent("Seam Padding (texels)", "Painted colour bleeds this far past UV island borders in the preview and export."), session.Meta.padding);
            if (EditorGUI.EndChangeCheck()) session.RebuildPadding(Mathf.Clamp(newPadding, 0, 256));

            assignOnExport = EditorGUILayout.Toggle(new GUIContent("Assign To Material", "After exporting, set the texture on the painted material slots."), assignOnExport);

            for (int i = 0; i < session.Parts.Count; i++)
            {
                var part = session.Parts[i];
                var pm = part.Meta;
                int partIndex = i;
                if (session.Parts.Count > 1)
                    EditorGUILayout.LabelField($"{part.Label}  ·  {part.Document.Width}x{part.Document.Height}", EditorStyles.miniBoldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Export PNG..."))
                    {
                        string folder = !string.IsNullOrEmpty(pm.exportPath) ? Path.GetDirectoryName(pm.exportPath)
                            : !string.IsNullOrEmpty(pm.sourceTexturePath) ? Path.GetDirectoryName(pm.sourceTexturePath) : "Assets";
                        string baseName = !string.IsNullOrEmpty(pm.sourceTexturePath) ? Path.GetFileNameWithoutExtension(pm.sourceTexturePath) + "_painted" : "PaintedTexture";
                        string path = EditorUtility.SaveFilePanelInProject("Export Painted Texture", baseName, "png", "Choose where to save " + part.Label, folder);
                        if (!string.IsNullOrEmpty(path)) Export(partIndex, path);
                        GUIUtility.ExitGUI();
                    }

                    bool canOverwrite = !string.IsNullOrEmpty(pm.sourceTexturePath) && pm.sourceTexturePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                    using (new EditorGUI.DisabledScope(!canOverwrite))
                    {
                        if (GUILayout.Button(new GUIContent("Overwrite Source", canOverwrite ? pm.sourceTexturePath : "Only PNG source textures can be overwritten")))
                        {
                            if (EditorUtility.DisplayDialog("Overwrite Texture", $"Replace '{pm.sourceTexturePath}' with the flattened painting? The file is written as an 8 bit PNG. This cannot be undone.", "Overwrite", "Cancel"))
                                Export(partIndex, pm.sourceTexturePath);
                            GUIUtility.ExitGUI();
                        }
                    }
                }
                if (!string.IsNullOrEmpty(pm.exportPath))
                    EditorGUILayout.LabelField("Last export", pm.exportPath, EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Re-bake Pose", "Refresh the mesh after posing, moving or changing blend shapes (also happens automatically)."))) session.Rebake();
                if (GUILayout.Button("Refresh Preview")) session.RefreshPreview();
            }
        }

        void Export(int partIndex, string assetPath)
        {
            try
            {
                EditorUtility.DisplayProgressBar("VRC Texture Painter", "Exporting texture...", 0.5f);
                var tex = session.ExportTexture(partIndex, assetPath, assignOnExport);
                if (tex != null) EditorGUIUtility.PingObject(tex);
                lastError = null;
            }
            catch (Exception e)
            {
                lastError = "Export failed: " + e.Message;
                Debug.LogException(e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        bool SaveProject(bool saveAs)
        {
            string path = session.ProjectPath;
            if (saveAs || string.IsNullOrEmpty(path))
            {
                string source = session.Parts[0].Meta.sourceTexturePath;
                string folder = !string.IsNullOrEmpty(source) ? Path.GetDirectoryName(source) : "Assets";
                string name = session.Target.Renderer != null ? session.Target.Renderer.name : "Painting";
                path = EditorUtility.SaveFilePanel("Save Paint Project", folder, name, PaintProjectIO.Extension);
                if (string.IsNullOrEmpty(path)) return false;
            }
            try
            {
                EditorUtility.DisplayProgressBar("VRC Texture Painter", "Saving project...", 0.5f);
                session.SaveProject(path);
                string projectRoot = Path.GetFullPath(".").Replace('\\', '/') + "/";
                if (path.Replace('\\', '/').StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)) AssetDatabase.Refresh();
                lastError = null;
                return true;
            }
            catch (Exception e)
            {
                lastError = "Save failed: " + e.Message;
                Debug.LogException(e);
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void DrawHelp()
        {
            helpFoldout = EditorGUILayout.Foldout(helpFoldout, "Controls", true);
            if (!helpFoldout) return;
            EditorGUILayout.HelpBox(
                "Left drag: paint    Alt + drag: orbit (as usual)\n" +
                "[ / ]: radius    Shift + [ / ]: strength\n" +
                "Ctrl + scroll: radius    Ctrl + Shift + scroll: strength\n" +
                "3 Hard   4 Soft   5 Blur   6 Blend   7 Eraser   (or numpad 1 – 5)\n" +
                "C or numpad 0: pick colour under cursor    Esc: cancel stroke\n" +
                "Numpad keys need Num Lock on\n" +
                "Right mouse + WASD / QE: fly as usual (painting keys pause meanwhile)\n" +
                "Ctrl + Z / Ctrl + Y: undo / redo (Unity undo)", MessageType.None);
        }
    }
}
