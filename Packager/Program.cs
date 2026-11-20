using System.Security.Cryptography;
using System.Text.Json;
using PdfCustode.Core;
using PdfCustode.Server;

if (!(args.Length == 3 && args[0] == "prepare") && !(args.Length == 2 && (args[0] == "issue" || args[0] == "export")))
{
    Console.Error.WriteLine("Uso: prepare <input.pdf> <directory-output>");
    Console.Error.WriteLine("Uso: issue <directory-server>");
    Console.Error.WriteLine("Uso: export <directory-client-esistente>");
    return 2;
}

try
{
    if (args[0] == "export")
    {
        string clientFolder = Path.GetFullPath(args[1]);
        var existing = JsonSerializer.Deserialize<EncryptedDocument>(File.ReadAllBytes(Path.Combine(clientFolder, "document.json")))
            ?? throw new InvalidDataException("Documento mancante.");
        var package = new DocumentPackage(1, existing, File.ReadAllText(Path.Combine(clientFolder, "authority.pub")).Trim());
        package.Validate();
        string path = Path.Combine(clientFolder, "documento.pdfcustode");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(package, PackageJson.Default.DocumentPackage));
        Console.WriteLine($"File da inviare al lettore: {path}");
        return 0;
    }
    if (args[0] == "issue")
    {
        using var store = new ActivationStore(Path.GetFullPath(args[1]));
        Console.WriteLine($"Codice di attivazione: {store.IssueCode()}");
        return 0;
    }

    string input = Path.GetFullPath(args[1]);
    string output = Path.GetFullPath(args[2]);
    if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        throw new IOException("La cartella di output deve essere vuota.");
    byte[] pdf = File.ReadAllBytes(input);
    if (!pdf.AsSpan().StartsWith("%PDF-"u8))
        throw new InvalidDataException("Il file di input non ha un'intestazione PDF.");
    byte[] key = RandomNumberGenerator.GetBytes(32);
    try
    {
        using RSA authority = RSA.Create(3072);
        string id = Guid.NewGuid().ToString("N");
        var document = Crypto.Encrypt(pdf, id, Path.GetFileNameWithoutExtension(input), key);
        string client = Path.Combine(output, "client");
        string server = Path.Combine(output, "server");
        Directory.CreateDirectory(client);
        File.WriteAllBytes(Path.Combine(client, "document.json"), JsonSerializer.SerializeToUtf8Bytes(document));
        File.WriteAllText(Path.Combine(client, "authority.pub"), Convert.ToBase64String(authority.ExportSubjectPublicKeyInfo()));
        string packagePath = Path.Combine(client, "documento.pdfcustode");
        var package = new DocumentPackage(1, document, Convert.ToBase64String(authority.ExportSubjectPublicKeyInfo()));
        File.WriteAllBytes(packagePath, JsonSerializer.SerializeToUtf8Bytes(package));
        string code = ActivationStore.Create(server, id, key, authority);
        File.WriteAllText(Path.Combine(server, "activation-code.txt"), code + Environment.NewLine);
        Console.WriteLine($"Codice di attivazione: {code}");
        Console.WriteLine($"Materiale client: {client}");
        Console.WriteLine($"File da inviare al lettore: {packagePath}");
        Console.WriteLine($"Materiale amministratore: {server}");
        return 0;
    }
    finally { CryptographicOperations.ZeroMemory(key); }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

