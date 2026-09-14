using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PostgresCommandExecuter.Editors
{
    /// <summary>
    /// Kinds of lexical tokens understood by the JSON viewer.  The tokenizer is
    /// deliberately independent from WPF so it can also be used by validation
    /// and tests without touching the UI thread.
    /// </summary>
    public enum JsonSyntaxTokenKind
    {
        Whitespace,
        PropertyName,
        String,
        Number,
        Boolean,
        Null,
        Punctuation,
        Invalid
    }

    /// <summary>
    /// A contiguous range of characters in the source JSON document.
    /// </summary>
    public sealed class JsonSyntaxToken
    {
        public JsonSyntaxToken(int start, int length, JsonSyntaxTokenKind kind)
        {
            Start = start;
            Length = length;
            Kind = kind;
        }

        public int Start { get; private set; }

        public int Length { get; private set; }

        public JsonSyntaxTokenKind Kind { get; private set; }
    }

    /// <summary>
    /// Result returned to the UI instead of propagating JSON parser exceptions.
    /// Line and column are one-based when an error has a known location.
    /// </summary>
    public sealed class JsonValidationResult
    {
        private JsonValidationResult(bool isValid, string message, int line, int column)
        {
            IsValid = isValid;
            Message = message;
            Line = line;
            Column = column;
        }

        public bool IsValid { get; private set; }

        public string Message { get; private set; }

        public int Line { get; private set; }

        public int Column { get; private set; }

        public static JsonValidationResult Valid()
        {
            return new JsonValidationResult(true, "JSON válido.", 0, 0);
        }

        public static JsonValidationResult Invalid(string message, int line, int column)
        {
            return new JsonValidationResult(false, message, Math.Max(1, line), Math.Max(1, column));
        }
    }

    /// <summary>
    /// Formatting operation result.  When formatting fails the original text is
    /// left untouched and Validation contains the friendly error information.
    /// </summary>
    public sealed class JsonFormatResult
    {
        public JsonFormatResult(JsonValidationResult validation, string formattedText)
        {
            Validation = validation;
            FormattedText = formattedText;
        }

        public JsonValidationResult Validation { get; private set; }

        public string FormattedText { get; private set; }

        public bool Success
        {
            get { return Validation != null && Validation.IsValid; }
        }
    }

    /// <summary>
    /// Provides resilient lexical analysis, strict validation and indentation for
    /// JSON received from PostgreSQL or pasted into a future JSON editor.
    /// </summary>
    public static class JsonDocumentProcessor
    {
        /// <summary>
        /// Produces tokens for every character in <paramref name="text"/>.  It
        /// intentionally does not throw for incomplete input: an unfinished
        /// string, for example, is represented as an Invalid token and the rest
        /// of the document continues to be recognized.
        /// </summary>
        public static IList<JsonSyntaxToken> Tokenize(string text)
        {
            text = text ?? string.Empty;
            var tokens = new List<JsonSyntaxToken>();
            int index = 0;

            while (index < text.Length)
            {
                char current = text[index];
                int start = index;

                if (char.IsWhiteSpace(current))
                {
                    index++;
                    while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                    tokens.Add(new JsonSyntaxToken(start, index - start, JsonSyntaxTokenKind.Whitespace));
                    continue;
                }

                if (current == '"')
                {
                    bool closed = ReadString(text, ref index);
                    JsonSyntaxTokenKind kind = closed && IsPropertyName(text, index)
                        ? JsonSyntaxTokenKind.PropertyName
                        : closed ? JsonSyntaxTokenKind.String : JsonSyntaxTokenKind.Invalid;
                    tokens.Add(new JsonSyntaxToken(start, index - start, kind));
                    continue;
                }

                if (IsPunctuation(current))
                {
                    index++;
                    tokens.Add(new JsonSyntaxToken(start, 1, JsonSyntaxTokenKind.Punctuation));
                    continue;
                }

                int keywordLength;
                JsonSyntaxTokenKind keywordKind;
                if (TryReadKeyword(text, index, out keywordLength, out keywordKind))
                {
                    index += keywordLength;
                    tokens.Add(new JsonSyntaxToken(start, keywordLength, keywordKind));
                    continue;
                }

                int numberLength = ReadNumber(text, index);
                if (numberLength > 0)
                {
                    index += numberLength;
                    tokens.Add(new JsonSyntaxToken(start, numberLength, JsonSyntaxTokenKind.Number));
                    continue;
                }

                index++;
                tokens.Add(new JsonSyntaxToken(start, 1, JsonSyntaxTokenKind.Invalid));
            }

            return tokens;
        }

        /// <summary>
        /// Validates one complete, standard JSON value.  Parser errors are
        /// converted to a Portuguese message and a one-based source location.
        /// </summary>
        public static JsonValidationResult Validate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return JsonValidationResult.Invalid("O conteúdo JSON está vazio.", 1, 1);
            }

            // Json.NET deliberately accepts a few JavaScript extensions, such
            // as comments.  The result viewer promises JSON, so reject lexical
            // characters outside the RFC JSON alphabet before asking Json.NET to
            // parse the document.
            foreach (JsonSyntaxToken token in Tokenize(text))
            {
                if (token.Kind != JsonSyntaxTokenKind.Invalid) continue;

                int[] position = GetLineAndColumn(text, token.Start);
                return JsonValidationResult.Invalid(
                    "JSON inválido: caractere ou sequência não reconhecida.",
                    position[0],
                    position[1]);
            }

            try
            {
                using (var stringReader = new StringReader(text))
                using (var reader = new JsonTextReader(stringReader))
                {
                    reader.Culture = CultureInfo.InvariantCulture;
                    reader.DateParseHandling = DateParseHandling.None;
                    reader.FloatParseHandling = FloatParseHandling.Decimal;

                    JToken.ReadFrom(reader);

                    // JToken.ReadFrom consumes a single value.  A second non-
                    // whitespace value would otherwise be silently ignored.
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonToken.Comment) continue;
                        return JsonValidationResult.Invalid(
                            "Há conteúdo adicional após o primeiro valor JSON.",
                            Math.Max(1, reader.LineNumber),
                            Math.Max(1, reader.LinePosition));
                    }
                }

                return JsonValidationResult.Valid();
            }
            catch (JsonReaderException exception)
            {
                return JsonValidationResult.Invalid(
                    "JSON inválido: " + CleanParserMessage(exception.Message),
                    Math.Max(1, exception.LineNumber),
                    Math.Max(1, exception.LinePosition));
            }
            catch (JsonException exception)
            {
                return JsonValidationResult.Invalid(
                    "JSON inválido: " + CleanParserMessage(exception.Message),
                    1,
                    1);
            }
        }

        /// <summary>
        /// Parses and indents JSON only when it is valid.  No exception reaches
        /// the caller, which keeps the result pane stable while a document is
        /// being updated.
        /// </summary>
        public static JsonFormatResult TryFormat(string text)
        {
            JsonValidationResult validation = Validate(text);
            if (!validation.IsValid)
            {
                return new JsonFormatResult(validation, text ?? string.Empty);
            }

            try
            {
                var settings = new JsonLoadSettings
                {
                    CommentHandling = CommentHandling.Ignore,
                    LineInfoHandling = LineInfoHandling.Ignore
                };

                JToken token = JToken.Parse(text, settings);
                return new JsonFormatResult(validation, token.ToString(Formatting.Indented));
            }
            catch (JsonException exception)
            {
                // This branch is defensive: Validate has already parsed the
                // document, but a caller should still never receive an exception.
                return new JsonFormatResult(
                    JsonValidationResult.Invalid("JSON inválido: " + CleanParserMessage(exception.Message), 1, 1),
                    text ?? string.Empty);
            }
        }

        private static bool ReadString(string text, ref int index)
        {
            // Starts on the opening quote and always advances to the end of the
            // malformed token if necessary, preventing a single bad escape from
            // corrupting the highlighting of following JSON.
            index++;
            bool escape = false;

            while (index < text.Length)
            {
                char current = text[index++];
                if (escape)
                {
                    if (current == 'u')
                    {
                        int remaining = Math.Min(4, text.Length - index);
                        index += remaining;
                    }

                    escape = false;
                    continue;
                }

                if (current == '\\')
                {
                    escape = true;
                    continue;
                }

                if (current == '"') return true;
                if (current == '\r' || current == '\n') return false;
            }

            return false;
        }

        private static bool IsPropertyName(string text, int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            return index < text.Length && text[index] == ':';
        }

        private static bool IsPunctuation(char value)
        {
            return value == '{' || value == '}' || value == '[' || value == ']' || value == ',' || value == ':';
        }

        private static bool TryReadKeyword(string text, int start, out int length, out JsonSyntaxTokenKind kind)
        {
            length = 0;
            kind = JsonSyntaxTokenKind.Invalid;

            if (MatchesWord(text, start, "true") || MatchesWord(text, start, "false"))
            {
                length = text[start] == 't' ? 4 : 5;
                kind = JsonSyntaxTokenKind.Boolean;
                return true;
            }

            if (MatchesWord(text, start, "null"))
            {
                length = 4;
                kind = JsonSyntaxTokenKind.Null;
                return true;
            }

            return false;
        }

        private static bool MatchesWord(string text, int start, string value)
        {
            if (start + value.Length > text.Length) return false;
            for (int index = 0; index < value.Length; index++)
            {
                if (text[start + index] != value[index]) return false;
            }

            int end = start + value.Length;
            return end == text.Length || !IsIdentifierCharacter(text[end]);
        }

        private static bool IsIdentifierCharacter(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_';
        }

        private static int ReadNumber(string text, int start)
        {
            int index = start;
            if (text[index] == '-') index++;
            if (index >= text.Length || !char.IsDigit(text[index])) return 0;

            if (text[index] == '0')
            {
                index++;
            }
            else
            {
                while (index < text.Length && char.IsDigit(text[index])) index++;
            }

            if (index < text.Length && text[index] == '.')
            {
                int decimalStart = ++index;
                while (index < text.Length && char.IsDigit(text[index])) index++;
                if (decimalStart == index) return index - start;
            }

            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                int exponentStart = index++;
                if (index < text.Length && (text[index] == '+' || text[index] == '-')) index++;
                int exponentDigits = index;
                while (index < text.Length && char.IsDigit(text[index])) index++;
                if (exponentDigits == index) return exponentStart - start;
            }

            return index - start;
        }

        private static string CleanParserMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "O conteúdo não segue a sintaxe JSON.";

            int lineInfo = message.IndexOf(" Path '", StringComparison.Ordinal);
            return (lineInfo >= 0 ? message.Substring(0, lineInfo) : message).TrimEnd('.', ' ');
        }

        private static int[] GetLineAndColumn(string text, int offset)
        {
            int line = 1;
            int column = 1;
            int limit = Math.Max(0, Math.Min(offset, text.Length));

            for (int index = 0; index < limit; index++)
            {
                if (text[index] == '\r')
                {
                    if (index + 1 < limit && text[index + 1] == '\n') index++;
                    line++;
                    column = 1;
                }
                else if (text[index] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            return new[] { line, column };
        }
    }
}
