using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace erp_inteligente.Views.Converters;

/// <summary>Resolves a navigation icon key (e.g. "Stock") to the "Icon.Stock" geometry in Styles/Icons.axaml.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || Application.Current is not { } app)
            return null;
        return app.TryFindResource("Icon." + key, app.ActualThemeVariant, out var resource) ? resource as Geometry : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
