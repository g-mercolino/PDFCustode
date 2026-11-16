using System.Security.Cryptography;
using System.Text.Json;
using PdfCustode.Core;
using PdfCustode.Server;

if (!(args.Length == 3 && args[0] == "prepare") && !(args.Length == 2 && args[0] == "issue"))
{
    Console.Error.WriteLine("Uso: prepare <input.pdf> <directory-output>");
    Console.Error.WriteLine("Uso: issue <directory-server>");
    return 2;
}

try
{
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
        string code = ActivationStore.Create(server, id, key, authority);
        File.WriteAllText(Path.Combine(server, "activation-code.txt"), code + Environment.NewLine);
        Console.WriteLine($"Codice di attivazione: {code}");
        Console.WriteLine($"Materiale client: {client}");
        Console.WriteLine($"Materiale amministratore: {server}");
        return 0;
    }
    finally { CryptographicOperations.ZeroMemory(key); }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

