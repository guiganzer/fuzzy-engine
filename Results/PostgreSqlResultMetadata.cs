using Npgsql;
using Npgsql.PostgresTypes;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;

namespace PostgresCommandExecuter.Results
{
    /// <summary>Preserva a identidade PostgreSQL por ordinal antes de DataTable.Load.</summary>
    internal sealed class PostgreSqlResultMetadata
    {
        internal const string TypeNameProperty = "PostgreSql.TypeName";
        internal const string TypeDisplayNameProperty = "PostgreSql.TypeDisplayName";
        internal const string TypeOidProperty = "PostgreSql.TypeOid";
        internal const string SemanticTypeNameProperty = "PostgreSql.SemanticTypeName";
        internal const string StructureProperty = "PostgreSql.Structure";
        internal const string CategoryProperty = "PostgreSql.Category";

        private readonly IList<ColumnMetadata> columns;

        private PostgreSqlResultMetadata(IList<ColumnMetadata> columns) { this.columns = columns; }

        public static PostgreSqlResultMetadata Capture(NpgsqlDataReader reader)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            DataTable schema = TryGetSchemaTable(reader);
            var columns = new List<ColumnMetadata>(Math.Max(0, reader.FieldCount));
            for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                PostgresType type = TryGetPostgresType(reader, ordinal);
                uint oid = type == null ? TryGetOid(reader, ordinal) : type.OID;
                string typeName = TryGetTypeName(reader, ordinal);
                string displayName = type == null ? typeName : type.DisplayName;
                DataRow schemaRow = schema != null && ordinal < schema.Rows.Count ? schema.Rows[ordinal] : null;
                columns.Add(new ColumnMetadata(ordinal, typeName, ApplyFacets(displayName, typeName, schemaRow), oid,
                    GetSemanticTypeName(type, typeName), GetStructure(type), string.Empty));
            }
            return new PostgreSqlResultMetadata(columns);
        }

        public void ApplyTo(DataTable table)
        {
            if (table == null) throw new ArgumentNullException("table");
            int count = Math.Min(table.Columns.Count, columns.Count);
            for (int ordinal = 0; ordinal < count; ordinal++)
            {
                ColumnMetadata metadata = columns[ordinal];
                DataColumn column = table.Columns[ordinal];
                column.ExtendedProperties[TypeNameProperty] = metadata.TypeName;
                column.ExtendedProperties[TypeDisplayNameProperty] = metadata.DisplayName;
                column.ExtendedProperties[TypeOidProperty] = metadata.Oid;
                column.ExtendedProperties[SemanticTypeNameProperty] = metadata.SemanticTypeName;
                column.ExtendedProperties[StructureProperty] = metadata.Structure;
                column.ExtendedProperties[CategoryProperty] = metadata.Category;
            }
        }

        public void Enrich(PostgreSqlTypeCatalog catalog)
        {
            if (catalog == null) return;
            foreach (ColumnMetadata metadata in columns)
            {
                PostgreSqlTypeDescriptor descriptor;
                if (!catalog.TryGet(metadata.Oid, out descriptor)) continue;
                metadata.TypeName = descriptor.QualifiedName;
                if (metadata.DisplayName.IndexOf('(') < 0)
                    metadata.DisplayName = descriptor.DisplayName;
                metadata.Category = descriptor.Category;
                metadata.Structure = descriptor.Structure;
                metadata.SemanticTypeName = catalog.ResolveBaseTypeName(descriptor);
            }
        }

        public static string GetDisplayName(DataColumn column)
        {
            if (column == null) return "desconhecido";
            string value = column.ExtendedProperties[TypeDisplayNameProperty] as string;
            return string.IsNullOrWhiteSpace(value) ? "desconhecido" : value;
        }

        public static string GetTypeName(DataColumn column) { return column == null ? string.Empty : column.ExtendedProperties[TypeNameProperty] as string ?? string.Empty; }
        public static string GetSemanticTypeName(DataColumn column) { return column == null ? string.Empty : column.ExtendedProperties[SemanticTypeNameProperty] as string ?? GetTypeName(column); }
        public static string GetStructure(DataColumn column) { return column == null ? string.Empty : column.ExtendedProperties[StructureProperty] as string ?? string.Empty; }

        private static DataTable TryGetSchemaTable(NpgsqlDataReader reader) { try { return reader.GetSchemaTable(); } catch { return null; } }
        private static PostgresType TryGetPostgresType(NpgsqlDataReader reader, int ordinal) { try { return reader.GetPostgresType(ordinal); } catch { return null; } }
        private static uint TryGetOid(NpgsqlDataReader reader, int ordinal) { try { return reader.GetDataTypeOID(ordinal); } catch { return 0; } }
        private static string TryGetTypeName(NpgsqlDataReader reader, int ordinal) { try { return reader.GetDataTypeName(ordinal); } catch { return string.Empty; } }

        private static string GetSemanticTypeName(PostgresType type, string fallback)
        {
            var domain = type as PostgresDomainType;
            while (domain != null && domain.BaseType != null)
            {
                type = domain.BaseType;
                domain = type as PostgresDomainType;
            }
            return type == null ? fallback : type.Name;
        }

        private static string GetStructure(PostgresType type)
        {
            if (type is PostgresArrayType) return "array";
            if (type is PostgresRangeType) return "range";
            if (type is PostgresDomainType) return "domain";
            if (type is PostgresEnumType) return "enum";
            if (type is PostgresCompositeType) return "composite";
            return string.Empty;
        }

        private static string ApplyFacets(string displayName, string typeName, DataRow schemaRow)
        {
            string display = string.IsNullOrWhiteSpace(displayName) ? typeName : displayName;
            if (schemaRow == null || string.IsNullOrWhiteSpace(display)) return display;
            string normalized = display.ToLowerInvariant();
            int precision = GetSchemaInteger(schemaRow, "NumericPrecision");
            int scale = GetSchemaInteger(schemaRow, "NumericScale");
            int size = GetSchemaInteger(schemaRow, "ColumnSize");
            if ((normalized == "numeric" || normalized == "decimal") && precision > 0)
                return display + "(" + precision.ToString(CultureInfo.InvariantCulture) + "," + Math.Max(scale, 0).ToString(CultureInfo.InvariantCulture) + ")";
            if ((normalized == "varchar" || normalized == "character varying" || normalized == "char" || normalized == "character" || normalized == "bit" || normalized == "varbit" || normalized == "bit varying") && size > 0)
                return display + "(" + size.ToString(CultureInfo.InvariantCulture) + ")";
            return display;
        }

        private static int GetSchemaInteger(DataRow row, string name)
        {
            if (row.Table == null || !row.Table.Columns.Contains(name) || row.IsNull(name)) return 0;
            try { return Convert.ToInt32(row[name], CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private sealed class ColumnMetadata
        {
            internal readonly int Ordinal;
            internal string TypeName;
            internal string DisplayName;
            internal readonly uint Oid;
            internal string SemanticTypeName;
            internal string Structure;
            internal string Category;

            internal ColumnMetadata(int ordinal, string typeName, string displayName, uint oid, string semanticTypeName, string structure, string category)
            {
                Ordinal = ordinal;
                TypeName = typeName ?? string.Empty;
                DisplayName = displayName ?? TypeName;
                Oid = oid;
                SemanticTypeName = semanticTypeName ?? TypeName;
                Structure = structure ?? string.Empty;
                Category = category ?? string.Empty;
            }
        }
    }
}
