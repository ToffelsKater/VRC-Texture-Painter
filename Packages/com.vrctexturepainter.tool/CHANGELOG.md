# Changelog

## [1.1.1] - 2026-09-19

### Changed

- lilToon's `_Main2ndTex` and `_Main3rdTex` (Main 2nd / 3rd layers) are now listed in the Texture Painter instead of the Mask Painter.
- The UV channel of a texture is read from lilToon's per-layer UV Mode (`<property>_UVMode`) as well as Poiyomi's. lilToon's MatCap mode shows the non-UV mapping warning.

## [1.1.0] - 2026-09-17

### Added

- **Mask Painter** tab for shader masks such as Poiyomi's `_EmissionMask` or lilToon masks. Paint R, G, B, White or Black; channels mix instead of replacing each other, and alpha is kept. Fill, Clear and Invert work per channel, and **Show Mask On Model** previews the mask on the avatar.
- **Gradient** tool (`8` / Num `6`): drag a straight line that runs from one colour to another, with a live preview, 15° snapping with Shift and Esc to cancel. In the Mask Painter it runs from the mixed R / G / B colour to black.
- **Create Texture...** for slots without a texture: saves a new PNG and assigns it to the material.
- **Output Folder**: Export PNG and Create Texture save straight into a chosen folder, named after the material and texture property, without a save dialog.

### Changed

- **Mix Colors In** also applies to the Gradient tool.
- Project files remember whether they are a texture or a mask project and open in the matching tab.

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
