using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DinkumSaveProtection.Core
{
    // Inspect serialized property names without interpreting or instantiating their values.
    // Array positions share a schema path; ES3's reserved __type marker is not player data.
    public static class JsonPropertyPaths
    {
        public static HashSet<string> Read(byte[] bytes, Encoding encoding)
        {
            var p = new Parser(encoding.GetString(bytes)); p.Value("", 0); p.White();
            if (p.Offset != p.Text.Length) throw new InvalidDataException("Trailing native JSON data");
            return p.Paths;
        }
        private sealed class Parser
        {
            internal readonly string Text;
            internal readonly HashSet<string> Paths = new HashSet<string>(StringComparer.Ordinal);
            internal int Offset;
            private int elements;
            internal Parser(string text) { Text = text; if (text.Length > 0 && text[0] == '\ufeff') Offset = 1; }
            internal void White() { while (Offset < Text.Length && char.IsWhiteSpace(Text[Offset])) Offset++; }
            private char Peek() { White(); return Offset < Text.Length ? Text[Offset] : '\0'; }
            private void Need(char c) { if (Peek() != c) throw new InvalidDataException("Malformed native JSON"); Offset++; }
            private string String()
            {
                Need('"'); var result = new StringBuilder();
                while (Offset < Text.Length)
                {
                    char c = Text[Offset++]; if (c == '"') return result.ToString();
                    if (c < 32) throw new InvalidDataException("Control character in native JSON");
                    if (c == '\\')
                    {
                        if (Offset == Text.Length) break;
                        char escape = Text[Offset++];
                        switch (escape)
                        {
                            case '"': case '\\': case '/': c = escape; break;
                            case 'b': c = '\b'; break; case 'f': c = '\f'; break; case 'n': c = '\n'; break; case 'r': c = '\r'; break; case 't': c = '\t'; break;
                            case 'u':
                                if (Offset + 4 > Text.Length || !ushort.TryParse(Text.Substring(Offset,4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ushort v)) throw new InvalidDataException("Invalid native JSON escape");
                                Offset += 4; c = (char)v; break;
                            default: throw new InvalidDataException("Invalid native JSON escape");
                        }
                    }
                    result.Append(c);
                }
                throw new InvalidDataException("Unterminated native JSON string");
            }
            internal void Value(string path, int depth)
            {
                if (depth > 64 || ++elements > BinaryData.MaxElements) throw new InvalidDataException("Native JSON inspection budget exceeded");
                char c = Peek();
                if (c == '{')
                {
                    Offset++; if (Peek() == '}') { Offset++; return; }
                    while (true)
                    {
                        string name = String(); Need(':'); string child = path + "/" + name.Replace("~", "~0").Replace("/", "~1");
                        if (name != "__type") Paths.Add(child);
                        Value(child, depth + 1);
                        if (Peek() == '}') { Offset++; return; } Need(',');
                    }
                }
                if (c == '[')
                {
                    Offset++; if (Peek() == ']') { Offset++; return; }
                    while (true) { Value(path + "[]", depth + 1); if (Peek() == ']') { Offset++; return; } Need(','); }
                }
                if (c == '"') { String(); return; }
                foreach (var literal in new[] { "true", "false", "null" })
                    if (Offset + literal.Length <= Text.Length && string.CompareOrdinal(Text, Offset, literal, 0, literal.Length) == 0) { Offset += literal.Length; return; }
                if (c == '-') Offset++;
                int start = Offset;
                if (Offset < Text.Length && Text[Offset] == '0') Offset++;
                else while (Offset < Text.Length && Text[Offset] >= '0' && Text[Offset] <= '9') Offset++;
                if (Offset == start) throw new InvalidDataException("Invalid native JSON value");
                if (Offset < Text.Length && Text[Offset] == '.') { Offset++; Digits(); }
                if (Offset < Text.Length && (Text[Offset] == 'e' || Text[Offset] == 'E')) { Offset++; if (Offset < Text.Length && (Text[Offset] == '+' || Text[Offset] == '-')) Offset++; Digits(); }
            }
            private void Digits()
            { int start = Offset; while (Offset < Text.Length && Text[Offset] >= '0' && Text[Offset] <= '9') Offset++; if (Offset == start) throw new InvalidDataException("Invalid native JSON number"); }
        }
    }
}
