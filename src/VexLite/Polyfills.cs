// Bits of newer .NET that the shared source uses and .NET Framework 4.8 lacks. Lite build only: the full build
// (net10) never sees this folder. Each is the smallest stand-in that gives the same result for how Vex uses it.
using System;
using System.Collections.Generic;
using System.Linq;

namespace System.Runtime.CompilerServices
{
    /// <summary>Lets records and init-only setters compile.</summary>
    internal static class IsExternalInit { }
}

namespace System
{
    /// <summary>x[^1]: an index counted from the start or the end.</summary>
    internal readonly struct Index
    {
        readonly int value;
        public Index(int value, bool fromEnd = false) { this.value = fromEnd ? ~value : value; }
        public int Value => value < 0 ? ~value : value;
        public bool IsFromEnd => value < 0;
        public static Index Start => new(0);
        public static Index End => new(0, true);
        public static Index FromStart(int v) => new(v);
        public static Index FromEnd(int v) => new(v, true);
        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;
        public static implicit operator Index(int v) => new(v);
    }

    /// <summary>x[a..b]: a slice of a string or an array.</summary>
    internal readonly struct Range
    {
        public Index Start { get; }
        public Index End { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public static Range StartAt(Index start) => new(start, Index.End);
        public static Range EndAt(Index end) => new(Index.Start, end);
        public static Range All => new(Index.Start, Index.End);
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int s = Start.GetOffset(length), e = End.GetOffset(length);
            if ((uint)e > (uint)length || (uint)s > (uint)e) throw new ArgumentOutOfRangeException(nameof(length));
            return (s, e - s);
        }
    }
}

namespace Vex
{
    /// <summary>
    /// System.Math plus Clamp (new in .NET Core). Inside the Vex namespaces this is found before System.Math, so the
    /// shared code's Math.Clamp, Math.Sin... all land here and go on to System.Math.
    /// </summary>
    internal static class Math
    {
        public const double PI = System.Math.PI;
        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static double Abs(double v) => System.Math.Abs(v);
        public static int Abs(int v) => System.Math.Abs(v);
        public static double Min(double a, double b) => System.Math.Min(a, b);
        public static int Min(int a, int b) => System.Math.Min(a, b);
        public static long Min(long a, long b) => System.Math.Min(a, b);
        public static double Max(double a, double b) => System.Math.Max(a, b);
        public static int Max(int a, int b) => System.Math.Max(a, b);
        public static long Max(long a, long b) => System.Math.Max(a, b);
        public static int Sign(double v) => System.Math.Sign(v);
        public static int Sign(int v) => System.Math.Sign(v);
        public static double Sin(double v) => System.Math.Sin(v);
        public static double Cos(double v) => System.Math.Cos(v);
        public static double Exp(double v) => System.Math.Exp(v);
        public static double Log(double v) => System.Math.Log(v);   // the daemon's random visit times (VexDaemon.cs)
        public static double Pow(double a, double b) => System.Math.Pow(a, b);
        public static double Sqrt(double v) => System.Math.Sqrt(v);
        public static double Floor(double v) => System.Math.Floor(v);
        public static double Ceiling(double v) => System.Math.Ceiling(v);
        public static double Round(double v) => System.Math.Round(v);
        public static double Round(double v, int digits) => System.Math.Round(v, digits);
    }

    /// <summary>String and LINQ helpers from newer .NET, as extension methods (only used where the real ones are missing).</summary>
    internal static class Polyfills
    {
        public static bool EndsWith(this string s, char c) => s.Length > 0 && s[s.Length - 1] == c;
        public static bool StartsWith(this string s, char c) => s.Length > 0 && s[0] == c;
        public static bool Contains(this string s, string value, StringComparison how) => s.IndexOf(value, how) >= 0;
        public static bool Contains(this string s, char c) => s.IndexOf(c) >= 0;
        public static string Replace(this string s, string a, string b, StringComparison how)
        {
            var sb = new System.Text.StringBuilder();
            int i = 0, j;
            while ((j = s.IndexOf(a, i, how)) >= 0) { sb.Append(s, i, j - i).Append(b); i = j + a.Length; }
            return sb.Append(s, i, s.Length - i).ToString();
        }
        public static IEnumerable<T> DistinctBy<T, K>(this IEnumerable<T> items, Func<T, K> key)
        {
            var seen = new HashSet<K>();
            foreach (var x in items) if (seen.Add(key(x))) yield return x;
        }
        public static IEnumerable<T> TakeLast<T>(this IEnumerable<T> items, int n)
        {
            var list = items.ToList();
            return list.Skip(System.Math.Max(0, list.Count - n));
        }
        public static V GetValueOrDefault<K, V>(this IDictionary<K, V> d, K key, V fallback) => d.TryGetValue(key, out var v) ? v : fallback;
        public static bool TryAdd<K, V>(this IDictionary<K, V> d, K key, V value) { if (d.ContainsKey(key)) return false; d.Add(key, value); return true; }
    }
}
