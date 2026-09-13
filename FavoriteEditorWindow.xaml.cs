using PostgresCommandExecuter.Favorites;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace PostgresCommandExecuter
{
    public partial class FavoriteEditorWindow : Window
    {
        private static readonly Regex ValidId = new Regex("^[a-zA-Z][a-zA-Z0-9_-]*$", RegexOptions.Compiled);

        public FavoriteEditorWindow(FavoriteQuery source)
        {
            InitializeComponent();
            if (source == null) throw new ArgumentNullException("source");

            IsEditing = !string.IsNullOrWhiteSpace(source.Id);
            OriginalId = source.Id;
            DialogTitle.Text = IsEditing ? "Editar consulta favorita" : "Adicionar consulta favorita";
            IdTextBox.Text = source.Id ?? "";
            CategoryTextBox.Text = source.Category ?? "geral";
            PurposeTextBox.Text = source.Purpose ?? "";
            ParametersTextBox.Text = source.Parameters == null ? "" : string.Join(", ", source.Parameters);
            NoteTextBox.Text = source.Note ?? "";
            RequiredFunctionTextBox.Text = source.RequiredFunction ?? "";
            SqlTextBox.Text = source.Sql ?? "";
            SelectComboValue(SectionComboBox, source.Section ?? "consultas");
            SelectComboValue(TypeComboBox, source.Type ?? "leitura");
            SelectComboValue(RiskComboBox, source.Risk ?? "baixo");
        }

        public bool IsEditing { get; private set; }
        public string OriginalId { get; private set; }
        public FavoriteQuery Result { get; private set; }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string id = IdTextBox.Text.Trim();
            if (!ValidId.IsMatch(id))
            {
                ValidationText.Text = "Use um id iniciado por letra, sem espaços.";
                IdTextBox.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(CategoryTextBox.Text) || string.IsNullOrWhiteSpace(PurposeTextBox.Text))
            {
                ValidationText.Text = "Categoria e finalidade são obrigatórias.";
                return;
            }
            if (string.IsNullOrWhiteSpace(SqlTextBox.Text))
            {
                ValidationText.Text = "Informe a consulta SQL.";
                SqlTextBox.Focus();
                return;
            }

            Result = new FavoriteQuery
            {
                Id = id,
                Section = ComboText(SectionComboBox),
                Category = CategoryTextBox.Text.Trim(),
                Type = ComboText(TypeComboBox),
                Risk = ComboText(RiskComboBox),
                Purpose = PurposeTextBox.Text.Trim(),
                Parameters = ParseParameters(ParametersTextBox.Text),
                Note = NullIfWhiteSpace(NoteTextBox.Text),
                RequiredFunction = NullIfWhiteSpace(RequiredFunctionTextBox.Text),
                Sql = SqlTextBox.Text.TrimEnd() + Environment.NewLine
            };
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private static string ComboText(ComboBox comboBox)
        {
            var item = comboBox.SelectedItem as ComboBoxItem;
            return item == null ? "" : Convert.ToString(item.Content);
        }

        private static void SelectComboValue(ComboBox comboBox, string value)
        {
            foreach (ComboBoxItem item in comboBox.Items)
            {
                if (string.Equals(Convert.ToString(item.Content), value, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }
        }

        private static List<string> ParseParameters(string value)
        {
            var values = (value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return values.Count == 0 ? null : values;
        }

        private static string NullIfWhiteSpace(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
