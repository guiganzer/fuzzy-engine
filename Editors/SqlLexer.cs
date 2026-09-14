using System;
using System.Collections.Generic;
using System.Globalization;

namespace PostgresCommandExecuter.Editors
{
    /// <summary>
    /// A lexical token in PostgreSQL-compatible SQL. Offsets and lengths are based on
    /// the original .NET string, so they can be used directly by an editor renderer.
    /// </summary>
    public sealed class SqlToken
    {
        public SqlToken(int offset, int length, SqlTokenKind kind)
        {
            Offset = offset;
            Length = length;
            Kind = kind;
        }

        public int Offset { get; private set; }

        public int Length { get; private set; }

        public int EndOffset
        {
            get { return Offset + Length; }
        }

        public SqlTokenKind Kind { get; private set; }
    }

    public enum SqlTokenKind
    {
        Comment,
        String,
        QuotedIdentifier,
        Identifier,
        Keyword,
        DataType,
        Function,
        Number,
        Parameter,
        Operator,
        Punctuation,
        Unknown
    }

    public enum SqlDiagnosticKind
    {
        UnterminatedBlockComment,
        UnterminatedString,
        UnterminatedQuotedIdentifier,
        UnterminatedDollarQuotedString
    }

    /// <summary>
    /// Non-fatal diagnostic emitted for incomplete input. The lexer always returns
    /// the tokens that it could recognize, which keeps live editor highlighting safe.
    /// </summary>
    public sealed class SqlDiagnostic
    {
        public SqlDiagnostic(SqlDiagnosticKind kind, int offset, int length, string message)
        {
            Kind = kind;
            Offset = offset;
            Length = length;
            Message = message;
        }

        public SqlDiagnosticKind Kind { get; private set; }

        public int Offset { get; private set; }

        public int Length { get; private set; }

        public string Message { get; private set; }
    }

    public sealed class SqlLexResult
    {
        internal SqlLexResult(IList<SqlToken> tokens, IList<SqlDiagnostic> diagnostics)
        {
            Tokens = tokens;
            Diagnostics = diagnostics;
        }

        public IList<SqlToken> Tokens { get; private set; }

        public IList<SqlDiagnostic> Diagnostics { get; private set; }
    }

    /// <summary>
    /// Lightweight, non-throwing PostgreSQL SQL lexer for presentation and local
    /// diagnostics. It deliberately does not claim to be a full SQL parser: server
    /// validation remains PostgreSQL's responsibility. The scanner is single-pass
    /// apart from searching the matching delimiter of a dollar-quoted string.
    /// </summary>
    public static class SqlLexer
    {
        private static readonly HashSet<string> Keywords = CreateSet(new[]
        {
            "ALL", "ANALYZE", "AND", "ANY", "ARRAY", "AS", "ASC", "ASYMMETRIC", "AUTHORIZATION",
            "BEGIN", "BETWEEN", "BOTH", "BY", "CASE", "CAST", "CHECK", "COLLATE", "COLUMN", "COMMIT",
            "CONCURRENTLY", "CONFLICT", "CONSTRAINT", "CREATE", "CROSS", "CURRENT_CATALOG", "CURRENT_DATE",
            "CURRENT_ROLE", "CURRENT_SCHEMA", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "DEFAULT",
            "DEFERRABLE", "DELETE", "DESC", "DISTINCT", "DO", "DROP", "ELSE", "END", "EXCEPT", "EXCLUDE",
            "EXISTS", "EXPLAIN", "FALSE", "FETCH", "FOR", "FOREIGN", "FROM", "FULL", "GRANT", "GROUP",
            "HAVING", "ILIKE", "IN", "INDEX", "INNER", "INSERT", "INTERSECT", "INTO", "IS", "JOIN", "KEY",
            "LATERAL", "LEFT", "LIKE", "LIMIT", "LOCK", "MATERIALIZED", "NATURAL", "NOT", "NOTHING", "NULL",
            "NULLS", "OFFSET", "ON", "ONLY", "OR", "ORDER", "OUTER", "OVER", "PARTITION", "PRIMARY", "REFERENCES",
            "RETURNING", "REVOKE", "RIGHT", "ROLLBACK", "ROW", "ROWS", "SELECT", "SET", "SHOW", "SIMILAR",
            "SOME", "SYMMETRIC", "TABLE", "TABLESAMPLE", "TEMP", "TEMPORARY", "THEN", "TO", "TRIGGER", "TRUE",
            "UNION", "UNIQUE", "UPDATE", "USING", "VACUUM", "VALUES", "VIEW", "WHEN", "WHERE", "WINDOW", "WITH"
        });

        private static readonly HashSet<string> DataTypes = CreateSet(new[]
        {
            "BIGINT", "BIGSERIAL", "BIT", "BOOLEAN", "BOX", "BYTEA", "CHAR", "CHARACTER", "CIDR", "CIRCLE",
            "DATE", "DEC", "DECIMAL", "DOUBLE", "FLOAT", "INET", "INT", "INT2", "INT4", "INT8", "INTEGER",
            "INTERVAL", "JSON", "JSONB", "LINE", "LSEG", "MACADDR", "MACADDR8", "MONEY", "NUMERIC", "PATH",
            "PG_LSN", "POINT", "POLYGON", "REAL", "RECORD", "REGCLASS", "SERIAL", "SERIAL2", "SERIAL4", "SERIAL8",
            "SMALLINT", "SMALLSERIAL", "TEXT", "TIME", "TIMESTAMP", "TIMESTAMPTZ", "TIMETZ", "UUID", "VARBIT",
            "VARCHAR", "XML"
        });

        private static readonly HashSet<string> Functions = CreateSet(new[]
        {
            "ARRAY_AGG", "AVG", "COALESCE", "CONCAT", "COUNT", "CURRENT_DATABASE", "CURRENT_SETTING", "DATE_PART",
            "DATE_TRUNC", "EXTRACT", "GENERATE_SERIES", "GREATEST", "JSONB_AGG", "JSONB_BUILD_OBJECT", "JSON_AGG",
            "JSON_BUILD_OBJECT", "LEAST", "LENGTH", "LOWER", "MAX", "MD5", "MIN", "NOW", "NULLIF", "PG_BACKEND_PID",
            "PG_SLEEP", "RANDOM", "ROUND", "ROW_NUMBER", "STRING_AGG", "SUM", "TO_CHAR", "TO_DATE", "TO_JSON",
            "TO_JSONB", "TRIM", "UPPER", "VERSION"
        });

        /// <summary>
        /// Tokenizes SQL without using regular expressions or throwing for partial text.
        /// A null input is treated as an empty document.
        /// </summary>
        public static SqlLexResult Tokenize(string sql)
        {
            sql = sql ?? string.Empty;

            var tokens = new List<SqlToken>();
            var diagnostics = new List<SqlDiagnostic>();
            var index = 0;

            while (index < sql.Length)
            {
                var current = sql[index];

                if (char.IsWhiteSpace(current))
                {
                    index++;
                    continue;
                }

                if (current == '-' && HasAt(sql, index + 1, '-'))
                {
                    index = ReadLineComment(sql, index, tokens);
                    continue;
                }

                if (current == '/' && HasAt(sql, index + 1, '*'))
                {
                    index = ReadBlockComment(sql, index, tokens, diagnostics);
                    continue;
                }

                var prefixLength = GetStringPrefixLength(sql, index);
                if (prefixLength > 0)
                {
                    index = ReadSingleQuotedString(sql, index, prefixLength, tokens, diagnostics);
                    continue;
                }

                if (current == '\'')
                {
                    index = ReadSingleQuotedString(sql, index, 0, tokens, diagnostics);
                    continue;
                }

                if (current == '"')
                {
                    index = ReadQuotedIdentifier(sql, index, tokens, diagnostics);
                    continue;
                }

                if (current == '$')
                {
                    var nextIndex = ReadDollarConstruct(sql, index, tokens, diagnostics);
                    if (nextIndex > index)
                    {
                        index = nextIndex;
                        continue;
                    }
                }

                if (char.IsDigit(current) || (current == '.' && HasAtDigit(sql, index + 1)))
                {
                    index = ReadNumber(sql, index, tokens);
                    continue;
                }

                if (IsIdentifierStart(current))
                {
                    index = ReadIdentifier(sql, index, tokens);
                    continue;
                }

                if (IsOperatorCharacter(current))
                {
                    index = ReadOperator(sql, index, tokens);
                    continue;
                }

                if (IsPunctuation(current))
                {
                    tokens.Add(new SqlToken(index, 1, SqlTokenKind.Punctuation));
                    index++;
                    continue;
                }

                tokens.Add(new SqlToken(index, 1, SqlTokenKind.Unknown));
                index++;
            }

            return new SqlLexResult(tokens, diagnostics);
        }

        private static int ReadLineComment(string sql, int start, IList<SqlToken> tokens)
        {
            var index = start + 2;
            while (index < sql.Length && sql[index] != '\r' && sql[index] != '\n')
            {
                index++;
            }

            tokens.Add(new SqlToken(start, index - start, SqlTokenKind.Comment));
            return index;
        }

        private static int ReadBlockComment(string sql, int start, IList<SqlToken> tokens, IList<SqlDiagnostic> diagnostics)
        {
            var index = start + 2;
            var depth = 1;

            while (index < sql.Length)
            {
                if (sql[index] == '/' && HasAt(sql, index + 1, '*'))
                {
                    depth++;
                    index += 2;
                    continue;
                }

                if (sql[index] == '*' && HasAt(sql, index + 1, '/'))
                {
                    depth--;
                    index += 2;
                    if (depth == 0)
                    {
                        tokens.Add(new SqlToken(start, index - start, SqlTokenKind.Comment));
                        return index;
                    }

                    continue;
                }

                index++;
            }

            tokens.Add(new SqlToken(start, sql.Length - start, SqlTokenKind.Comment));
            diagnostics.Add(new SqlDiagnostic(
                SqlDiagnosticKind.UnterminatedBlockComment,
                start,
                sql.Length - start,
                "Comentário de bloco não foi fechado."));
            return sql.Length;
        }

        private static int GetStringPrefixLength(string sql, int start)
        {
            if (start + 1 < sql.Length && sql[start + 1] == '\'' && IsEscapeOrBitStringPrefix(sql[start]))
            {
                return 1;
            }

            if (start + 2 < sql.Length && (sql[start] == 'u' || sql[start] == 'U') && sql[start + 1] == '&' && sql[start + 2] == '\'')
            {
                return 2;
            }

            return 0;
        }

        private static bool IsEscapeOrBitStringPrefix(char value)
        {
            return value == 'e' || value == 'E' || value == 'b' || value == 'B' || value == 'x' || value == 'X';
        }

        private static int ReadSingleQuotedString(string sql, int start, int prefixLength, IList<SqlToken> tokens, IList<SqlDiagnostic> diagnostics)
        {
            var openingQuote = start + prefixLength;
            var isEscapeString = prefixLength == 1 && (sql[start] == 'e' || sql[start] == 'E');
            var index = openingQuote + 1;

            while (index < sql.Length)
            {
                if (sql[index] == '\'')
                {
                    if (HasAt(sql, index + 1, '\''))
                    {
                        index += 2;
                        continue;
                    }

                    index++;
                    tokens.Add(new SqlToken(start, index - start, SqlTokenKind.String));
                    return index;
                }

                if (isEscapeString && sql[index] == '\\' && index + 1 < sql.Length)
                {
                    index += 2;
                    continue;
                }

                index++;
            }

            tokens.Add(new SqlToken(start, sql.Length - start, SqlTokenKind.String));
            diagnostics.Add(new SqlDiagnostic(
                SqlDiagnosticKind.UnterminatedString,
                start,
                sql.Length - start,
                "Literal de texto não foi fechado."));
            return sql.Length;
        }

        private static int ReadQuotedIdentifier(string sql, int start, IList<SqlToken> tokens, IList<SqlDiagnostic> diagnostics)
        {
            var index = start + 1;
            while (index < sql.Length)
            {
                if (sql[index] == '"')
                {
                    if (HasAt(sql, index + 1, '"'))
                    {
                        index += 2;
                        continue;
                    }

                    index++;
                    tokens.Add(new SqlToken(start, index - start, SqlTokenKind.QuotedIdentifier));
                    return index;
                }

                index++;
            }

            tokens.Add(new SqlToken(start, sql.Length - start, SqlTokenKind.QuotedIdentifier));
            diagnostics.Add(new SqlDiagnostic(
                SqlDiagnosticKind.UnterminatedQuotedIdentifier,
                start,
                sql.Length - start,
                "Identificador entre aspas não foi fechado."));
            return sql.Length;
        }

        private static int ReadDollarConstruct(string sql, int start, IList<SqlToken> tokens, IList<SqlDiagnostic> diagnostics)
        {
            if (HasAtDigit(sql, start + 1))
            {
                var parameterEnd = start + 1;
                while (HasAtDigit(sql, parameterEnd))
                {
                    parameterEnd++;
                }

                tokens.Add(new SqlToken(start, parameterEnd - start, SqlTokenKind.Parameter));
                return parameterEnd;
            }

            var delimiterLength = GetDollarQuoteDelimiterLength(sql, start);
            if (delimiterLength == 0)
            {
                return start;
            }

            var delimiter = sql.Substring(start, delimiterLength);
            var contentStart = start + delimiterLength;
            var closingStart = sql.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
            if (closingStart >= 0)
            {
                var end = closingStart + delimiterLength;
                tokens.Add(new SqlToken(start, end - start, SqlTokenKind.String));
                return end;
            }

            tokens.Add(new SqlToken(start, sql.Length - start, SqlTokenKind.String));
            diagnostics.Add(new SqlDiagnostic(
                SqlDiagnosticKind.UnterminatedDollarQuotedString,
                start,
                sql.Length - start,
                "Literal delimitado por dólar não foi fechado."));
            return sql.Length;
        }

        private static int GetDollarQuoteDelimiterLength(string sql, int start)
        {
            var index = start + 1;
            if (index >= sql.Length)
            {
                return 0;
            }

            if (sql[index] == '$')
            {
                return 2;
            }

            if (!IsIdentifierStart(sql[index]))
            {
                return 0;
            }

            index++;
            while (index < sql.Length && IsIdentifierPart(sql[index]))
            {
                index++;
            }

            return HasAt(sql, index, '$') ? index - start + 1 : 0;
        }

        private static int ReadNumber(string sql, int start, IList<SqlToken> tokens)
        {
            var index = start;
            var sawDecimalPoint = false;

            if (sql[index] == '.')
            {
                sawDecimalPoint = true;
                index++;
            }

            while (index < sql.Length && (char.IsDigit(sql[index]) || sql[index] == '_'))
            {
                index++;
            }

            if (!sawDecimalPoint && HasAt(sql, index, '.') && !HasAt(sql, index + 1, '.'))
            {
                sawDecimalPoint = true;
                index++;
                while (index < sql.Length && (char.IsDigit(sql[index]) || sql[index] == '_'))
                {
                    index++;
                }
            }

            if (index < sql.Length && (sql[index] == 'e' || sql[index] == 'E'))
            {
                var exponentStart = index;
                index++;
                if (index < sql.Length && (sql[index] == '+' || sql[index] == '-'))
                {
                    index++;
                }

                var digitsStart = index;
                while (index < sql.Length && (char.IsDigit(sql[index]) || sql[index] == '_'))
                {
                    index++;
                }

                if (digitsStart == index)
                {
                    index = exponentStart;
                }
            }

            tokens.Add(new SqlToken(start, index - start, SqlTokenKind.Number));
            return index;
        }

        private static int ReadIdentifier(string sql, int start, IList<SqlToken> tokens)
        {
            var index = start + 1;
            while (index < sql.Length && IsIdentifierPart(sql[index]))
            {
                index++;
            }

            var value = sql.Substring(start, index - start);
            var kind = ClassifyIdentifier(value);
            tokens.Add(new SqlToken(start, index - start, kind));
            return index;
        }

        private static SqlTokenKind ClassifyIdentifier(string value)
        {
            if (Keywords.Contains(value))
            {
                return SqlTokenKind.Keyword;
            }

            if (DataTypes.Contains(value))
            {
                return SqlTokenKind.DataType;
            }

            if (Functions.Contains(value))
            {
                return SqlTokenKind.Function;
            }

            return SqlTokenKind.Identifier;
        }

        private static int ReadOperator(string sql, int start, IList<SqlToken> tokens)
        {
            var index = start + 1;
            while (index < sql.Length && IsOperatorCharacter(sql[index]))
            {
                index++;
            }

            tokens.Add(new SqlToken(start, index - start, SqlTokenKind.Operator));
            return index;
        }

        private static bool IsIdentifierStart(char value)
        {
            return value == '_' || char.IsLetter(value) || value >= 128;
        }

        private static bool IsIdentifierPart(char value)
        {
            return value == '$' || value == '_' || char.IsLetterOrDigit(value) || value >= 128;
        }

        private static bool IsOperatorCharacter(char value)
        {
            return value == '+' || value == '-' || value == '*' || value == '/' || value == '<' || value == '>' ||
                   value == '=' || value == '~' || value == '!' || value == '@' || value == '#' || value == '%' ||
                   value == '^' || value == '&' || value == '|' || value == '?' || value == ':';
        }

        private static bool IsPunctuation(char value)
        {
            return value == '(' || value == ')' || value == '[' || value == ']' || value == '{' || value == '}' ||
                   value == ',' || value == ';' || value == '.';
        }

        private static bool HasAt(string value, int index, char expected)
        {
            return index >= 0 && index < value.Length && value[index] == expected;
        }

        private static bool HasAtDigit(string value, int index)
        {
            return index >= 0 && index < value.Length && char.IsDigit(value[index]);
        }

        private static HashSet<string> CreateSet(IEnumerable<string> values)
        {
            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }
    }
}
