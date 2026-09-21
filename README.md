# BFP4F Mesh Viewer

A small Windows tool for viewing Refractor 2 `.bundledmesh` and `.staticmesh` files (Battlefield 2 /
Battlefield Play4Free) with their textures, and for exporting icon-sized PNGs
that match the look of the original BFP4F attachment icons.

No NuGet packages, no external DLLs. Rendering uses WPF's built-in `Viewport3D`,
and DDS files are decoded by a decoder included in this repository — no SharpDX
and no `texconv.exe` required.

---

## Building

Open `BFP4FMeshViewer.sln` in Visual Studio and press F5, or from a terminal:

```
dotnet build -c Release
```

The project targets **.NET Framework 4.7.2**. If Visual Studio complains that the
targeting pack is missing, install it through the Visual Studio Installer under
"Individual components", or switch one line in `BFP4FMeshViewer/BFP4FMeshViewer.csproj`:

```xml
<TargetFramework>net8.0-windows</TargetFramework>
```

---

## Getting started

1. Press **Load Folder…** and pick a folder containing `.bundledmesh` or `.staticmesh` files.
   The folder is searched recursively, and every image file found anywhere inside
   it becomes available as a texture.
2. Click an entry in the list. The mesh loads and is framed automatically.
3. Drag to orbit, scroll to zoom.
4. Press **Save Screenshot** (or **Ctrl+S**) to write a PNG.

Put the meshes and their textures in the same folder tree. The viewer resolves a
material's texture path by trying the full relative path first, then progressively
shorter path suffixes, then the bare file name, and finally the same name with a
different extension. In practice, dropping the `.dds` files anywhere under the
loaded folder is enough.

You can also pass a folder on the command line:

```
BFP4FMeshViewer.exe "D:\bfp4f\objects\weapons"
```

---

## Controls

| Input | Action |
|---|---|
| Left mouse drag | Orbit |
| Right mouse drag | Pan |
| Mouse wheel | Zoom |
| **Tab** | Hide/show the side panel — the render then fills the whole window |
| **F** | Re-frame the model |
| **F11** | Fullscreen, **Esc** to leave |
| **Ctrl+S** | Save screenshot |

---

## Panel options

**Filter** — narrows the mesh list as you type.

**LOD** — `Geom0` is the first-person version, `Geom1` third-person, `Geom2` the
wreck. `Lod0` is always the highest detail level. For weapon and attachment icons
you almost always want `Geom0 Lod0`.

**Texture Slot** — a material usually references several maps (diffuse, normal,
detail). The viewer starts at slot 0 and walks through the remaining slots until
one loads. If the wrong map is showing, change the slot here. The status line at
the bottom names the texture file actually used.

**Alpha always ignore** — BF2 stores the specular map in the alpha channel of the
diffuse texture. WPF would read that channel as opacity, which makes models
almost invisible. The viewer therefore discards alpha for materials whose
`alphaMode` is 0 (opaque). If a material still looks see-through, tick this box to
force it for every material — the trade-off is that real scope glass becomes
opaque too.

---

## Exporting icons

The export block at the bottom of the panel controls what **Save Screenshot**
writes. Files go to:

```
<loaded folder>\screenshots\<meshname>.png
```

Existing files are overwritten, so you can re-shoot a view until it looks right.
The full path is shown in the status line after each save.

**Picture** — output size in pixels. Defaults to **400 × 180**, the full-resolution
attachment icon format. The in-game icons are 125 × 56; both work.

**Angle** — camera yaw and pitch in degrees. These fields work both ways: type a
value and the view rotates, drag with the mouse and the fields follow. That makes
an angle readable and exactly repeatable across a whole set of items, which
dragging by hand does not. Yaw 180 gives a clean side view with the objective
bell pointing left.

**Margin** — breathing room around the model, in percent.

**Auto-Fit** — fits the model to the frame and computes the camera distance from
the eight corners of the bounding box rather than from a bounding sphere. For a
long, thin object like a rifle scope that is the difference between filling the
frame and sitting lost in the middle. Untick it to keep the zoom from the view
instead.

**Drop shadow** — the original BFP4F icons carry a soft shadow; screenshots do not,
unless this is ticked. The parameters were measured against the original 4327.png
(roughly 5 px down, near-black, peak opacity 0.85, soft falloff to about 17 px) and
scale with the output width. The alpha falloff of the result matches the original
to within 0.013 mean error.

### Rendering notes

Screenshots are rendered with **8× supersampling** and then downscaled with
high-quality filtering — at 400 × 180 the viewer renders 3200 × 1440 internally.
Without that, edges at icon size come out ragged. The background stays fully
transparent, so the PNG is true RGBA like the original icons.

The camera orientation always comes from the current view: line the model up in
the window first, then export.

---

## File format

Read in this order, everything little endian:

```
Header      u32 u1, u32 version, u32 u2, u32 u3, u32 u4, u8 u5
Geometry    u32 geomCount
            u32 lodCount   x geomCount
            u32 vertexElementCount
            {u16 flag, u16 offset, u16 varType, u16 usage}  x count
            u32 vertexFormat, u32 vertexStride, u32 vertexCount
            float  x (vertexCount * vertexStride / vertexFormat)
            u32 indexCount, u16 x indexCount
u32         (unknown)
Lods        x sum of all lodCount
  bundled   v6:  vec3 min, vec3 max, vec3 pivot, u32 nodeCount
            v10: vec3 min, vec3 max, u32 nodeCount,
                 if header.u5 == 1: {4x4 float matrix, string} x nodeCount
  static    vec3 min, vec3 max, (v4 only: vec3 pivot), u32 nodeCount,
            4x4 float matrix x nodeCount
Materials   x sum of all lodCount
            u32 materialCount
            { u32 alphaMode, string shader, string technique,
              u32 mapCount, string x mapCount,
              u32 vertexStart, u32 indexStart, u32 indexCount, u32 vertexCount,
              u32 u1, u16 u2, u16 u3,
              static v11 only: vec3 min, vec3 max }
```

The mesh type is taken from the file extension. For static meshes the texture
slot also picks the UV set (slot 0 base → UV1, 1 detail → UV2, 2 dirt → UV3,
3 crack → UV4), matching the `BaseDetailDirtCrack` layout; bundled meshes always
use UV1.

`string` = `u32` length followed by ASCII, no null terminator.

The vertex attribute table is actually evaluated instead of hard-coding where the
UVs sit. `flag == 0` means the attribute is used, and `offset` is a **byte** offset.
Usage values follow `D3DDECLUSAGE`: 0 position, 3 normal, 5 UV1, 6 tangent, 261 UV2.
With the usual BFP4F layout (position, normal, blend indices, UV1) the UVs land on
float index 7/8 — the same place other tools hard-code, but this way a mesh that
deviates still renders correctly.

Positions and normals are mirrored in Z, because Refractor is left-handed and WPF
is right-handed, and the triangle winding is swapped to match. Both material sides
are rendered, since winding is not consistent across BF2 meshes.

---

## Project layout

| File | Contents |
|---|---|
| `Bf2/Bf2Mesh.cs` | file format parser (bundled + static) |
| `Bf2/DdsImage.cs` | DDS decoder: BC1/DXT1, BC2/DXT3, BC3/DXT5, uncompressed |
| `Bf2/MeshBuilder.cs` | builds the `Model3DGroup`, resolves texture paths |
| `Bf2/Snapshot.cs` | offscreen rendering at a fixed size, drop shadow, PNG export |
| `MainWindow.xaml(.cs)` | user interface and orbit camera |

---

## Limitations

- `.bundledmesh` and `.staticmesh` only. `.skinnedmesh` uses different LOD
  (rig) and material blocks.
- Bundled mesh versions 6 and 10 only. Other versions stop with a clear message rather
  than silently reading garbage.
- Loose files in a folder; `.zip` archives are not read.
- Diffuse map only — no normal maps, no shaders.
- Bones are skipped, so meshes appear in bind pose.
- If the status line reports unparsed bytes at the end of a file, that mesh is a
  variant the parser does not fully understand yet. The render may still be fine,
  but treat it with suspicion.

## Verification status

The DDS decoder was checked against ImageMagick: DXT1 and DXT5 match to within one
step (rounding of the 5/6-bit expansion), alpha is exact, and uncompressed 32-bit
is bit-exact. DXT3 is implemented but has not been verified against a test file.

The drop shadow was fitted against the original 4327.png and reproduces its alpha
falloff to 0.013 mean error.
