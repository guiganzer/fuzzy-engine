using System;
using System.Windows;
using System.Windows.Input;

namespace PostgresCommandExecuter
{
    public partial class CellValueWindow : Window
    {
        private Rect restoredBounds;
        private bool isWorkspaceExpanded;

        public CellValueWindow(string value)
        {
            InitializeComponent();
            CellValueTextBox.Text = value ?? string.Empty;
        }

        public string ValueText
        {
            get { return CellValueTextBox == null ? string.Empty : CellValueTextBox.Text; }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CellValueTextBox.Focus();
            CellValueTextBox.SelectAll();
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
    }
}
