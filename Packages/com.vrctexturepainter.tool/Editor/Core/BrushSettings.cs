using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeshTexturePainter
{
    public enum PaintTool
    {
        HardBrush = 0,
        SoftBrush = 1,
        Blur = 2,
        ColorBlend = 3,
        Eraser = 4,
        /// <summary>A straight band between two points, coloured with a gradient along it.</summary>
        Gradient = 5,
        /// <summary>A brush whose dabs take the shape of a black and white texture.</summary>
        CustomBrush = 6,
        /// <summary>One black and white texture placed where you click.</summary>
        Stamp = 7
    }

    public enum TipRotation
    {
        /// <summary>Every dab at the set angle.</summary>
        Fixed = 0,
        /// <summary>Dabs turn with the direction of the stroke.</summary>
        FollowStroke = 1,
        /// <summary>Every dab turned by a random angle.</summary>
        Random = 2
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

    /// <summary>
    /// What the mask painter paints. Red, green and blue each move only their own
    /// channel, so masks painted in different channels add up in the same spot.
    /// </summary>
    public enum MaskChannel
    {
        Red = 0,
        Green = 1,
        Blue = 2,
        /// <summary>Every colour channel towards 1.</summary>
        White = 3,
        /// <summary>Every colour channel towards 0.</summary>
        Black = 4
    }

    internal static class MaskChannels
    {
        /// <summary>1 for every RGBA channel the mask colour changes. Alpha is never painted.</summary>
        public static Vector4 Weights(MaskChannel c)
        {
            switch (c)
            {
                case MaskChannel.Red: return new Vector4(1f, 0f, 0f, 0f);
                case MaskChannel.Green: return new Vector4(0f, 1f, 0f, 0f);
                case MaskChannel.Blue: return new Vector4(0f, 0f, 1f, 0f);
                default: return new Vector4(1f, 1f, 1f, 0f);
            }
        }

        public static ColorWriteMask WriteMask(MaskChannel c)
        {
            switch (c)
            {
                case MaskChannel.Red: return ColorWriteMask.Red;
                case MaskChannel.Green: return ColorWriteMask.Green;
                case MaskChannel.Blue: return ColorWriteMask.Blue;
                default: return ColorWriteMask.Red | ColorWriteMask.Green | ColorWriteMask.Blue;
            }
        }

        /// <summary>The value a brush moves the channels towards.</summary>
        public static float Value(MaskChannel c) => c == MaskChannel.Black ? 0f : 1f;

        /// <summary>Gradient of the mask painter: 1 for every colour channel of the mixed start colour (bits: 1 red, 2 green, 4 blue).</summary>
        public static Vector4 GradientWeights(int channels) =>
            new Vector4((channels & 1) != 0 ? 1f : 0f, (channels & 2) != 0 ? 1f : 0f, (channels & 4) != 0 ? 1f : 0f, 0f);

        /// <summary>The mixed start colour of a mask gradient.</summary>
        public static Color GradientDisplay(int channels)
        {
            var w = GradientWeights(channels);
            return new Color(w.x, w.y, w.z);
        }

        /// <summary>Colour of the channel in the window and the brush cursor.</summary>
        public static Color Display(MaskChannel c)
        {
            switch (c)
            {
                case MaskChannel.Red: return new Color(1f, 0.2f, 0.2f);
                case MaskChannel.Green: return new Color(0.2f, 0.9f, 0.2f);
                case MaskChannel.Blue: return new Color(0.3f, 0.5f, 1f);
                case MaskChannel.White: return Color.white;
                default: return Color.black;
            }
        }
    }

    [Serializable]
    public class ToolSettings
    {
        public float radius = 40f;      // screen pixels
        [Range(0f, 1f)] public float strength = 1f;
        [Range(0f, 1f)] public float hardness = 0.5f;
        [Range(0.01f, 1f)] public float spacing = 0.1f;

        /// <summary>Stamp and custom brush: asset GUID of the black and white texture that shapes each dab.</summary>
        public string tip = "";
        /// <summary>Stamp and custom brush: degrees the texture is turned counterclockwise on screen.</summary>
        public float angle;
        /// <summary>Stamp and custom brush: black paints instead of white.</summary>
        public bool invert;

        [NonSerialized] Texture2D tipTexture;

        /// <summary>The texture of `tip`. Textures that are not assets are kept until the next script reload.</summary>
        public Texture2D TipTexture
        {
            get
            {
                if (tipTexture == null && !string.IsNullOrEmpty(tip))
                    tipTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(tip));
                return tipTexture;
            }
            set
            {
                tipTexture = value;
                tip = value != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value)) : "";
            }
        }

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

        /// <summary>Asset GUIDs of the stamps in the package's Stamps folder: star, heart, sparkle, moon, paw, flower.</summary>
        public static readonly string[] DefaultStampGuids =
        {
            "af3981ec06f14583af1d7dfe3432ea75", "c5ae04a881234cfc9e28e83e905f2449", "ede4a4af0aa4469d82a8b3aece28f101",
            "8555b3f1fc8a4dc1a74a7382f4a04779", "6c6a34544f724b9dbccd7d01491608b0", "ca10344ca5ac460581069004d825834a"
        };

        [NonSerialized] string prefsKey = PrefsKey;

        public PaintTool tool = PaintTool.SoftBrush;
        public Color color = Color.white;
        public Color secondaryColor = Color.black;
        /// <summary>Mask painting: the channels the brushes paint.</summary>
        public MaskChannel maskChannel = MaskChannel.Red;
        /// <summary>Mask gradient: the channels mixed into the start colour, which runs to black (bits: 1 red, 2 green, 4 blue).</summary>
        public int gradientChannels = 1;

        public ToolSettings hard = new ToolSettings(20f, 1f, 1f, 0.08f);
        public ToolSettings soft = new ToolSettings(40f, 1f, 0f, 0.08f);
        public ToolSettings blur = new ToolSettings(40f, 0.5f, 0f, 0.15f);
        public ToolSettings blend = new ToolSettings(40f, 0.5f, 0.2f, 0.1f);
        public ToolSettings eraser = new ToolSettings(40f, 1f, 0.5f, 0.08f);
        /// <summary>Gradient: radius is half the width of the band, hardness its edge softness.</summary>
        public ToolSettings gradient = new ToolSettings(20f, 1f, 1f, 0.1f);
        /// <summary>Custom brush and stamp: radius is half the longer side of the texture on screen.</summary>
        public ToolSettings custom = new ToolSettings(40f, 1f, 1f, 0.25f);
        public ToolSettings stamp = new ToolSettings(100f, 1f, 1f, 1f) { tip = DefaultStampGuids[0] };

        /// <summary>Custom brush: how the dabs turn along the stroke.</summary>
        public TipRotation tipRotation = TipRotation.Fixed;
        /// <summary>Custom brush: erase in the shape of the texture instead of painting.</summary>
        public bool tipErase;

        /// <summary>Blur kernel size as a fraction of the brush radius.</summary>
        [Range(0.02f, 1f)] public float blurSize = 0.25f;
        /// <summary>Blur and color blend also change transparency.</summary>
        public bool affectAlpha;

        public ColorBlendMode blendMode = ColorBlendMode.Transition;
        /// <summary>Width of the colour transition as a fraction of the brush radius.</summary>
        [Range(0.05f, 1f)] public float blendWidth = 0.6f;
        /// <summary>Colour space blur, color blend and gradients mix colours in.</summary>
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
                case PaintTool.Gradient: return gradient;
                case PaintTool.CustomBrush: return custom;
                case PaintTool.Stamp: return stamp;
                default: return eraser;
            }
        }

        public static bool UsesColor(PaintTool t) => t == PaintTool.HardBrush || t == PaintTool.SoftBrush || t == PaintTool.Gradient || UsesTip(t);
        public static bool UsesHardness(PaintTool t) => t != PaintTool.HardBrush && !UsesTip(t);
        /// <summary>Tools whose dabs are shaped by a texture, always laid flat on the screen.</summary>
        public static bool UsesTip(PaintTool t) => t == PaintTool.CustomBrush || t == PaintTool.Stamp;
        /// <summary>Tools painted into the stroke mask and baked into the layer when the stroke ends. The gradient redraws its whole line and the stamp its one dab instead of adding dabs.</summary>
        public static bool IsStrokeBuffered(PaintTool t) => t == PaintTool.HardBrush || t == PaintTool.SoftBrush || t == PaintTool.Eraser || t == PaintTool.Gradient || UsesTip(t);
        /// <summary>The mask painter has no colour blend brush (blur covers smoothing masks) and no custom brush.</summary>
        public static bool UsableForMasks(PaintTool t) => t != PaintTool.ColorBlend && t != PaintTool.CustomBrush;

        public static BrushSettings Load() => Load(PrefsKey);

        /// <summary>Loads settings stored under their own key, so the texture and mask painters keep separate brushes.</summary>
        public static BrushSettings Load(string key)
        {
            var settings = new BrushSettings { prefsKey = key };
            var json = EditorPrefs.GetString(key, null);
            if (!string.IsNullOrEmpty(json))
            {
                try { JsonUtility.FromJsonOverwrite(json, settings); }
                catch (Exception) { settings = new BrushSettings { prefsKey = key }; }
            }
            return settings;
        }

        public void Save() => EditorPrefs.SetString(prefsKey, JsonUtility.ToJson(this));
    }
}
