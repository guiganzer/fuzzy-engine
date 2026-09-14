using System;
using System.Collections.Generic;
using System.Text;

namespace PostgresCommandExecuter.Editors
{
    public sealed class SqlFormatResult
    {
        public SqlFormatResult(bool success, string text, string message)
        {
            Success = success;
            Text = text ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public bool Success { get; private set; }

        public string Text { get; private set; }

        public string Message { get; private set; }
    }

    /// <summary>
    /// Conservative formatter for the query editor. It uses the PostgreSQL lexer
    /// and only changes whitespace/casing of known SQL words, so literals,
    /// comments, quoted identifiers and dollar-quoted bodies remain untouched.
    /// It is intentionally not a grammar validator; PostgreSQL remains the
    /// authority for complete syntax validation at execution time.
    /// </summary>
    public static class SqlDocumentFormatter
    {
        private static readonly HashSet<string> ClauseStarters = CreateSet(new[]
        {
            "SELECT", "FROM", "WHERE", "HAVING", "GROUP", "ORDER", "LIMIT", "OFFSET", "RETURNING",
            "INSERT", "UPDATE", "DELETE", "VALUES", "SET", "JOIN", "LEFT", "RIGHT", "FULL", "INNER",
            "CROSS", "UNION", "INTERSECT", "EXCEPT", "WITH"
        });

        private static readonly HashSet<string> LogicalOperators = CreateSet(new[] { "AND", "OR" });

        public static SqlFormatResult TryFormat(string sql)
        {
            sql = sql ?? string.Empty;
            SqlLexResult lexical = SqlLexer.Tokenize(sql);
            if (lexical.Diagnostics.Count > 0)
            {
                return new SqlFormatResult(false, sql, lexical.Diagnostics[0].Message + " Corrija antes de formatar.");
            }

            if (lexical.Tokens.Count == 0)
            {
                return new SqlFormatResult(true, string.Empty, "Nenhum conteúdo para formatar.");
            }

            var output = new StringBuilder(sql.Length + 64);
            SqlToken previous = null;
            string previousSource = null;

            foreach (SqlToken token in lexical.Tokens)
            {
                string source = sql.Substring(token.Offset, token.Length);
                if (token.Kind == SqlTokenKind.Comment)
                {
                    if (output.Length > 0 && !EndsWithLineBreak(output)) AppendLineBreak(output);
                    output.Append(source.TrimEnd());
                    AppendLineBreak(output);
                    previous = null;
                    previousSource = null;
                    continue;
                }

                bool beginsClause = token.Kind == SqlTokenKind.Keyword && ClauseStarters.Contains(source);
                bool isLogical = token.Kind == SqlTokenKind.Keyword && LogicalOperators.Contains(source);
                if ((isLogical || (beginsClause && !IsJoinContinuation(previousSource, source))) &&
                    output.Length > 0 && !EndsWithLineBreak(output))
                {
                    AppendLineBreak(output);
                }

                bool needsSpace = NeedsSpace(previous, previousSource, token, source);
                if (needsSpace && output.Length > 0 && !EndsWithLineBreak(output) && output[output.Length - 1] != ' ')
                {
                    output.Append(' ');
                }

                if (token.Kind == SqlTokenKind.Keyword || token.Kind == SqlTokenKind.DataType || token.Kind == SqlTokenKind.Function)
                {
                    output.Append(source.ToUpperInvariant());
                }
                else
                {
                    output.Append(source);
                }

                if (IsPunctuation(source, ',') || IsPunctuation(source, ';'))
                {
                    AppendLineBreak(output);
                    previous = null;
                    previousSource = null;
                    continue;
                }

                previous = token;
                previousSource = source;
            }

            string formatted = output.ToString().Trim();
            return new SqlFormatResult(true, formatted, "SQL formatado localmente.");
        }

        private static bool NeedsSpace(SqlToken previous, string previousSource, SqlToken current, string currentSource)
        {
            if (previous == null) return false;

            // Punctuation is the only syntax where surrounding whitespace is
            // structural. All other token pairs receive one separating space.
            if (current.Kind == SqlTokenKind.Punctuation &&
                (currentSource == "," || currentSource == ";" || currentSource == ")" ||
                 currentSource == "]" || currentSource == "}" || currentSource == ".")) return false;
            if (previous.Kind == SqlTokenKind.Punctuation &&
                (previousSource == "(" || previousSource == "[" || previousSource == "{" || previousSource == ".")) return false;
            if (current.Kind == SqlTokenKind.Punctuation && currentSource == "(" && previous.Kind == SqlTokenKind.Function) return false;
            return true;
        }

        private static bool IsPunctuation(string source, char value)
        {
            return source.Length == 1 && source[0] == value;
        }

        private static bool IsJoinContinuation(string previousSource, string currentSource)
        {
            if (!string.Equals(currentSource, "JOIN", StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(previousSource, "LEFT", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(previousSource, "RIGHT", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(previousSource, "FULL", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(previousSource, "INNER", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(previousSource, "CROSS", StringComparison.OrdinalIgnoreCase);
        }

        private static bool EndsWithLineBreak(StringBuilder builder)
        {
            return builder.Length > 0 && builder[builder.Length - 1] == '\n';
        }

        private static void AppendLineBreak(StringBuilder builder)
        {
            while (builder.Length > 0 && (builder[builder.Length - 1] == ' ' || builder[builder.Length - 1] == '\t'))
            {
                builder.Length--;
            }

            if (!EndsWithLineBreak(builder)) builder.AppendLine();
        }

        private static HashSet<string> CreateSet(IEnumerable<string> values)
        {
            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }
    }
}
