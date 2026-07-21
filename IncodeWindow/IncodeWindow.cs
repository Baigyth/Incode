// (C) 2015-20 christian.schladetsch@gmail.com

using System.Runtime.InteropServices;
using AudioSwitcher.AudioApi.CoreAudio;
using IncodeWindow;
using LedCSharp;

namespace Incode
{
    using System;
    using System.Media;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Threading;
    using System.Windows.Forms;
    using MouseKeyboardActivityMonitor;
    using MouseKeyboardActivityMonitor.WinApi;
    using WindowsInput;
    using Newtonsoft.Json;

    /// <summary>
    /// Press and hold the MouseEscape key (default to Right-Control) to enter MouseMode.
    /// Remap Right-Control to CapsLock using:
    /// 
    /// Yeah, this should be a service, or at least an app that minimises to the system tray.
    /// </summary>
    public partial class IncodeWindow : Form
    {
        public float Speed => _config.Speed;
        public float Accel => _config.Accel;
        public float ScrollScale => _config.ScrollScale;
        public float ScrollAccel => _config.ScrollAccel;
        public int ScrollAmount => _config.ScrollAmount;
        public float FilterRes => _config.MouseFilterResonance;
        public float FilterFreq => _config.MouseFilterFrequency;

        private Audio _audio = new Audio();

        private bool Abbreviating
        {
            get => _abbrMode;
            set
            {
                _abbrMode = value;
                _abbreviation = "";
                if (!value)
                {
                    _abbrevWindow?.Close();
                    _abbrevWindow = null;
                }
            }
        }

        // true if this app is interpreting and controlling input
        private bool Controlled
        {
            get => _controlled;
            set
            {
                _controlled = value;
                _timer.Enabled = value;

                if (value)
                    _controlStartTime = DateTime.Now;
                else if (_mouseLeftDown)
                {
                    _mouseOut.LeftButtonUp();
                    _mouseLeftDown = false;
                }

                ResetMouseFilter();

                // TODO: can't find correct format for dll (although LogiNumLock tool works)
                //SetKeyboardLights();
            }
        }

        private readonly keyboardNames[] _incodeKeys =
        {
            keyboardNames.Q,
            keyboardNames.W,
            keyboardNames.A,
            keyboardNames.S,
            keyboardNames.D,
            keyboardNames.E,
            keyboardNames.C,
            keyboardNames.SPACE,
        };

        private void SetKeyboardLights()
        {
            int r = _controlled ? 255 : 0;
            int g = _controlled ? 255 : 0;
            int b = _controlled ? 0 : 255;
            foreach (var key in _incodeKeys)
            {
                LogitechGSDK.LogiLedSetLightingForKeyWithKeyName(key,r,g,b);
            }
        }

        private KeyboardHookListener _keyboardIn;
        private MouseHookListener _mouseIn;
        private InputSimulator _inputSimulator;
        private IMouseSimulator _mouseOut;
        private IKeyboardSimulator _keyboardOut;
        private bool _controlled; // true while we control all input and output
        private const float Frequency = 100.0f; // Hertz
        private System.Windows.Forms.Timer _timer;
        private float _tx, _ty; // the target mouse position
        private LowPass _mx = new LowPass(Frequency, 2000, 2.5f);
        private LowPass _my = new LowPass(Frequency, 2000, 2.5f);
        private readonly Dictionary<Keys, Action> _keys = new Dictionary<Keys, Action>();
        private readonly Stopwatch _watch = new Stopwatch();
        private DateTime _controlStartTime;

        // the key to press to activate the custom mode
        // works well for Wasd 88-key blank keyboards ;)
        private Keys _overrideKey = Keys.RControlKey; // default, overridden by Config.InterruptKey
        private const string ConfigFileName = "Config.json";
        private int _inserting;
        private bool _mouseLeftDown;
        private bool _mouseRightDown;

        // enter abbreviation mode. press escape to leave
        private bool _abbrMode;
        private string _abbreviation;
        private ConfigData _config;
        private AbbreviationForm _abbrevWindow;
        private NotifyIcon _notifyIcon;
        private bool _isExiting;

        private void PlaySound(string name)
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var effects = Path.Combine(docs, "SoundBoard");
            var sfx = Path.Combine(effects, name);
            if (!File.Exists(sfx))
                return;
            var player = new SoundPlayer(sfx);
            player.Play();
        }

        public IncodeWindow()
        {
            InitializeComponent();
            Configure();
            InstallHooks();

            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = true;

            // Set icon from embedded resource
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            // System tray
            _notifyIcon = new NotifyIcon
            {
                Icon = Icon,
                Text = "InCode",
                Visible = true
            };
            _notifyIcon.ContextMenuStrip = new ContextMenuStrip();
            _notifyIcon.ContextMenuStrip.Items.Add("Show", null, (s, e) => ShowWindow());
            _notifyIcon.ContextMenuStrip.Items.Add("Exit", null, (s, e) => { _isExiting = true; try { _keyboardIn?.Stop(); _mouseIn?.Stop(); } finally { Environment.Exit(0); } });
            _notifyIcon.DoubleClick += (s, e) => ShowWindow();

            // Auto-hide to tray once handle is ready
            this.Load += (s, e) => HideToTray();

            // Warm up NAudio audio device to avoid first-use lag
            ThreadPool.QueueUserWorkItem(_ => {
                var warm = new NAudio.Wave.WaveOutEvent();
                warm.Init(new NAudio.Wave.SilenceProvider(new NAudio.Wave.WaveFormat(44100, 1)));
                warm.Play();
                Thread.Sleep(200);
                warm.Stop();
                warm.Dispose();
            });
        }

        void PlaySound(object sender, KeyEventArgs key) {
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_isExiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _keyboardIn?.Stop();
            _mouseIn?.Stop();

            _notifyIcon.Visible = false;
            _notifyIcon?.Dispose();
            _timer?.Stop();

            _timer?.Dispose();
            _keyboardIn?.Dispose();
            _mouseIn?.Dispose();

            base.OnFormClosed(e);
        }

        private void Configure()
        {
            ReadConfig();
            LoadKeyMap();
        }

        private void LoadKeyMap()
        {
            _keys.Clear();

            // Load interrupt key override from config (before keymap, must be outside branches)
            if (!string.IsNullOrEmpty(_config.InterruptKey)
                && Enum.TryParse(_config.InterruptKey, out Keys interruptKey))
            {
                _overrideKey = interruptKey;
            }

            // Register movement keys for audio feedback (up/down/left/right)
            // (called again after _keys is populated below)

            // If keymap is configured in Config.json, use it
            if (_config.Keymap != null && _config.Keymap.Count > 0)
            {
                foreach (var kvp in _config.Keymap)
                {
                    if (Enum.TryParse(kvp.Key, out Command cmd) && Enum.TryParse(kvp.Value, out Keys key))
                    {
                        _keys.Add(key, new Action(cmd));
                    }
                }
                RegisterAudioKeys();
                return;
            }

            // Fallback to hardcoded defaults
            _keys.Add(Keys.Escape, new Action(Command.Escape));
            _keys.Add(Keys.W, new Action(Command.Up));
            _keys.Add(Keys.A, new Action(Command.Left));
            _keys.Add(Keys.S, new Action(Command.Down));
            _keys.Add(Keys.D, new Action(Command.Right));
            _keys.Add(Keys.E, new Action(Command.ScrollUp));
            _keys.Add(Keys.C, new Action(Command.ScrollDown));
            _keys.Add(Keys.F, new Action(Command.RightDown));
            _keys.Add(Keys.Q, new Action(Command.Abbreviate));
            _keys.Add(Keys.Space, new Action(Command.LeftDown));
            _keys.Add(Keys.R, new Action(Command.ScrollUpAmount));
            _keys.Add(Keys.V, new Action(Command.ScrollDownAmount));
            _keys.Add(Keys.D1, new Action(Command.VolumeDown));
            _keys.Add(Keys.D2, new Action(Command.VolumeUp));
            _keys.Add(Keys.D3, new Action(Command.VolumeMute));

            RegisterAudioKeys();
        }

        private void RegisterAudioKeys()
        {
            var freqs = new Dictionary<Keys, float>();
            float[] tones = { 55f, 65.41f, 77.78f, 92.50f };
            Command[] dirs = { Command.Up, Command.Left, Command.Down, Command.Right };
            foreach (var kv in _keys)
                for (int i = 0; i < dirs.Length; i++)
                    if (kv.Value.Command == dirs[i])
                        freqs[kv.Key] = tones[i];
            _audio.RegisterKeys(freqs);
        }

        private void InstallHooks()
        {
            _inputSimulator = new InputSimulator();

            _mouseOut = _inputSimulator.Mouse;
            _keyboardOut = _inputSimulator.Keyboard;

            _mouseIn = new MouseHookListener(new GlobalHooker()) {Enabled = true};
            _keyboardIn = new KeyboardHookListener(new GlobalHooker()) {Enabled = true};

            _keyboardIn.KeyDown += OnKeyDown;
            _keyboardIn.KeyUp += OnKeyUp;

            _timer = new System.Windows.Forms.Timer {Interval = (int) (1000 / Frequency)};
            _timer.Tick += PerformCommands;

            _watch.Start();
        }

        private void PerformCommands(object sender, EventArgs e)
        {
            var dt = _watch.ElapsedMilliseconds / 1000.0f;
            _watch.Restart();

            var now = DateTime.Now;
            var earliest = ButtonsDown(DateTime.MaxValue);

            if (earliest == DateTime.MaxValue)
            {
                MoveMouse();
                return;
            }

            // for mouse movement
            var millis = (float) (now - earliest).TotalMilliseconds;
            var scale = Accel * millis / 1000.0f;
            var delta = dt * Speed * scale;

            PerformActions(now, delta);

            MoveMouse();
        }

        private DateTime ButtonsDown(DateTime earliest)
        {
            foreach (var action in _keys)
            {
                var act = action.Value;
                var button = act.Command == Command.LeftDown || act.Command == Command.RightDown;
                if (button)
                    continue;

                if (act.Started > DateTime.MinValue && act.Started < earliest)
                    earliest = act.Started;
            }

            return earliest;
        }

        private void MoveMouse()
        {
            // For accuracy, keep track of desired location in floats, and get nearest integer to set.
            // Allow for negative values correctly, as we all have multiple monitors!
            var fx = _mx.Next(_tx);
            var fy = _my.Next(_ty);
            var nx = (int) (fx < 0 ? (fx - 0.5f) : (fx + 0.5f));
            var ny = (int) (fy < 0 ? (fy - 0.5f) : (fy + 0.5f));

            // Clamp to virtual screen bounds so cursor cannot escape
            var bounds = SystemInformation.VirtualScreen;
            var clampedX = Math.Max(bounds.Left, Math.Min(bounds.Right - 1, nx));
            var clampedY = Math.Max(bounds.Top, Math.Min(bounds.Bottom - 1, ny));

            // If clamped, sync target position so cursor responds immediately when direction changes
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

                // for vertical scroll
                var ts = (now - act.Started).TotalSeconds;
                var accel = 1 + ScrollAccel * ts;
                var t = (int) (ts * accel * ScrollScale);

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

        public void DeltaVolume(int amount)
        {
            //var device = new CoreAudioController().DefaultPlaybackDevice;
            //device.Volume += amount;
        }

        public void OnKeyDown(object sender, KeyEventArgs e)
        {
            // We're inserting a text expansion. in this case, we get phony key downs.
            // From window's input system. ignore them.
            if (_inserting > 0)
            {
                _inserting--;
                return;
            }

            // If we're in the middle of an abbreviation, stop it.
            if (e.KeyCode == Keys.Escape && Abbreviating)
            {
                Abbreviating = false;
                Eat(e);
                return;
            }

            switch (CheckCompleteAbbreviation(e))
            {
                case AbbrevResult.Matching:
                    PlaySound("MacroCorrect.wav");
                    return;
                case AbbrevResult.NoMatch:
                    PlaySound("MacroFailed.wav");
                    return;
                case AbbrevResult.None:
                    //PlaySound("MacroFailed.wav");
                    break;
                case AbbrevResult.Matched:
                    PlaySound("MacroSuccess.wav");
                    return;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            if (TestAbbreviationStart(e.KeyCode))
            {
                ShowAbbreviations();
                Eat(e);
                return;
            }

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
                // Block override key repeats in control mode
                if (Controlled && e.KeyCode == _overrideKey)
                {
                    Eat(e);
                    return;
                }

                // Not an InCode command → let modifier keys pass through
                if (IsModifierKey(e.KeyCode))
                    return;

                Eat(e);
                return;
            }

            Eat(e);

            if (_config.SoundEnabled)
                _audio.StartSound(e.KeyCode);

            // One-shot commands: execute immediately, no Started tracking
            switch (action.Command)
            {
                case Command.ScrollUpAmount:
                    _mouseOut.VerticalScroll(ScrollAmount);
                    return;
                case Command.ScrollDownAmount:
                    _mouseOut.VerticalScroll(-ScrollAmount);
                    return;
                case Command.VolumeDown:
                    DeltaVolume(10);
                    return;
                case Command.VolumeUp:
                    DeltaVolume(-10);
                    return;
                case Command.VolumeMute:
                    return;
            }

            // Sustained commands: only set Started on first press
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

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;

        [DllImport("user32.dll")]
        private static extern int GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(int hwnd);

        private void ShowAbbreviations()
        {
            // Need to show a window of available abbreviations, but want all subsequent input to
            // be sent to current (native) control.
            var current = GetForegroundWindow();
            var cp = Cursor.Position;
            _abbrevWindow?.Close();
            _abbrevWindow = new AbbreviationForm(this) { Location = new Point(cp.X + 20, cp.Y - 20) };
            _abbrevWindow.Populate(_config.Abbreviations);
            _abbrevWindow.Show();
            SetForegroundWindow(current);
        }

        private AbbrevResult CheckCompleteAbbreviation(KeyEventArgs e)
        {
            if (!Abbreviating)
                return AbbrevResult.None;

            // Append char from keycode.
            _abbreviation += new KeysConverter().ConvertToString(e.KeyData)?.ToLower();

            // Eat the part of the abbreviation, even if it fails.
            Eat(e);

            // Check for an abbreviation being completed.
            foreach (var kv in _config.Abbreviations)
            {
                var test = CheckAbbrev(kv.Key);
                switch (test)
                {
                    case AbbrevResult.Matching:
                        Trace($"Prefix {kv.Key} matches so far");
                        return test;

                    case AbbrevResult.Matched:
                        Trace($"Inserting: {kv.Key} -> {kv.Value}");
                        _inserting = kv.Value.Length;

                        _keyboardOut.TextEntry(kv.Value);
                        Abbreviating = false;
                        return test;
                }
            }

            Trace($"No abbrev found for {_abbreviation}");
            Abbreviating = false;

            return AbbrevResult.NoMatch;
        }

        private AbbrevResult CheckAbbrev(string key)
        {
            if (_abbreviation.ToLower() == key)
                return AbbrevResult.Matched;
            return key.StartsWith(_abbreviation) ? AbbrevResult.Matching : AbbrevResult.NoMatch;
        }

        /// <summary>
        /// Return true if we have just entered abbreviation mode
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        private bool TestAbbreviationStart(Keys key)
        {
            if (Abbreviating)
                return true;

            if (!Controlled)
                return false;

            if (!_keys.TryGetValue(key, out var action) || action.Command != Command.Abbreviate)
                return false;

            Abbreviating = true;

            Trace("Entering abbreviation mode");
            PlaySound("MacroStart.wav");

            return true;
        }

        private static void Trace(string fmt, params object[] args)
            => Debug.WriteLine(fmt, args);

        private void OnKeyUp(object sender, KeyEventArgs e)
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

                Controlled = false;
                Trace("Not controlling");
                _abbrevWindow?.Close();
                Abbreviating = false;
                return;
            }

            if (!Controlled)
                return;

            if (!_keys.TryGetValue(e.KeyCode, out var action))
            {
                // Not an InCode command → let modifier keys pass through
                if (IsModifierKey(e.KeyCode))
                    return;

                Eat(e);
                return;
            }

            Eat(e);

            // Sentinel values are bad. I use one here to indicate that an action is not active.
            action.Started = DateTime.MinValue;

            if (_config.SoundEnabled)
                _audio.StopSound();

            switch (action.Command)
            {
                case Command.RightDown:
                    _mouseOut.RightButtonUp();
                    break;
                case Command.LeftDown:
                    _mouseOut.LeftButtonUp();
                    _mouseLeftDown = false;
                    break;
            }
        }

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

        /// <summary>
        /// Take over keyboard control from Windows
        /// </summary>
        private void StartControl()
        {
            foreach (var kv in _keys)
                kv.Value.Started = DateTime.MinValue;

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

        /// <summary>
        /// Move the cursor to the center of the first display.
        /// </summary>
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

        private void WriteValue(Action<float> write, TextBox text)
        {
            write(float.Parse(text.Text));
            WriteConfig();
        }

        private void ReadConfig()
        {
            var configFileName = Path.Combine(Directory.GetCurrentDirectory(), ConfigFileName);

            if (File.Exists(configFileName))
            {
                var text = File.ReadAllText(configFileName);
                _config = JsonConvert.DeserializeObject<ConfigData>(text);
            }

            UpdateUi();
        }

        private void UpdateUi()
        {
            _speedText.Text = Speed.ToString();
            _accelText.Text = Accel.ToString();
            _scrollAccelText.Text = ScrollAccel.ToString();
            _scrollScaleText.Text = ScrollScale.ToString();
            _scrollAmount.Text = ScrollAmount.ToString();
            _filterFreq.Text = FilterFreq.ToString();
            _filterRes.Text = FilterRes.ToString();
        }

        private void WriteConfig()
            => File.WriteAllText(ConfigFileName, JsonConvert.SerializeObject(_config));

        private void _scrollAccelText_Leave(object sender, EventArgs e)
            => WriteValue(f => _config.ScrollAccel = f, _scrollAccelText);

        private void _scrollScaleText_Leave(object sender, EventArgs e)
            => WriteValue(f => _config.ScrollScale = f, _scrollScaleText);

        private void _scrollAmountText_Leave(object sender, EventArgs e)
            => WriteValue(f => _config.ScrollAmount = (int) f, _scrollAmount);

        private void _accelText_Leave(object sender, EventArgs e)
            => WriteValue(f => _config.Accel = f, _accelText);

        private void _speedText_Leave(object sender, EventArgs e)
            => WriteValue(f => _config.Speed = f, _speedText);

        private void _filterFreq_Leave(object sender, EventArgs e)
        {
            WriteValue(f => _config.MouseFilterFrequency = f, _filterFreq);
            UpdateMouseFilter();
        }

        private void _filterRes_Leave(object sender, EventArgs e)
        {
            WriteValue(f => _config.MouseFilterResonance = f, _filterRes);
            UpdateMouseFilter();
        }

        private void UpdateMouseFilter()
        {
            _mx = new LowPass(Frequency, _config.MouseFilterFrequency, _config.MouseFilterResonance);
            _my = new LowPass(Frequency, _config.MouseFilterFrequency, _config.MouseFilterResonance);
            ResetMouseFilter();
        }

        private void ShowWindow()
        {
            int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, exStyle & ~WS_EX_TOOLWINDOW);

            Show();
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            BringToFront();
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;

            int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        }

        private void _exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            HideToTray();
        }
    }
}
