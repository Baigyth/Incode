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
        private LowPass _mx = new LowPass(Frequency, 2000, 2.5f);
        private LowPass _my = new LowPass(Frequency, 2000, 2.5f);
        private readonly Dictionary<Keys, Action> _keys = new Dictionary<Keys, Action>();
        private readonly Stopwatch _watch = new Stopwatch();
        private DateTime _controlStartTime;
        private Keys _overrideKey = Keys.RControlKey;
        private Keys _fineModifierKey = Keys.None;
        private bool _fineModifierHeld;
        private const string ConfigFileName = "Config.json";
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

            if (!_keys.TryGetValue(e.KeyCode, out var action))
            {
                if (Controlled && e.KeyCode == _overrideKey)
                {
                    Eat(e);
                    return;
                }

                if (Controlled && _fineModifierKey != Keys.None && e.KeyCode == _fineModifierKey)
                {
                    _fineModifierHeld = true;
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
                Controlled = false;
                Trace("Not controlling");
                return;
            }

            if (!Controlled)
                return;

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

                Eat(e);
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
                    Speed = 150,
                    Accel = 0,
                    AccelDelay = 0.5f,
                    FineSpeed = 50,
                    ScrollScale = 30,
                    ScrollAccel = 0,
                    ScrollAmount = 3,
                    MouseFilterFrequency = 2000,
                    MouseFilterResonance = 2.5f,
                    InterruptKey = "RControlKey",
                    FineModifierKey = "LShift",
                    Keymap = new Dictionary<string, string>
                    {
                        ["Up"] = "W", ["Down"] = "S", ["Left"] = "A", ["Right"] = "D",
                        ["ScrollUp"] = "E", ["ScrollDown"] = "C",
                        ["LeftDown"] = "Space", ["RightDown"] = "F",
                        ["ScrollUpAmount"] = "R", ["ScrollDownAmount"] = "V"
                    }
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
                _config.Keymap = new Dictionary<string, string>
                {
                    ["Up"] = "W", ["Down"] = "S", ["Left"] = "A", ["Right"] = "D",
                    ["ScrollUp"] = "E", ["ScrollDown"] = "C",
                    ["LeftDown"] = "Space", ["RightDown"] = "F",
                    ["ScrollUpAmount"] = "R", ["ScrollDownAmount"] = "V"
                };
                if (string.IsNullOrEmpty(_config.FineModifierKey))
                    _config.FineModifierKey = "LShift";
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
            if (_config.Speed <= 0) _config.Speed = 150;
            if (_config.Accel < 0) _config.Accel = 0;
            if (_config.AccelDelay <= 0) _config.AccelDelay = 0.5f;
            if (_config.FineSpeed <= 0) _config.FineSpeed = 50;
            if (_config.ScrollScale <= 0) _config.ScrollScale = 30;
            if (_config.ScrollAmount <= 0) _config.ScrollAmount = 3;
            if (_config.MouseFilterFrequency <= 0) _config.MouseFilterFrequency = 2000;
            if (_config.MouseFilterResonance <= 0) _config.MouseFilterResonance = 2.5f;
        }
    }
}
