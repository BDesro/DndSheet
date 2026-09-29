using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Hearthsheet.App.ViewModels;

namespace Hearthsheet.App.Views;

public partial class SheetView
{
    public SheetView() => InitializeComponent();

    private MainViewModel? Main => Window.GetWindow(this)?.DataContext as MainViewModel;

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || Main is not { } main) return;
        main.Zoom += e.Delta > 0 ? 0.1 : -0.1;
        e.Handled = true;
    }

    private void OnGoToPage(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FrameworkElement page }) return;
        var offset = page.TransformToAncestor(Pages).Transform(new Point(0, 0)).Y * (Main?.Zoom ?? 1);
        Scroller.ScrollToVerticalOffset(offset);
    }

    private void OnPrint(object sender, RoutedEventArgs e) => Print();

    /// <summary>
    /// Prints the live page visuals as one job. Pages are laid out at 8.5×11in (96 DPI), so they map
    /// 1:1 onto letter paper; they are scaled down to fit a smaller printable area when necessary.
    /// </summary>
    public void Print()
    {
        var dialog = new PrintDialog { UserPageRangeEnabled = false };
        if (dialog.ShowDialog() != true) return;

        Keyboard.ClearFocus(); // no focus underline or caret in the printout
        Border[] pages = [Page1, Page2, Page3];
        var effects = pages.Select(p => p.Effect).ToArray();
        var transforms = pages.Select(p => p.RenderTransform).ToArray();
        try
        {
            var writer = PrintQueue.CreateXpsDocumentWriter(dialog.PrintQueue);
            var collator = writer.CreateVisualsCollator();
            collator.BeginBatchWrite();
            foreach (var page in pages)
            {
                page.Effect = null; // shadows would force rasterization of the whole page
                var scale = Math.Min(1.0, Math.Min(dialog.PrintableAreaWidth / page.ActualWidth, dialog.PrintableAreaHeight / page.ActualHeight));
                page.RenderTransform = new ScaleTransform(scale, scale);
                page.UpdateLayout();
                collator.Write(page);
            }
            collator.EndBatchWrite();
        }
        finally
        {
            for (var i = 0; i < pages.Length; i++)
            {
                pages[i].Effect = effects[i] as Effect;
                pages[i].RenderTransform = transforms[i];
            }
        }
    }
}
