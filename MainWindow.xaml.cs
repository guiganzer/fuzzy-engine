using Microsoft.Win32;
using Newtonsoft.Json;
using Npgsql;
using PostgresCommandExecuter.Favorites;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PostgresCommandExecuter
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer highlightTimer;
        private CancellationTokenSource queryCancellation;
        private NpgsqlCommand activeCommand;
        private bool applyingHighlight;
        private bool isDarkTheme = true;
        private bool resultsPanelExpanded;
        private bool panelTransitionInProgress;
        private readonly FavoriteCatalogStore favoriteStore = new FavoriteCatalogStore();
        private string currentFavoriteId;
        private GridLength savedQueryHeight = new GridLength(1, GridUnitType.Star);
        private GridLength savedResultsHeight = new GridLength(1.15, GridUnitType.Star);

        private static readonly Regex SqlTokenRegex = new Regex(
            @"(?<comment>--[^\r\n]*|/\*[\s\S]*?\*/)|(?<string>'(?:''|[^'])*')|(?<number>\b\d+(?:\.\d+)?\b)|(?<keyword>\b(?:SELECT|FROM|WHERE|INSERT|INTO|UPDATE|DELETE|CREATE|ALTER|DROP|TABLE|VIEW|INDEX|JOIN|LEFT|RIGHT|FULL|INNER|OUTER|ON|AS|AND|OR|NOT|NULL|IS|IN|EXISTS|BETWEEN|LIKE|ILIKE|ORDER|BY|GROUP|HAVING|LIMIT|OFFSET|VALUES|SET|RETURNING|WITH|RECURSIVE|UNION|ALL|DISTINCT|CASE|WHEN|THEN|ELSE|END|ASC|DESC|TRUE|FALSE|BEGIN|COMMIT|ROLLBACK|EXPLAIN|ANALYZE|GRANT|REVOKE|PRIMARY|KEY|FOREIGN|REFERENCES|CONSTRAINT|DEFAULT)\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public MainWindow()
        {
            InitializeComponent();

            highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            highlightTimer.Tick += delegate
            {
                highlightTimer.Stop();
                ApplySqlHighlighting();
            };

            SetEditorText("SELECT version();\r\n\r\n-- Selecione um trecho ou pressione Ctrl+Enter para executar tudo.\r\nSELECT current_database(), current_user, now();");
            RefreshFavoritesCount();
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
                    ConnectionStatusText.Text = "● Conectado localmente";
                    ConnectionStatusText.Foreground = (Brush)FindResource("SuccessBrush");
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
                JsonTextBox.Clear();
                MessagesTextBox.Clear();

                using (var connection = new NpgsqlConnection(BuildConnectionString()))
                {
                    await connection.OpenAsync(queryCancellation.Token);
                    ConnectionStatusText.Text = "● Conectado localmente";
                    ConnectionStatusText.Foreground = (Brush)FindResource("SuccessBrush");

                    using (var command = new NpgsqlCommand(sql, connection))
                    {
                        activeCommand = command;
                        using (var reader = await command.ExecuteReaderAsync(queryCancellation.Token))
                        {
                            var table = await Task.Run(() =>
                            {
                                var loadedTable = new DataTable("result");
                                loadedTable.Load(reader);
                                return loadedTable;
                            }, queryCancellation.Token);
                            string json = await Task.Run(() => SerializeTable(table), queryCancellation.Token);
                            ResultsGrid.ItemsSource = table.DefaultView;
                            JsonTextBox.Text = json;

                            stopwatch.Stop();
                            string message = table.Columns.Count == 0
                                ? "Comando concluído."
                                : string.Format(CultureInfo.CurrentCulture, "{0:N0} linha(s), {1:N0} coluna(s).", table.Rows.Count, table.Columns.Count);
                            MessagesTextBox.Text = message + Environment.NewLine + "Tempo: " + stopwatch.ElapsedMilliseconds + " ms";
                            StatusText.Text = message + "  " + stopwatch.ElapsedMilliseconds + " ms";
                            ResultTabs.SelectedIndex = table.Columns.Count > 0 ? 0 : 2;
                        }
                    }
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

        private static string SerializeTable(DataTable table)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (DataRow row in table.Rows)
            {
                var item = new Dictionary<string, object>();
                foreach (DataColumn column in table.Columns)
                {
                    object value = row[column];
                    item[column.ColumnName] = value == DBNull.Value ? null : value;
                }
                rows.Add(item);
            }
            return JsonConvert.SerializeObject(rows, Formatting.Indented);
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

        private void ShowError(Exception ex)
        {
            StatusText.Text = "Falha";
            MessagesTextBox.Text = ex.Message;
            ResultTabs.SelectedIndex = 2;
        }

        private string GetSelectedSql()
        {
            string selected = SqlEditor.Selection.Text;
            if (!string.IsNullOrWhiteSpace(selected))
                return selected;
            return new TextRange(SqlEditor.Document.ContentStart, SqlEditor.Document.ContentEnd).Text;
        }

        private void SqlEditor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (applyingHighlight || highlightTimer == null)
                return;
            highlightTimer.Stop();
            highlightTimer.Start();
        }

        private void ApplySqlHighlighting()
        {
            if (applyingHighlight)
                return;

            applyingHighlight = true;
            try
            {
                var documentRange = new TextRange(SqlEditor.Document.ContentStart, SqlEditor.Document.ContentEnd);
                string text = documentRange.Text;
                documentRange.ApplyPropertyValue(TextElement.ForegroundProperty, (Brush)FindResource("TextPrimaryBrush"));
                documentRange.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);

                foreach (Match match in SqlTokenRegex.Matches(text))
                {
                    TextPointer start = GetTextPositionAtOffset(SqlEditor.Document.ContentStart, match.Index);
                    TextPointer end = GetTextPositionAtOffset(SqlEditor.Document.ContentStart, match.Index + match.Length);
                    if (start == null || end == null)
                        continue;

                    string brushKey = match.Groups["comment"].Success ? "SqlCommentBrush"
                        : match.Groups["string"].Success ? "SqlStringBrush"
                        : match.Groups["number"].Success ? "SqlNumberBrush"
                        : "SqlKeywordBrush";
                    var range = new TextRange(start, end);
                    range.ApplyPropertyValue(TextElement.ForegroundProperty, (Brush)FindResource(brushKey));
                    if (match.Groups["keyword"].Success)
                        range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
                }
            }
            finally
            {
                applyingHighlight = false;
            }
        }

        private static TextPointer GetTextPositionAtOffset(TextPointer start, int offset)
        {
            TextPointer pointer = start;
            int count = 0;
            while (pointer != null)
            {
                if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    int runLength = pointer.GetTextRunLength(LogicalDirection.Forward);
                    if (count + runLength >= offset)
                        return pointer.GetPositionAtOffset(offset - count);
                    count += runLength;
                }
                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
            }
            return start.DocumentEnd;
        }

        private void SetEditorText(string text)
        {
            SqlEditor.Document.Blocks.Clear();
            SqlEditor.Document.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0) });
            ApplySqlHighlighting();
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
                File.WriteAllText(dialog.FileName, new TextRange(SqlEditor.Document.ContentStart, SqlEditor.Document.ContentEnd).Text);
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
            ApplySqlHighlighting();
        }
    }
}
