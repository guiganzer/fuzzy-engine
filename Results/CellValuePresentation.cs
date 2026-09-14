using PostgresCommandExecuter.Editors;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PostgresCommandExecuter.Results
{
    /// <summary>
    /// Backend information used to choose a safe, presentation-only editor mode.
    /// It is intentionally independent from DataGrid and Npgsql so a value window
    /// can also be opened by future copy, history or comparison features.
    /// </summary>
    public sealed class CellValueMetadata
    {
        public CellValueMetadata(string columnName, string typeName, string structure)
            : this(columnName, typeName, typeName, structure)
        {
        }

        public CellValueMetadata(string columnName, string typeName, string semanticTypeName, string structure)
        {
            ColumnName = columnName ?? string.Empty;
            TypeName = typeName ?? string.Empty;
            SemanticTypeName = semanticTypeName ?? TypeName;
            Structure = structure ?? string.Empty;
        }

        public string ColumnName { get; private set; }

        public string TypeName { get; private set; }

        public string SemanticTypeName { get; private set; }

        public string Structure { get; private set; }
    }

    internal enum CellValuePresentationKind
    {
        PlainText,
        Sql,
        Json,
        JsonPath,
        Xml,
        Array,
        Range
    }

    internal sealed class CellValuePresentationResult
    {
        internal CellValuePresentationResult(IList<TokenStyleSpan> spans, string status)
        {
            Spans = spans;
            Status = status ?? string.Empty;
        }

        internal IList<TokenStyleSpan> Spans { get; private set; }

        internal string Status { get; private set; }
    }

    /// <summary>
    /// Produces display-only syntax spans.  It never changes content while users
    /// type; only valid json/jsonb values are indented when the dialog first opens.
    /// This protects SQL, XML and textual values where whitespace can be meaningful.
    /// </summary>
    internal static class CellValuePresentation
    {
        internal static CellValuePresentationKind ResolveKind(CellValueMetadata metadata)
        {
            string type = Normalize(metadata == null ? null : metadata.SemanticTypeName);
            string structure = Normalize(metadata == null ? null : metadata.Structure);
            string columnName = Normalize(metadata == null ? null : metadata.ColumnName);

            if (type == "json" || type == "jsonb") return CellValuePresentationKind.Json;
            if (type == "jsonpath") return CellValuePresentationKind.JsonPath;
            if (type == "xml") return CellValuePresentationKind.Xml;
            if (structure == "array" || type.EndsWith("[]", StringComparison.Ordinal)) return CellValuePresentationKind.Array;
            if (structure == "range" || structure == "multirange" ||
                type == "int4range" || type == "int8range" || type == "numrange" || type == "daterange" ||
                type == "tsrange" || type == "tstzrange" || type == "int4multirange" || type == "int8multirange" ||
                type == "nummultirange" || type == "datemultirange" || type == "tsmultirange" || type == "tstzmultirange") return CellValuePresentationKind.Range;

            // PostgreSQL has no built-in SQL scalar.  A caller can nevertheless
            // request SQL presentation explicitly through a domain/type name, or
            // through an unambiguous column name, without mutating the value.
            if (type == "sql" || type == "query" || type == "postgresql") return CellValuePresentationKind.Sql;
            if (columnName.IndexOf("sql", StringComparison.Ordinal) >= 0 ||
                columnName.IndexOf("query", StringComparison.Ordinal) >= 0)
                return CellValuePresentationKind.Sql;

            return CellValuePresentationKind.PlainText;
        }

        internal static string FormatInitialValue(string value, CellValueMetadata metadata, out string status)
        {
            value = value ?? string.Empty;
            if (ResolveKind(metadata) != CellValuePresentationKind.Json)
            {
                status = GetModeLabel(ResolveKind(metadata));
                return value;
            }

            JsonFormatResult formatted = JsonDocumentProcessor.TryFormat(value);
            if (formatted.Success)
            {
                status = "JSON válido e indentado para leitura.";
                return formatted.FormattedText;
            }

            status = BuildJsonError(formatted.Validation);
            return value;
        }

        internal static CellValuePresentationResult Analyze(string value, CellValueMetadata metadata)
        {
            value = value ?? string.Empty;
            CellValuePresentationKind kind = ResolveKind(metadata);
            IList<TokenStyleSpan> spans;
            string status;

            switch (kind)
            {
                case CellValuePresentationKind.Sql:
                    SqlLexResult sql = SqlLexer.Tokenize(value);
                    spans = ToSqlSpans(sql.Tokens);
                    status = sql.Diagnostics.Count == 0
                        ? "SQL: reconhecimento léxico local. A validação final é do PostgreSQL."
                        : sql.Diagnostics[0].Message;
                    break;

                case CellValuePresentationKind.Json:
                    spans = ToJsonSpans(JsonDocumentProcessor.Tokenize(value));
                    status = BuildJsonStatus(value);
                    break;

                case CellValuePresentationKind.JsonPath:
                    spans = ReadJsonPath(value);
                    status = "JSONPath: apresentação léxica local.";
                    break;

                case CellValuePresentationKind.Xml:
                    spans = ReadXml(value);
                    status = "XML: apresentação estrutural sem alterar o conteúdo.";
                    break;

                case CellValuePresentationKind.Array:
                    spans = ReadStructuredLiteral(value);
                    status = "Array PostgreSQL: apresentação estrutural sem alterar o conteúdo.";
                    break;

                case CellValuePresentationKind.Range:
                    spans = ReadStructuredLiteral(value);
                    status = "Range PostgreSQL: apresentação estrutural sem alterar o conteúdo.";
                    break;

                default:
                    spans = new List<TokenStyleSpan>();
                    status = "Texto: conteúdo preservado integralmente.";
                    break;
            }

            return new CellValuePresentationResult(spans, status);
        }

        internal static string GetModeLabel(CellValuePresentationKind kind)
        {
            switch (kind)
            {
                case CellValuePresentationKind.Sql: return "SQL: apresentação sem alterar o conteúdo.";
                case CellValuePresentationKind.Json: return "JSON: apresentação estruturada.";
                case CellValuePresentationKind.JsonPath: return "JSONPath: apresentação léxica local.";
                case CellValuePresentationKind.Xml: return "XML: apresentação estrutural sem alterar o conteúdo.";
                case CellValuePresentationKind.Array: return "Array PostgreSQL: conteúdo preservado.";
                case CellValuePresentationKind.Range: return "Range PostgreSQL: conteúdo preservado.";
                default: return "Texto: conteúdo preservado integralmente.";
            }
        }

        private static string BuildJsonStatus(string value)
        {
            JsonValidationResult validation = JsonDocumentProcessor.Validate(value);
            return validation.IsValid ? "JSON válido." : BuildJsonError(validation);
        }

        private static string BuildJsonError(JsonValidationResult validation)
        {
            if (validation == null) return "JSON inválido.";
            return validation.IsValid
                ? "JSON válido."
                : string.Format(CultureInfo.CurrentCulture, "{0} (linha {1}, coluna {2})", validation.Message, validation.Line, validation.Column);
        }

        private static IList<TokenStyleSpan> ToSqlSpans(IList<SqlToken> tokens)
        {
            var spans = new List<TokenStyleSpan>();
            foreach (SqlToken token in tokens)
            {
                TokenStyleKind kind;
                bool emphasized = false;
                switch (token.Kind)
                {
                    case SqlTokenKind.Keyword: kind = TokenStyleKind.Keyword; emphasized = true; break;
                    case SqlTokenKind.DataType: kind = TokenStyleKind.Type; break;
                    case SqlTokenKind.Function: kind = TokenStyleKind.Function; break;
                    case SqlTokenKind.String:
                    case SqlTokenKind.QuotedIdentifier: kind = TokenStyleKind.String; break;
                    case SqlTokenKind.Number: kind = TokenStyleKind.Number; break;
                    case SqlTokenKind.Comment: kind = TokenStyleKind.Comment; break;
                    case SqlTokenKind.Parameter: kind = TokenStyleKind.Parameter; break;
                    default: continue;
                }

                spans.Add(new TokenStyleSpan(token.Offset, token.Length, kind, emphasized));
            }

            return spans;
        }

        private static IList<TokenStyleSpan> ToJsonSpans(IList<JsonSyntaxToken> tokens)
        {
            var spans = new List<TokenStyleSpan>();
            foreach (JsonSyntaxToken token in tokens)
            {
                TokenStyleKind kind;
                switch (token.Kind)
                {
                    case JsonSyntaxTokenKind.PropertyName: kind = TokenStyleKind.PropertyName; break;
                    case JsonSyntaxTokenKind.String: kind = TokenStyleKind.String; break;
                    case JsonSyntaxTokenKind.Number: kind = TokenStyleKind.Number; break;
                    case JsonSyntaxTokenKind.Boolean:
                    case JsonSyntaxTokenKind.Null: kind = TokenStyleKind.Literal; break;
                    case JsonSyntaxTokenKind.Punctuation: kind = TokenStyleKind.Punctuation; break;
                    case JsonSyntaxTokenKind.Invalid: kind = TokenStyleKind.Invalid; break;
                    default: continue;
                }

                spans.Add(new TokenStyleSpan(token.Start, token.Length, kind, false));
            }

            return spans;
        }

        private static IList<TokenStyleSpan> ReadJsonPath(string text)
        {
            var spans = new List<TokenStyleSpan>();
            for (int index = 0; index < text.Length;)
            {
                char current = text[index];
                if (char.IsWhiteSpace(current)) { index++; continue; }

                if (current == '\'' || current == '"')
                {
                    int end = ReadQuoted(text, index, current, false);
                    spans.Add(new TokenStyleSpan(index, end - index, TokenStyleKind.String, false));
                    index = end;
                    continue;
                }

                if (current == '$' || current == '@')
                {
                    spans.Add(new TokenStyleSpan(index, 1, TokenStyleKind.Parameter, false));
                    index++;
                    continue;
                }

                if (IsJsonPathPunctuation(current))
                {
                    int start = index++;
                    while (index < text.Length && IsJsonPathPunctuation(text[index])) index++;
                    spans.Add(new TokenStyleSpan(start, index - start, TokenStyleKind.Punctuation, false));
                    continue;
                }

                if (char.IsDigit(current) || (current == '-' && index + 1 < text.Length && char.IsDigit(text[index + 1])))
                {
                    int start = index++;
                    while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.' || text[index] == 'e' || text[index] == 'E' || text[index] == '+' || text[index] == '-')) index++;
                    spans.Add(new TokenStyleSpan(start, index - start, TokenStyleKind.Number, false));
                    continue;
                }

                if (IsWordStart(current))
                {
                    int start = index++;
                    while (index < text.Length && IsWordPart(text[index])) index++;
                    string word = text.Substring(start, index - start);
                    TokenStyleKind wordKind = word == "true" || word == "false" || word == "null"
                        ? TokenStyleKind.Literal
                        : IsNextNonWhitespace(text, index, '(') ? TokenStyleKind.Function : TokenStyleKind.PropertyName;
                    spans.Add(new TokenStyleSpan(start, index - start, wordKind, wordKind == TokenStyleKind.Function));
                    continue;
                }

                spans.Add(new TokenStyleSpan(index, 1, TokenStyleKind.Invalid, false));
                index++;
            }

            return spans;
        }

        private static IList<TokenStyleSpan> ReadXml(string text)
        {
            var spans = new List<TokenStyleSpan>();
            int index = 0;
            while (index < text.Length)
            {
                int tagStart = text.IndexOf('<', index);
                if (tagStart < 0) break;

                if (StartsWith(text, tagStart, "<!--"))
                {
                    int end = text.IndexOf("-->", tagStart + 4, StringComparison.Ordinal);
                    end = end < 0 ? text.Length : end + 3;
                    spans.Add(new TokenStyleSpan(tagStart, end - tagStart, TokenStyleKind.Comment, false));
                    index = end;
                    continue;
                }

                int cursor = tagStart;
                spans.Add(new TokenStyleSpan(cursor, 1, TokenStyleKind.Punctuation, false));
                cursor++;
                if (cursor < text.Length && (text[cursor] == '/' || text[cursor] == '?' || text[cursor] == '!'))
                {
                    spans.Add(new TokenStyleSpan(cursor, 1, TokenStyleKind.Punctuation, false));
                    cursor++;
                }

                cursor = ReadXmlName(text, cursor, spans, TokenStyleKind.Keyword);
                while (cursor < text.Length && text[cursor] != '>')
                {
                    if (char.IsWhiteSpace(text[cursor]) || text[cursor] == '/') { cursor++; continue; }
                    if (text[cursor] == '=')
                    {
                        spans.Add(new TokenStyleSpan(cursor, 1, TokenStyleKind.Punctuation, false));
                        cursor++;
                        continue;
                    }

                    if (text[cursor] == '\'' || text[cursor] == '"')
                    {
                        int end = ReadQuoted(text, cursor, text[cursor], false);
                        spans.Add(new TokenStyleSpan(cursor, end - cursor, TokenStyleKind.String, false));
                        cursor = end;
                        continue;
                    }

                    cursor = ReadXmlName(text, cursor, spans, TokenStyleKind.PropertyName);
                }

                if (cursor < text.Length)
                {
                    spans.Add(new TokenStyleSpan(cursor, 1, TokenStyleKind.Punctuation, false));
                    cursor++;
                }

                index = cursor;
            }

            return spans;
        }

        private static IList<TokenStyleSpan> ReadStructuredLiteral(string text)
        {
            var spans = new List<TokenStyleSpan>();
            for (int index = 0; index < text.Length;)
            {
                char current = text[index];
                if (char.IsWhiteSpace(current)) { index++; continue; }
                if (current == '\'' || current == '"')
                {
                    int end = ReadQuoted(text, index, current, true);
                    spans.Add(new TokenStyleSpan(index, end - index, TokenStyleKind.String, false));
                    index = end;
                    continue;
                }
                if (current == '{' || current == '}' || current == '[' || current == ']' || current == '(' || current == ')' || current == ',' || current == ':')
                {
                    spans.Add(new TokenStyleSpan(index, 1, TokenStyleKind.Punctuation, false));
                    index++;
                    continue;
                }
                if (char.IsDigit(current) || (current == '-' && index + 1 < text.Length && char.IsDigit(text[index + 1])))
                {
                    int start = index++;
                    while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.' || text[index] == 'e' || text[index] == 'E' || text[index] == '+' || text[index] == '-')) index++;
                    spans.Add(new TokenStyleSpan(start, index - start, TokenStyleKind.Number, false));
                    continue;
                }
                if (IsWordStart(current))
                {
                    int start = index++;
                    while (index < text.Length && IsWordPart(text[index])) index++;
                    string word = text.Substring(start, index - start);
                    if (word.Equals("null", StringComparison.OrdinalIgnoreCase) || word.Equals("true", StringComparison.OrdinalIgnoreCase) || word.Equals("false", StringComparison.OrdinalIgnoreCase))
                        spans.Add(new TokenStyleSpan(start, index - start, TokenStyleKind.Literal, false));
                    continue;
                }
                index++;
            }

            return spans;
        }

        private static int ReadXmlName(string text, int index, IList<TokenStyleSpan> spans, TokenStyleKind kind)
        {
            int start = index;
            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_' || text[index] == '-' || text[index] == ':' || text[index] == '.')) index++;
            if (index > start) spans.Add(new TokenStyleSpan(start, index - start, kind, kind == TokenStyleKind.Keyword));
            return index > start ? index : Math.Min(text.Length, index + 1);
        }

        private static int ReadQuoted(string text, int start, char quote, bool doubledQuoteEscapes)
        {
            int index = start + 1;
            while (index < text.Length)
            {
                if (text[index] == '\\' && index + 1 < text.Length) { index += 2; continue; }
                if (text[index] == quote)
                {
                    if (doubledQuoteEscapes && index + 1 < text.Length && text[index + 1] == quote) { index += 2; continue; }
                    return index + 1;
                }
                index++;
            }
            return text.Length;
        }

        private static bool IsJsonPathPunctuation(char value)
        {
            return value == '.' || value == '[' || value == ']' || value == '(' || value == ')' || value == ',' || value == '?' ||
                   value == ':' || value == '*' || value == '+' || value == '-' || value == '/' || value == '%' || value == '=' ||
                   value == '!' || value == '<' || value == '>' || value == '&' || value == '|';
        }

        private static bool IsWordStart(char value)
        {
            return value == '_' || char.IsLetter(value);
        }

        private static bool IsWordPart(char value)
        {
            return value == '_' || char.IsLetterOrDigit(value);
        }

        private static bool IsNextNonWhitespace(string text, int index, char expected)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            return index < text.Length && text[index] == expected;
        }

        private static bool StartsWith(string text, int offset, string value)
        {
            return offset >= 0 && offset + value.Length <= text.Length &&
                   string.Compare(text, offset, value, 0, value.Length, StringComparison.Ordinal) == 0;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }
    }
}
