using System;
using System.Runtime.InteropServices;

namespace PitzkiLauncher
{
    /// <summary>
    /// Plays the launcher's short sounds with waveOut. Each sound type has its own output channel, so a
    /// typing click never cuts the launch sound (SoundPlayer shares one channel for the whole app).
    /// Everything is made and prepared once; playing is just one non-blocking waveOutWrite.
    /// </summary>
    public static class Sfx
    {
        private static readonly Voice? OpenVoice = Voice.Create(ClickSound.Open());
        private static readonly Voice? KeyVoice = Voice.Create(ClickSound.Key(0), ClickSound.Key(1), ClickSound.Key(2));
        private static readonly Voice? LaunchVoice = Voice.Create(ClickSound.Launch());
        private static long _lastKey;
        private static int _keyVariant;
        private static readonly Random Rnd = new();

        public static void Open() => OpenVoice?.Play(0);
        public static void Launch() => LaunchVoice?.Play(0);

        public static void Key()
        {
            long now = Environment.TickCount64;
            if (now - _lastKey < 25) return;   // keys faster than 25 ms apart: skip, no pile-up
            _lastKey = now;
            int v = Rnd.Next(2);                // never the same variant twice in a row
            _keyVariant = v >= _keyVariant ? v + 1 : v;
            KeyVoice?.Play(_keyVariant);
        }

        private sealed class Voice
        {
            private readonly IntPtr _h;
            private readonly IntPtr[] _hdrs;

            private Voice(IntPtr h, IntPtr[] hdrs) { _h = h; _hdrs = hdrs; }

            public static Voice? Create(params short[][] clips)
            {
                try
                {
                    var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = ClickSound.Rate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = ClickSound.Rate * 2 };
                    if (waveOutOpen(out var h, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) return null;
                    var hdrs = new IntPtr[clips.Length];
                    for (int i = 0; i < clips.Length; i++)
                    {
                        int bytes = clips[i].Length * 2;
                        var data = Marshal.AllocHGlobal(bytes);   // lives for the whole app run
                        Marshal.Copy(clips[i], 0, data, clips[i].Length);
                        var hdr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
                        Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = (uint)bytes }, hdr, false);
                        waveOutPrepareHeader(h, hdr, Marshal.SizeOf<WAVEHDR>());
                        hdrs[i] = hdr;
                    }
                    return new Voice(h, hdrs);
                }
                catch { return null; }
            }

            public void Play(int i)
            {
                try
                {
                    waveOutReset(_h);   // stop what this channel was playing (instant)
                    waveOutWrite(_h, _hdrs[i], Marshal.SizeOf<WAVEHDR>());
                }
                catch { }
            }
        }

        private const uint WAVE_MAPPER = unchecked((uint)-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr h, uint dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr h);
    }
}
