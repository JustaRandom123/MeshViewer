using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BFP4FMeshViewer.Bf2
{
    /// <summary>
    /// Minimaler DDS-Decoder: BC1/DXT1, BC2/DXT3, BC3/DXT5 und unkomprimierte
    /// 32/24-Bit-Formate. Nur Mip 0. Kein texconv.exe noetig.
    /// </summary>
    public static class DdsImage
    {
        private const uint FourCC_DDS = 0x20534444; // "DDS "
        private const uint FourCC_DXT1 = 0x31545844;
        private const uint FourCC_DXT3 = 0x33545844;
        private const uint FourCC_DXT5 = 0x35545844;
        private const uint FourCC_DX10 = 0x30315844;

        private const uint DDPF_FOURCC = 0x4;
        private const uint DDPF_RGB = 0x40;

        public static BitmapSource Load(string path)
        {
            return Decode(File.ReadAllBytes(path));
        }

        public static BitmapSource Decode(byte[] data)
        {
            if (data == null || data.Length < 128)
                throw new InvalidDataException("Datei zu klein fuer DDS.");
            if (U32(data, 0) != FourCC_DDS)
                throw new InvalidDataException("Kein DDS (Magic fehlt).");

            int height = (int)U32(data, 12);
            int width = (int)U32(data, 16);

            // Pixelformat-Block liegt ab Offset 76
            uint pfFlags = U32(data, 80);
            uint fourCC = U32(data, 84);
            uint rgbBits = U32(data, 88);
            uint rMask = U32(data, 92);
            uint gMask = U32(data, 96);
            uint bMask = U32(data, 100);
            uint aMask = U32(data, 104);

            int offset = 128;
            if ((pfFlags & DDPF_FOURCC) != 0 && fourCC == FourCC_DX10)
                offset += 20;   // DDS_HEADER_DXT10

            if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                throw new InvalidDataException("Unplausible DDS-Abmessungen.");

            byte[] bgra;   // 4 Byte pro Pixel, Reihenfolge B,G,R,A

            if ((pfFlags & DDPF_FOURCC) != 0)
            {
                switch (fourCC)
                {
                    case FourCC_DXT1: bgra = DecodeBc1(data, offset, width, height); break;
                    case FourCC_DXT3: bgra = DecodeBc2(data, offset, width, height); break;
                    case FourCC_DXT5: bgra = DecodeBc3(data, offset, width, height); break;
                    default:
                        throw new NotSupportedException(
                            "DDS-Komprimierung nicht unterstuetzt: " + FourCcToString(fourCC));
                }
            }
            else if ((pfFlags & DDPF_RGB) != 0)
            {
                bgra = DecodeUncompressed(data, offset, width, height, rgbBits, rMask, gMask, bMask, aMask);
            }
            else
            {
                throw new NotSupportedException("DDS-Pixelformat nicht unterstuetzt (flags 0x" + pfFlags.ToString("X") + ").");
            }

            var bmp = BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Bgra32, null, bgra, width * 4);
            bmp.Freeze();
            return bmp;
        }

        // -------------------------------------------------------------- BC1

        private static byte[] DecodeBc1(byte[] src, int offset, int w, int h)
        {
            var dst = new byte[w * h * 4];
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            Require(src, offset, bw * bh * 8, "DXT1");

            var c = new byte[16]; // 4 Farben x BGRA
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int p = offset + (by * bw + bx) * 8;
                    ushort c0 = (ushort)(src[p] | (src[p + 1] << 8));
                    ushort c1 = (ushort)(src[p + 2] | (src[p + 3] << 8));
                    BuildColorTable(c0, c1, c, true);
                    uint bits = U32(src, p + 4);
                    EmitBlock(dst, w, h, bx, by, c, bits, null);
                }
            }
            return dst;
        }

        // -------------------------------------------------------------- BC2

        private static byte[] DecodeBc2(byte[] src, int offset, int w, int h)
        {
            var dst = new byte[w * h * 4];
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            Require(src, offset, bw * bh * 16, "DXT3");

            var c = new byte[16];
            var alpha = new byte[16];
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int p = offset + (by * bw + bx) * 16;

                    // 16 x 4 Bit expliziter Alpha
                    for (int i = 0; i < 8; i++)
                    {
                        byte b = src[p + i];
                        int lo = b & 0x0F, hi = (b >> 4) & 0x0F;
                        alpha[i * 2] = (byte)(lo * 17);
                        alpha[i * 2 + 1] = (byte)(hi * 17);
                    }

                    ushort c0 = (ushort)(src[p + 8] | (src[p + 9] << 8));
                    ushort c1 = (ushort)(src[p + 10] | (src[p + 11] << 8));
                    BuildColorTable(c0, c1, c, false);
                    uint bits = U32(src, p + 12);
                    EmitBlock(dst, w, h, bx, by, c, bits, alpha);
                }
            }
            return dst;
        }

        // -------------------------------------------------------------- BC3

        private static byte[] DecodeBc3(byte[] src, int offset, int w, int h)
        {
            var dst = new byte[w * h * 4];
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            Require(src, offset, bw * bh * 16, "DXT5");

            var c = new byte[16];
            var alpha = new byte[16];
            var at = new byte[8];
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int p = offset + (by * bw + bx) * 16;

                    byte a0 = src[p], a1 = src[p + 1];
                    at[0] = a0; at[1] = a1;
                    if (a0 > a1)
                    {
                        for (int i = 1; i <= 6; i++)
                            at[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                    }
                    else
                    {
                        for (int i = 1; i <= 4; i++)
                            at[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                        at[6] = 0;
                        at[7] = 255;
                    }

                    // 16 x 3 Bit Alpha-Indizes in 6 Bytes
                    ulong abits = 0;
                    for (int i = 0; i < 6; i++)
                        abits |= (ulong)src[p + 2 + i] << (8 * i);
                    for (int i = 0; i < 16; i++)
                        alpha[i] = at[(int)((abits >> (3 * i)) & 0x7)];

                    ushort c0 = (ushort)(src[p + 8] | (src[p + 9] << 8));
                    ushort c1 = (ushort)(src[p + 10] | (src[p + 11] << 8));
                    BuildColorTable(c0, c1, c, false);
                    uint bits = U32(src, p + 12);
                    EmitBlock(dst, w, h, bx, by, c, bits, alpha);
                }
            }
            return dst;
        }

        // -------------------------------------------------------------- gemeinsam

        /// <summary>Baut die 4 Blockfarben als BGRA. punchThrough = BC1-Modus mit 1-Bit-Alpha.</summary>
        private static void BuildColorTable(ushort c0, ushort c1, byte[] c, bool punchThrough)
        {
            byte r0 = (byte)(((c0 >> 11) & 0x1F) * 255 / 31);
            byte g0 = (byte)(((c0 >> 5) & 0x3F) * 255 / 63);
            byte b0 = (byte)((c0 & 0x1F) * 255 / 31);
            byte r1 = (byte)(((c1 >> 11) & 0x1F) * 255 / 31);
            byte g1 = (byte)(((c1 >> 5) & 0x3F) * 255 / 63);
            byte b1 = (byte)((c1 & 0x1F) * 255 / 31);

            c[0] = b0; c[1] = g0; c[2] = r0; c[3] = 255;
            c[4] = b1; c[5] = g1; c[6] = r1; c[7] = 255;

            if (punchThrough && c0 <= c1)
            {
                c[8] = (byte)((b0 + b1) / 2);
                c[9] = (byte)((g0 + g1) / 2);
                c[10] = (byte)((r0 + r1) / 2);
                c[11] = 255;
                c[12] = 0; c[13] = 0; c[14] = 0; c[15] = 0;   // transparent
            }
            else
            {
                c[8] = (byte)((2 * b0 + b1) / 3);
                c[9] = (byte)((2 * g0 + g1) / 3);
                c[10] = (byte)((2 * r0 + r1) / 3);
                c[11] = 255;
                c[12] = (byte)((b0 + 2 * b1) / 3);
                c[13] = (byte)((g0 + 2 * g1) / 3);
                c[14] = (byte)((r0 + 2 * r1) / 3);
                c[15] = 255;
            }
        }

        /// <summary>Schreibt einen 4x4-Block, mit Randbehandlung fuer nicht durch 4 teilbare Groessen.</summary>
        private static void EmitBlock(byte[] dst, int w, int h, int bx, int by,
                                      byte[] table, uint bits, byte[] alpha)
        {
            for (int py = 0; py < 4; py++)
            {
                int y = by * 4 + py;
                if (y >= h) break;
                for (int px = 0; px < 4; px++)
                {
                    int x = bx * 4 + px;
                    if (x >= w) continue;

                    int i = py * 4 + px;
                    int sel = (int)((bits >> (2 * i)) & 0x3);
                    int o = (y * w + x) * 4;
                    dst[o] = table[sel * 4];
                    dst[o + 1] = table[sel * 4 + 1];
                    dst[o + 2] = table[sel * 4 + 2];
                    dst[o + 3] = alpha != null ? alpha[i] : table[sel * 4 + 3];
                }
            }
        }

        private static byte[] DecodeUncompressed(byte[] src, int offset, int w, int h,
            uint bits, uint rMask, uint gMask, uint bMask, uint aMask)
        {
            int bpp = (int)bits / 8;
            if (bpp != 3 && bpp != 4)
                throw new NotSupportedException("Unkomprimiertes DDS mit " + bits + " Bit wird nicht unterstuetzt.");
            Require(src, offset, w * h * bpp, "RGB");

            int rs = MaskShift(rMask), gs = MaskShift(gMask), bs = MaskShift(bMask), as_ = MaskShift(aMask);
            var dst = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                int p = offset + i * bpp;
                uint v = (uint)(src[p] | (src[p + 1] << 8) | (src[p + 2] << 16));
                if (bpp == 4) v |= (uint)src[p + 3] << 24;

                int o = i * 4;
                dst[o] = Chan(v, bMask, bs);
                dst[o + 1] = Chan(v, gMask, gs);
                dst[o + 2] = Chan(v, rMask, rs);
                dst[o + 3] = aMask != 0 ? Chan(v, aMask, as_) : (byte)255;
            }
            return dst;
        }

        private static int MaskShift(uint mask)
        {
            if (mask == 0) return 0;
            int s = 0;
            while ((mask & 1) == 0) { mask >>= 1; s++; }
            return s;
        }

        private static byte Chan(uint value, uint mask, int shift)
        {
            if (mask == 0) return 255;
            uint m = mask >> shift;
            uint v = (value & mask) >> shift;
            return m == 0 ? (byte)255 : (byte)(v * 255 / m);
        }

        private static void Require(byte[] data, int offset, int need, string what)
        {
            if (offset + need > data.Length)
                throw new InvalidDataException(
                    string.Format("DDS abgeschnitten: {0} braucht {1} Bytes ab {2}, Datei hat {3}.",
                        what, need, offset, data.Length));
        }

        private static uint U32(byte[] d, int o)
        {
            return (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        }

        private static string FourCcToString(uint v)
        {
            return new string(new[] { (char)(v & 0xFF), (char)((v >> 8) & 0xFF), (char)((v >> 16) & 0xFF), (char)((v >> 24) & 0xFF) });
        }
    }
}
