using UnityEngine;

namespace MeshTexturePainter
{
    /// <summary>
    /// Tiny object registered with Unity's undo system. Its serial number tracks
    /// which paint history step is applied, so Ctrl+Z / Ctrl+Y and Edit > Undo
    /// drive the GPU side history of the painter.
    /// </summary>
    internal sealed class PaintUndoProxy : ScriptableObject
    {
        public int appliedSerial;
    }
}
