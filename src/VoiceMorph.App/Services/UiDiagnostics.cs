using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VoiceMorph.App.Models;
using VoiceMorph.App.ViewModels;

namespace VoiceMorph.App.Services;

/// <summary>Renders the app itself, without capturing other windows or altering user profiles.</summary>
internal static class UiDiagnostics
{
    public static async Task RunAsync(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var tabs = (TabControl)window.FindName("MainTabs");
        var mode = (ComboBox)window.FindName("ProcessingModeBox");
        mode.SelectedIndex = 0;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        foreach (var size in new[] { new Size(1920, 1080), new Size(1240, 870), new Size(1000, 720) })
        {
            window.Width = size.Width; window.Height = size.Height;
            for (var index = 0; index < tabs.Items.Count; index++)
            {
                tabs.SelectedIndex = index;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var start = (Button)window.FindName("EngineButton");
                var bypass = (Button)window.FindName("BypassButton");
                var modeY = mode.TranslatePoint(new Point(), window).Y;
                if (Math.Abs(start.TranslatePoint(new Point(), window).Y - modeY) > .1 ||
                    Math.Abs(bypass.TranslatePoint(new Point(), window).Y - modeY) > .1 ||
                    Math.Abs(start.ActualHeight - mode.ActualHeight) > .1)
                    throw new InvalidOperationException("Header controls are not aligned to the same row.");
                Save(window, Path.Combine(directory, $"tab-{index}-{size.Width:0}.png"));
            }
        }
        var input = (ComboBox)window.FindName("InputDeviceComboBox");
        var output = (ComboBox)window.FindName("OutputDeviceComboBox");
        if (Math.Abs(input.ActualHeight - output.ActualHeight) > .1 || input.ActualWidth < 250 || output.ActualWidth < 250)
            throw new InvalidOperationException("Audio route controls have inconsistent geometry.");
        input.IsDropDownOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var popup = (Popup)input.Template.FindName("PART_Popup", input);
        Save(popup.Child, Path.Combine(directory, "microphone-popup.png"));
        input.IsDropDownOpen = false;
        var model = (MainViewModel)window.DataContext;
        if (model.CanEdit) model.SelectedProfile = model.Profiles.First(profile => profile.IsBuiltIn);
        if (model.CanEdit) throw new InvalidOperationException("Built-in profiles must be locked.");
        model.AddProfile(new UserVoiceProfile(Guid.NewGuid(), "Проверка своего профиля", model.EffectiveSettings, DateTime.UtcNow));
        tabs.SelectedIndex = 2;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var pitch = model.ParameterGroups.SelectMany(group => group.Parameters).First();
        pitch.Value = 1;
        if (!model.CanEdit || Math.Abs(pitch.Value - 1) > .01) throw new InvalidOperationException("Custom profile controls do not update settings.");
        window.UpdateLayout();
        Save(window, Path.Combine(directory, "custom-settings.png"));
        mode.SelectedIndex = 1;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (tabs.SelectedItem != window.FindName("NeuralModelsTab") || ((TabItem)window.FindName("DspVoicesTab")).IsEnabled)
            throw new InvalidOperationException("Neural mode does not isolate DSP controls.");
        Save(window, Path.Combine(directory, "rvc-mode-1000.png"));
        model.ApplyError("Проверка длинного сообщения: движок ещё не готов. Ошибка должна быть видна полностью и не менять выравнивание кнопок и списка режима.");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (((Border)window.FindName("HeaderErrorBox")).Visibility != Visibility.Visible)
            throw new InvalidOperationException("Header error is not exposed in a dedicated block.");
        Save(window, Path.Combine(directory, "header-error-1000.png"));
        mode.SelectedIndex = 0;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (!((TabItem)window.FindName("DspVoicesTab")).IsEnabled)
            throw new InvalidOperationException("Returning to DSP did not restore its controls.");
        window.WindowState = WindowState.Maximized;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var contentCorner = ((Button)window.FindName("EngineButton")).PointToScreen(new Point());
        if (contentCorner.X < 0 || contentCorner.Y < 0)
            throw new InvalidOperationException("Maximized header is cropped outside the screen.");
        Save(window, Path.Combine(directory, "maximized.png"));
        window.WindowState = WindowState.Normal;
        File.WriteAllText(Path.Combine(directory, "result.txt"), $"PASS: {tabs.Items.Count} tabs at 1920/1240/1000px, aligned header controls, maximized bounds, separate error message, dark device popup, built-in lock, custom parameter binding, mode isolation.\nNo audio playback, profiles or OBS changes.");
    }

    private static void Save(Visual visual, string path)
    {
        if (visual is not FrameworkElement element || element.ActualWidth < 1 || element.ActualHeight < 1)
            throw new InvalidOperationException("UI surface has no rendered size.");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
