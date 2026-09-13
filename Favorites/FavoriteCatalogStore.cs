using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PostgresCommandExecuter.Favorites
{
    public sealed class FavoriteCatalogStore
    {
        private readonly IDeserializer deserializer;
        private readonly ISerializer serializer;

        public FavoriteCatalogStore()
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            DirectoryPath = Path.Combine(localData, "PostgresCommandExecuter");
            FilePath = Path.Combine(DirectoryPath, "favorites.yaml");

            deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
                .Build();
        }

        public string DirectoryPath { get; private set; }
        public string FilePath { get; private set; }

        public FavoriteCatalogFile Load()
        {
            EnsureCreated();
            string yaml = File.ReadAllText(FilePath, Encoding.UTF8);
            var file = deserializer.Deserialize<FavoriteCatalogFile>(yaml) ?? new FavoriteCatalogFile();
            if (file.Catalog == null) file.Catalog = new FavoriteCatalog();
            if (file.Catalog.Queries == null) file.Catalog.Queries = new List<FavoriteQuery>();
            if (file.Catalog.Operations == null) file.Catalog.Operations = new List<FavoriteQuery>();
            if (file.Catalog.Silver == null) file.Catalog.Silver = new List<FavoriteQuery>();
            return file;
        }

        public void Upsert(FavoriteQuery query, string originalId)
        {
            if (query == null) throw new ArgumentNullException("query");
            var file = Load();
            if (!string.IsNullOrWhiteSpace(originalId))
                RemoveFromAllSections(file.Catalog, originalId);

            if (file.Catalog.AllQueries().Any(x => string.Equals(x.Id, query.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Já existe um favorito com o id '" + query.Id + "'.");

            GetSection(file.Catalog, query.Section).Add(query.Clone());
            Save(file);
        }

        public void Delete(string id)
        {
            var file = Load();
            if (RemoveFromAllSections(file.Catalog, id)) Save(file);
        }

        public CatalogImportResult Import(string sourcePath, bool replaceCatalog)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("Arquivo YAML não encontrado.", sourcePath);

            FavoriteCatalogFile imported;
            try
            {
                string yaml = File.ReadAllText(sourcePath, Encoding.UTF8);
                imported = deserializer.Deserialize<FavoriteCatalogFile>(yaml);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("O arquivo não é um catálogo YAML válido: " + ex.Message, ex);
            }

            if (imported == null || imported.Catalog == null)
                throw new InvalidDataException("A raiz 'catalogo_sql_operacao' não foi encontrada.");
            Normalize(imported);
            var importedQueries = imported.Catalog.AllQueries().ToList();
            if (importedQueries.Count == 0)
                throw new InvalidDataException("O catálogo não contém consultas, operações ou itens da seção prata.");

            var invalid = importedQueries.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Sql));
            if (invalid != null)
                throw new InvalidDataException("Todas as entradas importadas precisam possuir 'id' e 'sql'.");
            var duplicate = importedQueries.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
            if (duplicate != null)
                throw new InvalidDataException("O arquivo possui id duplicado: '" + duplicate.Key + "'.");

            var current = Load();
            var existingIds = new HashSet<string>(current.Catalog.AllQueries().Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            int updated = importedQueries.Count(x => existingIds.Contains(x.Id));
            int added = importedQueries.Count - updated;

            if (replaceCatalog)
            {
                Save(imported);
                return new CatalogImportResult(importedQueries.Count, added, updated, true);
            }

            foreach (var query in importedQueries)
            {
                RemoveFromAllSections(current.Catalog, query.Id);
                GetSection(current.Catalog, query.Section).Add(query.Clone());
            }
            if (imported.Catalog.Conventions != null) current.Catalog.Conventions = imported.Catalog.Conventions;
            if (imported.Catalog.Playbooks != null) current.Catalog.Playbooks = imported.Catalog.Playbooks;
            if (imported.Catalog.AntiPatterns != null) current.Catalog.AntiPatterns = imported.Catalog.AntiPatterns;
            Save(current);
            return new CatalogImportResult(importedQueries.Count, added, updated, false);
        }

        private void EnsureCreated()
        {
            Directory.CreateDirectory(DirectoryPath);
            if (!File.Exists(FilePath)) Save(new FavoriteCatalogFile());
        }

        private static void Normalize(FavoriteCatalogFile file)
        {
            if (file.Catalog == null) file.Catalog = new FavoriteCatalog();
            if (file.Catalog.Queries == null) file.Catalog.Queries = new List<FavoriteQuery>();
            if (file.Catalog.Operations == null) file.Catalog.Operations = new List<FavoriteQuery>();
            if (file.Catalog.Silver == null) file.Catalog.Silver = new List<FavoriteQuery>();
        }

        private void Save(FavoriteCatalogFile file)
        {
            Directory.CreateDirectory(DirectoryPath);
            string temporaryPath = FilePath + ".tmp";
            string backupPath = FilePath + ".bak";
            string yaml = serializer.Serialize(file);
            File.WriteAllText(temporaryPath, yaml, new UTF8Encoding(false));

            if (File.Exists(FilePath))
                File.Replace(temporaryPath, FilePath, backupPath, true);
            else
                File.Move(temporaryPath, FilePath);
        }

        private static List<FavoriteQuery> GetSection(FavoriteCatalog catalog, string section)
        {
            if (string.Equals(section, "operacoes", StringComparison.OrdinalIgnoreCase)) return catalog.Operations;
            if (string.Equals(section, "prata", StringComparison.OrdinalIgnoreCase)) return catalog.Silver;
            return catalog.Queries;
        }

        private static bool RemoveFromAllSections(FavoriteCatalog catalog, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            int removed = 0;
            removed += catalog.Queries.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            removed += catalog.Operations.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            removed += catalog.Silver.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            return removed > 0;
        }
    }

    public sealed class CatalogImportResult
    {
        public CatalogImportResult(int total, int added, int updated, bool replaced)
        {
            Total = total;
            Added = added;
            Updated = updated;
            Replaced = replaced;
        }

        public int Total { get; private set; }
        public int Added { get; private set; }
        public int Updated { get; private set; }
        public bool Replaced { get; private set; }
    }
}
