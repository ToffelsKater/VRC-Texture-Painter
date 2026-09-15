# Changelog

## [1.1.0] - 2026-09-16

### Added

- **Mask Painter** tab that paints the mask slots of a shader, such as Poiyomi's `_EmissionMask` or the Standard shader's `_DetailMask`.
- Paint **R**, **G**, **B**, **White** or **Black**. Red, green and blue only change their own channel, so masks painted in different channels add up in the same spot instead of replacing each other. Alpha is never painted.
- Hard, Soft, Blur and Eraser on masks: Blur softens only the selected channel, the Eraser takes it back to 0.
- **Fill**, **Clear** and **Invert** for the selected channel on the whole mask, with undo.
- **Show Mask On Model** puts the mask in place of the material's main texture while painting, so you can see it even when the effect it drives is off.
- Mask projects are saved as `.mtpaint` files and reopen in the Mask Painter tab.

### Changed

- The window now has two tabs, Texture Painter and Mask Painter, each with its own brush settings and its own session.
- The Texture Painter no longer lists mask slots; they belong to the Mask Painter.

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
