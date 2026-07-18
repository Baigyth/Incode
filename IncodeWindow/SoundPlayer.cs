using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;

namespace IncodeWindow {
    internal class Audio {
        private WaveOutEvent _waveOut;
        private readonly object _lock = new object();
        private Dictionary<Keys, byte[]> _waveData = new Dictionary<Keys, byte[]>();
        private const int SampleRate = 44100;
        private const float Duration = 5f;

        /// <summary>Pre-generate wave data for all registered keys.</summary>
        public void RegisterKeys(Dictionary<Keys, float> map) {
            var data = new Dictionary<Keys, byte[]>();
            foreach (var kv in map)
                data[kv.Key] = GenerateWave(kv.Value, Duration);
            lock (_lock) { _waveData = data; }
        }

        public void StartSound(Keys key) {
            byte[] data;
            lock (_lock) {
                if (!_waveData.TryGetValue(key, out data))
                    return;
            }
            var captured = data;
            ThreadPool.QueueUserWorkItem(_ => {
                lock (_lock) {
                    StopInternal();
                    var provider = new BufferedWaveProvider(new WaveFormat(SampleRate, 1)) {
                        BufferLength = captured.Length
                    };
                    provider.AddSamples(captured, 0, captured.Length);
                    _waveOut = new WaveOutEvent();
                    _waveOut.Init(provider);
                    _waveOut.Play();
                }
            });
        }

        public void StopSound() {
            ThreadPool.QueueUserWorkItem(_ => {
                lock (_lock) { StopInternal(); }
            });
        }

        private void StopInternal() {
            if (_waveOut == null) return;
            try { _waveOut.Stop(); } catch { }
            try { _waveOut.Dispose(); } catch { }
            _waveOut = null;
        }

        private static byte[] GenerateWave(float frequency, float seconds) {
            int numSamples = (int)(SampleRate * seconds);
            byte[] values = new byte[2 * numSamples];
            double increment = 2.0 * Math.PI * frequency / SampleRate;
            short amplitude = 8000;
            for (int n = 0; n < numSamples; n++) {
                short sample = (short)(amplitude * Math.Sin(increment * n));
                values[n * 2] = (byte)(sample & 0xFF);
                values[n * 2 + 1] = (byte)(sample >> 8);
            }
            return values;
        }
    }
}
