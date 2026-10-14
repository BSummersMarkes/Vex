using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

// The little of System.Text.Json that the shared source uses (settings, state, the activity log), for the lite build:
// .NET Framework hasn't got it, and the NuGet package would mean shipping extra DLLs beside the exe. Reflection over
// public properties, written the same way System.Text.Json writes them, so files look the same in both versions.
namespace System.Text.Json
{
    public sealed class JsonSerializerOptions
    {
        public bool WriteIndented { get; set; }
    }

    public static class JsonSerializer
    {
        public static string Serialize<T>(T value, JsonSerializerOptions? options = null)
        {
            var sb = new StringBuilder();
            Write(sb, value, options?.WriteIndented == true ? 0 : -1);
            return sb.ToString();
        }

        public static T? Deserialize<T>(string json)
        {
            var p = new Parser(json);
            var tree = p.Value();
            p.End();
            return (T?)Convert(tree, typeof(T));
        }

        // ───────────── writing ─────────────

        /// <param name="indent">Current depth when indenting, or -1 for one line.</param>
        static void Write(StringBuilder sb, object? v, int indent)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case DateTime d: WriteString(sb, d.ToString("o", CultureInfo.InvariantCulture)); return;
                case DateTimeOffset o: WriteString(sb, o.ToString("o", CultureInfo.InvariantCulture)); return;
                case TimeSpan t: WriteString(sb, t.ToString("c", CultureInfo.InvariantCulture)); return;
                case Enum e: sb.Append(System.Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); return;
                case double x: sb.Append(x.ToString("R", CultureInfo.InvariantCulture)); return;
                case float x: sb.Append(x.ToString("R", CultureInfo.InvariantCulture)); return;
                case IFormattable f when IsNumber(v.GetType()): sb.Append(f.ToString(null, CultureInfo.InvariantCulture)); return;
                case IDictionary dict:
                    WriteBlock(sb, '{', '}', indent, dict.Keys.Cast<object>().Select(k => (Key: (string?)System.Convert.ToString(k, CultureInfo.InvariantCulture), Value: dict[k])));
                    return;
                case IEnumerable list:
                    WriteBlock(sb, '[', ']', indent, list.Cast<object?>().Select(x => (Key: (string?)null, Value: x)));
                    return;
                default:
                    var props = v.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                 .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);
                    WriteBlock(sb, '{', '}', indent, props.Select(p => (Key: (string?)p.Name, Value: p.GetValue(v))));
                    return;
            }
        }

        static void WriteBlock(StringBuilder sb, char open, char close, int indent, IEnumerable<(string? Key, object? Value)> items)
        {
            sb.Append(open);
            bool any = false;
            foreach (var (key, value) in items)
            {
                if (any) sb.Append(',');
                any = true;
                if (indent >= 0) sb.Append('\n').Append(' ', (indent + 1) * 2);
                if (key != null) { WriteString(sb, key); sb.Append(indent >= 0 ? ": " : ":"); }
                Write(sb, value, indent >= 0 ? indent + 1 : -1);
            }
            if (any && indent >= 0) sb.Append('\n').Append(' ', indent * 2);
            sb.Append(close);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            sb.Append('"');
        }

        static bool IsNumber(Type t) => t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(uint)
                                        || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte) || t == typeof(decimal);

        // ───────────── reading ─────────────

        /// <summary>A parsed number, kept as its text until we know what type it's going into.</summary>
        sealed class Num { public string Text = ""; }

        sealed class Parser
        {
            readonly string s; int i;
            public Parser(string s) { this.s = s; }

            public void End() { Ws(); if (i < s.Length) throw Fail("trailing text"); }

            public object? Value()
            {
                Ws();
                if (i >= s.Length) throw Fail("unexpected end");
                char c = s[i];
                if (c == '{')
                {
                    i++;
                    var o = new Dictionary<string, object?>();
                    Ws();
                    if (Peek('}')) { i++; return o; }
                    while (true)
                    {
                        Ws();
                        var k = Str();
                        Ws(); Expect(':');
                        o[k] = Value();
                        Ws();
                        if (Peek(',')) { i++; continue; }
                        Expect('}'); return o;
                    }
                }
                if (c == '[')
                {
                    i++;
                    var a = new List<object?>();
                    Ws();
                    if (Peek(']')) { i++; return a; }
                    while (true)
                    {
                        a.Add(Value());
                        Ws();
                        if (Peek(',')) { i++; continue; }
                        Expect(']'); return a;
                    }
                }
                if (c == '"') return Str();
                if (Word("true")) return true;
                if (Word("false")) return false;
                if (Word("null")) return null;
                int start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                if (i == start) throw Fail($"unexpected '{c}'");
                return new Num { Text = s.Substring(start, i - start) };
            }

            string Str()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= s.Length) throw Fail("unterminated string");
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; break;
                        default: sb.Append(e); break;   // \" \\ \/
                    }
                }
            }

            void Ws() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
            bool Peek(char c) => i < s.Length && s[i] == c;
            void Expect(char c) { if (!Peek(c)) throw Fail($"expected '{c}'"); i++; }
            bool Word(string w) { if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false; i += w.Length; return true; }
            Exception Fail(string what) => new FormatException($"JSON: {what} at {i}");
        }

        /// <summary>A parsed value (dictionary, list, string, Num, bool or null) as the given type.</summary>
        static object? Convert(object? v, Type t)
        {
            var under = Nullable.GetUnderlyingType(t);
            if (v == null) return t.IsValueType && under == null ? Activator.CreateInstance(t) : null;
            if (under != null) t = under;

            if (t == typeof(object)) return v;
            if (t == typeof(string)) return v is Num n0 ? n0.Text : v.ToString();
            if (t == typeof(bool)) return v is bool b ? b : bool.Parse(v.ToString()!);
            if (t == typeof(DateTime)) return DateTime.Parse((string)v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (t == typeof(DateTimeOffset)) return DateTimeOffset.Parse((string)v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (t == typeof(TimeSpan)) return TimeSpan.Parse((string)v, CultureInfo.InvariantCulture);
            if (t.IsEnum) return v is Num ne ? Enum.ToObject(t, long.Parse(ne.Text, CultureInfo.InvariantCulture)) : Enum.Parse(t, (string)v, true);
            if (v is Num num)
            {
                if (t == typeof(double)) return double.Parse(num.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (t == typeof(float)) return float.Parse(num.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
                return System.Convert.ChangeType(decimal.Parse(num.Text, NumberStyles.Float, CultureInfo.InvariantCulture), t, CultureInfo.InvariantCulture);
            }

            if (t.IsArray && v is List<object?> arr)
            {
                var el = t.GetElementType()!;
                var a = Array.CreateInstance(el, arr.Count);
                for (int k = 0; k < arr.Count; k++) a.SetValue(Convert(arr[k], el), k);
                return a;
            }

            if (v is Dictionary<string, object?> map && Generic(t, typeof(IDictionary<,>)) is { } dargs)
            {
                var d = (IDictionary)Activator.CreateInstance(t.IsInterface ? typeof(Dictionary<,>).MakeGenericType(dargs) : t)!;
                foreach (var kv in map) d[kv.Key] = Convert(kv.Value, dargs[1]);
                return d;
            }

            if (v is List<object?> list && Generic(t, typeof(ICollection<>)) is { } cargs)
            {
                var c = Activator.CreateInstance(t.IsInterface ? typeof(List<>).MakeGenericType(cargs) : t)!;
                var add = typeof(ICollection<>).MakeGenericType(cargs).GetMethod("Add")!;
                foreach (var x in list) add.Invoke(c, new[] { Convert(x, cargs[0]) });
                return c;
            }

            if (v is Dictionary<string, object?> obj)
            {
                // A class with settable properties, or (a positional record) a constructor taking them.
                if (t.GetConstructor(Type.EmptyTypes) is { } ctor0)
                {
                    var o = ctor0.Invoke(null);
                    foreach (var kv in obj)
                        if (t.GetProperty(kv.Key, BindingFlags.Public | BindingFlags.Instance) is { CanWrite: true } p && p.GetSetMethod() != null)
                            p.SetValue(o, Convert(kv.Value, p.PropertyType));
                    return o;
                }
                var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
                var args = ctor.GetParameters().Select(p =>
                {
                    var hit = obj.FirstOrDefault(kv => string.Equals(kv.Key, p.Name, StringComparison.OrdinalIgnoreCase));
                    return hit.Key != null ? Convert(hit.Value, p.ParameterType) : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null);
                }).ToArray();
                return ctor.Invoke(args);
            }
            throw new FormatException($"JSON: can't read {v.GetType().Name} as {t.Name}");
        }

        /// <summary>The type arguments if t is or implements the given generic interface.</summary>
        static Type[]? Generic(Type t, Type iface)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == iface) return t.GetGenericArguments();
            return t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == iface)?.GetGenericArguments();
        }
    }
}
