using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
namespace SteamRipApp.Core
{
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b) return b ? Visibility.Visible : Visibility.Collapsed;
            return Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
    public class InvertedBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b) return !b ? Visibility.Visible : Visibility.Collapsed;
            return Visibility.Visible;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
    public class SafeUriConverter : IValueConverter
    {
        private const string PlaceholderImage = "https://steamrip.com/wp-content/uploads/2021/06/Site-logo3.png";
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string? url = value as string;
            if (string.IsNullOrWhiteSpace(url))
            {
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(PlaceholderImage));
            }
            try {
                if (!url.StartsWith("http"))
                {
                    if (!System.IO.File.Exists(url))
                    {
                        Logger.Log($"[SafeUriConverter] Found local path but file is MISSING physically on disc: {url}. Falling back to placeholder.");
                        return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(PlaceholderImage));
                    }
                    else
                    {
                        Logger.Log($"[SafeUriConverter] Successfully routed valid Local Physical File Path Binding: {url}");
                        var localBitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        localBitmap.CreateOptions = Microsoft.UI.Xaml.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
                        localBitmap.UriSource = new Uri(url, UriKind.Absolute);
                        return localBitmap;
                    }
                }
                Logger.Log($"[SafeUriConverter] Resolved Web URL: {url}");
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url, UriKind.Absolute));
            } catch (Exception ex) {
                Logger.LogError($"[SafeUriConverter] Engine parse failed for string -> {url}.", ex);
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(PlaceholderImage));
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
    public class PhaseToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var phase = value as string ?? "";
            var color = phase switch
            {
                "Downloading" => Microsoft.UI.Colors.SteelBlue,
                "Extracting"  => Microsoft.UI.Colors.DarkOrange,
                "Done"        => Microsoft.UI.Colors.SeaGreen,
                "Failed"      => Microsoft.UI.Colors.Crimson,
                _             => Microsoft.UI.Colors.Gray
            };
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }
    public class RunningToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isRunning && isRunning)
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
            return Application.Current.Resources["AccentFillColorDefaultBrush"];
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }
    public class RunningToStopWarningConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isRunning && isRunning)
                return "STOP: May Cause Data Loss. Use at own risk.";
            return "Launch the game";
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }
}

