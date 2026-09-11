using System;
using UnityEngine;

namespace MeshTexturePainter
{
    /// <summary>Order matches the switch in MTP_Blit.shader.</summary>
    public enum LayerBlendMode
    {
        Normal = 0,
        Darken = 1,
        Multiply = 2,
        ColorBurn = 3,
        Lighten = 4,
        Screen = 5,
        ColorDodge = 6,
        Add = 7,
        Overlay = 8,
        SoftLight = 9,
        HardLight = 10,
        Difference = 11,
        Subtract = 12
    }

    [Serializable]
    internal struct LayerProps : IEquatable<LayerProps>
    {
        public string name;
        public bool visible;
        public float opacity;
        public LayerBlendMode blendMode;
        public bool lockAlpha;

        public bool Equals(LayerProps o) =>
            name == o.name && visible == o.visible && opacity.Equals(o.opacity) && blendMode == o.blendMode && lockAlpha == o.lockAlpha;

        public override bool Equals(object obj) => obj is LayerProps o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = name != null ? name.GetHashCode() : 0;
                h = h * 31 + visible.GetHashCode();
                h = h * 31 + opacity.GetHashCode();
                h = h * 31 + (int)blendMode;
                h = h * 31 + lockAlpha.GetHashCode();
                return h;
            }
        }
    }

    /// <summary>
    /// One layer of the stack. Pixels live on the GPU as a straight alpha, 8 bit,
    /// non sRGB render texture holding the texture's own (gamma encoded) values,
    /// the same values a PNG of the texture would contain.
    /// </summary>
    internal sealed class PaintLayer
    {
        static int nextId = 1;

        public readonly int id = nextId++;
        public string name;
        public bool visible = true;
        public float opacity = 1f;
        public LayerBlendMode blendMode = LayerBlendMode.Normal;
        public bool lockAlpha;
        public RenderTexture texture;
        public int contentVersion;
        public bool released;

        public PaintLayer(string name, RenderTexture texture)
        {
            this.name = name;
            this.texture = texture;
        }

        public LayerProps Props
        {
            get => new LayerProps { name = name, visible = visible, opacity = opacity, blendMode = blendMode, lockAlpha = lockAlpha };
            set
            {
                name = value.name;
                visible = value.visible;
                opacity = value.opacity;
                blendMode = value.blendMode;
                lockAlpha = value.lockAlpha;
            }
        }

        public void MarkDirty() => contentVersion++;

        public void Release()
        {
            if (released) return;
            RTUtil.Release(ref texture);
            released = true;
        }
    }
}
