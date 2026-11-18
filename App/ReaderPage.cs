using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfCustode.Core;

namespace PdfCustode.App;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(EncryptedDocument))]
[JsonSerializable(typeof(ActivationRequest))]
[JsonSerializable(typeof(SignedLicense))]
internal partial class ReaderJson : JsonSerializerContext;

internal sealed class ReaderPage : ContentPage
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly string licensePath = Path.Combine(FileSystem.AppDataDirectory, "license.json");
    private readonly Entry endpoint = new() { Placeholder = "https://licenze.example.it", Keyboard = Keyboard.Url };
    private readonly Entry code = new() { Placeholder = "Codice di attivazione", IsPassword = true, IsSpellCheckEnabled = false };
    private readonly Label status = new() { TextColor = Color.FromArgb("#A33D37"), LineBreakMode = LineBreakMode.WordWrap };
    private readonly Button activate = new() { Text = "Attiva su questo dispositivo", IsEnabled = false };
    private EncryptedDocument? document;
    private string? authority;
    private bool initialized;

    public ReaderPage()
    {
        BackgroundColor = Color.FromArgb("#EEF4F4");
        var ink = Color.FromArgb("#122834");
        var muted = Color.FromArgb("#46616A");
        foreach (var field in new[] { endpoint, code })
        {
            field.BackgroundColor = Color.FromArgb("#F3F8F8");
            field.TextColor = ink;
            field.PlaceholderColor = muted;
            field.MinimumHeightRequest = 48;
        }
        activate.BackgroundColor = Color.FromArgb("#126157");
        activate.TextColor = Colors.White;
        activate.MinimumHeightRequest = 48;
        activate.CornerRadius = 10;
#if ALLOW_INSECURE_TEST_SERVER && ANDROID
        endpoint.Text = "http://10.0.2.2:5187";
#elif ALLOW_INSECURE_TEST_SERVER
        endpoint.Text = "http://localhost:5187";
#endif
        var form = new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                new Label { Text = "PDF Custode", FontSize = 34, FontAttributes = FontAttributes.Bold, TextColor = ink },
                new Label { Text = "Attiva il documento", FontSize = 20, FontAttributes = FontAttributes.Bold, TextColor = ink },
                new Label { Text = "Inserisci l’indirizzo del servizio e il codice ricevuti dal proprietario del PDF. Dopo l’attivazione puoi leggere e cercare nel documento offline.", TextColor = muted },
                new Label { Text = "Indirizzo del servizio", FontAttributes = FontAttributes.Bold, TextColor = ink },
                endpoint,
#if ALLOW_INSECURE_TEST_SERVER
                new Label { Text = "Collaudo locale: HTTP consentito solo sulla rete privata.", FontSize = 12, TextColor = muted },
#endif
                new Label { Text = "Codice", FontAttributes = FontAttributes.Bold, TextColor = ink },
                code,
                activate,
                status
            }
        };
        Content = new ScrollView
        {
            Padding = new Thickness(20, 36),
            Content = new Border
            {
                BackgroundColor = Colors.White,
                Stroke = Color.FromArgb("#CCDCDD"),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
                MaximumWidthRequest = 520,
                VerticalOptions = LayoutOptions.Start,
                Padding = new Thickness(24, 26),
                Content = form
            }
        };
        activate.Clicked += Activate;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (initialized)
            return;
        initialized = true;
        try
        {
            await using var source = await FileSystem.OpenAppPackageFileAsync("document.json");
            document = await JsonSerializer.DeserializeAsync(source, ReaderJson.Default.EncryptedDocument);
            await using var pub = await FileSystem.OpenAppPackageFileAsync("authority.pub");
            using var text = new StreamReader(pub);
            authority = (await text.ReadToEndAsync()).Trim();
            if (document is null || authority.Length == 0)
                throw new InvalidDataException();
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
            status.Text = "Il documento incorporato non è disponibile.";
            return;
        }

        activate.IsEnabled = true;
        if (!File.Exists(licensePath))
            return;
        try
        {
            var license = JsonSerializer.Deserialize(await File.ReadAllTextAsync(licensePath), ReaderJson.Default.SignedLicense)
                ?? throw new InvalidDataException();
            using var device = new DeviceKey();
            OpenReader(Decrypt(license, device));
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
            status.Text = "La licenza salvata non è valida per questo dispositivo. Contatta il proprietario del documento.";
        }
    }

    private async void Activate(object? sender, EventArgs args)
    {
        if (string.IsNullOrWhiteSpace(code.Text))
        {
            status.Text = "Inserisci il codice di attivazione.";
            return;
        }
        if (!Uri.TryCreate(endpoint.Text?.Trim(), UriKind.Absolute, out var service)
            || !string.IsNullOrEmpty(service.UserInfo)
            || (service.Scheme != Uri.UriSchemeHttps && !IsLocalTestEndpoint(service)))
        {
            status.Text = "Inserisci l’indirizzo HTTPS del servizio.";
            return;
        }

        activate.IsEnabled = false;
        status.Text = "Attivazione in corso…";
        try
        {
            using var device = new DeviceKey();
            var request = new ActivationRequest(code.Text.Trim(), document!.DocumentId, device.PublicKey);
            using var response = await Client.PostAsJsonAsync(new Uri(service, "/api/activate"), request, ReaderJson.Default.ActivationRequest);
            if (!response.IsSuccessStatusCode)
            {
                status.Text = response.StatusCode switch
                {
                    HttpStatusCode.Conflict => "Questo codice è già associato a un altro dispositivo.",
                    HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                        => "Il servizio ha rifiutato il codice per questo documento.",
                    _ => "Il servizio di attivazione non è disponibile. Riprova."
                };
                return;
            }

            var license = await response.Content.ReadFromJsonAsync(ReaderJson.Default.SignedLicense)
                ?? throw new InvalidDataException();
            var bytes = Decrypt(license, device);
            try
            {
                var temporaryPath = licensePath + ".tmp";
                await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(license, ReaderJson.Default.SignedLicense));
                File.Move(temporaryPath, licensePath, overwrite: true);
                code.Text = string.Empty;
                OpenReader(bytes);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw;
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            status.Text = "Impossibile contattare il servizio. Controlla l’indirizzo e la connessione.";
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
            status.Text = "Impossibile verificare o salvare la licenza.";
        }
        finally
        {
            activate.IsEnabled = true;
        }
    }

    private static bool IsLocalTestEndpoint(Uri address)
    {
#if ALLOW_INSECURE_TEST_SERVER
        if (address.Scheme != Uri.UriSchemeHttp)
            return false;
        if (address.IsLoopback)
            return true;
        // A physical tablet can reach the local test server over the same private IPv4 network.
        if (!IPAddress.TryParse(address.Host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
#else
        return false;
#endif
    }

    private byte[] Decrypt(SignedLicense license, DeviceKey device)
    {
        var verified = Crypto.Verify(license, authority!, document!.DocumentId, device.PublicKey);
        var key = device.Unwrap(verified);
        try
        {
            return Crypto.Decrypt(document, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private void OpenReader(byte[] bytes)
    {
        var view = new HybridWebView { HybridRoot = "wwwroot", DefaultFile = "index.html" };
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window platformWindow)
            WindowsProtection.ExcludeFromCapture(WinRT.Interop.WindowNative.GetWindowHandle(platformWindow));
        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 web)
                web.CoreWebView2Initialized += (_, args) =>
                {
                    if (args.Exception is not null || web.CoreWebView2 is null)
                        return;
                    var core = web.CoreWebView2;
                    core.Settings.AreDevToolsEnabled = false;
                    core.Settings.AreDefaultContextMenusEnabled = false;
                    core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                    core.Settings.IsPasswordAutosaveEnabled = false;
                    core.DownloadStarting += (_, download) => download.Cancel = true;
                    core.NewWindowRequested += (_, popup) => popup.Handled = true;
                    core.NavigationStarting += (_, navigation) =>
                    {
                        if (!Uri.TryCreate(navigation.Uri, UriKind.Absolute, out var target)
                            || target.Scheme != Uri.UriSchemeHttps || target.Host != "0.0.0.1")
                            navigation.Cancel = true;
                    };
                };
        };
#endif
        const int chunkSize = 48 * 1024;
        int offset = 0;
        view.RawMessageReceived += (_, message) =>
        {
            if (message.Message != "next")
                return;

            // Android's JavaScript bridge can call back outside the UI thread.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (offset > bytes.Length)
                    return;
                if (offset == bytes.Length)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    offset++;
                    view.SendRawMessage("end");
                    return;
                }

                int length = Math.Min(chunkSize, bytes.Length - offset);
                string chunk = Convert.ToBase64String(bytes, offset, length);
                offset += length;
                view.SendRawMessage(chunk);
            });
        };
        Content = view;
    }
}




