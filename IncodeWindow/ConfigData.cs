namespace Incode
{
    using System.Collections.Generic;

    /// <summary>
    /// Configuration for the app. Stored as a Json file.
    ///
    /// For security reasons, the underlying datafile is not stored in the repo.
    /// This is because it is intended to store things like passwords and credit-card details.
    /// 
    /// </summary>
    internal class ConfigData
    {
        public float Speed { get; set; }
        public float Accel { get; set; }
        public float AccelDelay { get; set; }
        public float ScrollScale { get; set; }
        public float ScrollAccel { get; set; }
        public int ScrollAmount { get; set; }
        public float MouseFilterResonance { get; set; }
        public float MouseFilterFrequency { get; set; }

        /// <summary>
        /// Key binding overrides. Key = Command enum name, Value = Keys enum name.
        /// Example: "Up": "W", "LeftDown": "Space", "ScrollUpAmount": "R"
        /// When null or empty, defaults are used.
        /// </summary>
        public Dictionary<string, string> Keymap { get; set; }

        /// <summary>
        /// The interrupt/override key that activates control mode. Keys enum name, e.g. "CapsLock", "RControlKey".
        /// Defaults to CapsLock when null/empty.
        /// </summary>
        public string InterruptKey { get; set; }

        /// <summary>
        /// The fine-movement modifier key held during control mode to use FineSpeed instead of regular
        /// Speed+Accel. Keys enum name, e.g. "LShiftKey", "LMenu". Empty/null disables fine mode.
        /// </summary>
        public string FineModifierKey { get; set; }

        /// <summary>
        /// Fixed slow speed (px/s) used when the fine modifier is held. No acceleration applied.
        /// </summary>
        public float FineSpeed { get; set; }

        /// <summary>
        /// Key that activates Grid (9-cell navigation) mode when held during control mode.
        /// Keys enum name, e.g. "LMenu" (Left Alt). When null/empty, Grid mode is disabled.
        /// Set to a Keys enum name (e.g. "LMenu") to enable.
        /// </summary>
        public string GridKey { get; set; }

        /// <summary>
        /// Array of 9 Keys enum names mapped to the 3x3 grid positions in row-major order.
        /// Index layout: 0=top-left, 1=top-center, 2=top-right,
        ///               3=middle-left, 4=middle-center, 5=middle-right,
        ///               6=bottom-left, 7=bottom-center, 8=bottom-right.
        /// Ignored when GridKey is empty. Defaults to ["Q","W","E","A","S","D","Z","X","C"] when null/empty.
        /// </summary>
        public string[] GridKeys { get; set; }

        /// <summary>
        /// When true (default), grid mode supports 2-level nested subdivision:
        /// pressing a grid key jumps to the corresponding cell, then pressing
        /// again subdivides that cell into a 3x3 sub-grid.
        /// When false, the original single-level grid behavior is used.
        /// </summary>
        public bool SubGridEnabled { get; set; }

        /// <summary>
        /// Font size (in points) for the grid overlay cell labels.
        /// Defaults to 48 if zero or negative.
        /// </summary>
        public float GridLabelFontSize { get; set; } = 48f;

    }
}
