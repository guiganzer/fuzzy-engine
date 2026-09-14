using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PostgresCommandExecuter.Results
{
    /// <summary>
    /// Semantic categories used by the result grid.  The rendering layer maps these
    /// stable categories to theme brushes; this class deliberately contains no WPF
    /// dependency, so type recognition remains testable and inexpensive.
    /// </summary>
    internal enum PostgreSqlValueSemantic
    {
        Null,
        BooleanTrue,
        BooleanFalse,
        Integer,
        PositiveNumber,
        NegativeNumber,
        ZeroNumber,
        NonFiniteNumber,
        Monetary,
        Text,
        Uuid,
        Date,
        Time,
        Timestamp,
        Interval,
        Binary,
        Json,
        Xml,
        Network,
        MacAddress,
        BitString,
        Geometric,
        FullText,
        Array,
        Range,
        Multirange,
        Enum,
        Composite,
        Custom,
        Unknown
    }

    /// <summary>
    /// Classifies a PostgreSQL result value from the backend type name, never from
    /// its CLR type.  That distinction is essential: json/jsonb, varchar/text,
    /// cidr/inet and user domains can all arrive as the same .NET type.
    /// </summary>
    internal static class PostgreSqlValueSemantics
    {
        private static readonly Regex NumericText = new Regex(
            @"^[+-]?(?:\d+(?:[\.,]\d*)?|[\.,]\d+)(?:[eE][+-]?\d+)?$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static PostgreSqlValueSemantic Classify(string backendTypeName, object value, string structure)
        {
            if (value == null || value == DBNull.Value)
                return PostgreSqlValueSemantic.Null;

            switch (structure)
            {
                case "array": return PostgreSqlValueSemantic.Array;
                case "range": return PostgreSqlValueSemantic.Range;
                case "multirange": return PostgreSqlValueSemantic.Multirange;
                case "enum": return PostgreSqlValueSemantic.Enum;
                case "composite": return PostgreSqlValueSemantic.Composite;
            }

            string type = Normalize(backendTypeName);
            if (type.Length == 0)
                return PostgreSqlValueSemantic.Unknown;

            if (type.EndsWith("[]", StringComparison.Ordinal))
                return PostgreSqlValueSemantic.Array;
            // Ranges personalizados são reconhecidos pelo typtype do catálogo.
            // Não use sufixo genérico: um tipo como "orange" não é um range.
            if (type == "int4range" || type == "int8range" || type == "numrange" || type == "daterange" ||
                type == "tsrange" || type == "tstzrange") return PostgreSqlValueSemantic.Range;
            if (type == "int4multirange" || type == "int8multirange" || type == "nummultirange" ||
                type == "datemultirange" || type == "tsmultirange" || type == "tstzmultirange") return PostgreSqlValueSemantic.Multirange;

            switch (type)
            {
                case "bool":
                case "boolean":
                    return IsTrue(value) ? PostgreSqlValueSemantic.BooleanTrue : PostgreSqlValueSemantic.BooleanFalse;

                case "int2":
                case "smallint":
                case "int4":
                case "integer":
                case "int":
                case "int8":
                case "bigint":
                case "oid":
                case "xid":
                case "xid8":
                case "cid":
                case "oidvector":
                case "regproc":
                case "regprocedure":
                case "regoper":
                case "regoperator":
                case "regclass":
                case "regcollation":
                case "regtype":
                case "regrole":
                case "regnamespace":
                case "regconfig":
                case "regdictionary":
                    return PostgreSqlValueSemantic.Integer;

                case "pg_lsn":
                case "pg_snapshot":
                case "txid_snapshot":
                    return PostgreSqlValueSemantic.Custom;

                case "float4":
                case "real":
                case "float8":
                case "double precision":
                case "numeric":
                case "decimal":
                    return ClassifyNumber(value);

                case "money":
                    return PostgreSqlValueSemantic.Monetary;

                case "uuid":
                    return PostgreSqlValueSemantic.Uuid;

                case "date":
                    return PostgreSqlValueSemantic.Date;

                case "time":
                case "time without time zone":
                case "time with time zone":
                case "timetz":
                    return PostgreSqlValueSemantic.Time;

                case "timestamp":
                case "timestamp without time zone":
                case "timestamp with time zone":
                case "timestamptz":
                    return PostgreSqlValueSemantic.Timestamp;

                case "interval":
                    return PostgreSqlValueSemantic.Interval;

                case "bytea":
                    return PostgreSqlValueSemantic.Binary;

                case "json":
                case "jsonb":
                case "jsonpath":
                    return PostgreSqlValueSemantic.Json;

                case "xml":
                    return PostgreSqlValueSemantic.Xml;

                case "inet":
                case "cidr":
                    return PostgreSqlValueSemantic.Network;

                case "macaddr":
                case "macaddr8":
                    return PostgreSqlValueSemantic.MacAddress;

                case "bit":
                case "bit varying":
                case "varbit":
                    return PostgreSqlValueSemantic.BitString;

                case "point":
                case "line":
                case "lseg":
                case "box":
                case "path":
                case "polygon":
                case "circle":
                case "geometry":
                case "geography":
                case "cube":
                    return PostgreSqlValueSemantic.Geometric;

                case "tsvector":
                case "tsquery":
                    return PostgreSqlValueSemantic.FullText;

                case "record":
                    return PostgreSqlValueSemantic.Composite;

                case "char":
                case "character":
                case "character varying":
                case "varchar":
                case "text":
                case "name":
                case "citext":
                case "bpchar":
                    return PostgreSqlValueSemantic.Text;

                case "hstore":
                case "ltree":
                case "lquery":
                case "ltxtquery":
                    return PostgreSqlValueSemantic.Custom;
            }

            // PostgreSQL extension and application types are intentionally not
            // guessed as text.  Keeping them distinct allows a later palette rule
            // for enums/domains/composites without reworking the grid plumbing.
            if (type.IndexOf(".", StringComparison.Ordinal) >= 0)
                return PostgreSqlValueSemantic.Custom;

            return PostgreSqlValueSemantic.Custom;
        }

        public static PostgreSqlValueSemantic Classify(string backendTypeName, object value)
        {
            return Classify(backendTypeName, value, string.Empty);
        }

        private static PostgreSqlValueSemantic ClassifyNumber(object value)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (text.Equals("NaN", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("Infinity", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("-Infinity", StringComparison.OrdinalIgnoreCase))
                return PostgreSqlValueSemantic.NonFiniteNumber;

            decimal decimalValue;
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimalValue))
            {
                if (decimalValue == 0) return PostgreSqlValueSemantic.ZeroNumber;
                return decimalValue < 0 ? PostgreSqlValueSemantic.NegativeNumber : PostgreSqlValueSemantic.PositiveNumber;
            }

            return NumericText.IsMatch(text)
                ? PostgreSqlValueSemantic.PositiveNumber
                : PostgreSqlValueSemantic.Unknown;
        }

        private static bool IsTrue(object value)
        {
            bool boolValue;
            return value is bool && (bool)value ||
                   bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out boolValue) && boolValue;
        }

        private static string Normalize(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return string.Empty;

            string type = typeName.Trim().ToLowerInvariant();
            int schemaSeparator = type.LastIndexOf('.');
            if (schemaSeparator >= 0 && schemaSeparator < type.Length - 1)
                type = type.Substring(schemaSeparator + 1).Trim('"');
            int facetIndex = type.IndexOf('(');
            if (facetIndex >= 0)
                type = type.Substring(0, facetIndex).TrimEnd();
            return type;
        }

    }
}
