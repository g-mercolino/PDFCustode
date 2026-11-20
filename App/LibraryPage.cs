using PdfCustode.Core;

namespace PdfCustode.App;

internal sealed class LibraryPage : ContentPage
{
    private readonly DocumentLibrary library = new(Path.Combine(FileSystem.AppDataDirectory, "library"));
    private bool opening;

    public LibraryPage()
    {
        BackgroundColor = Color.FromArgb("#EEF4F4");
        RefreshLibrary();
    }

    private void RefreshLibrary()
    {
        var import = new Button { Text = "Apri documento", BackgroundColor = Color.FromArgb("#126157"), TextColor = Colors.White, HorizontalOptions = LayoutOptions.Start, Padding = new Thickness(20, 8), CornerRadius = 10, FontSize = 14 };
        import.Clicked += async (_, _) =>
        {
            if (opening) return;
            opening = true;
            try
            {
                var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Scegli un file .pdfcustode" });
                if (file is null) return;
                if (!file.FileName.EndsWith(".pdfcustode", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Seleziona un file .pdfcustode.");
                await using var source = await file.OpenReadAsync();
                await OpenDocument(await library.ImportAsync(source));
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine(error);
                await DisplayAlertAsync("Importazione", "Impossibile importare il documento. Seleziona un file .pdfcustode valido.", "OK");
            }
            finally { opening = false; }
        };
        var list = new VerticalStackLayout { Spacing = 8, MaximumWidthRequest = 640, HorizontalOptions = LayoutOptions.Fill };
        list.Add(new Label { Text = "Home", FontSize = 28, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#122834"), Margin = new Thickness(0, 0, 0, 8) });
        list.Add(import);
        foreach (var item in library.Documents())
        {
            var button = new Button { Text = item.Title, BackgroundColor = Colors.White, TextColor = Color.FromArgb("#122834"), FontSize = 14, Padding = new Thickness(14, 6), MinimumHeightRequest = 44, CornerRadius = 10, BorderColor = Color.FromArgb("#CCDCDD"), BorderWidth = 1 };
            button.Clicked += async (_, _) =>
            {
                if (opening) return;
                opening = true;
                try { await OpenDocument(item); }
                catch (Exception error)
                {
                    System.Diagnostics.Debug.WriteLine(error);
                    await DisplayAlertAsync("Documento", "Impossibile aprire il documento salvato.", "OK");
                }
                finally { opening = false; }
            };
            list.Add(button);
        }
        Content = new ScrollView { Content = list, Padding = 24, MaximumWidthRequest = 688, HorizontalOptions = LayoutOptions.Start };
    }

    private async Task OpenDocument(LibraryDocument item)
    {
        var package = await DocumentLibrary.ReadAsync(item.Folder);
        var window = Window ?? throw new InvalidOperationException("Finestra non disponibile.");
        window.Page = new ReaderPage(package, item.Folder, () =>
        {
            RefreshLibrary();
            window.Page = this;
        });
    }
}
