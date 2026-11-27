using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AppStore
{
    /// <summary>
    /// Small JSON parser for .NET CF 3.5. Produces Dictionary&lt;string,object&gt;,
    /// List&lt;object&gt;, string, double, bool or null.
    /// </summary>
    public class MiniJson
    {
        readonly string s;
        int p;

        MiniJson(string text) { s = text; p = 0; }

        public static object Parse(string text)
        {
            MiniJson j = new MiniJson(text);
            object v = j.Value();
            j.Ws();
            return v;
        }

        // ---- helpers for reading parsed data --------------------------------

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return "";
            if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
            return v.ToString();
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        public static List<object> Arr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return v as List<object>;
        }

        // ---- parser ---------------------------------------------------------

        void Ws()
        {
            while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r' || s[p] == '\n')) p++;
        }

        Exception Err(string what)
        {
            return new FormatException("JSON: " + what + " at " + p);
        }

        object Value()
        {
            Ws();
            if (p >= s.Length) throw Err("unexpected end");
            char c = s[p];
            if (c == '{') return Object();
            if (c == '[') return Array();
            if (c == '"') return String();
            if (c == 't') { Expect("true"); return true; }
            if (c == 'f') { Expect("false"); return false; }
            if (c == 'n') { Expect("null"); return null; }
            return Number();
        }

        void Expect(string word)
        {
            if (p + word.Length > s.Length || s.Substring(p, word.Length) != word) throw Err("expected " + word);
            p += word.Length;
        }

        Dictionary<string, object> Object()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            p++; Ws();
            if (p < s.Length && s[p] == '}') { p++; return d; }
            for (;;)
            {
                Ws();
                if (p >= s.Length || s[p] != '"') throw Err("expected key");
                string k = String();
                Ws();
                if (p >= s.Length || s[p] != ':') throw Err("expected ':'");
                p++;
                d[k] = Value();
                Ws();
                if (p >= s.Length) throw Err("unterminated object");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == '}') { p++; return d; }
                throw Err("expected ',' or '}'");
            }
        }

        List<object> Array()
        {
            List<object> a = new List<object>();
            p++; Ws();
            if (p < s.Length && s[p] == ']') { p++; return a; }
            for (;;)
            {
                a.Add(Value());
                Ws();
                if (p >= s.Length) throw Err("unterminated array");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == ']') { p++; return a; }
                throw Err("expected ',' or ']'");
            }
        }

        string String()
        {
            StringBuilder sb = new StringBuilder();
            p++; // opening quote
            while (p < s.Length)
            {
                char c = s[p++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (p >= s.Length) break;
                char e = s[p++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (p + 4 > s.Length) throw Err("bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(p, 4), NumberStyles.HexNumber,
                                                  CultureInfo.InvariantCulture));
                        p += 4;
                        break;
                    default: throw Err("bad escape");
                }
            }
            throw Err("unterminated string");
        }

        object Number()
        {
            int start = p;
            while (p < s.Length && "+-0123456789.eE".IndexOf(s[p]) >= 0) p++;
            if (p == start) throw Err("unexpected character '" + s[p] + "'");
            return double.Parse(s.Substring(start, p - start), NumberStyles.Float,
                                CultureInfo.InvariantCulture);
        }
    }
}
