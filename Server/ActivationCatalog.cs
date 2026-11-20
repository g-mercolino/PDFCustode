using System.Text.Json;
using PdfCustode.Core;

namespace PdfCustode.Server;

public sealed class ActivationCatalog(string root)
{
    public SignedLicense? Activate(ActivationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DocumentId) || request.DocumentId.Length > 128)
            return null;
        // Discover new documents on each activation, including legacy single-document folders.
        foreach (string stateFile in Directory.EnumerateFiles(root, "server-state.json", SearchOption.AllDirectories))
        {
            using var state = JsonDocument.Parse(File.ReadAllText(stateFile));
            if (state.RootElement.GetProperty("DocumentId").GetString() != request.DocumentId)
                continue;
            using var store = new ActivationStore(Path.GetDirectoryName(stateFile)!);
            return store.Activate(request);
        }
        return null;
    }
}
