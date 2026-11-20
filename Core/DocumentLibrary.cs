using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfCustode.Core;

[JsonSerializable(typeof(DocumentPackage))]
public partial class PackageJson : JsonSerializerContext;

public sealed record LibraryDocument(string Folder, string Title);

public sealed class DocumentLibrary(string root)
{
    public IEnumerable<LibraryDocument> Documents()
    {
        Directory.CreateDirectory(root);
        return Directory.EnumerateDirectories(root)
            .Where(folder => !Path.GetFileName(folder).StartsWith('.') && File.Exists(Path.Combine(folder, "title.txt")))
            .Select(folder => new LibraryDocument(folder, File.ReadAllText(Path.Combine(folder, "title.txt"))))
            .OrderBy(document => document.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<LibraryDocument> ImportAsync(Stream source)
    {
        var package = await JsonSerializer.DeserializeAsync(source, PackageJson.Default.DocumentPackage)
            ?? throw new InvalidDataException("File PDF Custode vuoto.");
        package.Validate();
        Directory.CreateDirectory(root);
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(package.Document.DocumentId + "\n" + package.Authority)));
        string destination = Path.Combine(root, id);
        if (Directory.Exists(destination))
        {
            var existing = await ReadAsync(destination);
            if (existing != package) throw new InvalidDataException("Esiste già un documento diverso con la stessa identità.");
            return new(destination, package.Document.Title);
        }
        string temporary = Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(temporary, "document.pdfcustode"), JsonSerializer.Serialize(package, PackageJson.Default.DocumentPackage));
            await File.WriteAllTextAsync(Path.Combine(temporary, "title.txt"), package.Document.Title);
            try { Directory.Move(temporary, destination); }
            catch (IOException) when (Directory.Exists(destination))
            {
                if (await ReadAsync(destination) != package)
                    throw new InvalidDataException("Esiste già un documento diverso con la stessa identità.");
            }
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
        return new(destination, package.Document.Title);
    }

    public static async Task<DocumentPackage> ReadAsync(string folder)
    {
        await using var source = File.OpenRead(Path.Combine(folder, "document.pdfcustode"));
        var package = await JsonSerializer.DeserializeAsync(source, PackageJson.Default.DocumentPackage)
            ?? throw new InvalidDataException("File PDF Custode vuoto.");
        package.Validate();
        return package;
    }
}
