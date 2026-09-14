using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace PostgresCommandExecuter.Results
{
    /// <summary>Dados imutáveis da apresentação de uma coluna materializada.</summary>
    internal sealed class ResultColumnPresentation
    {
        internal ResultColumnPresentation(string propertyName, string semanticTypeName, string structure)
        {
            PropertyName = propertyName ?? string.Empty;
            SemanticTypeName = semanticTypeName ?? string.Empty;
            Structure = structure ?? string.Empty;
        }

        internal string PropertyName { get; private set; }
        internal string SemanticTypeName { get; private set; }
        internal string Structure { get; private set; }
    }

    /// <summary>
    /// Estilos de coluna e converter leves. A cor é calculada só para células
    /// materializadas pelo DataGrid, preservando virtualização e rolagem fluida.
    /// </summary>
    internal static class ResultGridStyleFactory
    {
        public static ResultBrushPalette CreatePalette(Func<string, Brush> resolveBrush)
        {
            return new ResultBrushPalette(resolveBrush);
        }

        public static Style CreateTextStyle(ResultColumnPresentation presentation, Brush selectedBrush, ResultBrushPalette palette)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6, 0, 4, 0)));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, GetTextAlignment(presentation.SemanticTypeName, presentation.Structure)));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(presentation.PropertyName)
            {
                Converter = new ResultValueBrushConverter(presentation.SemanticTypeName, presentation.Structure, palette)
            }));

            var selected = new DataTrigger
            {
                Binding = new Binding("IsSelected")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1)
                },
                Value = true
            };
            selected.Setters.Add(new Setter(TextBlock.ForegroundProperty, selectedBrush));
            style.Triggers.Add(selected);
            return style;
        }

        public static Style CreateCheckBoxStyle(Brush booleanBrush, Brush selectedBrush)
        {
            var style = new Style(typeof(CheckBox));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Control.ForegroundProperty, booleanBrush));

            var selected = new DataTrigger
            {
                Binding = new Binding("IsSelected")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1)
                },
                Value = true
            };
            selected.Setters.Add(new Setter(Control.ForegroundProperty, selectedBrush));
            style.Triggers.Add(selected);
            return style;
        }

        public static string GetBrushResourceKey(PostgreSqlValueSemantic semantic)
        {
            switch (semantic)
            {
                case PostgreSqlValueSemantic.Null: return "GridNullValueBrush";
                case PostgreSqlValueSemantic.BooleanTrue: return "GridBooleanTrueValueBrush";
                case PostgreSqlValueSemantic.BooleanFalse: return "GridBooleanFalseValueBrush";
                case PostgreSqlValueSemantic.Integer: return "GridIntegerValueBrush";
                case PostgreSqlValueSemantic.PositiveNumber: return "GridPositiveNumberValueBrush";
                case PostgreSqlValueSemantic.NegativeNumber: return "GridNegativeNumberValueBrush";
                case PostgreSqlValueSemantic.ZeroNumber: return "GridZeroNumberValueBrush";
                case PostgreSqlValueSemantic.NonFiniteNumber: return "GridNonFiniteNumberValueBrush";
                case PostgreSqlValueSemantic.Monetary: return "GridMoneyValueBrush";
                case PostgreSqlValueSemantic.Text: return "GridTextValueBrush";
                case PostgreSqlValueSemantic.Uuid: return "GridUuidValueBrush";
                case PostgreSqlValueSemantic.Date: case PostgreSqlValueSemantic.Time:
                case PostgreSqlValueSemantic.Timestamp: case PostgreSqlValueSemantic.Interval: return "GridTemporalValueBrush";
                case PostgreSqlValueSemantic.Binary: case PostgreSqlValueSemantic.BitString: return "GridBinaryValueBrush";
                case PostgreSqlValueSemantic.Json: return "GridJsonValueBrush";
                case PostgreSqlValueSemantic.Xml: return "GridXmlValueBrush";
                case PostgreSqlValueSemantic.Network: case PostgreSqlValueSemantic.MacAddress: return "GridNetworkValueBrush";
                case PostgreSqlValueSemantic.Geometric: return "GridGeometricValueBrush";
                case PostgreSqlValueSemantic.FullText: return "GridFullTextValueBrush";
                case PostgreSqlValueSemantic.Array: return "GridCollectionValueBrush";
                case PostgreSqlValueSemantic.Range: case PostgreSqlValueSemantic.Multirange: return "GridRangeValueBrush";
                case PostgreSqlValueSemantic.Enum: return "GridEnumValueBrush";
                case PostgreSqlValueSemantic.Composite: return "GridCompositeValueBrush";
                case PostgreSqlValueSemantic.Custom: return "GridCustomValueBrush";
                default: return "GridUnknownValueBrush";
            }
        }

        private static TextAlignment GetTextAlignment(string typeName, string structure)
        {
            PostgreSqlValueSemantic semantic = PostgreSqlValueSemantics.Classify(typeName, 1, structure);
            return semantic == PostgreSqlValueSemantic.Integer || semantic == PostgreSqlValueSemantic.PositiveNumber ||
                   semantic == PostgreSqlValueSemantic.NegativeNumber || semantic == PostgreSqlValueSemantic.ZeroNumber ||
                   semantic == PostgreSqlValueSemantic.Monetary
                ? TextAlignment.Right : TextAlignment.Left;
        }

        private sealed class ResultValueBrushConverter : IValueConverter
        {
            private readonly string semanticTypeName;
            private readonly string structure;
            private readonly ResultBrushPalette palette;

            public ResultValueBrushConverter(string semanticTypeName, string structure, ResultBrushPalette palette)
            {
                this.semanticTypeName = semanticTypeName;
                this.structure = structure;
                this.palette = palette;
            }

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return palette.Resolve(PostgreSqlValueSemantics.Classify(semanticTypeName, value, structure));
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }
    }

    internal sealed class ResultBrushPalette
    {
        private readonly Dictionary<PostgreSqlValueSemantic, Brush> brushes = new Dictionary<PostgreSqlValueSemantic, Brush>();

        internal ResultBrushPalette(Func<string, Brush> resolveBrush)
        {
            foreach (PostgreSqlValueSemantic semantic in Enum.GetValues(typeof(PostgreSqlValueSemantic)))
                brushes[semantic] = resolveBrush(ResultGridStyleFactory.GetBrushResourceKey(semantic)) ?? Brushes.Gray;
        }

        internal Brush Resolve(PostgreSqlValueSemantic semantic)
        {
            Brush brush;
            return brushes.TryGetValue(semantic, out brush) ? brush : Brushes.Gray;
        }
    }
}
