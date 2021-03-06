﻿using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AzurePipelines.TestLogger.Json
{
    internal class JsonBuffer
    {
        public const string ValueNull = "null";
        public const string ValueTrue = "true";
        public const string ValueFalse = "false";
        private readonly TextReader _reader;
        private readonly StringBuilder _buffer = new StringBuilder();
        private readonly StringBuilder _codePointBuffer = new StringBuilder(4);
        private JsonToken _token;
        private int _line;
        private int _column;

        public JsonBuffer(TextReader reader)
        {
            _reader = reader;
            _line = 1;
        }

        public JsonToken Read()
        {
            int first;
            while (true)
            {
                first = ReadNextChar();

                if (first == -1)
                {
                    _token.Type = JsonTokenType.EOF;
                    return _token;
                }
                else if (!IsWhitespace(first))
                {
                    break;
                }
            }

            _token.Value = ((char)first).ToString();
            _token.Line = _line;
            _token.Column = _column;

            if (first == '{')
            {
                _token.Type = JsonTokenType.LeftCurlyBracket;
            }
            else if (first == '}')
            {
                _token.Type = JsonTokenType.RightCurlyBracket;
            }
            else if (first == '[')
            {
                _token.Type = JsonTokenType.LeftSquareBracket;
            }
            else if (first == ']')
            {
                _token.Type = JsonTokenType.RightSquareBracket;
            }
            else if (first == ':')
            {
                _token.Type = JsonTokenType.Colon;
            }
            else if (first == ',')
            {
                _token.Type = JsonTokenType.Comma;
            }
            else if (first == '"')
            {
                _token.Type = JsonTokenType.String;
                _token.Value = ReadString();
            }
            else if (first == 't')
            {
                ReadLiteral(ValueTrue);
                _token.Type = JsonTokenType.True;
            }
            else if (first == 'f')
            {
                ReadLiteral(ValueFalse);
                _token.Type = JsonTokenType.False;
            }
            else if (first == 'n')
            {
                ReadLiteral(ValueNull);
                _token.Type = JsonTokenType.Null;
            }
            else if ((first >= '0' && first <= '9') || first == '-')
            {
                _token.Type = JsonTokenType.Number;
                _token.Value = ReadNumber(first);
            }
            else
            {
                throw new JsonDeserializerException(
                    JsonDeserializerResource.Format_IllegalCharacter(first),
                    _token);
            }

            // JsonToken is a value type
            return _token;
        }

        private int ReadNextChar()
        {
            while (true)
            {
                int value = _reader.Read();
                _column++;
                switch (value)
                {
                    case -1:
                        // This is the end of file
                        return -1;
                    case '\n':
                        // This is a new line. Let the next loop read the first character of the following line.
                        // Set position ahead of next line
                        _column = 0;
                        _line++;

                        continue;
                    case '\r':
                        break;
                    default:
                        // Returns the normal value
                        return value;
                }
            }
        }

        private string ReadNumber(int firstRead)
        {
#if NET35
            _buffer = new StringBuilder();
#else
            _buffer.Clear();
#endif
            _buffer.Append((char)firstRead);

            while (true)
            {
                int next = _reader.Peek();

                if ((next >= '0' && next <= '9')
                    || next == '.'
                    || next == 'e'
                    || next == 'E')
                {
                    _buffer.Append((char)ReadNextChar());
                }
                else
                {
                    break;
                }
            }

            return _buffer.ToString();
        }

        private void ReadLiteral(string literal)
        {
            for (int i = 1; i < literal.Length; ++i)
            {
                int next = _reader.Peek();
                if (next != literal[i])
                {
                    throw new JsonDeserializerException(
                        JsonDeserializerResource.Format_UnrecognizedLiteral(literal),
                        _line,
                        _column);
                }
                else
                {
                    ReadNextChar();
                }
            }

            int tail = _reader.Peek();
            if (tail != '}'
                && tail != ']'
                && tail != ','
                && tail != '\n'
                && tail != -1
                && !IsWhitespace(tail))
            {
                throw new JsonDeserializerException(
                    JsonDeserializerResource.Format_IllegalTrailingCharacterAfterLiteral(tail, literal),
                    _line,
                    _column);
            }
        }

        private string ReadString()
        {
#if NET35
            _buffer = new StringBuilder();
#else
            _buffer.Clear();
#endif
            bool escaped = false;

            while (true)
            {
                int next = ReadNextChar();

                if (next == -1 || next == '\n')
                {
                    throw new JsonDeserializerException(
                        JsonDeserializerResource.JSON_OpenString,
                        _line,
                        _column);
                }
                else if (escaped)
                {
                    if ((next == '"') || (next == '\\') || (next == '/'))
                    {
                        _buffer.Append((char)next);
                    }
                    else if (next == 'b')
                    {
                        // '\b' backspace
                        _buffer.Append('\b');
                    }
                    else if (next == 'f')
                    {
                        // '\f' form feed
                        _buffer.Append('\f');
                    }
                    else if (next == 'n')
                    {
                        // '\n' line feed
                        _buffer.Append('\n');
                    }
                    else if (next == 'r')
                    {
                        // '\r' carriage return
                        _buffer.Append('\r');
                    }
                    else if (next == 't')
                    {
                        // '\t' tab
                        _buffer.Append('\t');
                    }
                    else if (next == 'u')
                    {
                        // '\uXXXX' unicode
                        int unicodeLine = _line;
                        int unicodeColumn = _column;

#if NET35
                        _codePointBuffer = new StringBuilder(4);
#else
                        _codePointBuffer.Clear();
#endif
                        for (int i = 0; i < 4; ++i)
                        {
                            next = ReadNextChar();
                            if (next == -1)
                            {
                                throw new JsonDeserializerException(
                                    JsonDeserializerResource.JSON_InvalidEnd,
                                    unicodeLine,
                                    unicodeColumn);
                            }
                            else
                            {
                                _codePointBuffer[i] = (char)next;
                            }
                        }

                        try
                        {
                            int unicodeValue = int.Parse(_codePointBuffer.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            _buffer.Append((char)unicodeValue);
                        }
                        catch (FormatException ex)
                        {
                            throw new JsonDeserializerException(
                                JsonDeserializerResource.Format_InvalidUnicode(_codePointBuffer.ToString()),
                                ex,
                                unicodeLine,
                                unicodeColumn);
                        }
                    }
                    else
                    {
                        throw new JsonDeserializerException(
                            JsonDeserializerResource.Format_InvalidSyntaxNotExpected("character escape", "\\" + next),
                            _line,
                            _column);
                    }

                    escaped = false;
                }
                else if (next == '\\')
                {
                    escaped = true;
                }
                else if (next == '"')
                {
                    break;
                }
                else
                {
                    _buffer.Append((char)next);
                }
            }

            return _buffer.ToString();
        }

        private static bool IsWhitespace(int value)
        {
            return value == ' ' || value == '\t' || value == '\r';
        }
    }
}
