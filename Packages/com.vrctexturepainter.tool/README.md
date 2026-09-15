# VRC Texture Painter

Paint textures directly on your avatar in the Unity Scene view, the way Blender's
Texture Paint mode works. It has hard and soft brushes, a blur brush, a colour
blend brush, an eraser, and a Photoshop style layer stack. Painting works in 3D,
so strokes stay continuous across UV seams and land correctly on mirrored or
overlapping UVs and on any UV channel.

Built for VRChat avatar creators on **Unity 2022.3.22f1 (DX11, Built-in render
pipeline, Linear colour space)**. The tool is editor only. It adds no components
to your avatar and never touches your material assets while you paint, so
uploads are unaffected.

## Install

**With the VRChat Creator Companion:** open the
[package listing](https://toffelskater.github.io/VRC-Texture-Painter/) and click
**Add to VCC**, or add `https://toffelskater.github.io/VRC-Texture-Painter/index.json`
under *Settings > Packages > Add Repository*. Then add **VRC Texture Painter** to
your project under *Manage Project*.

**Manually:** download the `.unitypackage` or `.zip` from the
[latest release](https://github.com/ToffelsKater/VRC-Texture-Painter/releases/latest).
Import the `.unitypackage`, or unzip the `.zip` into your project's
`Packages/com.vrctexturepainter.tool` folder.

Then open **Tools > VRC Texture Painter**.

## Quick start

1. Select the avatar's body mesh (or right click the *Skinned Mesh Renderer* component and choose **Paint Texture**). To paint the shader's masks instead, use the **Mask Painter** tab (see [Mask painter](#mask-painter)).
2. In the window, click **Use Selection**, then check the material slots, the texture property (usually `_MainTex`) and the UV channel (usually `UV0`).
3. Click **Start Painting**. The current texture becomes the *Base* layer, and an empty *Layer 1* is added above it for painting.
4. Paint in the Scene view with the left mouse button. Alt + drag still orbits.
5. Use **Export PNG...** to write the flattened texture and assign it to the material, or **Overwrite Source** to replace the original PNG.
6. **Save** writes a `.mtpaint` project with every layer, so you can keep editing later.

## Brushes

| Tool | Key | What it does |
| --- | --- | --- |
| Hard | `3` / Num `1` | Solid, antialiased disc |
| Soft | `4` / Num `2` | Smooth falloff; *Hardness* sets the size of the solid core |
| Blur | `5` / Num `3` | Moves each texel towards the average of its neighbourhood. *Blur Size* sets the kernel width |
| Blend | `6` / Num `4` | Blends the colours under the brush into a smooth, seamless transition. *Blend Width* sets how long the gradient is; *Flatten* mode pulls the area towards one average colour instead |
| Eraser | `7` / Num `5` | Removes paint from the layer (makes it transparent) |

Hard, soft and eraser strokes use **Opacity** the way Photoshop does: a stroke
never builds up past its opacity, however often you go over the same spot.
Blur and blend use **Strength** per dab.

The Blend brush reads the colours as they were when the stroke started. Dragging
it across a border blends that border into a gradient without carrying colour
along the stroke (no smearing), and going over the same spot again only brings
it closer to the finished gradient.

**Mix Colors In** (Blur and Blend) decides how colours are averaged:

- *Perceptual* (default, OKLab) gives even transitions without dark, muddy midpoints.
- *Linear* mixes like light and gives brighter midpoints.
- *Srgb* uses the stored texture values, like Photoshop.

**Stroke options**

- **Falloff Shape.** *Projected* is a circle on screen that paints everything visible inside it. *Sphere* paints only surfaces within a 3D ball around the point under the cursor, which is handy for fingers, ears and hair cards that sit close together.
- **Occlusion** only paints surfaces visible from the camera.
- **Backface Culling** skips faces that point away from the camera.
- **Normal Falloff** fades paint on surfaces seen at a grazing angle, which prevents stretched streaks.
- **Mirror** also paints the mirror image across the avatar root's X, Y or Z axis.
- **Pen pressure** can control size and/or strength (Windows Ink tablets).

## Layers

- Blend modes: Normal, Darken, Multiply, Color Burn, Lighten, Screen, Color Dodge, Add, Overlay, Soft Light, Hard Light, Difference, Subtract.
- Per layer: opacity, visibility, and lock transparency. With lock transparency on, painting changes colour only where the layer already has pixels.
- Buttons: New, Duplicate, Delete, Merge Down, Fill (with the brush colour), Clear, and Import Image (PNG/JPG/TGA from anywhere, or any texture inside the project).
- Drag rows to reorder. Double click a name to rename the active layer.

Every paint stroke and layer operation is on **Ctrl+Z / Ctrl+Y** through Unity's
normal undo. History is kept on the GPU, 30 steps by default.

## Mask painter

The window has two tabs. **Texture Painter** is the colour painter described
above. **Mask Painter** paints the mask textures of a shader.

1. Pick the renderer and material slots as usual.
2. Under **Mask Texture**, pick one of the shader's mask slots and its UV channel.
   Mask slots are the texture slots whose property name or label contains "mask",
   such as Poiyomi's `_EmissionMask` or the Standard shader's `_DetailMask`. Only
   mask slots are listed here, and the Texture Painter lists every other texture
   slot. A slot without a texture starts black.
3. Click **Start Painting Mask** and choose what to paint: **R**, **G**, **B**,
   **White** or **Black**.

Masks mix instead of replacing each other. R, G and B each change only their own
channel, so painting red and then green over the same spot gives red + green
(yellow), and both masks are kept. White raises every colour channel, Black clears
every colour channel. Alpha is never painted and stays as it is.

| Tool | Key | On a mask |
| --- | --- | --- |
| Hard / Soft | `3` / `4` | Move the selected channel(s) towards 1 (Black: towards 0), up to the opacity |
| Blur | `5` | Softens only the selected channel(s) |
| Eraser | `7` | Takes the selected channel(s) back to 0 |

- **Fill**, **Clear** and **Invert** change the selected channel(s) on the whole mask.
- **Show Mask On Model** also puts the mask in place of the material's main
  texture, so you can see where it is painted even when the effect it drives is off.
- Masks have no layer stack. Strokes go straight into the mask texture, with undo.
- Export, Overwrite Source and projects work like in the texture painter.
- Each tab keeps its own brush settings and its own session. Both can be open at
  the same time, but only the visible tab paints and shows its preview on the
  model. A dot on a tab means it has a session open.

## Controls

| Input | Action |
| --- | --- |
| Left drag | Paint |
| `[` / `]` | Smaller / larger radius |
| `Shift` + `[` / `]` | Less / more strength |
| `Ctrl` + scroll | Radius |
| `Ctrl` + `Shift` + scroll | Strength |
| `3` – `7` or Num `1` – `5` | Hard, Soft, Blur, Blend, Eraser |
| `C` or Num `0` | Pick the colour under the cursor |
| Right mouse + `WASD` / `QE` | Fly through the scene as usual; painting keys are ignored meanwhile |
| `Esc` | Cancel the current stroke |
| `Ctrl` + `Z` / `Ctrl` + `Y` | Undo / redo |

The painting keys avoid everything Unity 2022.3 binds in the Scene view by
default (`Q`–`Y` tools, `WASD`/`QE` fly mode, `2` for 2D mode, `F`, `V`, `X`,
`Z`, `H`, `L`, `P`). Numpad keys need **Num Lock on**. With it off, Windows sends
arrow keys and Delete instead, and those move the camera or delete the selection.
While painting, Unity's transform tools stay switched off, so an accidental `Q`
(View tool) can't turn left drag into panning.

## Several textures and UV channels on one material

Some avatars show different textures through different UV channels on the same
material. A common setup is a Poiyomi body material whose main texture covers the
body on **UV0** while a **decal** draws the face texture through **UV2**. In
Blender the head's UV0 and the body's UV2 are then left empty (collapsed).

Add one entry per texture under **Textures** before you start painting. Poiyomi
decals on the material are offered as **+ Decal** buttons, with their UV channel
already filled in. An info box also tells you when triangles would not receive
paint and which UV channel they use instead.

While painting:

- Each triangle is painted into the **first texture in the list whose UV channel
  has a layout for it**, so body strokes go into the body texture and face
  strokes into the face texture.
- A stroke over the neck paints both textures. Blur and Blend mix colours across
  the border.
- The **layer stack is shared.** New, duplicate, delete, merge, fill, opacity,
  blend mode and undo apply to every texture. The layer list shows one thumbnail
  per texture.
- **Import Image** asks which texture the image belongs to. **Export** and
  **Overwrite Source** work per texture.

Decals must sit at their default placement (position 0.5 / 0.5, scale 1,
rotation 0, no tiling). Otherwise the setup shows a warning, because strokes would
land offset from where the decal is drawn.

## How UV-aware painting works

Most in-editor painters ray cast the cursor to a single UV coordinate and stamp a
circle into the texture. That approach breaks at UV seams, distorts on stretched
islands, and misses the other copies of mirrored or stacked UVs. This tool works
in reverse:

1. The posed mesh is baked in world space (skinning and blend shapes included).
2. Each dab rasterizes the mesh **into texture space** on the GPU. Every texel it
   covers knows the 3D surface point it belongs to.
3. That 3D point is tested against the brush, projected on screen or as a
   sphere, with occlusion and facing checks.

So a stroke crossing a seam paints both islands, since both sides are near the
brush in 3D. A texel shared by several faces (mirrored or overlapping UVs) takes
paint from whichever face is under the brush, and brush size stays constant
however the UVs are scaled.

The blur and blend brushes read colours from a small capture of the model around
the brush. That capture is continuous across seams, so blurring over a seam
smooths it instead of leaving a line.

**Seam padding** extends painted colour a few texels past island borders in the
preview and the exported file, so mip maps and bilinear filtering don't show
thin lines along seams. The default is 16 texels, and you can change it under
*Output*.

## Notes and limits

- **Pose.** The mesh is re-baked automatically when you pose the avatar, move it or change blend shapes, but not during a stroke. *Re-bake Pose* forces a refresh.
- **Base layer quality.** When the source texture is a PNG, JPG or TGA, the base layer is read from the original file at full quality. Other formats (PSD, TIFF, EXR) are read from the imported texture, which may be compressed or downscaled by its import settings.
- **Occluders.** Only the painted renderer counts for occlusion. Other meshes, such as clothing on a separate renderer, do not block paint.
- **UVs outside 0–1** are painted wherever the texture's wrap mode samples them. *Repeat* wraps them back into the texture, so islands moved to other tiles (Poiyomi UV Tile Discard, tiling) and islands crossing a tile edge work. *Mirror* and *Mirror Once* reflect them. *Clamp* only paints inside 0–1, since outside it the texture just shows its edge. The wrap mode comes from the texture's import settings, and you can override it with *UV Wrap* before starting. A triangle spanning more than 16 tiles is only painted in its first 16. UDIM (a separate texture per tile) is not supported.
- **Double application.** Where overlapping UV copies are both under the brush at the same time, blur and blend apply twice to those texels.
- **Precision.** Layers are 8 bits per channel. Very light blur passes use dithering, so they still converge.
- **Embedded materials.** Materials inside an FBX cannot be assigned automatically. Extract them first (*Materials > Extract Materials*).
- **Autosave.** Scripts reloading or entering Play mode discards GPU data, so the session is auto-saved to `Library/MeshTexturePainter/` and restored when you return to Edit mode.
- **VRAM.** Each layer at 4096×4096 uses about 64 MB, and each undo step the same. The history drops old steps when it passes 2 GB.

## Self test

The [GitHub repository](https://github.com/ToffelsKater/VRC-Texture-Painter) has a
GPU test suite in `Assets/MeshTexturePainterTests`, which is not part of the
installed package. It covers brush placement, seams, padding, blur and blend,
occlusion, blend modes, multiple textures, undo, save/load, export and skinned
meshes. Run it headless in the repository's Unity project. It quits Unity when
done and returns exit code 0 on success:

```
Unity.exe -batchmode -projectPath <repository> -executeMethod MeshTexturePainter.Tests.MTPTests.Run -logFile test.log
```

Search the log for `MTPTEST`. The suite creates and deletes temporary assets, so
don't run it in an avatar project.

## Project files

`.mtpaint` files hold a JSON header (target renderer, UV channel, slots,
texture property, padding, texture or mask project) and one PNG per layer. A mask
project always opens in the Mask Painter tab. You can store them inside or
outside `Assets`. When you open a project, it finds its renderer again by object
id or hierarchy path. If that fails, select the renderer first.
