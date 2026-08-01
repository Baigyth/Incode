using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Incode
{
    /// <summary>
    /// Transparent topmost overlay form that draws a semi-transparent 3×3 grid HUD
    /// showing the current grid cells and their key labels.
    /// Uses WS_EX_LAYERED for uniform alpha blending and WS_EX_TRANSPARENT so mouse
    /// events pass through to windows beneath.
    /// </summary>
    internal class GridOverlayForm : Form
    {
        private Rectangle _gridBounds;
        private int _gridLevel; // 1 = FullScreen, 2 = SubCell
        private readonly Keys[] _keyLabels = new Keys[9];
        private float _fontSize = 48f;
        private Font _font;

        private const byte FormAlpha = 100; // ~39% opacity — semi-transparent

        // GDI+ resources created per-instance and disposed properly
        private readonly Pen _gridPen;
        private readonly SolidBrush _cellBg;
        private readonly SolidBrush _textBrush;
        private readonly StringFormat _centerFormat;

        public GridOverlayForm()
        {
            _gridPen = new Pen(Color.FromArgb(180, Color.White), 2);
            _cellBg = new SolidBrush(Color.FromArgb(50, 0, 100, 220));
            _textBrush = new SolidBrush(Color.White);
            _centerFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            // Off-screen until first UpdateOverlay call
            Bounds = new Rectangle(-32000, -32000, 1, 1);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                // WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                p.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000;
                return p;
            }
        }

        internal void SetFontSize(float size)
        {
            if (_font == null || Math.Abs(_fontSize - size) > 0.5f)
            {
                _fontSize = size;
                _font?.Dispose();
                _font = new Font("Segoe UI", _fontSize, FontStyle.Bold);
                Invalidate();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!DesignMode)
                NativeMethods.SetLayeredWindowAttributes(Handle, 0, FormAlpha, NativeMethods.LWA_ALPHA);
        }

        /// <summary>
        /// Update the overlay bounds, grid data, and repaint.
        /// Called from IncodeEngine on the UI thread.
        /// </summary>
        public void UpdateOverlay(Rectangle gridBounds, IReadOnlyDictionary<Keys, int> positionMap, int gridLevel)
        {
            _gridBounds = gridBounds;
            _gridLevel = gridLevel;

            // Build reverse lookup: cellIndex → key
            Array.Clear(_keyLabels, 0, _keyLabels.Length);
            if (positionMap != null)
            {
                foreach (var kvp in positionMap)
                {
                    if (kvp.Value >= 0 && kvp.Value < 9)
                        _keyLabels[kvp.Value] = kvp.Key;
                }
            }

            // Resize and reposition to match the grid area
            Bounds = gridBounds;
            Invalidate();
        }

        /// <summary>
        /// Hide the overlay and move it off-screen to prevent visual residue.
        /// </summary>
        public void HideOverlay()
        {
            Hide();
            Bounds = new Rectangle(-32000, -32000, 1, 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (_gridBounds.Width <= 0 || _gridBounds.Height <= 0)
                return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int cellW = _gridBounds.Width / 3;
            int cellH = _gridBounds.Height / 3;

            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    int idx = row * 3 + col;
                    int x = col * cellW;
                    int y = row * cellH;
                    var cellRect = new Rectangle(x, y, cellW, cellH);

                    // Fill cell background (semi-transparent blue)
                    g.FillRectangle(_cellBg, cellRect);

                    // Draw cell border
                    g.DrawRectangle(_gridPen, cellRect);

                    // Draw key label centered in cell
                    Keys key = _keyLabels[idx];
                    if (key != Keys.None)
                    {
                        string label = StripKeySuffix(key.ToString());
                        g.DrawString(label, _font, _textBrush, cellRect, _centerFormat);
                    }
                }
            }

            // Draw a subtle outer border
            using (var outerPen = new Pen(Color.FromArgb(200, Color.White), 3))
            {
                g.DrawRectangle(outerPen, 0, 0, _gridBounds.Width - 1, _gridBounds.Height - 1);
            }
        }

        private static string StripKeySuffix(string name)
        {
            // "QKey" → "Q", "Space" → "Space", "RControlKey" → "RControl"
            if (name.EndsWith("Key") && name.Length > 3)
                return name.Substring(0, name.Length - 3);
            if (name.EndsWith("Key") && name.Length == 3)
                return name[0].ToString();
            return name;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _gridPen?.Dispose();
                _cellBg?.Dispose();
                _textBrush?.Dispose();
                _centerFormat?.Dispose();
                _font?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class NativeMethods
    {
        public const byte LWA_ALPHA = 0x02;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    }
}
