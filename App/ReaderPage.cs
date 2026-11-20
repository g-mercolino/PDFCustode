using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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

    private readonly string licensePath;
    private readonly Action backToLibrary;
    private Action? clearReader;
    private readonly View activationForm;
    private readonly IDispatcherTimer expirationTimer;
    private DateTimeOffset? readerExpiresAt;
    private readonly Entry endpoint = new() { Placeholder = "https://licenze.example.it", Keyboard = Keyboard.Url };
    private readonly Entry firstName = new() { Placeholder = "Il tuo nome", MaxLength = 100, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
    private readonly Entry lastName = new() { Placeholder = "Il tuo cognome", MaxLength = 100, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
    private readonly Entry userCode = new() { Placeholder = "Codice utente", MaxLength = 128, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
    private readonly Entry code = new() { Placeholder = "Codice di attivazione", IsPassword = true, IsSpellCheckEnabled = false };
    private readonly Label status = new() { TextColor = Color.FromArgb("#A33D37"), LineBreakMode = LineBreakMode.WordWrap };
    private readonly Button activate = new() { Text = "Attiva su questo dispositivo", IsEnabled = false };
    private EncryptedDocument? document;
    private string? authority;
    private bool initialized;

    public ReaderPage(DocumentPackage package, string folder, Action backToLibrary)
    {
        document = package.Document;
        authority = package.Authority;
        licensePath = Path.Combine(folder, "license.json");
        this.backToLibrary = backToLibrary;
        expirationTimer = Dispatcher.CreateTimer();
        expirationTimer.Interval = TimeSpan.FromSeconds(1);
        expirationTimer.Tick += (_, _) => CheckExpiration();
        BackgroundColor = Color.FromArgb("#EEF4F4");
        var ink = Color.FromArgb("#122834");
        var muted = Color.FromArgb("#46616A");
        foreach (var field in new[] { endpoint, firstName, lastName, userCode, code })
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
                new Label { Text = "Inserisci i tuoi dati, l’indirizzo del servizio e il codice di attivazione ricevuto dal proprietario del PDF. Tutti i campi sono obbligatori. La licenza dura 30 giorni dall’attivazione e consente la lettura offline entro la scadenza.", TextColor = muted },
                new Label { Text = "Nome", FontAttributes = FontAttributes.Bold, TextColor = ink },
                firstName,
                new Label { Text = "Cognome", FontAttributes = FontAttributes.Bold, TextColor = ink },
                lastName,
                new Label { Text = "Codice utente", FontAttributes = FontAttributes.Bold, TextColor = ink },
                userCode,
                new Label { Text = "Indirizzo del servizio", FontAttributes = FontAttributes.Bold, TextColor = ink },
                endpoint,
#if ALLOW_INSECURE_TEST_SERVER
                new Label { Text = "Collaudo locale: HTTP consentito solo sulla rete privata.", FontSize = 12, TextColor = muted },
#endif
                new Label { Text = "Codice di attivazione", FontAttributes = FontAttributes.Bold, TextColor = ink },
                code,
                new Label { Text = "Il nome del dispositivo e la piattaforma vengono rilevati automaticamente.", FontSize = 12, TextColor = muted },
                activate,
                status
            }
        };
        activationForm = new ScrollView
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
        SetDocumentContent(activationForm);
        activate.Clicked += Activate;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (initialized)
        {
            CheckExpiration();
            if (readerExpiresAt is not null) expirationTimer.Start();
            return;
        }
        initialized = true;
        status.Text = document!.Title;
        activate.IsEnabled = true;
        string saved = File.Exists(licensePath) ? licensePath : Path.Combine(FileSystem.AppDataDirectory, "license.json");
        if (!File.Exists(saved))
            return;
        try
        {
            var license = JsonSerializer.Deserialize(await File.ReadAllTextAsync(saved), ReaderJson.Default.SignedLicense)
                ?? throw new InvalidDataException();
            using var device = new DeviceKey();
            var bytes = Decrypt(license, device);
            try
            {
                if (saved != licensePath) await File.WriteAllTextAsync(licensePath, JsonSerializer.Serialize(license, ReaderJson.Default.SignedLicense));
                OpenReader(bytes);
            }
            catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        }
        catch (LicenseExpiredException error)
        {
            status.Text = error.Message;
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
            status.Text = File.Exists(licensePath) ? "La licenza salvata non è valida per questo dispositivo. Contatta il proprietario del documento." : document.Title;
        }
    }

    private async void Activate(object? sender, EventArgs args)
    {
        var nome = NormalizeProfileValue(firstName.Text, 100);
        if (nome is null)
        {
            status.Text = "Inserisci un nome valido (massimo 100 caratteri).";
            firstName.Focus();
            return;
        }
        var cognome = NormalizeProfileValue(lastName.Text, 100);
        if (cognome is null)
        {
            status.Text = "Inserisci un cognome valido (massimo 100 caratteri).";
            lastName.Focus();
            return;
        }
        var codiceUtente = NormalizeProfileValue(userCode.Text, 128);
        if (codiceUtente is null)
        {
            status.Text = "Inserisci un codice utente valido (massimo 128 caratteri).";
            userCode.Focus();
            return;
        }
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
            var request = new ActivationRequest(code.Text.Trim(), document!.DocumentId, device.PublicKey,
                Nome: nome, Cognome: cognome, CodiceUtente: codiceUtente,
                NomeMacchina: GetMachineName(), Piattaforma: GetPlatform());
            using var response = await Client.PostAsJsonAsync(new Uri(service, "/api/activate"), request, ReaderJson.Default.ActivationRequest);
            if (!response.IsSuccessStatusCode)
            {
                status.Text = response.StatusCode switch
                {
                    HttpStatusCode.Conflict => "Questo codice è già associato a un altro dispositivo.",
                    HttpStatusCode.BadRequest => "I dati di attivazione non sono validi. Controlla nome, cognome e codice utente.",
                    HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
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
                firstName.Text = string.Empty;
                lastName.Text = string.Empty;
                userCode.Text = string.Empty;
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
        catch (LicenseExpiredException error)
        {
            status.Text = error.Message;
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

    private static string? NormalizeProfileValue(string? value, int maximumLength)
    {
        if (value is null || value.Any(char.IsControl)) return null;
        string normalized;
        try { normalized = value.Trim().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return null; }
        return normalized.Length > 0 && normalized.Length <= maximumLength ? normalized : null;
    }

    private static string NormalizeMachineName(string? value)
    {
        string normalized;
        try
        {
            normalized = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).ToArray())
                .Trim().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException) { return string.Empty; }
        if (normalized.Length <= 255) return normalized;
        normalized = normalized[..255];
        return char.IsHighSurrogate(normalized[^1]) ? normalized[..^1] : normalized;
    }

    private static string GetMachineName()
    {
#if WINDOWS
        string name = NormalizeMachineName(Environment.MachineName);
        return string.IsNullOrWhiteSpace(name) ? "PC Windows" : name;
#else
        string name = NormalizeMachineName(DeviceInfo.Current.Name);
        if (string.IsNullOrWhiteSpace(name)) name = NormalizeMachineName(DeviceInfo.Current.Model);
        return string.IsNullOrWhiteSpace(name) ? "Dispositivo Android" : name;
#endif
    }

    private static string GetPlatform()
    {
#if WINDOWS
        return "Windows";
#else
        return "Android";
#endif
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
        readerExpiresAt = Crypto.GetExpiration(verified);
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
        if (DateTimeOffset.UtcNow >= readerExpiresAt) throw new LicenseExpiredException();
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
        bool closed = false;
        clearReader = () => { closed = true; CryptographicOperations.ZeroMemory(bytes); };
        view.RawMessageReceived += (_, message) =>
        {
            if (message.Message == "home")
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (!closed) ReturnHome();
                });
                return;
            }
            if (message.Message != "next")
                return;

            // Android's JavaScript bridge can call back outside the UI thread.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (closed || offset > bytes.Length)
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
        expirationTimer.Start();
    }

    private void SetDocumentContent(View view)
    {
        if (view.Parent is Layout parent) parent.Remove(view);
        var back = new Button { Text = "← Home", BackgroundColor = Color.FromArgb("#122834"), TextColor = Colors.White, HorizontalOptions = LayoutOptions.Start, Padding = new Thickness(14, 6), MinimumHeightRequest = 36, CornerRadius = 8, Margin = new Thickness(12, 8) };
        back.Clicked += (_, _) => ReturnHome();
        var layout = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        layout.Add(back, 0, 0);
        layout.Add(view, 0, 1);
        Content = layout;
    }

    private void ReturnHome()
    {
        if (!activate.IsEnabled) return;
        expirationTimer.Stop();
        clearReader?.Invoke();
        clearReader = null;
        backToLibrary();
    }

    protected override void OnDisappearing()
    {
        expirationTimer.Stop();
        base.OnDisappearing();
    }

    private void CheckExpiration()
    {
        if (readerExpiresAt is null || DateTimeOffset.UtcNow < readerExpiresAt) return;
        expirationTimer.Stop();
        clearReader?.Invoke();
        clearReader = null;
        readerExpiresAt = null;
        SetDocumentContent(activationForm);
        status.Text = "La licenza di 30 giorni è scaduta. Richiedi un nuovo codice di attivazione.";
    }
}




