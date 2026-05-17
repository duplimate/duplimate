using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Duplimate.ViewModels;

namespace Duplimate.Views;

/// <summary>
/// RadioButton.IsChecked is a bool, but our selected-nav state is an enum.
/// Two-way convert so a radio toggles the enum, and the enum lights the correct radio.
/// </summary>
public static class NavConverters
{
    public static readonly IValueConverter IsSelected = new IsSelectedConverter();

    private sealed class IsSelectedConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is NavItem current && parameter is NavItem want)
                return current == want;
            return false;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isChecked && isChecked && parameter is NavItem want)
                return want;
            return Avalonia.Data.BindingOperations.DoNothing;
        }
    }
}
