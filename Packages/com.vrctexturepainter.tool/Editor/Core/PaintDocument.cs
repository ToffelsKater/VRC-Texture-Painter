using System;
using System.Collections.Generic;
using UnityEngine;

namespace MeshTexturePainter
{
    internal enum StrokeKind
    {
        None = 0,
        Paint = 1,
        Erase = 2,
        /// <summary>Mask painting: only the stroke's channels move towards its colour.</summary>
        Channel = 3
    }

    /// <summary>
    /// The layer stack of one texture. Layers are stored bottom to top. Painting
    /// strokes accumulate into a stroke mask and are only baked into the layer
    /// when the stroke ends, which gives Photoshop style opacity (a stroke never
    /// builds up past its opacity) and a free undo snapshot.
    /// </summary>
    internal sealed class PaintDocument : IDisposable
    {
        public readonly int Width;
        public readonly int Height;
        public bool IsSRGB = true;
        /// <summary>
        /// A mask texture: its single layer is the texture itself, painted per channel
        /// and shown without alpha compositing, so colour channels survive where alpha is 0.
        /// </summary>
        public bool IsMask;
        public readonly List<PaintLayer> Layers = new List<PaintLayer>();

        public RenderTexture StrokeMask { get; private set; }
        public StrokeKind Stroke { get; private set; }
        public Color StrokeColor { get; private set; }
        public float StrokeOpacity { get; private set; }
        /// <summary>Channel strokes: 1 for every RGBA channel that moves towards the stroke colour.</summary>
        public Vector4 StrokeChannels { get; private set; }
        /// <summary>Gradient strokes: the colour runs from StrokeColor to StrokeEndColor along the line stored in the stroke mask's green channel.</summary>
        public bool StrokeGradient { get; private set; }
        public Color StrokeEndColor { get; private set; }
        public ColorMixSpace StrokeMixSpace { get; private set; }

        int activeIndex;
        int strokeVersion;
        RenderTexture composite, compositeTemp, belowCache;
        int belowKey = int.MinValue;
        int compositeKey = int.MinValue;
        bool disposed;

        public PaintDocument(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public int ActiveIndex
        {
            get => Layers.Count == 0 ? -1 : Mathf.Clamp(activeIndex, 0, Layers.Count - 1);
            set => activeIndex = Mathf.Clamp(value, 0, Math.Max(0, Layers.Count - 1));
        }

        public PaintLayer ActiveLayer => Layers.Count == 0 ? null : Layers[ActiveIndex];

        public PaintLayer CreateLayer(string name, Color fill)
        {
            var rt = RTUtil.CreateLayerTexture(Width, Height, "MTP " + name);
            RTUtil.Clear(rt, fill);
            return new PaintLayer(name, rt);
        }

        public string UniqueLayerName(string baseName)
        {
            int n = 1;
            string candidate;
            do candidate = $"{baseName} {n++}";
            while (Layers.Exists(l => l.name == candidate));
            return candidate;
        }

        // ------------------------------------------------------------------ strokes

        public void BeginStroke(StrokeKind kind, Color color, float opacity) => BeginStroke(kind, color, opacity, Vector4.one);

        /// <param name="channels">Channel strokes: 1 for every RGBA channel that moves towards the colour.</param>
        public void BeginStroke(StrokeKind kind, Color color, float opacity, Vector4 channels)
        {
            // red: coverage, green: position along a gradient line
            if (StrokeMask == null)
                StrokeMask = RTUtil.Create("MTP Stroke Mask", Width, Height, RenderTextureFormat.RGHalf, filter: FilterMode.Point);
            RTUtil.Clear(StrokeMask, Color.clear);
            Stroke = kind;
            StrokeColor = color;
            StrokeOpacity = opacity;
            StrokeChannels = channels;
            StrokeGradient = false;
            strokeVersion++;
        }

        /// <summary>A stroke whose colour runs from `start` to `end` along the gradient line, mixed in `mixSpace`.</summary>
        public void BeginGradientStroke(StrokeKind kind, Color start, Color end, ColorMixSpace mixSpace, float opacity, Vector4 channels)
        {
            BeginStroke(kind, start, opacity, channels);
            StrokeGradient = true;
            StrokeEndColor = end;
            StrokeMixSpace = mixSpace;
        }

        /// <summary>Empties the stroke mask of the stroke in progress, before a gradient line is drawn again.</summary>
        public void ClearStrokeMask()
        {
            if (StrokeMask != null) RTUtil.Clear(StrokeMask, Color.clear);
            strokeVersion++;
        }

        public void NotifyStrokeChanged() => strokeVersion++;

        /// <summary>Bakes the stroke into the active layer. Returns the previous layer texture (now owned by the caller).</summary>
        public RenderTexture CommitStroke()
        {
            var layer = ActiveLayer;
            if (Stroke == StrokeKind.None || layer == null) return null;
            var result = RTUtil.CreateLayerTexture(Width, Height, layer.texture.name);
            var mat = PaintResources.Blit;
            SetStrokeUniforms(mat, layer, true);
            RTUtil.SetTexSize(mat, Width, Height);
            RTUtil.Blit(layer.texture, result, mat, PaintResources.BlitCommit);
            var previous = layer.texture;
            layer.texture = result;
            layer.MarkDirty();
            CancelStroke();
            return previous;
        }

        public void CancelStroke()
        {
            Stroke = StrokeKind.None;
            strokeVersion++;
        }

        void SetStrokeUniforms(Material mat, PaintLayer layer, bool withStroke)
        {
            bool active = withStroke && Stroke != StrokeKind.None;
            mat.SetFloat(Ids.StrokeMode, active ? (float)Stroke : 0f);
            mat.SetFloat(Ids.StrokeOpacity, StrokeOpacity);
            var c = StrokeColor;
            mat.SetVector(Ids.BrushColor, new Vector4(c.r, c.g, c.b, c.a));
            mat.SetVector(Ids.StrokeChannels, StrokeChannels);
            mat.SetFloat(Ids.StrokeGradient, active && StrokeGradient ? 1f : 0f);
            var e = StrokeEndColor;
            mat.SetVector(Ids.BrushColor2, new Vector4(e.r, e.g, e.b, e.a));
            mat.SetFloat(Ids.MixSpace, (float)StrokeMixSpace);
            mat.SetFloat(Ids.LockAlpha, layer.lockAlpha ? 1f : 0f);
            mat.SetTexture(Ids.StrokeMask, StrokeMask != null ? (Texture)StrokeMask : Texture2D.blackTexture);
        }

        // ------------------------------------------------------------------ compositing

        int Key(int from, int to, bool includeStroke)
        {
            unchecked
            {
                int h = 17 + from * 7919 + to * 104729;
                for (int i = from; i < to; i++)
                {
                    var l = Layers[i];
                    h = h * 31 + l.id;
                    h = h * 31 + l.contentVersion;
                    h = h * 31 + l.Props.GetHashCode();
                    h = h * 31 + (l.texture != null ? l.texture.GetInstanceID() : 0);
                }
                if (includeStroke)
                {
                    h = h * 31 + strokeVersion;
                    h = h * 31 + ActiveIndex;
                }
                return h;
            }
        }

        public void InvalidateComposite()
        {
            compositeKey = int.MinValue;
            belowKey = int.MinValue;
        }

        void EnsureBuffers()
        {
            if (composite != null) return;
            composite = RTUtil.CreateLayerTexture(Width, Height, "MTP Composite");
            compositeTemp = RTUtil.CreateLayerTexture(Width, Height, "MTP Composite Temp");
            belowCache = RTUtil.CreateLayerTexture(Width, Height, "MTP Below Cache");
        }

        /// <summary>Flattened image of all visible layers including the stroke in progress (not seam padded).</summary>
        public RenderTexture GetComposite()
        {
            int key = Key(0, Layers.Count, true);
            if (key == compositeKey && composite != null) return composite;
            EnsureBuffers();

            if (IsMask)
            {
                // A mask is its one layer with the pending stroke applied. Alpha compositing
                // would drop the colour channels wherever the mask's alpha is 0.
                var layer = ActiveLayer;
                var commit = PaintResources.Blit;
                SetStrokeUniforms(commit, layer, true);
                RTUtil.SetTexSize(commit, Width, Height);
                RTUtil.Blit(layer.texture, composite, commit, PaintResources.BlitCommit);
                compositeKey = key;
                return composite;
            }

            int active = Math.Max(0, ActiveIndex);
            int below = Key(0, active, false);
            if (below != belowKey)
            {
                var r = ComposeRange(0, active, null);
                Graphics.CopyTexture(r, belowCache);
                belowKey = below;
            }

            var result = ComposeRange(active, Layers.Count, belowCache);
            if (result != composite)
            {
                compositeTemp = composite;
                composite = result;
            }
            compositeKey = key;
            return composite;
        }

        RenderTexture ComposeRange(int from, int to, RenderTexture start)
        {
            RenderTexture src = composite, dst = compositeTemp;
            if (start == null) RTUtil.Clear(src, Color.clear);
            else Graphics.CopyTexture(start, src);

            var mat = PaintResources.Blit;
            RTUtil.SetTexSize(mat, Width, Height);
            for (int i = from; i < to; i++)
            {
                var layer = Layers[i];
                if (!layer.visible || layer.opacity <= 0f) continue;
                mat.SetTexture(Ids.Layer, layer.texture);
                mat.SetFloat(Ids.Opacity, layer.opacity);
                mat.SetFloat(Ids.BlendMode, (float)layer.blendMode);
                SetStrokeUniforms(mat, layer, i == ActiveIndex);
                RTUtil.Blit(src, dst, mat, PaintResources.BlitComposite);
                (src, dst) = (dst, src);
            }
            return src;
        }

        /// <summary>Writes the composite with seam padding into dest (sRGB conversion for sRGB previews).</summary>
        public void Present(RenderTexture padMap, Vector4 wrapMode, RenderTexture dest)
        {
            var source = GetComposite();
            var mat = PaintResources.Blit;
            RTUtil.SetTexSize(mat, Width, Height);
            mat.SetTexture(Ids.PadMap, padMap);
            mat.SetVector(Ids.WrapMode, wrapMode);
            mat.SetFloat(Ids.ToLinear, dest.sRGB && RTUtil.LinearProject ? 1f : 0f);
            RTUtil.Blit(source, dest, mat, PaintResources.BlitPresent);
        }

        /// <summary>Renders `upper` onto a copy of `lower` and returns the merged texture.</summary>
        public RenderTexture RenderMerged(PaintLayer lower, PaintLayer upper)
        {
            var result = RTUtil.CreateLayerTexture(Width, Height, lower.texture.name);
            if (!upper.visible || upper.opacity <= 0f)
            {
                Graphics.CopyTexture(lower.texture, result);
                return result;
            }
            var mat = PaintResources.Blit;
            RTUtil.SetTexSize(mat, Width, Height);
            mat.SetTexture(Ids.Layer, upper.texture);
            mat.SetFloat(Ids.Opacity, upper.opacity);
            mat.SetFloat(Ids.BlendMode, (float)upper.blendMode);
            SetStrokeUniforms(mat, upper, false);
            RTUtil.Blit(lower.texture, result, mat, PaintResources.BlitComposite);
            return result;
        }

        public RenderTexture RenderFilled(Color color)
        {
            var result = RTUtil.CreateLayerTexture(Width, Height, "MTP Layer");
            RTUtil.Clear(result, color);
            return result;
        }

        /// <summary>Sets the given channels of a layer texture to a value (or inverts them) and returns the result.</summary>
        public RenderTexture RenderChannelOperation(RenderTexture source, Vector4 channels, bool invert, float value)
        {
            var result = RTUtil.CreateLayerTexture(Width, Height, source.name);
            var mat = PaintResources.Blit;
            RTUtil.SetTexSize(mat, Width, Height);
            mat.SetVector(Ids.ChannelMask, channels);
            mat.SetFloat(Ids.ChannelInvert, invert ? 1f : 0f);
            mat.SetVector(Ids.FillColor, new Vector4(value, value, value, value));
            RTUtil.Blit(source, result, mat, PaintResources.BlitChannels);
            return result;
        }

        /// <summary>Resamples any texture into a new layer texture holding raw (gamma encoded) values.</summary>
        public RenderTexture RenderImported(Texture source, bool sourceIsSRGBSampled)
        {
            var result = RTUtil.CreateLayerTexture(Width, Height, "MTP Layer");
            var mat = PaintResources.Blit;
            mat.SetFloat(Ids.ToGamma, sourceIsSRGBSampled && RTUtil.LinearProject ? 1f : 0f);
            RTUtil.Blit(source, result, mat, PaintResources.BlitImport);
            return result;
        }

        public long EstimateBytes()
        {
            long total = RTUtil.EstimateBytes(composite) * 3 + RTUtil.EstimateBytes(StrokeMask);
            foreach (var l in Layers) total += RTUtil.EstimateBytes(l.texture);
            return total;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var l in Layers) l.Release();
            Layers.Clear();
            RTUtil.Release(ref composite);
            RTUtil.Release(ref compositeTemp);
            RTUtil.Release(ref belowCache);
            var mask = StrokeMask;
            RTUtil.Release(ref mask);
            StrokeMask = null;
        }
    }
}
