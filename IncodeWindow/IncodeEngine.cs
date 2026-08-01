// (C) 2015-20 christian.schladetsch@gmail.com

using System.Runtime.InteropServices;

namespace Incode
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Windows.Forms;
    using MouseKeyboardActivityMonitor;
    using MouseKeyboardActivityMonitor.WinApi;
    using WindowsInput;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Core engine: hooks, key processing, mouse simulation, config.
    /// Windowless — no Form dependency.
    /// </summary>
    internal class IncodeEngine : IDisposable
    {
        /// <summary>
        /// Grid navigation nesting level.
        /// </summary>
        internal enum GridLevel
        {
            Inactive,   // Not in grid mode
            FullScreen, // First level: full-screen 3x3
            SubCell,    // Second level: sub-grid within selected cell
        }

        public float Speed => _config.Speed;
        public float Accel => _config.Accel;
        public float AccelDelay => _config.AccelDelay;
        public float ScrollScale => _config.ScrollScale;
        public float ScrollAccel => _config.ScrollAccel;
        public int ScrollAmount => _config.ScrollAmount;
        public float FilterRes => _config.MouseFilterResonance;
        public float FilterFreq => _config.MouseFilterFrequency;

        private bool Controlled
        {
            get => _controlled;
            set
            {
                _controlled = value;
                _timer.Enabled = value;

                if (value)
                    _controlStartTime = DateTime.Now;
                else
                {
                    if (_mouseLeftDown)
                    {
                        _mouseOut.LeftButtonUp();
                        _mouseLeftDown = false;
                    }
                    if (_mouseRightDown)
                    {
                        _mouseOut.RightButtonUp();
                        _mouseRightDown = false;
                    }
                }

                ResetMouseFilter();
            }
        }

        private KeyboardHookListener _keyboardIn;
        private MouseHookListener _mouseIn;
        private InputSimulator _inputSimulator;
        private IMouseSimulator _mouseOut;
        private IKeyboardSimulator _keyboardOut;
        private bool _controlled;
        private const float Frequency = 100.0f;
        private System.Windows.Forms.Timer _timer;
        private float _tx, _ty;
        private LowPass _mx = new LowPass(Frequency, DefaultMouseFilterFrequency, DefaultMouseFilterResonance);
        private LowPass _my = new LowPass(Frequency, DefaultMouseFilterFrequency, DefaultMouseFilterResonance);
        private readonly Dictionary<Keys, Action> _keys = new Dictionary<Keys, Action>();
        private GridLevel _gridLevel;
        private Rectangle _gridBounds;
        private Keys _gridActivateKey = Keys.None;
        private readonly Dictionary<Keys, int> _gridPositionMap = new Dictionary<Keys, int>();
        private bool _subGridEnabled;
        private GridOverlayForm _gridOverlay;
        private readonly Stopwatch _watch = new Stopwatch();
        private DateTime _controlStartTime;
        private Keys _overrideKey = Keys.RControlKey;
        private Keys _fineModifierKey = Keys.None;
        private bool _fineModifierHeld;
        private Keys _gridKeyDownEaten = Keys.None;
        private Keys _fineModKeyDownEaten = Keys.None;
        private const string ConfigFileName = "Config.json";

        // Default config values — single source of truth
        private const float DefaultSpeed = 150;
        private const float DefaultAccel = 0;
        private const float DefaultAccelDelay = 0.5f;
        private const float DefaultFineSpeed = 50;
        private const float DefaultScrollScale = 30;
        private const float DefaultScrollAccel = 0;
        private const int DefaultScrollAmount = 3;
        private const float DefaultMouseFilterFrequency = 2000;
        private const float DefaultMouseFilterResonance = 2.5f;
        private const string DefaultInterruptKey = "RControlKey";
        private const string DefaultFineModifierKey = "LShiftKey";
        private const bool DefaultSubGridEnabled = false;
        private const float DefaultGridLabelFontSize = 48f;

        private static readonly string[] DefaultGridKeys = new[] { "Q", "W", "E", "A", "S", "D", "Z", "X", "C" };
        private static readonly Dictionary<string, string> DefaultKeymap = new Dictionary<string, string>
        {
            ["Up"] = "W", ["Down"] = "S", ["Left"] = "A", ["Right"] = "D",
            ["ScrollUp"] = "E", ["ScrollDown"] = "C",
            ["LeftDown"] = "Space", ["RightDown"] = "F",
            ["ScrollUpAmount"] = "R", ["ScrollDownAmount"] = "V"
        };
        private bool _mouseLeftDown;
        private bool _mouseRightDown;
        private ConfigData _config;
        private bool _disposed;
        private readonly object _syncRoot = new object();

        public IncodeEngine()
        {
            Configure();
            InstallHooks();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _timer?.Stop();
            _timer?.Dispose();

            _keyboardIn?.Stop();
            _keyboardIn?.Dispose();

            _mouseIn?.Stop();
            _mouseIn?.Dispose();

            _gridOverlay?.Dispose();
            _gridOverlay = null;
        }

        private void Configure()
        {
            ReadConfig();
            LoadKeyMap();
            LoadGridMap();
        }

        private void LoadKeyMap()
        {
            _keys.Clear();

            if (!string.IsNullOrEmpty(_config.InterruptKey)
                && Enum.TryParse(_config.InterruptKey, out Keys interruptKey))
            {
                _overrideKey = interruptKey;
            }

            if (!string.IsNullOrEmpty(_config.FineModifierKey)
                && Enum.TryParse(_config.FineModifierKey, out Keys fineModKey))
            {
                _fineModifierKey = fineModKey;
            }
            else
            {
                _fineModifierKey = Keys.None;
            }

            if (_config.Keymap != null && _config.Keymap.Count > 0)
            {
                foreach (var kvp in _config.Keymap)
                {
                    if (Enum.TryParse(kvp.Key, out Command cmd) && Enum.TryParse(kvp.Value, out Keys key))
                    {
                        if (!_keys.ContainsKey(key))
                            _keys.Add(key, new Action(cmd));
                    }
                }
                return;
            }

        }

        private void LoadGridMap()
        {
            _gridPositionMap.Clear();

            // Parse GridKey
            if (!string.IsNullOrEmpty(_config.GridKey)
                && Enum.TryParse(_config.GridKey, out Keys gridKey))
            {
                _gridActivateKey = gridKey;
            }
            else
            {
                _gridActivateKey = Keys.None;
            }

            // GridKey not configured → skip grid mode entirely
            if (_gridActivateKey == Keys.None)
                return;

            // Build grid position map from the configured keys array
            var gridKeys = _config.GridKeys;
            if (gridKeys == null || gridKeys.Length < 9)
            {
                gridKeys = DefaultGridKeys;
            }

            for (int i = 0; i < 9 && i < gridKeys.Length; i++)
            {
                if (!string.IsNullOrEmpty(gridKeys[i])
                    && Enum.TryParse(gridKeys[i], out Keys key))
                {
                    if (!_gridPositionMap.ContainsKey(key))
                        _gridPositionMap.Add(key, i);
                }
            }

            _subGridEnabled = _config.SubGridEnabled;
        }

        private void InstallHooks()
        {
            _inputSimulator = new InputSimulator();

            _mouseOut = _inputSimulator.Mouse;
            _keyboardOut = _inputSimulator.Keyboard;

            // Thread safety note: MouseKeyboardActivityMonitor routes hook callbacks
            // through Application.AddMessageFilter, so both OnKeyDown and OnKeyUp
            // execute on the WinForms UI message-pump thread. Direct Form/control
            // operations (GridOverlayForm.Show, .Bounds =, .Invalidate) are safe
            // without Invoke.
            _mouseIn = new MouseHookListener(new GlobalHooker()) { Enabled = true };
            _keyboardIn = new KeyboardHookListener(new GlobalHooker()) { Enabled = true };

            _keyboardIn.KeyDown += OnKeyDown;
            _keyboardIn.KeyUp += OnKeyUp;

            _timer = new System.Windows.Forms.Timer { Interval = (int)(1000 / Frequency) };
            _timer.Tick += PerformCommands;

            _watch.Start();
        }

        private void PerformCommands(object sender, EventArgs e)
        {
            var dt = _watch.ElapsedMilliseconds / 1000.0f;
            _watch.Restart();

            lock (_syncRoot)
            {
                var now = DateTime.Now;
            var earliest = ButtonsDown(DateTime.MaxValue);

            if (earliest == DateTime.MaxValue)
            {
                MoveMouse();
                return;
            }

            // for mouse movement
            var seconds = (float)(now - earliest).TotalSeconds;
            var velocity = _fineModifierHeld
                ? _config.FineSpeed
                : Speed * (1.0f + Accel * Math.Max(0, seconds - AccelDelay));
            var delta = dt * velocity;

            PerformActions(now, delta);

            MoveMouse();
            }
        }

        private DateTime ButtonsDown(DateTime earliest)
        {
            foreach (var action in _keys)
            {
                var act = action.Value;
                if (act.Command == Command.LeftDown || act.Command == Command.RightDown)
                    continue;

                if (act.Started > DateTime.MinValue && act.Started < earliest)
                    earliest = act.Started;
            }

            return earliest;
        }

        private void MoveMouse()
        {
            var fx = _mx.Next(_tx);
            var fy = _my.Next(_ty);
            var nx = (int)(fx < 0 ? (fx - 0.5f) : (fx + 0.5f));
            var ny = (int)(fy < 0 ? (fy - 0.5f) : (fy + 0.5f));

            var bounds = SystemInformation.VirtualScreen;
            var clampedX = Math.Max(bounds.Left, Math.Min(bounds.Right - 1, nx));
            var clampedY = Math.Max(bounds.Top, Math.Min(bounds.Bottom - 1, ny));

            if (clampedX != nx) _tx = clampedX;
            if (clampedY != ny) _ty = clampedY;

            Cursor.Position = new Point(clampedX, clampedY);
        }

        private void PerformActions(DateTime now, float delta)
        {
            foreach (var action in _keys)
            {
                var act = action.Value;
                if (act.Started == DateTime.MinValue)
                    continue;

                var ts = (now - act.Started).TotalSeconds;
                var accel = 1 + ScrollAccel * ts;
                var t = (int)(ts * accel * ScrollScale);

                switch (act.Command)
                {
                    case Command.Up:
                        _ty -= delta;
                        break;
                    case Command.Down:
                        _ty += delta;
                        break;
                    case Command.Left:
                        _tx -= delta;
                        break;
                    case Command.Right:
                        _tx += delta;
                        break;
                    case Command.ScrollUp:
                        _mouseOut.VerticalScroll(t);
                        break;
                    case Command.ScrollDown:
                        _mouseOut.VerticalScroll(-t);
                        break;
                }
            }
        }

        public void OnKeyDown(object sender, KeyEventArgs e)
        {
            lock (_syncRoot)
            {
                if (!Controlled)
            {
                if (e.KeyCode == _overrideKey)
                {
                    StartControl();
                    Eat(e);
                }

                return;
            }

            // GridKey press → enter grid mode
            if (_gridActivateKey != Keys.None && e.KeyCode == _gridActivateKey)
            {
                _gridLevel = GridLevel.FullScreen;
                _gridKeyDownEaten = e.KeyCode;
                var screen = Screen.FromPoint(Cursor.Position);
                _gridBounds = screen.WorkingArea;
                if (_subGridEnabled)
                    ShowGridOverlay();
                Eat(e);
                return;
            }

            // Grid mode active → only grid position keys work; all others blocked
            if (_gridLevel != GridLevel.Inactive)
            {
                if (_gridPositionMap.TryGetValue(e.KeyCode, out int cellIndex))
                {
                    if (_subGridEnabled && _gridLevel == GridLevel.FullScreen)
                    {
                        // First-level: jump to cell center using current (full screen) bounds,
                        // then narrow _gridBounds for subsequent sub-cell navigation.
                        int cellW = _gridBounds.Width / 3;
                        int cellH = _gridBounds.Height / 3;
                        int row = cellIndex / 3;
                        int col = cellIndex % 3;

                        // Jump first with the original bounds
                        ExecuteGridJump(cellIndex, _gridBounds);

                        // Then narrow bounds for future sub-cell navigation
                        _gridBounds = new Rectangle(
                            _gridBounds.Left + col * cellW,
                            _gridBounds.Top + row * cellH,
                            cellW, cellH);
                        _gridLevel = GridLevel.SubCell;
                        UpdateGridOverlay();
                    }
                    else
                    {
                        // Sub-cell level (with sub-grid) or original single-level
                        ExecuteGridJump(cellIndex, _gridBounds);
                    }
                    Eat(e);
                    return;
                }
                Eat(e);
                return;
            }

            if (!_keys.TryGetValue(e.KeyCode, out var action))
            {
                if (e.KeyCode == _overrideKey)
                {
                    Eat(e);
                    return;
                }

                if (_fineModifierKey != Keys.None && e.KeyCode == _fineModifierKey)
                {
                    _fineModifierHeld = true;
                    _fineModKeyDownEaten = e.KeyCode;
                    Eat(e);
                    return;
                }

                if (IsModifierKey(e.KeyCode))
                    return;

                Eat(e);
                return;
            }

            Eat(e);

            switch (action.Command)
            {
                case Command.ScrollUpAmount:
                    _mouseOut.VerticalScroll(ScrollAmount);
                    return;
                case Command.ScrollDownAmount:
                    _mouseOut.VerticalScroll(-ScrollAmount);
                    return;
            }

            if (action.Started != DateTime.MinValue)
                return;

            action.Started = DateTime.Now;

            switch (action.Command)
            {
                case Command.RightDown:
                    _mouseOut.RightButtonDown();
                    _mouseRightDown = true;
                    break;
                case Command.LeftDown:
                    _mouseOut.LeftButtonDown();
                    _mouseLeftDown = true;
                    break;
            }
            }
        }

        public void OnKeyUp(object sender, KeyEventArgs e)
        {
            lock (_syncRoot)
            {
                if (e.KeyCode == _overrideKey)
            {
                Eat(e);
                if (_mouseLeftDown)
                {
                    _mouseOut.LeftButtonUp();
                    _mouseLeftDown = false;
                }

                if (_mouseRightDown)
                {
                    _mouseOut.RightButtonUp();
                    _mouseRightDown = false;
                }

                _fineModifierHeld = false;
                _gridLevel = GridLevel.Inactive;
                HideGridOverlay();
                _gridKeyDownEaten = Keys.None;
                _fineModKeyDownEaten = Keys.None;
                Controlled = false;
                Trace("Not controlling");
                return;
            }

            if (!Controlled)
                return;

            // GridKey release → exit grid mode (only eat if KeyDown was also eaten)
            if (_gridActivateKey != Keys.None && e.KeyCode == _gridActivateKey)
            {
                _gridLevel = GridLevel.Inactive;
                HideGridOverlay();
                if (_gridKeyDownEaten == e.KeyCode)
                {
                    _gridKeyDownEaten = Keys.None;
                    Eat(e);
                }
                return;
            }

            // FineModifierKey release (only eat if KeyDown was also eaten)
            if (_fineModifierKey != Keys.None && e.KeyCode == _fineModifierKey)
            {
                _fineModifierHeld = false;

                // Reset all active direction-key timestamps to prevent the accumulated
                // seconds from FineModifier mode causing an instant velocity spike
                // when exiting fine mode
                var now = DateTime.Now;
                foreach (var kv in _keys)
                {
                    if (kv.Value.Started != DateTime.MinValue)
                        kv.Value.Started = now;
                }

                if (_fineModKeyDownEaten == e.KeyCode)
                {
                    _fineModKeyDownEaten = Keys.None;
                    Eat(e);
                }
                return;
            }

            if (!_keys.TryGetValue(e.KeyCode, out var action))
            {
                if (IsModifierKey(e.KeyCode))
                    return;

                Eat(e);
                return;
            }

            Eat(e);

            action.Started = DateTime.MinValue;

            switch (action.Command)
            {
                case Command.RightDown:
                    _mouseOut.RightButtonUp();
                    _mouseRightDown = false;
                    break;
                case Command.LeftDown:
                    _mouseOut.LeftButtonUp();
                    _mouseLeftDown = false;
                    break;
            }
            }
        }

        private static void Trace(string fmt, params object[] args)
            => Debug.WriteLine(fmt, args);

        private static bool IsModifierKey(Keys key)
        {
            return key == Keys.LShiftKey
                || key == Keys.RShiftKey
                || key == Keys.LControlKey
                || key == Keys.RControlKey
                || key == Keys.LMenu
                || key == Keys.RMenu;
        }

        private static void Eat(KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void StartControl()
        {
            foreach (var kv in _keys)
                kv.Value.Started = DateTime.MinValue;

            _fineModifierHeld = false;
            _gridLevel = GridLevel.Inactive;
            HideGridOverlay();
            _gridKeyDownEaten = Keys.None;
            _fineModKeyDownEaten = Keys.None;

            var pos = Cursor.Position;
            _tx = pos.X;
            _ty = pos.Y;

            _mx.Set(_tx);
            _my.Set(_ty);

            var delta = DateTime.Now - _controlStartTime;
            if (delta.TotalMilliseconds < 300)
            {
                CenterCursor();
                return;
            }

            Controlled = true;
        }

        private void CenterCursor()
        {
            var screen = Screen.FromPoint(Cursor.Position);
            var area = screen.WorkingArea;
            Cursor.Position = new Point(area.Width / 2, area.Height / 2);

            Controlled = false;
            _timer.Enabled = false;
            _watch.Restart();

            ResetMouseFilter();
        }

        private void ExecuteGridJump(int cellIndex, Rectangle bounds)
        {
            if (cellIndex < 0 || cellIndex >= 9)
                return;

            int cellWidth = bounds.Width / 3;
            int cellHeight = bounds.Height / 3;

            int row = cellIndex / 3;
            int col = cellIndex % 3;

            int centerX = bounds.Left + col * cellWidth + cellWidth / 2;
            int centerY = bounds.Top + row * cellHeight + cellHeight / 2;

            Cursor.Position = new Point(centerX, centerY);

            _tx = centerX;
            _ty = centerY;
            ResetMouseFilter();
        }

        private void ResetMouseFilter()
        {
            Trace("MouseCursor: {0}", Cursor.Position);
            _mx.Set(Cursor.Position.X);
            _my.Set(Cursor.Position.Y);
        }

        // ── Grid overlay helpers ──────────────────────────────────────────

        private void ShowGridOverlay()
        {
            if (_gridOverlay == null || _gridOverlay.IsDisposed)
            {
                _gridOverlay = new GridOverlayForm();
                _gridOverlay.SetFontSize(_config.GridLabelFontSize);
                _gridOverlay.Show();
            }
            else if (!_gridOverlay.Visible)
            {
                _gridOverlay.Show();
            }
            UpdateGridOverlay();
        }

        private void UpdateGridOverlay()
        {
            if (_gridOverlay != null && !_gridOverlay.IsDisposed && _gridOverlay.Visible)
            {
                int level = _gridLevel == GridLevel.SubCell ? 2 : 1;
                _gridOverlay.UpdateOverlay(_gridBounds, _gridPositionMap, level);
            }
        }

        private void HideGridOverlay()
        {
            if (_gridOverlay != null && !_gridOverlay.IsDisposed)
            {
                _gridOverlay.HideOverlay();
            }
        }

        public void ReadConfig()
        {
            var configFileName = Path.Combine(Directory.GetCurrentDirectory(), ConfigFileName);

            if (!File.Exists(configFileName))
            {
                var defaultConfig = new ConfigData
                {
                    Speed = DefaultSpeed,
                    Accel = DefaultAccel,
                    AccelDelay = DefaultAccelDelay,
                    FineSpeed = DefaultFineSpeed,
                    ScrollScale = DefaultScrollScale,
                    ScrollAccel = DefaultScrollAccel,
                    ScrollAmount = DefaultScrollAmount,
                    MouseFilterFrequency = DefaultMouseFilterFrequency,
                    MouseFilterResonance = DefaultMouseFilterResonance,
                    InterruptKey = DefaultInterruptKey,
                    FineModifierKey = DefaultFineModifierKey,
                    GridKey = "LMenu",
                    GridKeys = DefaultGridKeys,
                    SubGridEnabled = DefaultSubGridEnabled,
                    GridLabelFontSize = DefaultGridLabelFontSize,
                    Keymap = new Dictionary<string, string>(DefaultKeymap)
                };
                var json = JsonConvert.SerializeObject(defaultConfig, Formatting.Indented);
                File.WriteAllText(configFileName, json);
                MessageBox.Show(
                    $"InCode configuration file not found.\n\nA default config has been created at:\n{configFileName}\n\nPlease edit it to your preferences and restart InCode.",
                    "InCode — Config Created",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                Environment.Exit(0);
                return;
            }

            var text = File.ReadAllText(configFileName);
            try
            {
                _config = JsonConvert.DeserializeObject<ConfigData>(text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to parse Config.json — JSON syntax error.\n\n{ex.Message}\n\nPlease fix:\n{configFileName}\n\nThen restart InCode.",
                    "InCode — Config Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                Environment.Exit(0);
                return;
            }

            // Rebuild and exit when Keymap is empty — the user cannot use InCode
            // without knowing what keys are bound.
            if (_config != null && (_config.Keymap == null || _config.Keymap.Count == 0))
            {
                _config.Keymap = new Dictionary<string, string>(DefaultKeymap);
                if (string.IsNullOrEmpty(_config.FineModifierKey))
                    _config.FineModifierKey = DefaultFineModifierKey;
                var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                File.WriteAllText(configFileName, json);
                MessageBox.Show(
                    "InCode key bindings were empty.\n\nDefault key bindings have been written to:\n" + configFileName + "\n\nPlease edit them to your preferences and restart InCode.",
                    "InCode — Keymap Created",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                Environment.Exit(0);
                return;
            }

            if (_config == null)
                _config = new ConfigData();

            // Ensure defaults for zero values (missing config or parse failure)
            if (_config.Speed <= 0) _config.Speed = DefaultSpeed;
            if (_config.Accel < 0) _config.Accel = DefaultAccel;
            if (_config.AccelDelay <= 0) _config.AccelDelay = DefaultAccelDelay;
            if (_config.FineSpeed <= 0) _config.FineSpeed = DefaultFineSpeed;
            if (_config.ScrollScale <= 0) _config.ScrollScale = DefaultScrollScale;
            if (_config.ScrollAmount <= 0) _config.ScrollAmount = DefaultScrollAmount;
            if (_config.MouseFilterFrequency <= 0) _config.MouseFilterFrequency = DefaultMouseFilterFrequency;
            if (_config.MouseFilterResonance <= 0) _config.MouseFilterResonance = DefaultMouseFilterResonance;
            if (_config.GridLabelFontSize <= 0) _config.GridLabelFontSize = DefaultGridLabelFontSize;

            // Upgrade: write default values for fields missing in old-version configs
            bool upgraded = false;
            if (_config.InterruptKey == null)
            {
                _config.InterruptKey = DefaultInterruptKey;
                upgraded = true;
            }
            if (_config.FineModifierKey == null)
            {
                _config.FineModifierKey = "";
                _config.FineSpeed = DefaultFineSpeed;
                upgraded = true;
            }
            if (_config.GridKeys == null)
            {
                _config.GridKey = "";
                _config.GridKeys = DefaultGridKeys;
                upgraded = true;
            }

            // SubGridEnabled is a bool — missing in JSON yields default(false) (struct default).
            // Check raw JSON to distinguish "not present" from "explicitly false".
            try
            {
                var raw = JObject.Parse(text);
                if (!raw.ContainsKey("SubGridEnabled"))
                {
                    _config.SubGridEnabled = DefaultSubGridEnabled;
                    upgraded = true;
                }

            }
            catch (Exception ex)
            {
                Trace("Config upgrade: JObject re-parse failed — {0}", ex.Message);
            }

            if (upgraded)
            {
                var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                File.WriteAllText(configFileName, json);
            }
        }
    }
}
