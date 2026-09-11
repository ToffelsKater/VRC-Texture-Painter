using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MeshTexturePainter
{
    internal struct LayerState
    {
        public PaintLayer layer;
        public LayerProps props;
    }

    internal abstract class HistoryStep : IDisposable
    {
        public string name;
        public int serial;
        public abstract void Undo();
        public abstract void Redo();
        public virtual long Bytes => 0;
        public virtual void CollectLayers(HashSet<PaintLayer> into) { }
        public virtual void Dispose() { }
    }

    /// <summary>Pixel change on one layer. Undo and redo swap textures, nothing is copied.</summary>
    internal sealed class PixelStep : HistoryStep
    {
        readonly PaintLayer layer;
        RenderTexture other;

        public PixelStep(string name, PaintLayer layer, RenderTexture before)
        {
            this.name = name;
            this.layer = layer;
            other = before;
        }

        void Swap()
        {
            (layer.texture, other) = (other, layer.texture);
            layer.MarkDirty();
        }

        public override void Undo() => Swap();
        public override void Redo() => Swap();
        public override long Bytes => RTUtil.EstimateBytes(other);
        public override void CollectLayers(HashSet<PaintLayer> into) => into.Add(layer);
        public override void Dispose() => RTUtil.Release(ref other);
    }

    /// <summary>Layer stack change of every texture in the session: order, additions, removals and layer properties.</summary>
    internal sealed class StackStep : HistoryStep
    {
        public PaintDocument[] docs;
        public LayerState[][] before, after;
        public int activeBefore, activeAfter;
        public string coalesceKey;
        public double time;

        public override void Undo() => Apply(before, activeBefore);
        public override void Redo() => Apply(after, activeAfter);

        void Apply(LayerState[][] states, int active)
        {
            for (int d = 0; d < docs.Length; d++)
            {
                var doc = docs[d];
                doc.Layers.Clear();
                foreach (var s in states[d])
                {
                    s.layer.Props = s.props;
                    doc.Layers.Add(s.layer);
                }
                doc.ActiveIndex = active;
                doc.InvalidateComposite();
            }
        }

        public override void CollectLayers(HashSet<PaintLayer> into)
        {
            foreach (var stack in before)
                foreach (var s in stack) into.Add(s.layer);
            foreach (var stack in after)
                foreach (var s in stack) into.Add(s.layer);
        }
    }

    internal sealed class CompoundStep : HistoryStep
    {
        readonly List<HistoryStep> steps;

        public CompoundStep(string name, params HistoryStep[] steps)
        {
            this.name = name;
            this.steps = new List<HistoryStep>(steps);
        }

        public override void Undo()
        {
            for (int i = steps.Count - 1; i >= 0; i--) steps[i].Undo();
        }

        public override void Redo()
        {
            foreach (var s in steps) s.Redo();
        }

        public override long Bytes
        {
            get
            {
                long b = 0;
                foreach (var s in steps) b += s.Bytes;
                return b;
            }
        }

        public override void CollectLayers(HashSet<PaintLayer> into)
        {
            foreach (var s in steps) s.CollectLayers(into);
        }

        public override void Dispose()
        {
            foreach (var s in steps) s.Dispose();
        }
    }

    /// <summary>
    /// GPU history of all textures of a session, kept in sync with Unity's undo
    /// stack through <see cref="PaintUndoProxy"/>.
    /// </summary>
    internal sealed class PaintHistory : IDisposable
    {
        public int maxSteps = 30;
        public long maxBytes = 2L * 1024 * 1024 * 1024;

        readonly PaintDocument[] docs;
        readonly List<HistoryStep> steps = new List<HistoryStep>();
        readonly HashSet<PaintLayer> knownLayers = new HashSet<PaintLayer>();
        PaintUndoProxy proxy;
        int applied;
        int nextSerial = 1;

        public event Action Changed;

        public PaintHistory(IEnumerable<PaintDocument> documents)
        {
            docs = documents.ToArray();
            proxy = ScriptableObject.CreateInstance<PaintUndoProxy>();
            proxy.hideFlags = HideFlags.HideAndDontSave;
            proxy.name = "VRC Texture Painter";
            foreach (var doc in docs)
                foreach (var l in doc.Layers) knownLayers.Add(l);
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        public int Count => steps.Count;
        public bool CanUndo => applied > 0;
        public bool CanRedo => applied < steps.Count;
        public string UndoName => CanUndo ? steps[applied - 1].name : null;
        public string RedoName => CanRedo ? steps[applied].name : null;

        public void RegisterLayer(PaintLayer layer) => knownLayers.Add(layer);

        /// <summary>Layer stacks of every texture, in document order.</summary>
        public LayerState[][] CaptureStacks()
        {
            var result = new LayerState[docs.Length][];
            for (int d = 0; d < docs.Length; d++)
            {
                var layers = docs[d].Layers;
                result[d] = new LayerState[layers.Count];
                for (int i = 0; i < layers.Count; i++)
                    result[d][i] = new LayerState { layer = layers[i], props = layers[i].Props };
            }
            return result;
        }

        public StackStep CreateStackStep(string name, LayerState[][] before, int activeBefore, string coalesceKey = null) => new StackStep
        {
            name = name,
            docs = docs,
            before = before,
            after = CaptureStacks(),
            activeBefore = activeBefore,
            activeAfter = docs[0].ActiveIndex,
            coalesceKey = coalesceKey,
            time = EditorApplication.timeSinceStartup
        };

        /// <summary>Records a layer stack change made after `before` was captured. Consecutive edits with the same key merge.</summary>
        public void PushStack(string name, LayerState[][] before, int activeBefore, string coalesceKey = null)
        {
            double now = EditorApplication.timeSinceStartup;
            if (coalesceKey != null && applied == steps.Count && applied > 0 &&
                steps[applied - 1] is StackStep top && top.coalesceKey == coalesceKey && now - top.time < 1.5)
            {
                top.after = CaptureStacks();
                top.activeAfter = docs[0].ActiveIndex;
                top.time = now;
                Changed?.Invoke();
                return;
            }
            Push(CreateStackStep(name, before, activeBefore, coalesceKey));
        }

        public void Push(HistoryStep step)
        {
            for (int i = applied; i < steps.Count; i++) steps[i].Dispose();
            if (applied < steps.Count) steps.RemoveRange(applied, steps.Count - applied);

            step.serial = nextSerial++;
            steps.Add(step);
            applied = steps.Count;
            step.CollectLayers(knownLayers);
            foreach (var doc in docs)
                foreach (var l in doc.Layers) knownLayers.Add(l);

            Trim();
            ReleaseOrphans();

            if (proxy != null)
            {
                Undo.RecordObject(proxy, "Paint: " + step.name);
                proxy.appliedSerial = step.serial;
                Undo.FlushUndoRecordObjects();
                Undo.IncrementCurrentGroup();
            }
            Changed?.Invoke();
        }

        void Trim()
        {
            long total = 0;
            foreach (var s in steps) total += s.Bytes;
            while (steps.Count > 1 && applied > 1 && (steps.Count > maxSteps || total > maxBytes))
            {
                total -= steps[0].Bytes;
                steps[0].Dispose();
                steps.RemoveAt(0);
                applied--;
            }
        }

        void ReleaseOrphans()
        {
            var referenced = new HashSet<PaintLayer>();
            foreach (var doc in docs) referenced.UnionWith(doc.Layers);
            foreach (var s in steps) s.CollectLayers(referenced);
            knownLayers.RemoveWhere(l =>
            {
                if (referenced.Contains(l)) return false;
                l.Release();
                return true;
            });
        }

        public void PerformUndo()
        {
            if (CanUndo) Undo.PerformUndo();
        }

        public void PerformRedo()
        {
            if (CanRedo) Undo.PerformRedo();
        }

        void OnUndoRedo()
        {
            if (proxy == null) return;
            int target = proxy.appliedSerial;
            bool changed = false;
            while (applied > 0 && steps[applied - 1].serial > target)
            {
                steps[applied - 1].Undo();
                applied--;
                changed = true;
            }
            while (applied < steps.Count && steps[applied].serial <= target)
            {
                steps[applied].Redo();
                applied++;
                changed = true;
            }
            if (changed)
            {
                foreach (var doc in docs) doc.InvalidateComposite();
                Changed?.Invoke();
            }
        }

        public void Dispose()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            foreach (var s in steps) s.Dispose();
            steps.Clear();
            // layers still in a document are released by the document itself
            foreach (var l in knownLayers)
                if (!docs.Any(d => d.Layers.Contains(l))) l.Release();
            knownLayers.Clear();
            if (proxy != null)
            {
                Undo.ClearUndo(proxy);
                Object.DestroyImmediate(proxy);
                proxy = null;
            }
        }
    }
}
