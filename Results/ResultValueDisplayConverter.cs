using System;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows.Data;

namespace PostgresCommandExecuter.Results
{
    /// <summary>Representação consistente da grade sem alterar o valor armazenado.</summary>
    internal sealed class ResultValueDisplayConverter : IValueConverter
    {
        internal static readonly ResultValueDisplayConverter Instance = new ResultValueDisplayConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || value == DBNull.Value) return "NULL";
            if (value is bool) return (bool)value ? "true" : "false";
            var address = value as IPAddress;
            if (address != null) return address.ToString();
            var mac = value as PhysicalAddress;
            if (mac != null) return mac.ToString();
            var bytes = value as byte[];
            if (bytes != null) return System.Convert.ToBase64String(bytes);
            var formattable = value as IFormattable;
            return formattable == null ? value.ToString() : formattable.ToString(null, culture ?? CultureInfo.CurrentCulture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
