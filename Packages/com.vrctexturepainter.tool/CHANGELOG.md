# Changelog

## [1.0.0] - 2026-09-11

First release.

### Added

- Paint directly on meshes in the Scene view with Hard, Soft, Blur, Color Blend and Eraser brushes.
- 3D projection painting that works across UV seams, mirrored and overlapping UVs, any UV channel, and UVs outside 0–1 (following the texture's wrap mode).
- Several textures with different UV channels in one session, for example a Poiyomi body texture on UV0 and a face decal on UV2, with a shared layer stack.
- Color Blend brush that turns borders into seamless gradients, with perceptual (OKLab), linear or sRGB colour mixing.
- Photoshop style layers: 13 blend modes, opacity, visibility, lock transparency, merge down, fill and image import.
- Undo and redo through Unity's undo system.
- Project files (`.mtpaint`), PNG export that copies the original texture's import settings, and autosave across script reloads and Play mode.
- Occlusion, backface culling, normal falloff, sphere falloff, mirror painting and pen pressure.
