using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace BFP4FMeshViewer.Bf2
{
    /// <summary>Baut aus einem Bundled-/StaticMesh-LOD ein WPF-Model3DGroup.</summary>
    public static class MeshBuilder
    {
        public sealed class BuildResult
        {
            public Model3DGroup Model;
            public Rect3D Bounds;
            public List<string> Notes = new List<string>();
            public int TriangleCount;
        }

        public static BuildResult Build(Bf2Mesh mesh, int flatLodIndex,
                                        TextureLibrary textures, int preferredTextureSlot,
                                        bool forceOpaque = false)
        {
            var result = new BuildResult();
            var group = new Model3DGroup();

            var geo = mesh.Geometry;
            int stride = geo.FloatsPerVertex;
            if (stride <= 0)
                throw new InvalidDataException("Vertexstride ist 0 - Datei vermutlich beschaedigt.");

            int posOff = geo.FloatOffsetOf(DeclUsage.Position);
            int nrmOff = geo.FloatOffsetOf(DeclUsage.Normal);
            int uvOff1 = geo.FloatOffsetOf(DeclUsage.Uv1);

            if (posOff < 0)
                throw new InvalidDataException("Mesh hat kein POSITION-Attribut.");
            if (uvOff1 < 0)
                result.Notes.Add("Kein UV1-Attribut - Textur wird nicht abgebildet.");

            if (flatLodIndex < 0 || flatLodIndex >= mesh.GeomMaterials.Count)
                flatLodIndex = 0;

            var lodMaterials = mesh.GeomMaterials[flatLodIndex];

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;

            foreach (var mat in lodMaterials.Materials)
            {
                if (mat.IndexCount == 0) continue;

                // Textur zuerst aufloesen: bei StaticMeshes haengt der UV-Satz vom Slot ab
                BitmapSource tex = null;
                string usedPath = null;
                int usedSlot = -1;
                if (textures != null && mat.TextureMaps.Count > 0)
                {
                    // BF2 legt die Specular-Map in den Alphakanal der Diffuse-Textur.
                    // WPF liest Alpha als Deckkraft - bei alphaMode 0 (opak) also verwerfen,
                    // sonst waere das Modell fast unsichtbar.
                    bool opaque = forceOpaque || mat.AlphaMode == 0;
                    tex = textures.Resolve(mat.TextureMaps, preferredTextureSlot, opaque, out usedPath, out usedSlot);
                }

                int uvOff = UvOffsetFor(mesh, usedSlot, uvOff1);

                var positions = new Point3DCollection((int)mat.IndexCount);
                var normals = nrmOff >= 0 ? new Vector3DCollection((int)mat.IndexCount) : null;
                var uvs = uvOff >= 0 ? new PointCollection((int)mat.IndexCount) : null;
                var tris = new Int32Collection((int)mat.IndexCount);

                for (int j = 0; j < mat.IndexCount; j++)
                {
                    long ii = mat.IndexStartIndex + j;
                    if (ii < 0 || ii >= geo.Indices.Length)
                        throw new InvalidDataException("Indexbereich des Materials liegt ausserhalb des Indexbuffers.");

                    long vi = geo.Indices[ii] + mat.VertexStartIndex;
                    long baseF = vi * stride;
                    if (baseF < 0 || baseF + stride > geo.Vertices.Length)
                        throw new InvalidDataException("Vertexbereich des Materials liegt ausserhalb des Vertexbuffers.");

                    // Refractor ist linkshaendig, WPF rechtshaendig -> Z spiegeln
                    double x = geo.Vertices[baseF + posOff];
                    double y = geo.Vertices[baseF + posOff + 1];
                    double z = -geo.Vertices[baseF + posOff + 2];
                    positions.Add(new Point3D(x, y, z));

                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                    any = true;

                    if (normals != null)
                        normals.Add(new Vector3D(
                            geo.Vertices[baseF + nrmOff],
                            geo.Vertices[baseF + nrmOff + 1],
                            -geo.Vertices[baseF + nrmOff + 2]));

                    if (uvs != null)
                        uvs.Add(new System.Windows.Point(
                            geo.Vertices[baseF + uvOff],
                            geo.Vertices[baseF + uvOff + 1]));

                    tris.Add(j);
                }

                // Z-Spiegelung dreht die Umlaufrichtung -> Dreiecke zuruecktauschen
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    int tmp = tris[t + 1];
                    tris[t + 1] = tris[t + 2];
                    tris[t + 2] = tmp;
                }

                var g = new MeshGeometry3D
                {
                    Positions = positions,
                    TriangleIndices = tris
                };
                if (normals != null) g.Normals = normals;
                if (uvs != null) g.TextureCoordinates = uvs;

                Material wpfMat;
                if (tex != null)
                {
                    var brush = new ImageBrush(tex)
                    {
                        TileMode = TileMode.Tile,
                        ViewportUnits = BrushMappingMode.Absolute,
                        Viewport = new System.Windows.Rect(0, 0, 1, 1),
                        Stretch = Stretch.Fill
                    };
                    brush.Freeze();
                    wpfMat = new DiffuseMaterial(brush);
                    result.Notes.Add(Path.GetFileName(usedPath));
                }
                else
                {
                    wpfMat = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(170, 170, 175)));
                    result.Notes.Add(mat.TextureMaps.Count == 0
                        ? "Material ohne Textureintrag"
                        : "Textur nicht gefunden: " + Path.GetFileName(mat.TextureMaps[0].Replace('/', '\\')));
                }
                wpfMat.Freeze();

                var model = new GeometryModel3D(g, wpfMat);
                model.BackMaterial = wpfMat;   // BF2-Umlaufrichtung ist nicht immer konsistent
                group.Children.Add(model);

                result.TriangleCount += (int)(mat.IndexCount / 3);
            }

            if (!any)
                throw new InvalidDataException("LOD enthaelt keine darstellbare Geometrie.");

            group.Freeze();
            result.Model = group;
            result.Bounds = new Rect3D(minX, minY, minZ, maxX - minX, maxY - minY, maxZ - minZ);
            return result;
        }

        private static readonly DeclUsage[] UvByIndex =
            { DeclUsage.Uv1, DeclUsage.Uv2, DeclUsage.Uv3, DeclUsage.Uv4, DeclUsage.Uv5 };

        /// <summary>
        /// StaticMesh (BaseDetailDirtCrack): Textur-Slot n nutzt den UV-Satz n+1
        /// (Base=UV1, Detail=UV2, Dirt=UV3, Crack=UV4). Ab Slot 4 folgen Normal-Maps,
        /// dafuer und fuer BundledMeshes bleibt es bei UV1.
        /// </summary>
        private static int UvOffsetFor(Bf2Mesh mesh, int textureSlot, int uv1Offset)
        {
            if (mesh.Kind != MeshKind.Static || textureSlot <= 0 || textureSlot > 3)
                return uv1Offset;
            int off = mesh.Geometry.FloatOffsetOf(UvByIndex[textureSlot]);
            return off >= 0 ? off : uv1Offset;
        }
    }

    /// <summary>Findet Texturdateien im geladenen Ordner und dekodiert sie bei Bedarf.</summary>
    public sealed class TextureLibrary
    {
        private readonly Dictionary<string, string> _byFileName =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _byRelPath =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BitmapSource> _cache =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        public int FileCount { get { return _byRelPath.Count; } }

        private static readonly string[] Extensions = { ".dds", ".png", ".tga", ".jpg", ".jpeg", ".bmp" };

        public TextureLibrary(string rootFolder)
        {
            foreach (var file in Directory.GetFiles(rootFolder, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (Array.IndexOf(Extensions, ext) < 0) continue;

                string rel = file.Substring(rootFolder.Length).TrimStart('\\', '/').Replace('/', '\\');
                if (!_byRelPath.ContainsKey(rel)) _byRelPath[rel] = file;

                string name = Path.GetFileName(file);
                if (!_byFileName.ContainsKey(name)) _byFileName[name] = file;
            }
        }

        /// <summary>
        /// Probiert ab <paramref name="preferredSlot"/> alle Texturpfade des Materials durch
        /// und liefert die erste, die sich laden laesst.
        /// </summary>
        public BitmapSource Resolve(IList<string> textureMaps, int preferredSlot,
                                    bool forceOpaque, out string usedPath)
        {
            int usedSlot;
            return Resolve(textureMaps, preferredSlot, forceOpaque, out usedPath, out usedSlot);
        }

        public BitmapSource Resolve(IList<string> textureMaps, int preferredSlot,
                                    bool forceOpaque, out string usedPath, out int usedSlot)
        {
            usedPath = null;
            usedSlot = -1;
            int n = textureMaps.Count;
            for (int k = 0; k < n; k++)
            {
                int i = (preferredSlot + k) % n;
                string file = Find(textureMaps[i]);
                if (file == null) continue;

                var bmp = LoadCached(file, forceOpaque);
                if (bmp != null) { usedPath = file; usedSlot = i; return bmp; }
            }
            return null;
        }

        /// <summary>Kopie mit Alpha = 255. Noetig, weil BF2 Specular im Alphakanal ablegt.</summary>
        public static BitmapSource MakeOpaque(BitmapSource src)
        {
            BitmapSource conv = src.Format == PixelFormats.Bgra32
                ? src
                : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

            int w = conv.PixelWidth, h = conv.PixelHeight;
            int stride = w * 4;
            var px = new byte[h * stride];
            conv.CopyPixels(px, stride, 0);
            for (int i = 3; i < px.Length; i += 4)
                px[i] = 255;

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            bmp.Freeze();
            return bmp;
        }

        private string Find(string ingamePath)
        {
            if (string.IsNullOrEmpty(ingamePath)) return null;
            string norm = ingamePath.Replace('/', '\\').TrimStart('\\');

            string hit;
            if (_byRelPath.TryGetValue(norm, out hit)) return hit;

            // Pfadsuffixe von hinten kuerzen: objects/weapons/x/tex.dds -> weapons/x/tex.dds -> x/tex.dds
            var parts = norm.Split('\\');
            for (int start = 1; start < parts.Length; start++)
            {
                string sub = string.Join("\\", parts, start, parts.Length - start);
                if (_byRelPath.TryGetValue(sub, out hit)) return hit;
            }

            string fileName = parts[parts.Length - 1];
            if (_byFileName.TryGetValue(fileName, out hit)) return hit;

            // Gleicher Name, andere Endung (z.B. .dds im Mesh, .png im Ordner)
            string stem = Path.GetFileNameWithoutExtension(fileName);
            foreach (var ext in Extensions)
                if (_byFileName.TryGetValue(stem + ext, out hit))
                    return hit;

            return null;
        }

        private BitmapSource LoadCached(string file, bool forceOpaque)
        {
            string key = forceOpaque ? file + "|opaque" : file;
            BitmapSource cached;
            if (_cache.TryGetValue(key, out cached)) return cached;

            BitmapSource bmp = null;
            try
            {
                if (Path.GetExtension(file).Equals(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    bmp = DdsImage.Load(file);
                }
                else
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.UriSource = new Uri(file, UriKind.Absolute);
                    img.EndInit();
                    img.Freeze();
                    bmp = img;
                }
            }
            catch
            {
                bmp = null;   // nicht ladbar - Aufrufer nimmt den naechsten Slot
            }

            if (bmp != null && forceOpaque)
            {
                try { bmp = MakeOpaque(bmp); }
                catch { /* Originalbild behalten */ }
            }

            _cache[key] = bmp;
            return bmp;
        }
    }
}
