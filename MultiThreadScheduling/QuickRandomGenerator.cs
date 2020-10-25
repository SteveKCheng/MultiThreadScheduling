using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Quick and dirty, low-quality pseudo-random generator.
    /// </summary>
    /// <remarks>
    /// This pseudo-random generator was copied from the .NET Core source code
    /// for its thread pool.  Copyright .NET Foundation, MIT license.
    /// </remarks>
    internal struct QuickRandomGenerator
    {
        private uint _w;
        private uint _x;
        private uint _y;
        private uint _z;

        /// <summary>
        /// Initialize with a seed.
        /// </summary>
        public QuickRandomGenerator(uint seed)
        {
            _x = seed;
            _w = 88675123;
            _y = 362436069;
            _z = 521288629;
        }

        /// <summary>
        /// Generate a 32-bit integer.
        /// </summary>
        public uint Next()
        {
            uint t = _x ^ (_x << 11);
            _x = _y; _y = _z; _z = _w;
            _w = _w ^ (_w >> 19) ^ (t ^ (t >> 8));
            return _w;
        }

        /// <summary>
        /// Get the floor of the logarithm to the base of two for an integer.
        /// </summary>
        /// <remarks>
        /// This implementation is not one of the fast ones using "bit twiddling",
        /// but we only call it once during construction of this class.
        /// </remarks>
        private static int Log2(ulong v)
        {
            int r = 0;
            while ((v >>= 1) != 0)
                r++;
            return r;
        }

        /// <summary>
        /// Compute a seed based on the current time.
        /// </summary>
        public static uint GetSeedFromTime()
        {
            var log2Frequency = Log2((ulong)Stopwatch.Frequency);
            var time = (ulong)Stopwatch.GetTimestamp();
            return (uint)(time >> log2Frequency);
        }
    }
}
