using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace AI_IT_Company.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var empty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        if (parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
