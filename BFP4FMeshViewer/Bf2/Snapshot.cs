using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace BFP4FMeshViewer.Bf2
{
    /// <summary>
    /// Rendert das Modell abseits des Bildschirms in eine feste Bildgroesse,
    /// mit transparentem Hintergrund und Supersampling.
    /// </summary>
    public static class Snapshot
    {
        /// <summary>Gesamtpixel der Zwischenstufe, begrenzt den Speicherbedarf.</summary>
        private const long MaxSuperPixels = 16L * 1024 * 1024;

        public static BitmapSource Render(
            Model3DGroup model, Rect3D bounds,
            double yaw, double pitch, Point3D target, double distance,
            int width, int height, double fovDeg,
            bool autoFit, double marginFraction, int supersample)
        {
            if (model == null) throw new ArgumentNullException("model");
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException("width");

            // Supersampling so weit reduzieren, dass die Zwischenstufe handhabbar bleibt
            if (supersample < 1) supersample = 1;
            while (supersample > 1 && (long)width * height * supersample * supersample > MaxSuperPixels)
                supersample--;

            int bigW = width * supersample, bigH = height * supersample;
            double aspect = (double)width / height;

            var camera = BuildCamera(bounds, yaw, pitch, target, distance,
                                     fovDeg, aspect, autoFit, marginFraction);

            var viewport = new Viewport3D
            {
                Camera = camera,
                ClipToBounds = true,
                Width = bigW,
                Height = bigH
            };

            // Beleuchtung identisch zur Ansicht im Fenster
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(0x6A, 0x6A, 0x70)));
            lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.4, -0.6, -0.7)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x8A, 0x8F, 0x99), new Vector3D(0.7, -0.2, 0.5)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x55, 0x58, 0x5F), new Vector3D(0.1, 0.9, 0.2)));
            viewport.Children.Add(new ModelVisual3D { Content = lights });

            // model ist frozen und darf in mehreren Visual-Trees haengen
            viewport.Children.Add(new ModelVisual3D { Content = model });

            var size = new Size(bigW, bigH);
            viewport.Measure(size);
            viewport.Arrange(new Rect(size));
            viewport.UpdateLayout();

            // Kein Hintergrund gesetzt -> Alpha bleibt 0
            var big = new RenderTargetBitmap(bigW, bigH, 96, 96, PixelFormats.Pbgra32);
            big.Render(viewport);

            if (supersample == 1)
            {
                big.Freeze();
                return big;
            }

            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen())
                dc.DrawImage(big, new Rect(0, 0, width, height));

            var final = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            final.Render(dv);
            final.Freeze();
            return final;
        }

        private static PerspectiveCamera BuildCamera(
            Rect3D bounds, double yaw, double pitch, Point3D target, double distance,
            double fovDeg, double aspect, bool autoFit, double marginFraction)
        {
            double cp = Math.Cos(pitch), sp = Math.Sin(pitch);
            var dir = new Vector3D(Math.Cos(yaw) * cp, sp, Math.Sin(yaw) * cp);

            var center = autoFit
                ? new Point3D(bounds.X + bounds.SizeX / 2,
                              bounds.Y + bounds.SizeY / 2,
                              bounds.Z + bounds.SizeZ / 2)
                : target;

            double d = distance;
            if (autoFit && !bounds.IsEmpty)
                d = FitDistance(bounds, center, dir, fovDeg, aspect, marginFraction);

            var pos = center + dir * d;
            return new PerspectiveCamera
            {
                FieldOfView = fovDeg,
                Position = pos,
                LookDirection = center - pos,
                UpDirection = new Vector3D(0, 1, 0),
                NearPlaneDistance = Math.Max(d * 0.001, 1e-4),
                FarPlaneDistance = d * 20 + 100
            };
        }

        /// <summary>
        /// Kleinster Kameraabstand, bei dem alle acht Eckpunkte der Bounding Box
        /// im Bild liegen. Exakt statt ueber eine Huellkugel, sonst wird ein
        /// langes schmales Objekt viel zu klein.
        /// </summary>
        private static double FitDistance(Rect3D b, Point3D center, Vector3D dir,
                                          double fovDeg, double aspect, double marginFraction)
        {
            // Kamerabasis: forward zeigt vom Betrachter zum Objekt
            var forward = -dir;
            forward.Normalize();

            var up = new Vector3D(0, 1, 0);
            var right = Vector3D.CrossProduct(forward, up);
            if (right.Length < 1e-6) right = new Vector3D(1, 0, 0);
            right.Normalize();
            up = Vector3D.CrossProduct(right, forward);
            up.Normalize();

            // WPF interpretiert FieldOfView horizontal, vertikal folgt aus dem Seitenverhaeltnis
            double tanH = Math.Tan(fovDeg * Math.PI / 180.0 / 2.0);
            double tanV = tanH / aspect;

            double need = 0;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Point3D(
                    (i & 1) == 0 ? b.X : b.X + b.SizeX,
                    (i & 2) == 0 ? b.Y : b.Y + b.SizeY,
                    (i & 4) == 0 ? b.Z : b.Z + b.SizeZ);

                var r = corner - center;
                double rx = Vector3D.DotProduct(r, right);
                double ry = Vector3D.DotProduct(r, up);
                double rz = Vector3D.DotProduct(r, forward);   // positiv = weiter weg

                need = Math.Max(need, Math.Abs(rx) / tanH - rz);
                need = Math.Max(need, Math.Abs(ry) / tanV - rz);
            }

            if (marginFraction > 0) need *= 1.0 + marginFraction;
            if (need <= 1e-6) need = 1;
            return need;
        }

        /// <summary>
        /// Adds the soft drop shadow the original BFP4F attachment icons carry.
        /// The parameters were fitted against 4327.png: the alpha falloff of the
        /// result matches the original to within 0.013 mean error.
        /// </summary>
        public static BitmapSource ApplyDropShadow(BitmapSource source)
        {
            // Measured on the 400x180 originals, scaled with the image width.
            const double SigmaAt400 = 10.0;
            const double Gain = 2.5;      // denser near the object than a plain blur would be
            const double Cap = 0.85;
            const double OffsetYAt400 = 5.2;
            const double ShadowLevel = 37.0 / 255.0;

            int w = source.PixelWidth, h = source.PixelHeight;
            if (w < 2 || h < 2) return source;

            // Straight alpha - RenderTargetBitmap hands out premultiplied Pbgra32
            BitmapSource straight = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int stride = w * 4;
            var px = new byte[h * stride];
            straight.CopyPixels(px, stride, 0);

            var alpha = new double[w * h];
            for (int i = 0; i < w * h; i++)
                alpha[i] = px[i * 4 + 3] / 255.0;

            double scale = w / 400.0;
            double sigma = SigmaAt400 * scale;
            double offsetY = OffsetYAt400 * scale;
            if (sigma < 0.5) return source;

            var blurred = GaussianBlur(alpha, w, h, sigma);

            var outPx = new byte[h * stride];
            for (int y = 0; y < h; y++)
            {
                // sample the mask offsetY higher up, so the shadow lands below the object
                double sy = y - offsetY;
                int y0 = (int)Math.Floor(sy);
                double fy = sy - y0;

                for (int x = 0; x < w; x++)
                {
                    double s0 = (y0 >= 0 && y0 < h) ? blurred[y0 * w + x] : 0.0;
                    double s1 = (y0 + 1 >= 0 && y0 + 1 < h) ? blurred[(y0 + 1) * w + x] : 0.0;
                    double sh = s0 * (1 - fy) + s1 * fy;

                    sh *= Gain;
                    if (sh > Cap) sh = Cap;
                    if (sh < 0) sh = 0;

                    int o = y * stride + x * 4;
                    double a = px[o + 3] / 255.0;
                    double wsh = sh * (1 - a);
                    double oa = a + wsh;

                    if (oa <= 1e-6)
                    {
                        outPx[o] = outPx[o + 1] = outPx[o + 2] = outPx[o + 3] = 0;
                        continue;
                    }

                    for (int c = 0; c < 3; c++)
                    {
                        double v = (px[o + c] / 255.0 * a + ShadowLevel * wsh) / oa;
                        int b = (int)(v * 255 + 0.5);
                        outPx[o + c] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                    }
                    int ab = (int)(oa * 255 + 0.5);
                    outPx[o + 3] = (byte)(ab < 0 ? 0 : ab > 255 ? 255 : ab);
                }
            }

            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, outPx, stride);
            result.Freeze();
            return result;
        }

        /// <summary>Separable Gaussian over a single channel.</summary>
        private static double[] GaussianBlur(double[] src, int w, int h, double sigma)
        {
            int radius = (int)Math.Ceiling(sigma * 3);
            if (radius < 1) radius = 1;

            var kernel = new double[radius * 2 + 1];
            double sum = 0, twoSigmaSq = 2 * sigma * sigma;
            for (int i = -radius; i <= radius; i++)
            {
                double v = Math.Exp(-(i * i) / twoSigmaSq);
                kernel[i + radius] = v;
                sum += v;
            }
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;

            var tmp = new double[w * h];
            var dst = new double[w * h];

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double acc = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int xx = x + k;
                        if (xx < 0 || xx >= w) continue;   // outside the frame counts as transparent
                        acc += src[y * w + xx] * kernel[k + radius];
                    }
                    tmp[y * w + x] = acc;
                }

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double acc = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int yy = y + k;
                        if (yy < 0 || yy >= h) continue;
                        acc += tmp[yy * w + x] * kernel[k + radius];
                    }
                    dst[y * w + x] = acc;
                }

            return dst;
        }

        public static void SavePng(BitmapSource image, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                encoder.Save(fs);
        }
    }
}
