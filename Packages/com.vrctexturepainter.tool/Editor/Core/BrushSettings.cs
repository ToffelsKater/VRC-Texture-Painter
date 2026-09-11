using System;
using UnityEditor;
using UnityEngine;

namespace MeshTexturePainter
{
    public enum PaintTool
    {
        HardBrush = 0,
        SoftBrush = 1,
        Blur = 2,
        ColorBlend = 3,
        Eraser = 4
    }

    public enum FalloffShape
    {
        /// <summary>Screen space circle projected onto the model (Blender's "Projected").</summary>
        Projected = 0,
        /// <summary>3D sphere around the surface point under the cursor.</summary>
        Sphere = 1
    }

    public enum ColorBlendMode
    {
        /// <summary>Blend the colours under the brush into a smooth transition.</summary>
        Transition = 0,
        /// <summary>Pull everything under the brush towards one average colour.</summary>
        Flatten = 1
    }

    public enum ColorMixSpace
    {
        /// <summary>OKLab: even, natural looking transitions.</summary>
        Perceptual = 0,
        /// <summary>Linear light: physically correct mixing, brighter midpoints.</summary>
        Linear = 1,
        /// <summary>The stored texture values, like Photoshop.</summary>
        Srgb = 2
    }

    public enum MirrorAxis
    {
        X = 0,
        Y = 1,
        Z = 2
    }

    [Serializable]
    public class ToolSettings
    {
        public float radius = 40f;      // screen pixels
        [Range(0f, 1f)] public float strength = 1f;
        [Range(0f, 1f)] public float hardness = 0.5f;
        [Range(0.01f, 1f)] public float spacing = 0.1f;

        public ToolSettings() { }

        public ToolSettings(float radius, float strength, float hardness, float spacing)
        {
            this.radius = radius;
            this.strength = strength;
            this.hardness = hardness;
            this.spacing = spacing;
        }
    }

    [Serializable]
    public class BrushSettings
    {
        const string PrefsKey = "MeshTexturePainter.BrushSettings";

        public PaintTool tool = PaintTool.SoftBrush;
        public Color color = Color.white;
        public Color secondaryColor = Color.black;

        public ToolSettings hard = new ToolSettings(20f, 1f, 1f, 0.08f);
        public ToolSettings soft = new ToolSettings(40f, 1f, 0f, 0.08f);
        public ToolSettings blur = new ToolSettings(40f, 0.5f, 0f, 0.15f);
        public ToolSettings blend = new ToolSettings(40f, 0.5f, 0.2f, 0.1f);
        public ToolSettings eraser = new ToolSettings(40f, 1f, 0.5f, 0.08f);

        /// <summary>Blur kernel size as a fraction of the brush radius.</summary>
        [Range(0.02f, 1f)] public float blurSize = 0.25f;
        /// <summary>Blur and color blend also change transparency.</summary>
        public bool affectAlpha;

        public ColorBlendMode blendMode = ColorBlendMode.Transition;
        /// <summary>Width of the colour transition as a fraction of the brush radius.</summary>
        [Range(0.05f, 1f)] public float blendWidth = 0.6f;
        /// <summary>Colour space blur and color blend mix colours in.</summary>
        public ColorMixSpace mixSpace = ColorMixSpace.Perceptual;

        public FalloffShape falloffShape = FalloffShape.Projected;
        public bool occlusion = true;
        public bool backfaceCulling = true;
        public bool normalFalloff = true;
        [Range(10f, 90f)] public float normalAngle = 80f;

        public bool pressureSize;
        public bool pressureStrength = true;

        public bool mirror;
        public MirrorAxis mirrorAxis = MirrorAxis.X;

        public ToolSettings Current => For(tool);

        public ToolSettings For(PaintTool t)
        {
            switch (t)
            {
                case PaintTool.HardBrush: return hard;
                case PaintTool.SoftBrush: return soft;
                case PaintTool.Blur: return blur;
                case PaintTool.ColorBlend: return blend;
                default: return eraser;
            }
        }

        public static bool UsesColor(PaintTool t) => t == PaintTool.HardBrush || t == PaintTool.SoftBrush;
        public static bool UsesHardness(PaintTool t) => t != PaintTool.HardBrush;
        public static bool IsStrokeBuffered(PaintTool t) => t == PaintTool.HardBrush || t == PaintTool.SoftBrush || t == PaintTool.Eraser;

        public static BrushSettings Load()
        {
            var settings = new BrushSettings();
            var json = EditorPrefs.GetString(PrefsKey, null);
            if (!string.IsNullOrEmpty(json))
            {
                try { JsonUtility.FromJsonOverwrite(json, settings); }
                catch (Exception) { settings = new BrushSettings(); }
            }
            return settings;
        }

        public void Save() => EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(this));
    }
}
