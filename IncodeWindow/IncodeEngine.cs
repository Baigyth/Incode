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

    /// <summary>
    /// Core engine: hooks, key processing, mouse simulation, config.
    /// Windowless — no Form dependency.
    /// </summary>
    internal class IncodeEngine : IDisposable
    {
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
        private bool _gridMode;
        private Keys _gridActivateKey = Keys.None;
        private readonly Dictionary<Keys, int> _gridPositionMap = new Dictionary<Keys, int>();
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
        }

        private void InstallHooks()
        {
            _inputSimulator = new InputSimulator();

            _mouseOut = _inputSimulator.Mouse;
            _keyboardOut = _inputSimulator.Keyboard;

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
                _gridMode = true;
                _gridKeyDownEaten = e.KeyCode;
                Eat(e);
                return;
            }

            // Grid mode active → only grid position keys work; all others blocked
            if (_gridMode)
            {
                if (_gridPositionMap.TryGetValue(e.KeyCode, out int cellIndex))
                {
                    ExecuteGridJump(cellIndex);
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
                _gridMode = false;
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
                _gridMode = false;
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
            _gridMode = false;
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

        private void ExecuteGridJump(int cellIndex)
        {
            if (cellIndex < 0 || cellIndex > 8)
                return;

            var screen = Screen.FromPoint(Cursor.Position);
            var area = screen.WorkingArea;

            int cellWidth = area.Width / 3;
            int cellHeight = area.Height / 3;

            int row = cellIndex / 3;
            int col = cellIndex % 3;

            int centerX = area.Left + col * cellWidth + cellWidth / 2;
            int centerY = area.Top + row * cellHeight + cellHeight / 2;

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
            if (upgraded)
            {
                var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                File.WriteAllText(configFileName, json);
            }
        }
    }
}
