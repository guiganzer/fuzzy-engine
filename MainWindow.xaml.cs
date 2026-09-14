using Microsoft.Win32;
using Newtonsoft.Json;
using Npgsql;
using PostgresCommandExecuter.Editors;
using PostgresCommandExecuter.Favorites;
using PostgresCommandExecuter.Results;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PostgresCommandExecuter
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer highlightTimer;
        private readonly DispatcherTimer connectionPulseTimer;
        private CancellationTokenSource queryCancellation;
        private NpgsqlCommand activeCommand;
        private bool isDarkTheme = true;
        private bool resultsPanelExpanded;
        private bool panelTransitionInProgress;
        private bool isConnected;
        private readonly FavoriteCatalogStore favoriteStore = new FavoriteCatalogStore();
        private string currentFavoriteId;
        private GridLength savedQueryHeight = new GridLength(1, GridUnitType.Star);
        private GridLength savedResultsHeight = new GridLength(1.15, GridUnitType.Star);
        private readonly TokenColorizer sqlColorizer;
        private readonly TokenColorizer jsonColorizer;
        private readonly Dictionary<DataGridColumn, ResultColumnPresentation> resultColumnStyles = new Dictionary<DataGridColumn, ResultColumnPresentation>();
        private ResultBrushPalette resultBrushPalette;
        private const int MaximumLiveAnalysisLength = 350000;

        public MainWindow()
        {
            InitializeComponent();

            sqlColorizer = new TokenColorizer(ResolveSyntaxBrush);
            jsonColorizer = new TokenColorizer(ResolveSyntaxBrush);
            SqlEditor.TextArea.TextView.LineTransformers.Add(sqlColorizer);
            JsonViewer.TextArea.TextView.LineTransformers.Add(jsonColorizer);
            ConfigureSyntaxEditors();

            highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            highlightTimer.Tick += delegate
            {
                highlightTimer.Stop();
                RefreshSqlAnalysis();
            };

            connectionPulseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            connectionPulseTimer.Tick += delegate { PulseConnectionIndicator(); };

            SetEditorText("SELECT version();\r\n\r\n-- Selecione um trecho ou pressione Ctrl+Enter para executar tudo.\r\nSELECT current_database(), current_user, now();");
            RefreshFavoritesCount();
            RefreshResultActions();
        }

        private void AddFavorite_Click(object sender, RoutedEventArgs e)
        {
            string sql = GetSelectedSql();
            if (string.IsNullOrWhiteSpace(sql))
            {
                StatusText.Text = "Selecione ou escreva uma consulta antes de favoritar.";
                return;
            }

            Tuple<string, string> sqlKind = GuessSqlKind(sql);
            var dialog = new FavoriteEditorWindow(new FavoriteQuery
            {
                Sql = sql.Trim(),
                Section = sqlKind.Item1,
                Type = sqlKind.Item2,
                Risk = sqlKind.Item2 == "leitura" ? "baixo" : "medio",
                Category = "geral"
            }) { Owner = this };

            if (dialog.ShowDialog() != true) return;
            SaveFavoriteFromDialog(dialog);
        }

        private void ShowFavorites_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var favorites = favoriteStore.Load().Catalog.AllQueries()
                    .OrderBy(x => x.Section).ThenBy(x => x.Category).ThenBy(x => x.Id).ToList();
                var menu = new ContextMenu { PlacementTarget = FavoritesButton };

                var importItem = new MenuItem { Header = "＋  Importar catálogo YAML..." };
                importItem.Click += ImportFavorites_Click;
                menu.Items.Add(importItem);
                menu.Items.Add(new Separator());

                if (favorites.Count == 0)
                {
                    menu.Items.Add(new MenuItem { Header = "   Nenhum favorito salvo", IsEnabled = false });
                }
                else
                {
                    foreach (var section in favorites.GroupBy(x => x.Section))
                    {
                        var sectionItem = new MenuItem { Header = SectionTitle(section.Key) };
                        foreach (var category in section.GroupBy(x => x.Category))
                        {
                            var categoryItem = new MenuItem { Header = category.Key };
                            foreach (var favorite in category)
                            {
                                var queryItem = new MenuItem
                                {
                                    Header = favorite.Id,
                                    Tag = favorite,
                                    ToolTip = favorite.Purpose
                                };
                                queryItem.Click += LoadFavorite_Click;
                                categoryItem.Items.Add(queryItem);
                            }
                            sectionItem.Items.Add(categoryItem);
                        }
                        menu.Items.Add(sectionItem);
                    }
                }

                menu.Items.Add(new Separator());
                var editItem = new MenuItem { Header = "Editar favorito carregado...", IsEnabled = !string.IsNullOrEmpty(currentFavoriteId) };
                editItem.Click += EditCurrentFavorite_Click;
                menu.Items.Add(editItem);
                var deleteItem = new MenuItem { Header = "Excluir favorito carregado...", IsEnabled = !string.IsNullOrEmpty(currentFavoriteId) };
                deleteItem.Click += DeleteCurrentFavorite_Click;
                menu.Items.Add(deleteItem);
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = "Catálogo local  •  " + favorites.Count + " item(ns)", IsEnabled = false });

                FavoritesButton.ContextMenu = menu;
                menu.IsOpen = true;
            }
            catch (Exception ex)
            {
                ShowFavoriteError(ex);
            }
        }

        private void ImportFavorites_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Importar catálogo de consultas",
                Filter = "Catálogo YAML (*.yaml;*.yml)|*.yaml;*.yml|Todos os arquivos (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;

            var choice = MessageBox.Show(this,
                "Como deseja importar o catálogo?\n\n" +
                "SIM — Mesclar e atualizar favoritos com o mesmo id.\n" +
                "NÃO — Substituir todo o catálogo local.\n" +
                "CANCELAR — Não importar.\n\n" +
                "Uma cópia de backup será mantida.",
                "Importar catálogo YAML", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;

            try
            {
                bool replace = choice == MessageBoxResult.No;
                CatalogImportResult result = favoriteStore.Import(dialog.FileName, replace);
                currentFavoriteId = null;
                RefreshFavoritesCount();
                StatusText.Text = replace
                    ? "Catálogo substituído: " + result.Total + " item(ns)."
                    : "Importação concluída: " + result.Added + " novo(s), " + result.Updated + " atualizado(s).";
            }
            catch (Exception ex)
            {
                ShowFavoriteError(ex);
            }
        }

        private void LoadFavorite_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuItem;
            var favorite = item == null ? null : item.Tag as FavoriteQuery;
            if (favorite == null) return;
            SetEditorText(favorite.Sql ?? "");
            currentFavoriteId = favorite.Id;
            StatusText.Text = "Favorito carregado: " + favorite.Id;
            SqlEditor.Focus();
        }

        private void EditCurrentFavorite_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var favorite = favoriteStore.Load().Catalog.AllQueries()
                    .FirstOrDefault(x => string.Equals(x.Id, currentFavoriteId, StringComparison.OrdinalIgnoreCase));
                if (favorite == null)
                {
                    currentFavoriteId = null;
                    RefreshFavoritesCount();
                    StatusText.Text = "O favorito não existe mais.";
                    return;
                }

                var dialog = new FavoriteEditorWindow(favorite.Clone()) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                SaveFavoriteFromDialog(dialog);
            }
            catch (Exception ex)
            {
                ShowFavoriteError(ex);
            }
        }

        private void DeleteCurrentFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentFavoriteId)) return;
            if (MessageBox.Show(this, "Excluir o favorito '" + currentFavoriteId + "'?", "Excluir favorito",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                favoriteStore.Delete(currentFavoriteId);
                StatusText.Text = "Favorito excluído: " + currentFavoriteId;
                currentFavoriteId = null;
                RefreshFavoritesCount();
            }
            catch (Exception ex)
            {
                ShowFavoriteError(ex);
            }
        }

        private void SaveFavoriteFromDialog(FavoriteEditorWindow dialog)
        {
            try
            {
                favoriteStore.Upsert(dialog.Result, dialog.OriginalId);
                currentFavoriteId = dialog.Result.Id;
                RefreshFavoritesCount();
                StatusText.Text = "Favorito salvo localmente: " + dialog.Result.Id;
            }
            catch (Exception ex)
            {
                ShowFavoriteError(ex);
            }
        }

        private void RefreshFavoritesCount()
        {
            try
            {
                int count = favoriteStore.Load().Catalog.AllQueries().Count();
                FavoritesButton.Content = "★ Favoritos (" + count + ") ▾";
            }
            catch (Exception ex)
            {
                FavoritesButton.Content = "★ Favoritos !";
                MessagesTextBox.Text = "Não foi possível carregar favoritos: " + ex.Message;
            }
        }

        private void ShowFavoriteError(Exception ex)
        {
            StatusText.Text = "Falha no catálogo de favoritos";
            MessagesTextBox.Text = ex.Message + Environment.NewLine + Environment.NewLine + "Arquivo: " + favoriteStore.FilePath;
            ResultTabs.SelectedIndex = 2;
        }

        private static Tuple<string, string> GuessSqlKind(string sql)
        {
            string normalized = (sql ?? "").TrimStart();
            bool readOnly = Regex.IsMatch(normalized, "^(SELECT|WITH|SHOW|EXPLAIN|VALUES)\\b", RegexOptions.IgnoreCase);
            return Tuple.Create(readOnly ? "consultas" : "operacoes", readOnly ? "leitura" : "escrita");
        }

        private static string SectionTitle(string section)
        {
            if (string.Equals(section, "operacoes", StringComparison.OrdinalIgnoreCase)) return "Operações";
            if (string.Equals(section, "prata", StringComparison.OrdinalIgnoreCase)) return "Prata";
            return "Consultas";
        }

        private string BuildConnectionString()
        {
            int port;
            if (!int.TryParse(PortTextBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                throw new InvalidOperationException("A porta deve ser um número entre 1 e 65535.");
            if (string.IsNullOrWhiteSpace(DatabaseTextBox.Text))
                throw new InvalidOperationException("Informe o banco de dados.");
            if (string.IsNullOrWhiteSpace(UserTextBox.Text))
                throw new InvalidOperationException("Informe o usuário.");

            // O host é deliberadamente fixo: não aceite texto externo aqui.
            return new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = port,
                Database = DatabaseTextBox.Text.Trim(),
                Username = UserTextBox.Text.Trim(),
                Password = PasswordInput.Password,
                ApplicationName = "PostgresCommandExecuter",
                Timeout = 8,
                CommandTimeout = 0,
                Pooling = true
            }.ConnectionString;
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetBusy(true, "Testando conexão...");
                using (var connection = new NpgsqlConnection(BuildConnectionString()))
                {
                    await connection.OpenAsync();
                    SetConnectionStatus(true);
                    StatusText.Text = "Conexão válida";
                }
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            finally
            {
                SetBusy(false, StatusText.Text);
            }
        }

        private async void Execute_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteSqlAsync();
        }

        private void FormatSql_Click(object sender, RoutedEventArgs e)
        {
            string selection = SqlEditor.SelectedText;
            bool hasSelection = !string.IsNullOrEmpty(selection);
            string source = hasSelection ? selection : SqlEditor.Text;
            SqlFormatResult result = SqlDocumentFormatter.TryFormat(source);
            if (!result.Success)
            {
                StatusText.Text = result.Message;
                return;
            }

            if (hasSelection)
                SqlEditor.SelectedText = result.Text;
            else
                SqlEditor.Text = result.Text;

            RefreshSqlAnalysis();
            StatusText.Text = result.Message;
        }

        private async Task ExecuteSqlAsync()
        {
            if (queryCancellation != null)
                return;

            string sql = GetSelectedSql();
            if (string.IsNullOrWhiteSpace(sql))
            {
                StatusText.Text = "Digite uma consulta SQL.";
                return;
            }

            queryCancellation = new CancellationTokenSource();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                SetBusy(true, "Executando...");
                ResultsGrid.ItemsSource = null;
                resultColumnStyles.Clear();
                SetJsonText(string.Empty);
                ExportCsvButton.IsEnabled = false;
                ExportJsonButton.IsEnabled = false;
                GridCopyHint.Visibility = Visibility.Collapsed;
                MessagesTextBox.Clear();

                using (var connection = new NpgsqlConnection(BuildConnectionString()))
                {
                    await connection.OpenAsync(queryCancellation.Token);
                    SetConnectionStatus(true);

                    if (MayChangeTypeCatalog(sql))
                        PostgreSqlTypeCatalog.Invalidate(connection);

                    ResultTableLoad loaded = await LoadResultTableAsync(connection, sql, queryCancellation.Token);
                    string json = await Task.Run(() => SerializeTable(loaded.Table), queryCancellation.Token);
                    ResultsGrid.ItemsSource = loaded.Table.DefaultView;
                    SetJsonText(json);
                    ExportCsvButton.IsEnabled = loaded.Table.Columns.Count > 0;
                    ExportJsonButton.IsEnabled = !string.IsNullOrWhiteSpace(json);

                    stopwatch.Stop();
                    string message = loaded.Table.Columns.Count == 0
                        ? "Comando concluído."
                        : string.Format(CultureInfo.CurrentCulture, "{0:N0} linha(s), {1:N0} coluna(s).", loaded.Table.Rows.Count, loaded.Table.Columns.Count);
                    MessagesTextBox.Text = message + (string.IsNullOrEmpty(loaded.Warning) ? string.Empty : Environment.NewLine + "Aviso: " + loaded.Warning) +
                        Environment.NewLine + "Tempo: " + stopwatch.ElapsedMilliseconds + " ms";
                    StatusText.Text = message + "  " + stopwatch.ElapsedMilliseconds + " ms";
                    ResultTabs.SelectedIndex = loaded.Table.Columns.Count > 0 ? 0 : 2;
                }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Consulta cancelada.";
                MessagesTextBox.Text = "A execução foi cancelada pelo usuário.";
                ResultTabs.SelectedIndex = 2;
            }
            catch (PostgresException ex)
            {
                StatusText.Text = "Erro do PostgreSQL";
                MessagesTextBox.Text = "SQLSTATE " + ex.SqlState + Environment.NewLine + ex.MessageText +
                    (string.IsNullOrEmpty(ex.Detail) ? "" : Environment.NewLine + ex.Detail) +
                    (string.IsNullOrEmpty(ex.Hint) ? "" : Environment.NewLine + "Dica: " + ex.Hint);
                ResultTabs.SelectedIndex = 2;
            }
            catch (Exception ex) when (IsLegacyUnknownTypeError(ex))
            {
                StatusText.Text = "Tipo PostgreSQL não suportado pelo driver";
                MessagesTextBox.Text = "O Npgsql 4.1 não reconhece um tipo retornado pelo PostgreSQL 18. " +
                    "Para preservar a segurança da execução, a consulta não é repetida automaticamente." + Environment.NewLine +
                    "Converta a coluna incompatível para texto na própria consulta, por exemplo: coluna::text AS coluna." + Environment.NewLine +
                    "Detalhe: " + ex.Message;
                ResultTabs.SelectedIndex = 2;
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            finally
            {
                activeCommand = null;
                if (queryCancellation != null)
                {
                    queryCancellation.Dispose();
                    queryCancellation = null;
                }
                SetBusy(false, StatusText.Text);
            }
        }

        private sealed class ResultTableLoad
        {
            internal DataTable Table;
            internal string Warning;
        }

        private async Task<ResultTableLoad> LoadResultTableAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
        {
            using (var command = new NpgsqlCommand(sql, connection))
            {
                activeCommand = command;
                using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    PostgreSqlResultMetadata metadata = PostgreSqlResultMetadata.Capture(reader);
                    var table = await Task.Run(() =>
                    {
                        var loadedTable = new DataTable("result");
                        loadedTable.Load(reader);
                        return loadedTable;
                    }, cancellationToken);
                    reader.Close();

                    string warning = null;
                    try
                    {
                        metadata.Enrich(await PostgreSqlTypeCatalog.GetAsync(connection, cancellationToken));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception catalogException)
                    {
                        warning = "Metadados de pg_type indisponíveis: " + catalogException.Message;
                    }
                    metadata.ApplyTo(table);
                    return new ResultTableLoad { Table = table, Warning = warning };
                }
            }
        }

        private static bool IsLegacyUnknownTypeError(Exception ex)
        {
            string message = ex == null ? string.Empty : ex.ToString();
            return message.IndexOf("Couldn't find PostgreSQL type", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("unknown PostgreSQL type", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MayChangeTypeCatalog(string sql)
        {
            return Regex.IsMatch(sql ?? string.Empty,
                @"\b(create|alter|drop)\s+(type|domain|extension)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string SerializeTable(DataTable table)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (DataRow row in table.Rows)
            {
                var item = new Dictionary<string, object>();
                foreach (DataColumn column in table.Columns)
                {
                    object value = row[column];
                    item[column.ColumnName] = PrepareValueForJson(value);
                }
                rows.Add(item);
            }
            return JsonConvert.SerializeObject(rows, Formatting.Indented);
        }

        private static object PrepareValueForJson(object value)
        {
            if (value == null || value == DBNull.Value)
                return null;

            // Npgsql 4.1 entrega inet/cidr como IPAddress. O Json.NET tenta
            // refletir ScopeId, que lança para alguns endereços IPv6. Para a
            // aba JSON, a representação textual é a forma estável e legível.
            var address = value as IPAddress;
            if (address != null)
                return address.ToString();

            var physicalAddress = value as PhysicalAddress;
            if (physicalAddress != null)
                return physicalAddress.ToString();

            var bytes = value as byte[];
            if (bytes != null)
                return Convert.ToBase64String(bytes);

            return value;
        }

        private void ExportJson_Click(object sender, RoutedEventArgs e)
        {
            string json = JsonViewer.Text;
            if (string.IsNullOrWhiteSpace(json))
            {
                StatusText.Text = "Não há resultado JSON para exportar.";
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Exportar resultado JSON",
                Filter = "Arquivo JSON (*.json)|*.json|Todos os arquivos (*.*)|*.*",
                DefaultExt = ".json",
                AddExtension = true,
                FileName = BuildExportFileName("resultado", "json")
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                File.WriteAllText(dialog.FileName, json, new UTF8Encoding(true));
                StatusText.Text = "JSON exportado: " + dialog.FileName;
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            DataTable table = GetResultsTable();
            if (table == null || table.Columns.Count == 0)
            {
                StatusText.Text = "Não há resultado tabular para exportar.";
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Exportar resultado CSV",
                Filter = "Arquivo CSV (*.csv)|*.csv|Todos os arquivos (*.*)|*.*",
                DefaultExt = ".csv",
                AddExtension = true,
                FileName = BuildExportFileName("resultado", "csv")
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                using (var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true)))
                {
                    writer.WriteLine(string.Join(",", table.Columns.Cast<DataColumn>().Select(column => EscapeCsv(column.ColumnName))));
                    foreach (DataRow row in table.Rows)
                        writer.WriteLine(string.Join(",", table.Columns.Cast<DataColumn>().Select(column => EscapeCsv(GetEditableCellText(row[column])))));
                }

                StatusText.Text = string.Format(CultureInfo.CurrentCulture, "CSV exportado: {0:N0} linha(s) em {1}", table.Rows.Count, dialog.FileName);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private DataTable GetResultsTable()
        {
            var view = ResultsGrid.ItemsSource as DataView;
            return view == null ? null : view.Table;
        }

        private static string BuildExportFileName(string prefix, string extension)
        {
            return prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "." + extension;
        }

        private static string EscapeCsv(string value)
        {
            string safe = value ?? string.Empty;
            return "\"" + safe.Replace("\"", "\"\"") + "\"";
        }

        private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            GridCopyHint.Visibility = ResultsGrid.SelectedItems.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ResultsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            e.Column.SortMemberPath = e.PropertyName;

            var view = ResultsGrid.ItemsSource as DataView;
            DataColumn column = view == null || !view.Table.Columns.Contains(e.PropertyName)
                ? null
                : view.Table.Columns[e.PropertyName];
            string typeName = PostgreSqlResultMetadata.GetDisplayName(column);
            if (typeName == "desconhecido")
                typeName = GetPostgreSqlTypeName(column == null ? e.PropertyType : column.DataType);
            string semanticTypeName = PostgreSqlResultMetadata.GetSemanticTypeName(column);
            if (string.IsNullOrEmpty(semanticTypeName))
                semanticTypeName = typeName;
            string structure = PostgreSqlResultMetadata.GetStructure(column);

            // Booleanos como texto permitem distinguir true, false e NULL pela
            // mesma paleta semântica da grade, inclusive durante a seleção.
            if (e.Column is DataGridCheckBoxColumn)
            {
                e.Column = new DataGridTextColumn
                {
                    SortMemberPath = e.PropertyName,
                    Binding = new System.Windows.Data.Binding(e.PropertyName) { Converter = ResultValueDisplayConverter.Instance }
                };
            }

            var generatedTextColumn = e.Column as DataGridTextColumn;
            if (generatedTextColumn != null)
                generatedTextColumn.Binding = new System.Windows.Data.Binding(e.PropertyName) { Converter = ResultValueDisplayConverter.Instance };
            resultColumnStyles[e.Column] = new ResultColumnPresentation(e.PropertyName, semanticTypeName, structure);

            var header = new StackPanel { Orientation = Orientation.Vertical };
            header.Children.Add(new TextBlock
            {
                Text = e.PropertyName,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var type = new TextBlock
            {
                Text = typeName,
                FontWeight = FontWeights.Normal,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            type.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            header.Children.Add(type);
            e.Column.Header = header;

            ApplyResultColumnStyle(e.Column, resultColumnStyles[e.Column]);
        }

        private Brush ResolveBrush(string resourceKey)
        {
            return TryFindResource(resourceKey) as Brush ?? Brushes.Gray;
        }

        private void ApplyResultColumnStyle(DataGridColumn column, ResultColumnPresentation styleInfo)
        {
            if (resultBrushPalette == null)
                resultBrushPalette = ResultGridStyleFactory.CreatePalette(ResolveBrush);
            var textColumn = column as DataGridTextColumn;
            if (textColumn != null)
            {
                textColumn.ElementStyle = ResultGridStyleFactory.CreateTextStyle(
                    styleInfo, ResolveBrush("TextPrimaryBrush"), resultBrushPalette);
            }

            var checkBoxColumn = column as DataGridCheckBoxColumn;
            if (checkBoxColumn != null)
            {
                checkBoxColumn.ElementStyle = ResultGridStyleFactory.CreateCheckBoxStyle(
                    ResolveBrush("GridBooleanTrueValueBrush"), ResolveBrush("TextPrimaryBrush"));
            }
        }

        private void RefreshResultColumnStyles()
        {
            resultBrushPalette = ResultGridStyleFactory.CreatePalette(ResolveBrush);
            foreach (var item in resultColumnStyles.ToList())
            {
                if (ResultsGrid.Columns.Contains(item.Key))
                    ApplyResultColumnStyle(item.Key, item.Value);
            }
        }

        private void ResultTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != ResultTabs)
                return;

            RefreshResultActions();
        }

        private void RefreshResultActions()
        {
            if (ExportCsvButton == null || ExportJsonButton == null || ResultTabs == null)
                return;

            bool jsonSelected = ResultTabs.SelectedIndex == 1;
            ExportCsvButton.Visibility = jsonSelected ? Visibility.Collapsed : Visibility.Visible;
            ExportJsonButton.Visibility = jsonSelected ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string GetPostgreSqlTypeName(Type type)
        {
            if (type == null || type == typeof(object)) return "desconhecido";
            if (type == typeof(bool)) return "boolean";
            if (type == typeof(byte)) return "smallint";
            if (type == typeof(short)) return "smallint";
            if (type == typeof(int)) return "integer";
            if (type == typeof(long)) return "bigint";
            if (type == typeof(decimal)) return "numeric";
            if (type == typeof(float)) return "real";
            if (type == typeof(double)) return "double precision";
            if (type == typeof(string) || type == typeof(char)) return "text";
            if (type == typeof(Guid)) return "uuid";
            if (type == typeof(DateTime)) return "timestamp";
            if (type == typeof(DateTimeOffset)) return "timestamp with time zone";
            if (type == typeof(TimeSpan)) return "interval";
            if (type == typeof(byte[])) return "bytea";
            if (type == typeof(IPAddress)) return "inet";
            if (type == typeof(PhysicalAddress)) return "macaddr";
            return type.Name.ToLowerInvariant();
        }

        private void ResultsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2)
                return;

            var source = e.OriginalSource as DependencyObject;
            DataGridCell cell = FindParent<DataGridCell>(source);
            if (cell == null || cell.Column == null)
                return;

            var rowView = cell.DataContext as DataRowView;
            string columnName = cell.Column.SortMemberPath;
            if (string.IsNullOrEmpty(columnName))
                columnName = cell.Column.Header as string;
            if (rowView == null || string.IsNullOrEmpty(columnName) || !rowView.Row.Table.Columns.Contains(columnName))
                return;

            e.Handled = true;
            object value = rowView.Row[columnName];
            DataColumn resultColumn = rowView.Row.Table.Columns[columnName];
            var dialog = new CellValueWindow(GetEditableCellText(value), new CellValueMetadata(
                columnName,
                PostgreSqlResultMetadata.GetDisplayName(resultColumn),
                PostgreSqlResultMetadata.GetSemanticTypeName(resultColumn),
                PostgreSqlResultMetadata.GetStructure(resultColumn)))
            {
                Owner = this,
                Title = "Valor: " + columnName + "  •  Ctrl+Enter aplica"
            };

            if (dialog.ShowDialog() != true)
                return;

            string error;
            if (!TryApplyCellValue(rowView.Row, columnName, dialog.ValueText, out error))
            {
                StatusText.Text = error;
                return;
            }

            SetJsonText(SerializeTable(rowView.Row.Table));
            StatusText.Text = "Valor alterado apenas no resultado atual; o banco não foi atualizado.";
        }

        private void ResultsGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
                return;

            ScrollViewer scrollViewer = FindVisualChild<ScrollViewer>(ResultsGrid);
            if (scrollViewer == null || scrollViewer.ScrollableWidth <= 0)
                return;

            // A roda para baixo avança para as últimas colunas; para cima volta.
            const double horizontalStep = 88;
            double direction = e.Delta < 0 ? 1 : -1;
            double steps = Math.Max(1, Math.Abs(e.Delta) / 120.0);
            scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset + (direction * horizontalStep * steps));
            e.Handled = true;
        }

        private static T FindParent<T>(DependencyObject source) where T : DependencyObject
        {
            while (source != null)
            {
                var match = source as T;
                if (match != null)
                    return match;

                var visual = source as Visual;
                source = visual != null
                    ? VisualTreeHelper.GetParent(visual)
                    : LogicalTreeHelper.GetParent(source);
            }

            return null;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                return null;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                var match = child as T;
                if (match != null)
                    return match;

                T nested = FindVisualChild<T>(child);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private static string GetEditableCellText(object value)
        {
            value = PrepareValueForJson(value);
            if (value == null)
                return string.Empty;

            var formattable = value as IFormattable;
            return formattable != null
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool TryApplyCellValue(DataRow row, string columnName, string valueText, out string error)
        {
            var column = row.Table.Columns[columnName];
            try
            {
                if (string.IsNullOrWhiteSpace(valueText) && column.AllowDBNull)
                {
                    row[column] = DBNull.Value;
                    error = null;
                    return true;
                }

                Type targetType = column.DataType;
                object converted;
                if (targetType == typeof(string) || targetType == typeof(object))
                {
                    converted = valueText;
                }
                else if (targetType == typeof(IPAddress))
                {
                    converted = IPAddress.Parse(valueText);
                }
                else if (targetType == typeof(PhysicalAddress))
                {
                    converted = PhysicalAddress.Parse(valueText.Replace(":", string.Empty).Replace("-", string.Empty));
                }
                else if (targetType == typeof(byte[]))
                {
                    converted = Convert.FromBase64String(valueText);
                }
                else
                {
                    TypeConverter converter = TypeDescriptor.GetConverter(targetType);
                    if (converter != null && converter.CanConvertFrom(typeof(string)))
                        converted = converter.ConvertFrom(null, CultureInfo.InvariantCulture, valueText);
                    else
                        converted = Convert.ChangeType(valueText, targetType, CultureInfo.InvariantCulture);
                }

                row[column] = converted;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = "Não foi possível aplicar o valor para '" + columnName + "': " + ex.Message;
                return false;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (queryCancellation == null)
                return;
            StatusText.Text = "Cancelando...";
            queryCancellation.Cancel();
            try
            {
                if (activeCommand != null)
                    activeCommand.Cancel();
            }
            catch
            {
                // A conexão pode já ter sido encerrada pelo cancelamento.
            }
        }

        private void SetBusy(bool busy, string status)
        {
            ExecuteButton.IsEnabled = !busy;
            CancelButton.IsEnabled = busy;
            PortTextBox.IsEnabled = !busy;
            DatabaseTextBox.IsEnabled = !busy;
            UserTextBox.IsEnabled = !busy;
            PasswordInput.IsEnabled = !busy;
            StatusText.Text = status;
        }

        private void SetConnectionStatus(bool connected)
        {
            isConnected = connected;
            if (!connected)
            {
                connectionPulseTimer.Stop();
                ConnectionStatusText.BeginAnimation(OpacityProperty, null);
                ConnectionStatusText.Opacity = 1;
                ConnectionStatusText.Text = "● Desconectado";
                ConnectionStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
                return;
            }

            ConnectionStatusText.Text = "● Conectado localmente";
            ConnectionStatusText.Foreground = (Brush)FindResource("SuccessBrush");
            connectionPulseTimer.Start();
            PulseConnectionIndicator();
        }

        private void PulseConnectionIndicator()
        {
            if (!isConnected)
                return;

            var easing = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            var pulse = new DoubleAnimation(1, 0.42, new Duration(TimeSpan.FromMilliseconds(360)))
            {
                AutoReverse = true,
                EasingFunction = easing
            };
            ConnectionStatusText.BeginAnimation(OpacityProperty, pulse);
        }

        private void ShowError(Exception ex)
        {
            StatusText.Text = "Falha";
            MessagesTextBox.Text = ex.Message;
            ResultTabs.SelectedIndex = 2;
        }

        private string GetSelectedSql()
        {
            string selected = SqlEditor.SelectedText;
            if (!string.IsNullOrWhiteSpace(selected))
                return selected;
            return SqlEditor.Text;
        }

        private void SqlEditor_TextChanged(object sender, EventArgs e)
        {
            if (highlightTimer == null)
                return;
            highlightTimer.Stop();
            highlightTimer.Start();
        }

        private void RefreshSqlAnalysis()
        {
            string text = SqlEditor.Text ?? string.Empty;
            if (text.Length > MaximumLiveAnalysisLength)
            {
                sqlColorizer.SetSpans(new List<TokenStyleSpan>());
                SqlEditor.TextArea.TextView.Redraw();
                SqlAnalysisText.Text = "  •  análise pausada (arquivo muito grande)";
                return;
            }

            SqlLexResult result = SqlLexer.Tokenize(text);
            sqlColorizer.SetSpans(ToSqlStyleSpans(result.Tokens));
            SqlEditor.TextArea.TextView.Redraw();

            if (result.Diagnostics.Count == 0)
            {
                SqlAnalysisText.Text = "  •  PostgreSQL reconhecido";
            }
            else
            {
                SqlAnalysisText.Text = "  •  " + result.Diagnostics[0].Message;
            }
        }

        private void ConfigureSyntaxEditors()
        {
            SqlEditor.Options.ConvertTabsToSpaces = true;
            SqlEditor.Options.IndentationSize = 4;
            SqlEditor.Options.EnableHyperlinks = false;
            SqlEditor.Options.EnableEmailHyperlinks = false;
            JsonViewer.Options.EnableHyperlinks = false;
            JsonViewer.Options.EnableEmailHyperlinks = false;
            ApplySyntaxTheme();
        }

        private void ApplySyntaxTheme()
        {
            Brush primary = FindResource("TextPrimaryBrush") as Brush;
            Brush secondary = FindResource("TextSecondaryBrush") as Brush;
            Brush selection = FindResource("SelectionBrush") as Brush;

            SqlEditor.TextArea.SelectionBrush = selection;
            SqlEditor.TextArea.SelectionForeground = primary;
            SqlEditor.TextArea.Caret.CaretBrush = primary;
            SqlEditor.LineNumbersForeground = secondary;
            JsonViewer.TextArea.SelectionBrush = selection;
            JsonViewer.TextArea.SelectionForeground = primary;
            JsonViewer.LineNumbersForeground = secondary;
        }

        private Brush ResolveSyntaxBrush(TokenStyleKind kind)
        {
            string key;
            switch (kind)
            {
                case TokenStyleKind.Keyword: key = "SqlKeywordBrush"; break;
                case TokenStyleKind.Type: key = "SqlTypeBrush"; break;
                case TokenStyleKind.Function: key = "SqlFunctionBrush"; break;
                case TokenStyleKind.String: key = "SqlStringBrush"; break;
                case TokenStyleKind.Number: key = "SqlNumberBrush"; break;
                case TokenStyleKind.Comment: key = "SqlCommentBrush"; break;
                case TokenStyleKind.Parameter: key = "SqlParameterBrush"; break;
                case TokenStyleKind.PropertyName: key = "JsonPropertyBrush"; break;
                case TokenStyleKind.Literal: key = "JsonLiteralBrush"; break;
                case TokenStyleKind.Punctuation: key = "JsonPunctuationBrush"; break;
                case TokenStyleKind.Invalid: key = "SyntaxInvalidBrush"; break;
                default: return null;
            }

            return FindResource(key) as Brush;
        }

        private static IList<TokenStyleSpan> ToSqlStyleSpans(IList<SqlToken> tokens)
        {
            var spans = new List<TokenStyleSpan>();
            foreach (SqlToken token in tokens)
            {
                TokenStyleKind style;
                bool emphasized = false;
                switch (token.Kind)
                {
                    case SqlTokenKind.Keyword: style = TokenStyleKind.Keyword; emphasized = true; break;
                    case SqlTokenKind.DataType: style = TokenStyleKind.Type; break;
                    case SqlTokenKind.Function: style = TokenStyleKind.Function; break;
                    case SqlTokenKind.String:
                    case SqlTokenKind.QuotedIdentifier: style = TokenStyleKind.String; break;
                    case SqlTokenKind.Number: style = TokenStyleKind.Number; break;
                    case SqlTokenKind.Comment: style = TokenStyleKind.Comment; break;
                    case SqlTokenKind.Parameter: style = TokenStyleKind.Parameter; break;
                    default: continue;
                }

                spans.Add(new TokenStyleSpan(token.Offset, token.Length, style, emphasized));
            }

            return spans;
        }

        private static IList<TokenStyleSpan> ToJsonStyleSpans(IList<JsonSyntaxToken> tokens)
        {
            var spans = new List<TokenStyleSpan>();
            foreach (JsonSyntaxToken token in tokens)
            {
                TokenStyleKind style;
                switch (token.Kind)
                {
                    case JsonSyntaxTokenKind.PropertyName: style = TokenStyleKind.PropertyName; break;
                    case JsonSyntaxTokenKind.String: style = TokenStyleKind.String; break;
                    case JsonSyntaxTokenKind.Number: style = TokenStyleKind.Number; break;
                    case JsonSyntaxTokenKind.Boolean:
                    case JsonSyntaxTokenKind.Null: style = TokenStyleKind.Literal; break;
                    case JsonSyntaxTokenKind.Punctuation: style = TokenStyleKind.Punctuation; break;
                    case JsonSyntaxTokenKind.Invalid: style = TokenStyleKind.Invalid; break;
                    default: continue;
                }

                spans.Add(new TokenStyleSpan(token.Start, token.Length, style, false));
            }

            return spans;
        }

        private void SetJsonText(string text)
        {
            JsonFormatResult formatted = JsonDocumentProcessor.TryFormat(text);
            JsonViewer.Text = formatted.Success ? formatted.FormattedText : (text ?? string.Empty);
            RefreshJsonAnalysis();
        }

        private void RefreshJsonAnalysis()
        {
            string text = JsonViewer.Text ?? string.Empty;
            if (text.Length > MaximumLiveAnalysisLength)
            {
                jsonColorizer.SetSpans(new List<TokenStyleSpan>());
                JsonViewer.TextArea.TextView.Redraw();
                return;
            }

            jsonColorizer.SetSpans(ToJsonStyleSpans(JsonDocumentProcessor.Tokenize(text)));
            JsonViewer.TextArea.TextView.Redraw();
        }

        private void SetEditorText(string text)
        {
            SqlEditor.Text = text ?? string.Empty;
            SqlEditor.CaretOffset = 0;
            RefreshSqlAnalysis();
        }

        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await ExecuteSqlAsync();
            }
            else if (e.Key == Key.Space && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                e.Handled = true;
                SetResultsPanelExpanded(!resultsPanelExpanded);
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                e.Handled = true;
                FormatSql_Click(this, new RoutedEventArgs());
            }
            else if (e.Key == Key.Escape && resultsPanelExpanded)
            {
                e.Handled = true;
                SetResultsPanelExpanded(false);
            }
        }

        private void ToggleResultsPanel_Click(object sender, RoutedEventArgs e)
        {
            SetResultsPanelExpanded(!resultsPanelExpanded);
        }

        private void SetResultsPanelExpanded(bool expanded)
        {
            if (panelTransitionInProgress || resultsPanelExpanded == expanded)
                return;

            panelTransitionInProgress = true;
            ExpandResultsButton.IsEnabled = false;
            var duration = new Duration(TimeSpan.FromMilliseconds(110));

            if (expanded)
            {
                savedQueryHeight = QueryRow.Height;
                savedResultsHeight = ResultsRow.Height;

                var fadeOut = new DoubleAnimation(1, 0, duration)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                fadeOut.Completed += delegate
                {
                    QueryPanel.Visibility = Visibility.Collapsed;
                    QueryPanel.BeginAnimation(OpacityProperty, null);
                    QueryPanel.Opacity = 1;
                    QueryResultSplitter.Visibility = Visibility.Collapsed;
                    QueryRow.Height = new GridLength(0);
                    SplitterRow.Height = new GridLength(0);
                    ResultsRow.Height = new GridLength(1, GridUnitType.Star);
                    resultsPanelExpanded = true;
                    ExpandResultsButton.Content = "↙ Restaurar";
                    ExpandResultsButton.ToolTip = "Restaurar editor (Esc ou Ctrl+Shift+Espaço)";
                    ExpandResultsButton.IsEnabled = true;
                    panelTransitionInProgress = false;
                    ResultTabs.Focus();
                };
                QueryPanel.BeginAnimation(OpacityProperty, fadeOut);
                return;
            }

            QueryRow.Height = savedQueryHeight;
            SplitterRow.Height = new GridLength(8);
            ResultsRow.Height = savedResultsHeight;
            QueryResultSplitter.Visibility = Visibility.Visible;
            QueryPanel.Visibility = Visibility.Visible;
            QueryPanel.Opacity = 0;

            var fadeIn = new DoubleAnimation(0, 1, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            fadeIn.Completed += delegate
            {
                QueryPanel.BeginAnimation(OpacityProperty, null);
                QueryPanel.Opacity = 1;
                resultsPanelExpanded = false;
                ExpandResultsButton.Content = "↗ Expandir";
                ExpandResultsButton.ToolTip = "Expandir resultados (Ctrl+Shift+Espaço)";
                ExpandResultsButton.IsEnabled = true;
                panelTransitionInProgress = false;
            };
            QueryPanel.BeginAnimation(OpacityProperty, fadeIn);
        }

        private void OpenSql_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Arquivos SQL (*.sql)|*.sql|Todos os arquivos (*.*)|*.*" };
            if (dialog.ShowDialog(this) == true)
                SetEditorText(File.ReadAllText(dialog.FileName));
        }

        private void SaveSql_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { Filter = "Arquivos SQL (*.sql)|*.sql|Todos os arquivos (*.*)|*.*", DefaultExt = ".sql" };
            if (dialog.ShowDialog(this) == true)
                File.WriteAllText(dialog.FileName, SqlEditor.Text);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            ApplyTheme(!isDarkTheme);
        }

        private void DarkTheme_Click(object sender, RoutedEventArgs e)
        {
            ApplyTheme(true);
        }

        private void LightTheme_Click(object sender, RoutedEventArgs e)
        {
            ApplyTheme(false);
        }

        private void ApplyTheme(bool dark)
        {
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var palette = new ResourceDictionary
            {
                Source = new Uri(dark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml", UriKind.Relative)
            };

            if (dictionaries.Count == 0)
                dictionaries.Add(palette);
            else
                dictionaries[0] = palette;

            isDarkTheme = dark;
            ThemeButton.Content = dark ? "☀ Tema claro" : "☾ Tema escuro";
            ApplySyntaxTheme();
            RefreshResultColumnStyles();
            RefreshSqlAnalysis();
            RefreshJsonAnalysis();
        }
    }
}
