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
    internal struct ConfigData
    {
        public float Speed;
        public float Accel;
        public float AccelDelay;
        public float ScrollScale;
        public float ScrollAccel;
        public int ScrollAmount;
        public float MouseFilterResonance;
        public float MouseFilterFrequency;

        /// <summary>
        /// Key binding overrides. Key = Command enum name, Value = Keys enum name.
        /// Example: "Up": "W", "LeftDown": "Space", "ScrollUpAmount": "R"
        /// When null or empty, defaults are used.
        /// </summary>
        public Dictionary<string, string> Keymap;

        /// <summary>
        /// The interrupt/override key that activates control mode. Keys enum name, e.g. "CapsLock", "RControlKey".
        /// Defaults to CapsLock when null/empty.
        /// </summary>
        public string InterruptKey;

    }
}
