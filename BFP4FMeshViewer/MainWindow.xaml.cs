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
        private bool _syncingCamFields;
        private WindowState _preFullscreenState;
        private WindowStyle _preFullscreenStyle;

        public MainWindow()
        {
            InitializeComponent();
            UpdateCamera();
            SyncCamFields();

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
                Description = "Select folder with .bundledmesh files and textures",
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
                TxtStatus.Text = string.Format("{0} Meshes, {1} Texture files found.",
                    _allMeshes.Count, _textures.FileCount);

                if (_allMeshes.Count == 0)
                    TxtStatus.Text += " No .bundledmesh files in the folder.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Folder could not be read",
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
                TxtInfo.Text = string.Format("{0}  ·  v{1}  ·  {2} Tris  ·  {3} Materials",
                    Path.GetFileName(_current.SourcePath), head.Version,
                    built.TriangleCount, _current.GeomMaterials[lod].Materials.Count);

                var notes = built.Notes.Distinct().Take(4);
                string note = string.Join(", ", notes);
                TxtStatus.Text = string.IsNullOrEmpty(note) ? "" : "Textures: " + note;
                if (_current.TrailingBytes != 0)
                    TxtStatus.Text += string.Format("  (Warning: {0} bytes at end of file not parsed)",
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
                TxtStatus.Text = "No model loaded.";
                return;
            }

            int w, h;
            if (!int.TryParse(TxtShotW.Text, out w) || w < 1 || w > 8192 ||
                !int.TryParse(TxtShotH.Text, out h) || h < 1 || h > 8192)
            {
                TxtStatus.Text = "Invalid image size (1 to 8192).";
                return;
            }

            double marginPct;
            if (!double.TryParse(TxtMargin.Text, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out marginPct))
                double.TryParse(TxtMargin.Text, out marginPct);
            if (marginPct < 0 || marginPct > 200) marginPct = 4;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                var img = Snapshot.Render(
                    _builtModel, _builtBounds,
                    _yaw, _pitch, _target, _distance,
                    w, h, Camera.FieldOfView,
                    ChkAutoFit.IsChecked == true, marginPct / 100.0, 8);

                if (ChkShadow.IsChecked == true)
                    img = Snapshot.ApplyDropShadow(img);

                string dir = Path.Combine(_rootFolder ?? Path.GetDirectoryName(_current.SourcePath),
                                          "screenshots");
                string file = Path.Combine(dir,
                    Path.GetFileNameWithoutExtension(_current.SourcePath) + ".png");

                Snapshot.SavePng(img, file);
                TxtStatus.Text = string.Format("Saved: {0} ({1}x{2})", file, w, h);
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "Failed to take screenshot: " + ex.Message;
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

        /// <summary>Degree fields -> camera. Inactive while the fields are being updated themselves.</summary>
        private void CamField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded || _syncingCamFields) return;

            double yawDeg, pitchDeg;
            if (!TryParseAngle(TxtYaw.Text, out yawDeg)) return;
            if (!TryParseAngle(TxtPitch.Text, out pitchDeg)) return;

            _yaw = yawDeg * Math.PI / 180.0;
            _pitch = Clamp(pitchDeg, -89, 89) * Math.PI / 180.0;
            UpdateCamera();
        }

        private static bool TryParseAngle(string s, out double value)
        {
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out value))
                return true;
            return double.TryParse(s, out value);   // also accepts a comma as decimal separator
        }

        /// <summary>Camera -> degree fields, after dragging with the mouse.</summary>
        private void SyncCamFields()
        {
            if (TxtYaw == null || TxtPitch == null) return;
            _syncingCamFields = true;
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                TxtYaw.Text = NormalizeDeg(_yaw * 180.0 / Math.PI).ToString("0.#", ci);
                TxtPitch.Text = (_pitch * 180.0 / Math.PI).ToString("0.#", ci);
            }
            finally { _syncingCamFields = false; }
        }

        private static double NormalizeDeg(double d)
        {
            d %= 360.0;
            if (d > 180.0) d -= 360.0;
            if (d < -180.0) d += 360.0;
            return d;
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
                SyncCamFields();
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
