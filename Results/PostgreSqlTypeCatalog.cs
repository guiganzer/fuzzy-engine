using Npgsql;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PostgresCommandExecuter.Results
{
    /// <summary>
    /// Cache curto do catálogo pg_type por banco. Complementa o mapeamento legado
    /// do Npgsql e reconhece domains, enums, composites e tipos de extensões.
    /// </summary>
    internal sealed class PostgreSqlTypeCatalog
    {
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, CacheEntry> Cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private readonly Dictionary<uint, PostgreSqlTypeDescriptor> types;

        private PostgreSqlTypeCatalog(Dictionary<uint, PostgreSqlTypeDescriptor> types) { this.types = types; }

        public static async Task<PostgreSqlTypeCatalog> GetAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            string cacheKey = connection.Host + ":" + connection.Port.ToString(CultureInfo.InvariantCulture) + "/" + connection.Database;
            lock (CacheLock)
            {
                CacheEntry cached;
                if (Cache.TryGetValue(cacheKey, out cached) && DateTime.UtcNow - cached.CreatedUtc < TimeSpan.FromMinutes(3))
                    return cached.Catalog;
            }

            var types = new Dictionary<uint, PostgreSqlTypeDescriptor>();
            const string sql = @"
SELECT t.oid::text, n.nspname, t.typname, t.typtype::text, t.typcategory::text,
       t.typbasetype::text, t.typelem::text, COALESCE(r.rngsubtype, 0)::text,
       format_type(t.oid, NULL)
FROM pg_catalog.pg_type t
JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
LEFT JOIN pg_catalog.pg_range r ON r.rngtypid = t.oid";
            using (var command = new NpgsqlCommand(sql, connection))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    uint oid;
                    if (!UInt32.TryParse(reader.GetString(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out oid)) continue;
                    types[oid] = new PostgreSqlTypeDescriptor(
                        oid, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                        ParseOid(reader.GetString(5)), ParseOid(reader.GetString(6)), ParseOid(reader.GetString(7)), reader.GetString(8));
                }
            }

            var catalog = new PostgreSqlTypeCatalog(types);
            lock (CacheLock) Cache[cacheKey] = new CacheEntry(catalog, DateTime.UtcNow);
            return catalog;
        }

        public static void Invalidate(NpgsqlConnection connection)
        {
            if (connection == null) return;
            string cacheKey = connection.Host + ":" + connection.Port.ToString(CultureInfo.InvariantCulture) + "/" + connection.Database;
            lock (CacheLock) Cache.Remove(cacheKey);
        }

        public bool TryGet(uint oid, out PostgreSqlTypeDescriptor descriptor) { return types.TryGetValue(oid, out descriptor); }

        public string ResolveBaseTypeName(PostgreSqlTypeDescriptor descriptor)
        {
            if (descriptor == null) return string.Empty;
            var visited = new HashSet<uint>();
            while (descriptor.BaseTypeOid != 0 && visited.Add(descriptor.Oid))
            {
                PostgreSqlTypeDescriptor baseType;
                if (!types.TryGetValue(descriptor.BaseTypeOid, out baseType)) break;
                descriptor = baseType;
            }
            return descriptor.Name;
        }

        private static uint ParseOid(string value)
        {
            uint oid;
            return UInt32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out oid) ? oid : 0;
        }

        private sealed class CacheEntry
        {
            internal readonly PostgreSqlTypeCatalog Catalog;
            internal readonly DateTime CreatedUtc;
            internal CacheEntry(PostgreSqlTypeCatalog catalog, DateTime createdUtc) { Catalog = catalog; CreatedUtc = createdUtc; }
        }
    }

    internal sealed class PostgreSqlTypeDescriptor
    {
        internal readonly uint Oid;
        internal readonly string Schema;
        internal readonly string Name;
        internal readonly string TypeKind;
        internal readonly string Category;
        internal readonly uint BaseTypeOid;
        internal readonly uint ElementTypeOid;
        internal readonly uint RangeSubtypeOid;
        internal readonly string DisplayName;

        internal PostgreSqlTypeDescriptor(uint oid, string schema, string name, string typeKind, string category,
            uint baseTypeOid, uint elementTypeOid, uint rangeSubtypeOid, string displayName)
        {
            Oid = oid; Schema = schema ?? string.Empty; Name = name ?? string.Empty; TypeKind = typeKind ?? string.Empty;
            Category = category ?? string.Empty; BaseTypeOid = baseTypeOid; ElementTypeOid = elementTypeOid;
            RangeSubtypeOid = rangeSubtypeOid; DisplayName = displayName ?? Name;
        }

        internal string QualifiedName { get { return Schema == "pg_catalog" ? Name : Schema + "." + Name; } }
        internal string Structure
        {
            get
            {
                switch (TypeKind)
                {
                    case "d": return "domain";
                    case "e": return "enum";
                    case "c": return "composite";
                    case "r": return "range";
                    case "m": return "multirange";
                }
                return Category == "A" ? "array" : string.Empty;
            }
        }
    }
}
