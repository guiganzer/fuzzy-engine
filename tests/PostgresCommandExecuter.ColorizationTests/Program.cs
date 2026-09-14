using PostgresCommandExecuter.Results;
using System;
using System.Data;
using Npgsql;

namespace PostgresCommandExecuter.ColorizationTests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                RunContractTests();
                if (Array.Exists(args, argument => string.Equals(argument, "--integration", StringComparison.OrdinalIgnoreCase)))
                    RunPostgreSql18IntegrationTests();

                Console.WriteLine("Colorization tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void RunContractTests()
        {
            AssertSemantic("boolean true", PostgreSqlValueSemantics.Classify("boolean", true), PostgreSqlValueSemantic.BooleanTrue);
            AssertSemantic("boolean false", PostgreSqlValueSemantics.Classify("bool", false), PostgreSqlValueSemantic.BooleanFalse);
            AssertSemantic("null", PostgreSqlValueSemantics.Classify("jsonb", DBNull.Value), PostgreSqlValueSemantic.Null);
            AssertSemantic("negative numeric", PostgreSqlValueSemantics.Classify("numeric", -0.25m), PostgreSqlValueSemantic.NegativeNumber);
            AssertSemantic("zero numeric", PostgreSqlValueSemantics.Classify("float8", 0d), PostgreSqlValueSemantic.ZeroNumber);
            AssertSemantic("json", PostgreSqlValueSemantics.Classify("jsonb", "{}"), PostgreSqlValueSemantic.Json);
            AssertSemantic("network", PostgreSqlValueSemantics.Classify("cidr", "10.0.0.0/8"), PostgreSqlValueSemantic.Network);
            AssertSemantic("array", PostgreSqlValueSemantics.Classify("int4[]", "{1,2}"), PostgreSqlValueSemantic.Array);
            AssertSemantic("custom range metadata", PostgreSqlValueSemantics.Classify("teal", "[1,2)", "range"), PostgreSqlValueSemantic.Range);
            AssertSemantic("no suffix false positive", PostgreSqlValueSemantics.Classify("orange", "value"), PostgreSqlValueSemantic.Custom);
            AssertSemantic("domain base", PostgreSqlValueSemantics.Classify("numeric", 4m, "domain"), PostgreSqlValueSemantic.PositiveNumber);

            foreach (PostgreSqlValueSemantic semantic in Enum.GetValues(typeof(PostgreSqlValueSemantic)))
            {
                if (string.IsNullOrWhiteSpace(ResultGridStyleFactory.GetBrushResourceKey(semantic)))
                    throw new InvalidOperationException("Brush ausente para " + semantic + ".");
            }
        }

        private static void RunPostgreSql18IntegrationTests()
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = ReadEnvironment("POSTGRES_EXECUTER_TEST_HOST", "127.0.0.1"),
                Port = Int32.Parse(ReadEnvironment("POSTGRES_EXECUTER_TEST_PORT", "55432")),
                Database = ReadEnvironment("POSTGRES_EXECUTER_TEST_DATABASE", "postgres_executor_test"),
                Username = ReadEnvironment("POSTGRES_EXECUTER_TEST_USERNAME", "executor_dev"),
                Password = ReadEnvironment("POSTGRES_EXECUTER_TEST_PASSWORD", "local_dev_password")
            };

            using (var connection = new NpgsqlConnection(builder.ConnectionString))
            {
                connection.Open();
                DataTable showcase = LoadWithMetadata(connection,
                    "SELECT sample_id, amount_numeric, short_name, variable_mask, event_data_json, profile_jsonb, optional_note " +
                    "FROM public.postgresql_type_showcase ORDER BY sample_id");
                if (showcase.Rows.Count < 3 || showcase.Columns.Count != 7)
                    throw new InvalidOperationException("A base fake não retornou a grade esperada.");
                AssertContains("faceta numeric", PostgreSqlResultMetadata.GetDisplayName(showcase.Columns[1]), "numeric");
                AssertSemantic("jsonb da base fake",
                    PostgreSqlValueSemantics.Classify(PostgreSqlResultMetadata.GetSemanticTypeName(showcase.Columns[5]), showcase.Rows[0][5], PostgreSqlResultMetadata.GetStructure(showcase.Columns[5])),
                    PostgreSqlValueSemantic.Json);

                DataTable duplicates = LoadWithMetadata(connection,
                    "SELECT 7::integer AS duplicate, 'alpha'::text AS duplicate, '{\"key\":true}'::jsonb AS duplicate");
                if (duplicates.Columns.Count != 3)
                    throw new InvalidOperationException("A consulta com aliases duplicados não preservou três ordinais.");
                AssertContains("ordinal 1", PostgreSqlResultMetadata.GetSemanticTypeName(duplicates.Columns[0]), "int");
                AssertContains("ordinal 2", PostgreSqlResultMetadata.GetSemanticTypeName(duplicates.Columns[1]), "text");
                AssertSemantic("ordinal 3", PostgreSqlValueSemantics.Classify(PostgreSqlResultMetadata.GetSemanticTypeName(duplicates.Columns[2]), duplicates.Rows[0][2], PostgreSqlResultMetadata.GetStructure(duplicates.Columns[2])), PostgreSqlValueSemantic.Json);

                DataTable compatibleMultirange = LoadWithMetadata(connection,
                    "SELECT integer_windows::text AS integer_windows_text FROM public.postgresql_type_showcase LIMIT 1");
                if (compatibleMultirange.Rows.Count != 1 || string.IsNullOrWhiteSpace(Convert.ToString(compatibleMultirange.Rows[0][0])))
                    throw new InvalidOperationException("A conversão segura de multirange para texto falhou.");
            }
            Console.WriteLine("PostgreSQL 18 fake-database integration tests passed.");
        }

        private static DataTable LoadWithMetadata(NpgsqlConnection connection, string sql)
        {
            using (var command = new NpgsqlCommand(sql, connection))
            using (var reader = command.ExecuteReader())
            {
                PostgreSqlResultMetadata metadata = PostgreSqlResultMetadata.Capture(reader);
                var table = new DataTable("result");
                table.Load(reader);
                reader.Close();
                metadata.Enrich(PostgreSqlTypeCatalog.GetAsync(connection, default(System.Threading.CancellationToken)).GetAwaiter().GetResult());
                metadata.ApplyTo(table);
                return table;
            }
        }

        private static string ReadEnvironment(string name, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static void AssertContains(string name, string actual, string expectedFragment)
        {
            if (actual == null || actual.IndexOf(expectedFragment, StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException(name + ": esperava '" + expectedFragment + "', recebido '" + actual + "'.");
        }

        private static void AssertSemantic(string name, PostgreSqlValueSemantic actual, PostgreSqlValueSemantic expected)
        {
            if (actual != expected)
                throw new InvalidOperationException(name + ": esperado " + expected + ", recebido " + actual + ".");
        }
    }
}
