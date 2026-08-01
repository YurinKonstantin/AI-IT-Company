using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace AI_IT_Company.Converters
{
    public sealed class BoolNegationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is bool b ? !b : true;
        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => value is bool b ? !b : false;
    }
}
