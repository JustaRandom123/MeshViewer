using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BFP4FMeshViewer.Bf2
{
    public enum MeshKind { Bundled, Static }

    /// <summary>
    /// Parser fuer Refractor-2 .bundledmesh und .staticmesh (BF2 / BFP4F).
    /// Reihenfolge und Feldbreiten entsprechen dem BFP4F Explorer von Warranty Voider.
    /// Beide Formate teilen Header und Geometrie; sie unterscheiden sich nur in
    /// den LOD-Tabellen (StaticMesh: Node-Matrizen) und den Materialien
    /// (StaticMesh v11: zusaetzliche Bounding-Box).
    /// </summary>
    public sealed class Bf2Mesh
    {
        public MeshKind Kind { get; private set; }
        public MeshHeader Header { get; private set; }
        public MeshGeometry Geometry { get; private set; }
        public uint UnknownAfterGeometry { get; private set; }
        public List<Lod> Lods { get; private set; }
        public List<GeometryMaterial> GeomMaterials { get; private set; }

        public string SourcePath { get; private set; }

        public static readonly string[] Extensions = { ".bundledmesh", ".staticmesh" };

        public static bool IsSupportedFile(string path)
        {
            string ext = Path.GetExtension(path);
            foreach (var e in Extensions)
                if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static MeshKind KindFromPath(string path)
        {
            return string.Equals(Path.GetExtension(path), ".staticmesh", StringComparison.OrdinalIgnoreCase)
                ? MeshKind.Static : MeshKind.Bundled;
        }

        public static Bf2Mesh Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var mesh = Parse(bytes, KindFromPath(path));
            mesh.SourcePath = path;
            return mesh;
        }

        public static Bf2Mesh Parse(byte[] data, MeshKind kind)
        {
            var mesh = new Bf2Mesh { Kind = kind };
            using (var ms = new MemoryStream(data, false))
            {
                mesh.Header = new MeshHeader(ms);
                mesh.Geometry = new MeshGeometry(ms);
                mesh.UnknownAfterGeometry = R.U32(ms);

                int lodCount = mesh.Geometry.TotalLodCount;

                mesh.Lods = new List<Lod>(lodCount);
                for (int i = 0; i < lodCount; i++)
                    mesh.Lods.Add(new Lod(ms, mesh.Header, kind));

                mesh.GeomMaterials = new List<GeometryMaterial>(lodCount);
                for (int i = 0; i < lodCount; i++)
                    mesh.GeomMaterials.Add(new GeometryMaterial(ms, mesh.Header, kind));

                mesh.TrailingBytes = data.Length - (int)ms.Position;
            }
            return mesh;
        }

        /// <summary>Uebrige Bytes nach dem Parsen. 0 = Format vollstaendig verstanden.</summary>
        public int TrailingBytes { get; private set; }

        /// <summary>Flache Liste aller Geom/Lod-Kombinationen in Dateireihenfolge.</summary>
        public IEnumerable<LodRef> EnumerateLods()
        {
            int flat = 0;
            for (int g = 0; g < Geometry.LodsPerGeom.Count; g++)
                for (int l = 0; l < Geometry.LodsPerGeom[g]; l++)
                    yield return new LodRef(g, l, flat++, Kind);
        }

        public struct LodRef
        {
            public readonly int Geom, Lod, FlatIndex;
            public readonly MeshKind Kind;
            public LodRef(int g, int l, int f, MeshKind kind) { Geom = g; Lod = l; FlatIndex = f; Kind = kind; }
            public override string ToString()
            {
                string hint = "";
                if (Kind == MeshKind.Bundled)
                    hint = Geom == 0 ? " (1P)" : Geom == 1 ? " (3P)" : Geom == 2 ? " (Wrack)" : "";
                return string.Format("Geom{0} Lod{1}{2}", Geom, Lod, hint);
            }
        }

        // ---------------------------------------------------------------

        public sealed class MeshHeader
        {
            public uint U1, Version, U2, U3, U4;
            public byte U5;   // bei Version 10: 1 = Bone-Namen in den Lods

            public MeshHeader(Stream s)
            {
                U1 = R.U32(s);
                Version = R.U32(s);
                U2 = R.U32(s);
                U3 = R.U32(s);
                U4 = R.U32(s);
                U5 = (byte)s.ReadByte();
            }
        }

        public sealed class MeshGeometry
        {
            public uint GeomCount;
            public List<uint> LodsPerGeom;
            public List<VertexElement> VertexElements;
            public uint VertexFormat;   // Bytes pro Komponente, praktisch immer 4
            public uint VertexStride;   // Bytes pro Vertex
            public uint VertexCount;
            public float[] Vertices;    // flach, FloatsPerVertex je Vertex
            public uint IndexCount;
            public ushort[] Indices;

            public int FloatsPerVertex
            {
                get { return VertexFormat == 0 ? 0 : (int)(VertexStride / VertexFormat); }
            }

            public int TotalLodCount
            {
                get { int n = 0; foreach (var u in LodsPerGeom) n += (int)u; return n; }
            }

            public MeshGeometry(Stream s)
            {
                GeomCount = R.U32(s);
                LodsPerGeom = new List<uint>((int)GeomCount);
                for (int i = 0; i < GeomCount; i++)
                    LodsPerGeom.Add(R.U32(s));

                uint elemCount = R.U32(s);
                VertexElements = new List<VertexElement>((int)elemCount);
                for (int i = 0; i < elemCount; i++)
                    VertexElements.Add(new VertexElement(s));

                VertexFormat = R.U32(s);
                VertexStride = R.U32(s);
                VertexCount = R.U32(s);

                long floatCount = (long)VertexCount * FloatsPerVertex;
                if (floatCount < 0 || floatCount > 64L * 1024 * 1024)
                    throw new InvalidDataException("Unplausible Vertexanzahl: " + floatCount);

                Vertices = new float[floatCount];
                var buf = new byte[floatCount * 4];
                ReadExactly(s, buf, buf.Length);
                Buffer.BlockCopy(buf, 0, Vertices, 0, buf.Length);

                IndexCount = R.U32(s);
                Indices = new ushort[IndexCount];
                var ibuf = new byte[IndexCount * 2];
                ReadExactly(s, ibuf, ibuf.Length);
                Buffer.BlockCopy(ibuf, 0, Indices, 0, ibuf.Length);
            }

            /// <summary>Float-Index (nicht Byte-Offset) eines Attributs, oder -1.</summary>
            public int FloatOffsetOf(DeclUsage usage)
            {
                foreach (var e in VertexElements)
                    if (e.IsUsed && e.Usage == (ushort)usage)
                        return e.Offset / 4;
                return -1;
            }

            public bool Has(DeclUsage usage) { return FloatOffsetOf(usage) >= 0; }
        }

        public sealed class VertexElement
        {
            public ushort Flag;     // 0 = benutzt, 255 = unbenutzt
            public ushort Offset;   // Byte-Offset im Vertex
            public ushort VarType;  // DeclType
            public ushort Usage;    // DeclUsage

            public bool IsUsed { get { return Flag == 0; } }

            public VertexElement(Stream s)
            {
                Flag = R.U16(s);
                Offset = R.U16(s);
                VarType = R.U16(s);
                Usage = R.U16(s);
            }

            public override string ToString()
            {
                return string.Format("{0} @{1} type={2} {3}",
                    (DeclUsage)Usage, Offset, (DeclType)VarType, IsUsed ? "" : "(unused)");
            }
        }

        public sealed class Lod
        {
            public float[] Min, Max, Pivot;
            public uint NodeCount;
            public List<string> BoneNames = new List<string>();
            /// <summary>Nur StaticMesh: eine 4x4-Matrix (16 floats) pro Node.</summary>
            public List<float[]> NodeMatrices = new List<float[]>();

            public Lod(Stream s, MeshHeader header, MeshKind kind)
            {
                if (kind == MeshKind.Static)
                    ReadStatic(s, header);
                else
                    ReadBundled(s, header);
            }

            private void ReadStatic(Stream s, MeshHeader header)
            {
                Min = R.Vec3(s); Max = R.Vec3(s);
                if (header.Version == 4)
                    Pivot = R.Vec3(s);
                NodeCount = R.U32(s);
                if (NodeCount > 4096)
                    throw new InvalidDataException("Unplausible Node-Anzahl: " + NodeCount);
                for (int i = 0; i < NodeCount; i++)
                {
                    var m = new float[16];
                    for (int k = 0; k < 16; k++) m[k] = R.F32(s);
                    NodeMatrices.Add(m);
                }
            }

            private void ReadBundled(Stream s, MeshHeader header)
            {
                if (header.Version == 6)
                {
                    Min = R.Vec3(s); Max = R.Vec3(s); Pivot = R.Vec3(s);
                    NodeCount = R.U32(s);
                }
                else if (header.Version == 10)
                {
                    Min = R.Vec3(s); Max = R.Vec3(s);
                    NodeCount = R.U32(s);
                    if (header.U5 == 1)
                    {
                        for (int i = 0; i < NodeCount; i++)
                        {
                            s.Seek(64, SeekOrigin.Current);   // 4x4 float Matrix
                            BoneNames.Add(R.CString(s));
                        }
                    }
                }
                else
                {
                    throw new InvalidDataException(
                        "Nicht unterstuetzte BundledMesh-Version: " + header.Version + " (erwartet 6 oder 10)");
                }
            }
        }

        public sealed class GeometryMaterial
        {
            public List<Material> Materials;

            public GeometryMaterial(Stream s, MeshHeader header, MeshKind kind)
            {
                uint n = R.U32(s);
                if (n > 4096)
                    throw new InvalidDataException("Unplausible Materialanzahl: " + n);
                Materials = new List<Material>((int)n);
                for (int i = 0; i < n; i++)
                    Materials.Add(new Material(s, header, kind));
            }
        }

        public sealed class Material
        {
            public uint AlphaMode;
            public string ShaderFile;
            public string Technique;
            public List<string> TextureMaps;
            public uint VertexStartIndex;
            public uint IndexStartIndex;
            public uint IndexCount;
            public uint VertexCount;
            public uint U1;
            public ushort U2, U3;
            /// <summary>Nur StaticMesh v11: Bounding-Box des Materials, sonst null.</summary>
            public float[] Min, Max;

            public Material(Stream s, MeshHeader header, MeshKind kind)
            {
                AlphaMode = R.U32(s);
                ShaderFile = R.CString(s);
                Technique = R.CString(s);
                uint n = R.U32(s);
                TextureMaps = new List<string>((int)n);
                for (int i = 0; i < n; i++)
                    TextureMaps.Add(R.CString(s));
                VertexStartIndex = R.U32(s);
                IndexStartIndex = R.U32(s);
                IndexCount = R.U32(s);
                VertexCount = R.U32(s);
                U1 = R.U32(s);
                U2 = R.U16(s);
                U3 = R.U16(s);
                if (kind == MeshKind.Static && header.Version == 11)
                {
                    Min = R.Vec3(s);
                    Max = R.Vec3(s);
                }
            }

            public override string ToString()
            {
                return string.Format("{0} / {1} ({2} Tris)", ShaderFile, Technique, IndexCount / 3);
            }
        }

        // ---------------------------------------------------------------

        private static void ReadExactly(Stream s, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, read, count - read);
                if (n <= 0) throw new EndOfStreamException("Datei endet vorzeitig.");
                read += n;
            }
        }

        private static class R
        {
            public static ushort U16(Stream s)
            {
                int a = s.ReadByte(), b = s.ReadByte();
                if (b < 0) throw new EndOfStreamException();
                return (ushort)(a | (b << 8));
            }

            public static uint U32(Stream s)
            {
                int a = s.ReadByte(), b = s.ReadByte(), c = s.ReadByte(), d = s.ReadByte();
                if (d < 0) throw new EndOfStreamException();
                return (uint)(a | (b << 8) | (c << 16) | (d << 24));
            }

            public static float F32(Stream s)
            {
                var b = new byte[4];
                if (s.Read(b, 0, 4) != 4) throw new EndOfStreamException();
                return BitConverter.ToSingle(b, 0);
            }

            public static float[] Vec3(Stream s)
            {
                return new[] { F32(s), F32(s), F32(s) };
            }

            /// <summary>Laengenpraefixierter ASCII-String (u32 Laenge, kein Nullterminator).</summary>
            public static string CString(Stream s)
            {
                uint len = U32(s);
                if (len > 4096) throw new InvalidDataException("Unplausible Stringlaenge: " + len);
                var b = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int n = s.Read(b, read, (int)len - read);
                    if (n <= 0) throw new EndOfStreamException();
                    read += n;
                }
                return Encoding.ASCII.GetString(b);
            }
        }
    }

    public enum DeclType : ushort
    {
        Float1 = 0, Float2 = 1, Float3 = 2, Float4 = 3, D3DColor = 4, Unused = 17
    }

    public enum DeclUsage : ushort
    {
        Position = 0, BlendWeight = 1, BlendIndices = 2, Normal = 3, PSize = 4,
        Uv1 = 5, Tangent = 6, Binormal = 7, TessFactor = 8, PositionT = 9,
        Color = 10, Fog = 11, Depth = 12, Sample = 13,
        Uv2 = 261, Uv3 = 517, Uv4 = 773, Uv5 = 1029
    }
}
