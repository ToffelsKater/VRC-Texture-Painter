# VRC Texture Painter

Paint textures directly on your avatar in the Unity Scene view, the way Blender's
Texture Paint mode works. Hard and soft brushes, blur, a colour blend brush for
seamless transitions, an eraser and a Photoshop style layer stack. Strokes are
projected in 3D, so they stay continuous across UV seams and land correctly on
mirrored UVs, overlapping UVs and different UV channels.

Made for VRChat avatar creators on **Unity 2022.3** (Built-in render pipeline).
The tool is editor only: it adds nothing to your avatar and doesn't touch your
materials while you paint.

## Install

**With the VRChat Creator Companion (recommended)**

1. Open the [package listing](https://toffelskater.github.io/VRC-Texture-Painter/) and click **Add to VCC**.
   Or in the Creator Companion go to *Settings > Packages > Add Repository* and paste
   `https://toffelskater.github.io/VRC-Texture-Painter/index.json`.
2. Open your avatar project in the Creator Companion (*Manage Project*) and add **VRC Texture Painter**.
3. In Unity, open **Tools > VRC Texture Painter**.

**Manually:** download the `.zip` or `.unitypackage` from the
[latest release](https://github.com/ToffelsKater/VRC-Texture-Painter/releases/latest).

## Features

- **Brushes:** Hard, Soft, Blur, Color Blend (turns hard borders into smooth gradients without smearing), Eraser and a Gradient line tool, with pen pressure.
- **Mask Painter:** paint shader masks such as Poiyomi or lilToon emission masks per R, G and B channel, with Fill, Invert and a preview on the model.
- **UV aware:** paint across seams, on mirrored and overlapping UVs, any UV channel, and UVs outside 0–1.
- **Several textures at once:** for example a Poiyomi body texture on UV0 and a face decal on UV2, painted in one stroke.
- **Layers:** 13 blend modes, opacity, visibility, lock transparency, merge down, fill and image import.
- **Workflow:** undo and redo with Ctrl+Z, project files that keep your layers, and PNG export that copies the original texture's import settings.
- **Brush options:** occlusion, backface culling, normal falloff, sphere falloff and mirror painting.

Full documentation: [package README](Packages/com.vrctexturepainter.tool/README.md) ·
[Changelog](Packages/com.vrctexturepainter.tool/CHANGELOG.md)

## Development

This repository is a Unity 2022.3 project made from the VRChat
[template-package](https://github.com/vrchat-community/template-package).

- The package lives in `Packages/com.vrctexturepainter.tool`.
- A GPU test suite lives in `Assets/MeshTexturePainterTests` (not part of the package). Run it headless:
  `Unity.exe -batchmode -projectPath <this repo> -executeMethod MeshTexturePainter.Tests.MTPTests.Run -logFile test.log`
  and search the log for `MTPTEST`.
- **Releasing:** raise `version` in `package.json`, add a `CHANGELOG.md` entry, push, then run the **Build Release** workflow in the *Actions* tab. The Creator Companion listing rebuilds automatically after the release.

## License

[MIT](Packages/com.vrctexturepainter.tool/LICENSE.md) © ToffelsKater
