using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using MainCore.UI.ViewModels.Tabs.Villages;

namespace WPFUI.Converter
{
    public class ForecastStatusToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush InfoBrush = CreateBrush("#FFB0BEC5");
        private static readonly SolidColorBrush ReadyBrush = CreateBrush("#FF4CAF50");
        private static readonly SolidColorBrush WaitingBrush = CreateBrush("#FFFFC107");
        private static readonly SolidColorBrush BlockedBrush = CreateBrush("#FFF4511E");
        private static readonly SolidColorBrush ErrorBrush = CreateBrush("#FFD32F2F");

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is BuildForecastStatus status)
            {
                return status switch
                {
                    BuildForecastStatus.Ready => ReadyBrush,
                    BuildForecastStatus.Waiting => WaitingBrush,
                    BuildForecastStatus.Blocked => BlockedBrush,
                    BuildForecastStatus.Error => ErrorBrush,
                    _ => InfoBrush,
                };
            }

            return InfoBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

        private static SolidColorBrush CreateBrush(string hex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex)!;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
