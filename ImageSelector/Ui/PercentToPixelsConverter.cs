using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ImageSelector.Ui;

/// <summary>Sliderの倍率（50～300%）をサムネイル表示寸法へ変換。</summary>
public sealed class PercentToPixelsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double percent || parameter is not string text)
            return DependencyProperty.UnsetValue;

        // "基本サイズ,固定余白"。例: "82,48"。
        var numbers = text.Split(',');
        if (!double.TryParse(numbers[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var basePixels))
            return DependencyProperty.UnsetValue;

        var margin = 0.0;
        if (numbers.Length > 1 &&
            !double.TryParse(numbers[1], NumberStyles.Float, CultureInfo.InvariantCulture, out margin))
            return DependencyProperty.UnsetValue;

        return Math.Max(1.0, basePixels * percent / 100.0 + margin);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
