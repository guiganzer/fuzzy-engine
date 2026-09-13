using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace PostgresCommandExecuter.Favorites
{
    public sealed class FavoriteCatalogFile
    {
        [YamlMember(Alias = "catalogo_sql_operacao", Order = 1)]
        public FavoriteCatalog Catalog { get; set; } = new FavoriteCatalog();
    }

    public sealed class FavoriteCatalog
    {
        [YamlMember(Alias = "versao", Order = 1)]
        public string Version { get; set; } = "1.0";

        [YamlMember(Alias = "projeto", Order = 2)]
        public string Project { get; set; } = "PostgreSQL Command Executer - Favoritos locais";

        [YamlMember(Alias = "banco", Order = 3)]
        public string Database { get; set; } = "PostgreSQL";

        [YamlMember(Alias = "objetivo", Order = 4)]
        public string Objective { get; set; } = "Catálogo pessoal de consultas SQL.";

        [YamlMember(Alias = "convencoes", Order = 5)]
        public Dictionary<object, object> Conventions { get; set; }

        [YamlMember(Alias = "consultas", Order = 10)]
        public List<FavoriteQuery> Queries { get; set; } = new List<FavoriteQuery>();

        [YamlMember(Alias = "operacoes", Order = 11)]
        public List<FavoriteQuery> Operations { get; set; } = new List<FavoriteQuery>();

        [YamlMember(Alias = "prata", Order = 12)]
        public List<FavoriteQuery> Silver { get; set; } = new List<FavoriteQuery>();

        [YamlMember(Alias = "playbooks", Order = 20)]
        public List<Dictionary<object, object>> Playbooks { get; set; }

        [YamlMember(Alias = "anti_padroes", Order = 21)]
        public List<string> AntiPatterns { get; set; }

        public IEnumerable<FavoriteQuery> AllQueries()
        {
            foreach (var item in Queries ?? new List<FavoriteQuery>()) { item.Section = "consultas"; yield return item; }
            foreach (var item in Operations ?? new List<FavoriteQuery>()) { item.Section = "operacoes"; yield return item; }
            foreach (var item in Silver ?? new List<FavoriteQuery>()) { item.Section = "prata"; yield return item; }
        }
    }

    public sealed class FavoriteQuery
    {
        [YamlIgnore]
        public string Section { get; set; } = "consultas";

        [YamlMember(Alias = "id", Order = 1)]
        public string Id { get; set; }

        [YamlMember(Alias = "categoria", Order = 2)]
        public string Category { get; set; }

        [YamlMember(Alias = "tipo", Order = 3)]
        public string Type { get; set; }

        [YamlMember(Alias = "risco", Order = 4)]
        public string Risk { get; set; }

        [YamlMember(Alias = "finalidade", Order = 5)]
        public string Purpose { get; set; }

        [YamlMember(Alias = "parametros", Order = 6)]
        public List<string> Parameters { get; set; }

        [YamlMember(Alias = "sql", Order = 7, ScalarStyle = YamlDotNet.Core.ScalarStyle.Literal)]
        public string Sql { get; set; }

        [YamlMember(Alias = "observacao", Order = 8)]
        public string Note { get; set; }

        [YamlMember(Alias = "requer_funcao", Order = 9)]
        public string RequiredFunction { get; set; }

        public FavoriteQuery Clone()
        {
            return new FavoriteQuery
            {
                Section = Section,
                Id = Id,
                Category = Category,
                Type = Type,
                Risk = Risk,
                Purpose = Purpose,
                Parameters = Parameters == null ? null : new List<string>(Parameters),
                Sql = Sql,
                Note = Note,
                RequiredFunction = RequiredFunction
            };
        }
    }
}
