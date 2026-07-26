using System;

namespace Habagat
{
    /// <summary>
    /// JavaScript numeric semantics that the original simulation depends on.
    ///
    /// This is not pedantry. The terrain generator's LCG is
    /// <c>s = (s * 1103515245 + 12345) &amp; 0x7fffffff</c>, and in JavaScript every
    /// number is a double: <c>s</c> can approach 2^31, so the product reaches
    /// ~2.3e18, which is far past 2^53 where doubles stop representing integers
    /// exactly. The multiply therefore LOSES low bits, and the <c>&amp;</c> then
    /// coerces that already-inexact double to int32.
    ///
    /// A naive C# port using <c>int</c> or <c>long</c> arithmetic is exact, does
    /// NOT lose those bits, and so produces a completely different random stream
    /// and a completely different terrain. Reproducing the original maps means
    /// reproducing the imprecision.
    /// </summary>
    public static class JsMath
    {
        private const double TwoPow32 = 4294967296.0;

        /// <summary>ECMAScript ToInt32: truncate, take modulo 2^32, reinterpret as signed.</summary>
        public static int ToInt32(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            double m = Math.Truncate(value) % TwoPow32;
            if (m < 0) m += TwoPow32;
            return unchecked((int)(uint)m);
        }

        /// <summary>
        /// The presets' linear congruential generator, evaluated in double
        /// precision exactly as the browser does it.
        /// </summary>
        public struct Lcg
        {
            private double _s;

            public Lcg(int seed) { _s = seed; }

            /// <summary>
            /// Returns a double, not a float. The JS control grid is a plain
            /// Array, so it holds full-precision doubles; narrowing here would
            /// shift the terrain in the seventh significant figure.
            /// </summary>
            public double Next()
            {
                _s = ToInt32(_s * 1103515245.0 + 12345.0) & 0x7fffffff;
                return _s / 0x7fffffff;
            }
        }
    }
}
