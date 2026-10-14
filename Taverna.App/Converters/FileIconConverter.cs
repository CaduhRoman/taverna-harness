using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Taverna.App.Converters;

/// <summary>Maps an icon key (a FileKind name, or FolderOpen) to its "Icon.*" geometry resource.</summary>
public sealed class FileIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var app = Application.Current;
        if (app is null) return null;
        if (app.TryGetResource($"Icon.{value}", app.ActualThemeVariant, out var icon) && icon is Geometry) return icon;
        return app.TryGetResource("Icon.File", app.ActualThemeVariant, out icon) ? icon : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
