using ICSharpCode.AvalonEdit.Document;
using PostgresCommandExecuter.Editors;
using PostgresCommandExecuter.Results;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PostgresCommandExecuter
{
    public partial class CellValueWindow : Window
    {
        private const int MaximumLiveAnalysisLength = 350000;
        private Rect restoredBounds;
        private bool isWorkspaceExpanded;
        private readonly CellValueMetadata metadata;
        private readonly TokenColorizer colorizer;
        private readonly DispatcherTimer analysisTimer;

        public CellValueWindow(string value)
            : this(value, new CellValueMetadata(string.Empty, string.Empty, string.Empty))
        {
        }

        public CellValueWindow(string value, string columnName, string typeName, string structure)
            : this(value, new CellValueMetadata(columnName, typeName, structure))
        {
        }

        public CellValueWindow(string value, CellValueMetadata metadata)
        {
            this.metadata = metadata ?? new CellValueMetadata(string.Empty, string.Empty, string.Empty);
            InitializeComponent();

            colorizer = new TokenColorizer(ResolveSyntaxBrush);
            CellValueEditor.TextArea.TextView.LineTransformers.Add(colorizer);
            ConfigureEditor();

            analysisTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            analysisTimer.Tick += delegate
            {
                analysisTimer.Stop();
                RefreshPresentation();
            };

            string initialStatus;
            CellValueEditor.Text = CellValuePresentation.FormatInitialValue(value, this.metadata, out initialStatus);
            ConfigureMetadata(initialStatus);
            RefreshPresentation();
        }

        public string ValueText
        {
            get { return CellValueEditor == null ? string.Empty : CellValueEditor.Text; }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CellValueEditor.Focus();
            CellValueEditor.SelectAll();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            analysisTimer.Stop();
            CellValueEditor.TextArea.TextView.LineTransformers.Remove(colorizer);
        }

        private void CellValueEditor_TextChanged(object sender, EventArgs e)
        {
            if (analysisTimer == null) return;
            analysisTimer.Stop();
            analysisTimer.Start();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                DialogResult = true;
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        }

        private void ToggleWorkspace_Click(object sender, RoutedEventArgs e)
        {
            if (isWorkspaceExpanded)
                RestorePreviousBounds();
            else
                ExpandToWorkspace();
        }

        private void ExpandToWorkspace()
        {
            restoredBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
            Rect workArea = SystemParameters.WorkArea;
            const double inset = 12;

            WindowState = WindowState.Normal;
            Left = workArea.Left + inset;
            Top = workArea.Top + inset;
            Width = Math.Max(MinWidth, workArea.Width - (inset * 2));
            Height = Math.Max(MinHeight, workArea.Height - (inset * 2));
            isWorkspaceExpanded = true;
            ToggleWorkspaceButton.Content = "↙";
            ToggleWorkspaceButton.ToolTip = "Restaurar tamanho anterior";
        }

        private void RestorePreviousBounds()
        {
            if (restoredBounds.Width > 0 && restoredBounds.Height > 0)
            {
                WindowState = WindowState.Normal;
                Left = restoredBounds.Left;
                Top = restoredBounds.Top;
                Width = restoredBounds.Width;
                Height = restoredBounds.Height;
            }

            isWorkspaceExpanded = false;
            ToggleWorkspaceButton.Content = "↗";
            ToggleWorkspaceButton.ToolTip = "Ocupar a área útil da tela";
        }

        private void ConfigureEditor()
        {
            CellValueEditor.Options.ConvertTabsToSpaces = true;
            CellValueEditor.Options.IndentationSize = 2;
            CellValueEditor.Options.EnableHyperlinks = false;
            CellValueEditor.Options.EnableEmailHyperlinks = false;

            Brush primary = FindResource("TextPrimaryBrush") as Brush;
            Brush secondary = FindResource("TextSecondaryBrush") as Brush;
            Brush selection = FindResource("SelectionBrush") as Brush;
            CellValueEditor.TextArea.SelectionBrush = selection;
            CellValueEditor.TextArea.SelectionForeground = primary;
            CellValueEditor.TextArea.Caret.CaretBrush = primary;
            CellValueEditor.LineNumbersForeground = secondary;
        }

        private void ConfigureMetadata(string initialStatus)
        {
            string column = string.IsNullOrWhiteSpace(metadata.ColumnName) ? "Valor da célula" : metadata.ColumnName;
            string type = string.IsNullOrWhiteSpace(metadata.TypeName) ? "texto" : metadata.TypeName;
            ColumnNameText.Text = column;
            TypeMetadataText.Text = string.IsNullOrWhiteSpace(metadata.Structure)
                ? type
                : type + "  •  " + metadata.Structure;
            PresentationStatusText.Text = initialStatus ?? string.Empty;
        }

        private void RefreshPresentation()
        {
            string text = CellValueEditor.Text ?? string.Empty;
            if (text.Length > MaximumLiveAnalysisLength)
            {
                colorizer.SetSpans(new List<TokenStyleSpan>());
                PresentationStatusText.Text = "Análise pausada para um valor muito grande.";
                CellValueEditor.TextArea.TextView.Redraw();
                return;
            }

            CellValuePresentationResult result = CellValuePresentation.Analyze(text, metadata);
            colorizer.SetSpans(result.Spans);
            PresentationStatusText.Text = result.Status;
            CellValueEditor.TextArea.TextView.Redraw();
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
    }
}
