using System;
using System.IO;

namespace PitzkiLauncher
{
    /// <summary>
    /// Launcher sounds, made in code once (44.1 kHz, 16-bit mono):
    /// Open  = mechanical keyboard "clack": bright high-passed noise click + metal ping, low 300->160 Hz "thock", release click at 20 ms. 75 ms.
    /// Key   = soft tactile typing click, in 3 variants (pitch/volume) so it doesn't sound robotic. 45 ms.
    /// Launch = quick rising whoosh (noise + 260->1100 Hz chirp) that ends in a soft pop. 220 ms.
    /// </summary>
    public static class ClickSound
    {
        public const int Rate = 44100;
        /// <summary>Overall level of every launcher sound (0.55 = about -5 dB, asked for 2026-11-17).</summary>
        public const double Volume = 0.55;

        public static short[] Open() => Clack(75, click: 1.0, body: 0.9, pitch: 1.0, release: 0.45, peak: 0.45, seed: 42);

        // Typing = style "3 tactile-soft" (picked by yahav 2026-11-17): soft click + rounded 280 Hz body, small release click.
        public static short[] Key(int variant) => variant switch
        {
            0 => Tactile(0.94, 0.24, seed: 1),
            1 => Tactile(1.00, 0.27, seed: 2),
            _ => Tactile(1.07, 0.25, seed: 3),
        };

        private static short[] Tactile(double pitch, double peak, int seed)
        {
            int n = Rate * 45 / 1000;
            var buf = new double[n];
            var rnd = new Random(seed);
            AddClick(buf, rnd, 0.000, 0.5, 0.0006, lenMs: 5);
            AddClick(buf, rnd, 0.018, 0.2, 0.00042, lenMs: 5);
            double ph = 0, bodyHz = 280 * pitch, pingHz = 2200 * pitch;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                ph += 2 * Math.PI * bodyHz * (1 + 0.8 * Math.Exp(-t * 120)) / Rate;
                buf[i] += 0.6 * (Math.Sin(ph) + 0.2 * Math.Sin(2 * ph)) * Math.Min(1, t / 0.0008) * Math.Exp(-t * 75);
                buf[i] += 0.05 * Math.Sin(2 * Math.PI * pingHz * t) * Math.Exp(-t * 700);
            }
            return ToPcm(buf, peak, fadeMs: 4);
        }

        public static short[] Launch()
        {
            int n = Rate * 220 / 1000;
            var buf = new double[n];
            var rnd = new Random(9);
            double lp = 0, phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate, u = t / 0.22;
                // whoosh: noise through a low-pass whose cutoff rises with time
                double a = 0.04 + 0.5 * u;
                lp += a * ((rnd.NextDouble() * 2 - 1) - lp);
                double wenv = Math.Sin(Math.PI * Math.Min(1, u / 0.85));   // swell up, then away
                buf[i] += 0.55 * lp * wenv;
                // rising tone
                double f = 260 + 840 * u * u;
                phase += 2 * Math.PI * f / Rate;
                buf[i] += 0.30 * Math.Sin(phase) * wenv;
                // pop at the end
                double tp = t - 0.170;
                if (tp >= 0) buf[i] += 0.6 * Math.Sin(2 * Math.PI * 900 * tp) * Math.Exp(-tp * 160);
            }
            return ToPcm(buf, 0.40, fadeMs: 8);
        }

        /// <summary>The open sound as a .wav file (for listening / tuning).</summary>
        public static byte[] Wav() => ToWav(Open());

        public static byte[] ToWav(short[] pcm)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(pcm.Length * 2);
            foreach (var s in pcm) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }

        private static short[] Clack(int ms, double click, double body, double pitch, double release, double peak, int seed)
        {
            int n = Rate * ms / 1000;
            var buf = new double[n];
            var rnd = new Random(seed);
            AddClick(buf, rnd, 0.000, click, 0.0008);              // press click: high-passed noise, ~3 ms
            if (release > 0) AddClick(buf, rnd, 0.020, release, 0.0006);   // key release click
            double lo = 160 * pitch, sweep = 140 * pitch;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                // thock body: pitch slides (300 -> 160 Hz at pitch 1), 1 ms attack, ~35 ms decay
                double phase = 2 * Math.PI * (lo * t + sweep * (1 - Math.Exp(-t * 120)) / 120);
                double b = Math.Sin(phase) + 0.25 * Math.Sin(2 * phase);
                buf[i] += body * b * Math.Min(1, t / 0.001) * Math.Exp(-t * 80);
                // switch-leaf ping, gone in ~4 ms
                buf[i] += 0.18 * click * Math.Sin(2 * Math.PI * 3400 * pitch * t) * Math.Exp(-t * 900);
            }
            return ToPcm(buf, peak, fadeMs: 5);
        }

        private static void AddClick(double[] buf, Random rnd, double at, double amp, double tau, int lenMs = 4)
        {
            int start = (int)(at * Rate), len = Rate * lenMs / 1000;
            double prev = 0;
            for (int k = 0; k < len && start + k < buf.Length; k++)
            {
                double x = rnd.NextDouble() * 2 - 1;
                double hp = x - prev; prev = x;   // simple high-pass: keeps the bright, crisp part
                buf[start + k] += amp * 0.5 * hp * Math.Exp(-(double)k / Rate / tau);
            }
        }

        private static short[] ToPcm(double[] buf, double peakOut, int fadeMs)
        {
            double peak = 1e-9;
            foreach (var v in buf) peak = Math.Max(peak, Math.Abs(v));
            double gain = peakOut * Volume / peak;
            int n = buf.Length, fade = Rate * fadeMs / 1000;
            var pcm = new short[n];
            for (int i = 0; i < n; i++)
            {
                double s = buf[i] * gain;
                if (i > n - fade) s *= (double)(n - i) / fade;
                pcm[i] = (short)Math.Clamp(s * short.MaxValue, short.MinValue, short.MaxValue);
            }
            return pcm;
        }
    }
}
