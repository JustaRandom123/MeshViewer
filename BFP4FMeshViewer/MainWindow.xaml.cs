using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using BFP4FMeshViewer.Bf2;

namespace BFP4FMeshViewer
{
    public partial class MainWindow : Window
    {
        public sealed class MeshEntry
        {
            public string FullPath { get; set; }
            public string Display { get; set; }
            public override string ToString() { return Display; }
        }

        private string _rootFolder;
        private TextureLibrary _textures;
        private Model3DGroup _builtModel;
        private Rect3D _builtBounds;
        private List<MeshEntry> _allMeshes = new List<MeshEntry>();
        private BundledMesh _current;

        // Orbit-Kamera
        private double _yaw = 0.7, _pitch = 0.35, _distance = 3;
        private Point3D _target = new Point3D(0, 0, 0);
        private double _modelRadius = 1;

        private System.Windows.Point _lastMouse;
        private bool _orbiting, _panning;
        private WindowState _preFullscreenState;
        private WindowStyle _preFullscreenStyle;

        public MainWindow()
        {
            InitializeComponent();
            UpdateCamera();

            RenderHost.MouseLeftButtonDown += Viewport_MouseLeftDown;
            RenderHost.MouseLeftButtonUp += Viewport_MouseUp;
            RenderHost.MouseRightButtonDown += Viewport_MouseRightDown;
            RenderHost.MouseRightButtonUp += Viewport_MouseUp;
            RenderHost.MouseMove += Viewport_MouseMove;
            RenderHost.MouseWheel += Viewport_MouseWheel;
            PreviewKeyDown += OnKeyDown;
        }

        // ------------------------------------------------------------ Laden

        private void BtnFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Ordner mit .bundledmesh-Dateien und Texturen waehlen",
                ShowNewFolderButton = false
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            LoadFolder(dlg.SelectedPath);
        }

        public void LoadFolder(string folder)
        {
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                _rootFolder = folder;
                _textures = new TextureLibrary(folder);

                var files = Directory.GetFiles(folder, "*.bundledmesh", SearchOption.AllDirectories)
                                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

                _allMeshes = files.Select(f => new MeshEntry
                {
                    FullPath = f,
                    Display = f.Substring(folder.Length).TrimStart('\\', '/')
                }).ToList();

                ApplyFilter();

                TxtFolder.Text = folder;
                TxtFolder.ToolTip = folder;
                TxtStatus.Text = string.Format("{0} Meshes, {1} Texturdateien gefunden.",
                    _allMeshes.Count, _textures.FileCount);

                if (_allMeshes.Count == 0)
                    TxtStatus.Text += " Keine .bundledmesh-Datei im Ordner.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Ordner konnte nicht gelesen werden",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void ApplyFilter()
        {
            string q = TxtFilter.Text == null ? "" : TxtFilter.Text.Trim();
            LstMeshes.ItemsSource = q.Length == 0
                ? _allMeshes
                : _allMeshes.Where(m => m.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded) ApplyFilter();
        }

        // ------------------------------------------------------------ Auswahl

        private void LstMeshes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var entry = LstMeshes.SelectedItem as MeshEntry;
            if (entry == null) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                _current = BundledMesh.Load(entry.FullPath);

                CmbLod.SelectionChanged -= CmbLod_SelectionChanged;
                CmbLod.Items.Clear();
                foreach (var l in _current.EnumerateLods())
                    CmbLod.Items.Add(l.ToString());
                CmbLod.SelectedIndex = 0;
                CmbLod.SelectionChanged += CmbLod_SelectionChanged;

                Render(true);
            }
            catch (Exception ex)
            {
                ModelHost.Content = null;
                _current = null;
                CmbLod.Items.Clear();
                TxtInfo.Text = "";
                TxtStatus.Text = "Fehler: " + ex.Message;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void CmbLod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_current != null) Render(true);
        }

        private void CmbSlot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_current != null && IsLoaded) Render(false);
        }

        private void ChkOpaque_Changed(object sender, RoutedEventArgs e)
        {
            if (_current != null && IsLoaded) Render(false);
        }

        private void Render(bool frameCamera)
        {
            try
            {
                int lod = Math.Max(0, CmbLod.SelectedIndex);
                int slot = Math.Max(0, CmbSlot.SelectedIndex);
                bool forceOpaque = ChkOpaque.IsChecked == true;

                var built = MeshBuilder.Build(_current, lod, _textures, slot, forceOpaque);
                ModelHost.Content = built.Model;
                _builtModel = built.Model;
                _builtBounds = built.Bounds;

                if (frameCamera) FrameBounds(built.Bounds);

                var head = _current.Header;
                TxtInfo.Text = string.Format("{0}  ·  v{1}  ·  {2} Tris  ·  {3} Materialien",
                    Path.GetFileName(_current.SourcePath), head.Version,
                    built.TriangleCount, _current.GeomMaterials[lod].Materials.Count);

                var notes = built.Notes.Distinct().Take(4);
                string note = string.Join(", ", notes);
                TxtStatus.Text = string.IsNullOrEmpty(note) ? "" : "Texturen: " + note;
                if (_current.TrailingBytes != 0)
                    TxtStatus.Text += string.Format("  (Achtung: {0} Bytes am Dateiende nicht geparst)",
                        _current.TrailingBytes);
            }
            catch (Exception ex)
            {
                ModelHost.Content = null;
                _builtModel = null;
                TxtStatus.Text = "Fehler: " + ex.Message;
            }
        }

        // ------------------------------------------------------------ Screenshot

        private void BtnShot_Click(object sender, RoutedEventArgs e)
        {
            SaveSnapshot();
        }

        private void SaveSnapshot()
        {
            if (_builtModel == null || _current == null)
            {
                TxtStatus.Text = "Kein Modell geladen.";
                return;
            }

            int w, h;
            if (!int.TryParse(TxtShotW.Text, out w) || w < 1 || w > 8192 ||
                !int.TryParse(TxtShotH.Text, out h) || h < 1 || h > 8192)
            {
                TxtStatus.Text = "Bildgroesse ungueltig (1 bis 8192).";
                return;
            }

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                var img = Snapshot.Render(
                    _builtModel, _builtBounds,
                    _yaw, _pitch, _target, _distance,
                    w, h, Camera.FieldOfView,
                    ChkAutoFit.IsChecked == true, 0.04, 8);

                string dir = Path.Combine(_rootFolder ?? Path.GetDirectoryName(_current.SourcePath),
                                          "screenshots");
                string file = Path.Combine(dir,
                    Path.GetFileNameWithoutExtension(_current.SourcePath) + ".png");

                Snapshot.SavePng(img, file);
                TxtStatus.Text = string.Format("Gespeichert: {0} ({1}x{2})", file, w, h);
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "Screenshot fehlgeschlagen: " + ex.Message;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        // ------------------------------------------------------------ Kamera

        private void FrameBounds(Rect3D b)
        {
            _target = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            _modelRadius = 0.5 * Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ);
            if (_modelRadius <= 1e-6) _modelRadius = 1;

            double fov = Camera.FieldOfView * Math.PI / 180.0;
            _distance = _modelRadius / Math.Sin(fov / 2) * 1.25;
            UpdateCamera();
        }

        private void UpdateCamera()
        {
            double cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
            var dir = new Vector3D(Math.Cos(_yaw) * cp, sp, Math.Sin(_yaw) * cp);
            var pos = _target + dir * _distance;

            Camera.Position = pos;
            Camera.LookDirection = _target - pos;
            Camera.UpDirection = new Vector3D(0, 1, 0);
            Camera.NearPlaneDistance = Math.Max(_distance * 0.001, 1e-4);
            Camera.FarPlaneDistance = _distance * 20 + 100;
        }

        private void Viewport_MouseLeftDown(object sender, MouseButtonEventArgs e)
        {
            _orbiting = true; _lastMouse = e.GetPosition(RenderHost);
            RenderHost.CaptureMouse();
        }

        private void Viewport_MouseRightDown(object sender, MouseButtonEventArgs e)
        {
            _panning = true; _lastMouse = e.GetPosition(RenderHost);
            RenderHost.CaptureMouse();
        }

        private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _orbiting = _panning = false;
            RenderHost.ReleaseMouseCapture();
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_orbiting && !_panning) return;
            var p = e.GetPosition(RenderHost);
            double dx = p.X - _lastMouse.X, dy = p.Y - _lastMouse.Y;
            _lastMouse = p;

            if (_orbiting)
            {
                _yaw += dx * 0.01;
                _pitch = Clamp(_pitch + dy * 0.01, -1.53, 1.53);
            }
            else
            {
                var look = Camera.LookDirection; look.Normalize();
                var right = Vector3D.CrossProduct(look, new Vector3D(0, 1, 0));
                if (right.Length < 1e-6) right = new Vector3D(1, 0, 0);
                right.Normalize();
                var up = Vector3D.CrossProduct(right, look); up.Normalize();

                double scale = _distance * 0.0015;
                _target -= right * (dx * scale);
                _target += up * (dy * scale);
            }
            UpdateCamera();
        }

        private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _distance *= e.Delta > 0 ? 0.88 : 1.0 / 0.88;
            _distance = Clamp(_distance, _modelRadius * 0.05, _modelRadius * 60);
            UpdateCamera();
        }

        // ------------------------------------------------------------ Tasten

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrlS = e.Key == Key.S
                && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            // Im Textfeld nur Strg+S und F11 durchlassen, sonst tippt man nicht mehr
            if (e.OriginalSource is TextBox && e.Key != Key.F11 && !ctrlS) return;

            if (ctrlS)
            {
                SaveSnapshot();
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Tab:
                    SidePanel.Visibility = SidePanel.Visibility == Visibility.Visible
                        ? Visibility.Collapsed : Visibility.Visible;
                    e.Handled = true;
                    break;

                case Key.F:
                    if (_current != null) Render(true);
                    e.Handled = true;
                    break;

                case Key.F11:
                    ToggleFullscreen();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    if (WindowStyle == WindowStyle.None) ToggleFullscreen();
                    e.Handled = true;
                    break;
            }
        }

        private void ToggleFullscreen()
        {
            if (WindowStyle != WindowStyle.None)
            {
                _preFullscreenState = WindowState;
                _preFullscreenStyle = WindowStyle;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;   // erzwingt Neuberechnung
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = _preFullscreenStyle;
                ResizeMode = ResizeMode.CanResize;
                WindowState = _preFullscreenState;
            }
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
