using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XRay.PsdFx
{
    /// <summary>Kinds of value in a Photoshop text-engine (<c>EngineData</c>) tree.</summary>
    public enum EngineValueKind
    {
        Dictionary,
        List,
        Number,
        Boolean,
        String,
        Name,
    }

    /// <summary>
    /// One node of the PostScript-like <c>EngineData</c> structure stored in type
    /// layers (<c>TySh</c>) and the global <c>Txt2</c> block. Strings are decoded
    /// from their UTF-16 form.
    /// </summary>
    public sealed class EngineValue
    {
        private static readonly IReadOnlyList<KeyValuePair<string, EngineValue>> EmptyEntries =
            new KeyValuePair<string, EngineValue>[0];

        private static readonly IReadOnlyList<EngineValue> EmptyItems = new EngineValue[0];

        public EngineValueKind Kind { get; set; }

        public double Number { get; set; }

        public bool Boolean { get; set; }

        /// <summary>Decoded text for String kind, or the name for Name kind.</summary>
        public string Text { get; set; } = string.Empty;

        public IReadOnlyList<KeyValuePair<string, EngineValue>> Entries { get; set; } = EmptyEntries;

        public IReadOnlyList<EngineValue> Items { get; set; } = EmptyItems;

        public EngineValue this[string key]
        {
            get
            {
                foreach (var entry in Entries)
                {
                    if (entry.Key == key)
                    {
                        return entry.Value;
                    }
                }

                return null;
            }
        }

        public EngineValue this[int index]
        {
            get { return index >= 0 && index < Items.Count ? Items[index] : null; }
        }

        /// <summary>Follows a path of dictionary keys and list indices, e.g. Get("EngineDict", "Editor", "Text").</summary>
        public EngineValue Get(params object[] path)
        {
            EngineValue current = this;
            foreach (var step in path)
            {
                var key = step as string;
                if (key != null)
                {
                    current = current == null ? null : current[key];
                }
                else if (step is int)
                {
                    current = current == null ? null : current[(int)step];
                }
                else
                {
                    return null;
                }

                if (current == null)
                {
                    return null;
                }
            }

            return current;
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case EngineValueKind.Number:
                    return Number.ToString(CultureInfo.InvariantCulture);
                case EngineValueKind.Boolean:
                    return Boolean ? "true" : "false";
                case EngineValueKind.String:
                    return "(" + Text + ")";
                case EngineValueKind.Name:
                    return "/" + Text;
                case EngineValueKind.List:
                    return "[" + Items.Count + "]";
                default:
                    return "<<" + Entries.Count + ">>";
            }
        }
    }

    /// <summary>Parser for the text-engine data format.</summary>
    public static class EngineDataParser
    {
        private const int MaxDepth = 256;

        /// <summary>Parses EngineData bytes. Returns null when the data is malformed.</summary>
        public static EngineValue Parse(byte[] data)
        {
            var parser = new Parser(data);
            try
            {
                return parser.ParseRoot();
            }
            catch (FormatException)
            {
                return null;
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }

        /// <summary>Decodes an EngineData string body (between the parentheses), unescaping and decoding UTF-16.</summary>
        public static string DecodeString(byte[] body)
        {
            var unescaped = new byte[body.Length];
            var length = 0;
            for (var i = 0; i < body.Length; i++)
            {
                var b = body[i];
                if (b == (byte)'\\' && i + 1 < body.Length)
                {
                    i++;
                    b = body[i];
                }

                unescaped[length++] = b;
            }

            if (length >= 2 && unescaped[0] == 0xFE && unescaped[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(unescaped, 2, length - 2);
            }

            if (length >= 2 && unescaped[0] == 0xFF && unescaped[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(unescaped, 2, length - 2);
            }

            return Encoding.GetEncoding(28591).GetString(unescaped, 0, length);
        }

        private sealed class Parser
        {
            private readonly byte[] _data;
            private int _pos;

            public Parser(byte[] data)
            {
                _data = data;
                _pos = 0;
            }

            public EngineValue ParseRoot()
            {
                SkipWhitespace();
                if (Peek2("<<"))
                {
                    return ParseValue(0);
                }

                // Txt2-style data can be a bare sequence of /key value pairs.
                return ParseDictionaryEntries(0, true);
            }

            private static bool IsSpace(byte c)
            {
                return c == (byte)' ' || c == (byte)'\t' || c == (byte)'\r' || c == (byte)'\n' || c == 0;
            }

            private static bool IsDelimiter(byte c)
            {
                return c == (byte)'<' || c == (byte)'>' || c == (byte)'[' || c == (byte)']' || c == (byte)'(' || c == (byte)')' || c == (byte)'/';
            }

            private void SkipWhitespace()
            {
                while (_pos < _data.Length && IsSpace(_data[_pos]))
                {
                    _pos++;
                }
            }

            private bool Peek2(string token)
            {
                return _pos + 1 < _data.Length && _data[_pos] == token[0] && _data[_pos + 1] == token[1];
            }

            private EngineValue ParseValue(int depth)
            {
                if (depth > MaxDepth)
                {
                    throw new FormatException("EngineData nesting is too deep.");
                }

                SkipWhitespace();
                if (_pos >= _data.Length)
                {
                    throw new FormatException("Unexpected end of EngineData.");
                }

                var c = _data[_pos];
                if (Peek2("<<"))
                {
                    _pos += 2;
                    return ParseDictionaryEntries(depth + 1, false);
                }

                if (c == (byte)'[')
                {
                    _pos++;
                    var items = new List<EngineValue>();
                    while (true)
                    {
                        SkipWhitespace();
                        if (_pos >= _data.Length)
                        {
                            throw new FormatException("Unterminated EngineData list.");
                        }

                        if (_data[_pos] == (byte)']')
                        {
                            _pos++;
                            return new EngineValue { Kind = EngineValueKind.List, Items = items };
                        }

                        items.Add(ParseValue(depth + 1));
                    }
                }

                if (c == (byte)'(')
                {
                    _pos++;
                    var start = _pos;
                    while (_pos < _data.Length)
                    {
                        if (_data[_pos] == (byte)'\\')
                        {
                            _pos += 2;
                            continue;
                        }

                        if (_data[_pos] == (byte)')')
                        {
                            break;
                        }

                        _pos++;
                    }

                    if (_pos >= _data.Length)
                    {
                        throw new FormatException("Unterminated EngineData string.");
                    }

                    var body = new byte[_pos - start];
                    Array.Copy(_data, start, body, 0, body.Length);
                    var text = DecodeString(body);
                    _pos++;
                    return new EngineValue { Kind = EngineValueKind.String, Text = text };
                }

                if (c == (byte)'/')
                {
                    _pos++;
                    return new EngineValue { Kind = EngineValueKind.Name, Text = ReadBareToken() };
                }

                if (c == (byte)'>' || c == (byte)']' || c == (byte)')')
                {
                    throw new FormatException("Unexpected EngineData delimiter.");
                }

                var token = ReadBareToken();
                if (token == "true" || token == "false")
                {
                    return new EngineValue { Kind = EngineValueKind.Boolean, Boolean = token == "true" };
                }

                double number;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    return new EngineValue { Kind = EngineValueKind.Number, Number = number };
                }

                // Unknown bare tokens (rare) are kept as names.
                return new EngineValue { Kind = EngineValueKind.Name, Text = token };
            }

            private string ReadBareToken()
            {
                var start = _pos;
                while (_pos < _data.Length && !IsSpace(_data[_pos]) && !IsDelimiter(_data[_pos]))
                {
                    _pos++;
                }

                if (_pos == start)
                {
                    throw new FormatException("Empty EngineData token.");
                }

                return Encoding.GetEncoding(28591).GetString(_data, start, _pos - start);
            }

            private EngineValue ParseDictionaryEntries(int depth, bool bare)
            {
                var entries = new List<KeyValuePair<string, EngineValue>>();
                while (true)
                {
                    SkipWhitespace();
                    if (_pos >= _data.Length)
                    {
                        if (bare)
                        {
                            return new EngineValue { Kind = EngineValueKind.Dictionary, Entries = entries };
                        }

                        throw new FormatException("Unterminated EngineData dictionary.");
                    }

                    if (Peek2(">>"))
                    {
                        _pos += 2;
                        if (bare)
                        {
                            throw new FormatException("Unbalanced EngineData dictionary.");
                        }

                        return new EngineValue { Kind = EngineValueKind.Dictionary, Entries = entries };
                    }

                    if (_data[_pos] != (byte)'/')
                    {
                        throw new FormatException("EngineData dictionary key expected.");
                    }

                    _pos++;
                    var key = ReadBareToken();
                    entries.Add(new KeyValuePair<string, EngineValue>(key, ParseValue(depth + 1)));
                }
            }
        }
    }
}
