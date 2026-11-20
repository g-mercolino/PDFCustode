using PdfCustode.Core;

namespace PdfCustode.Server;

public sealed class ActivationCatalog(string root)
{
    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".audit", "audit", "logs", "pending", "history", "acknowledged", ".git"
    };

    public SignedLicense? Activate(ActivationRequest request, int? maxActivations = null)
    {
        var document = FindDocument(request.DocumentId);
        if (document is null) return null;
        using var store = new ActivationStore(document.Folder);
        return store.Activate(request, maxActivations);
    }

    public SignedLicense? TryGetExisting(ActivationRequest request)
    {
        var document = FindDocument(request.DocumentId);
        if (document is null) return null;
        using var store = new ActivationStore(document.Folder);
        return store.TryGetExisting(request);
    }

    public IReadOnlyList<DocumentSnapshot> GetSnapshots()
    {
        var result = new List<DocumentSnapshot>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string folder in DocumentFolders())
        {
            using var store = new ActivationStore(folder);
            var snapshot = store.GetSnapshot();
            if (!ids.Add(snapshot.DocumentId))
                throw new InvalidDataException("Identificativo documento duplicato nell'archivio amministrativo.");
            result.Add(snapshot);
        }
        return result;
    }

    private DocumentSnapshot? FindDocument(string? documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId) || documentId.Length > 128) return null;
        return GetSnapshots().SingleOrDefault(document => document.DocumentId == documentId);
    }

    private IEnumerable<string> DocumentFolders()
    {
        var remaining = new Stack<string>();
        remaining.Push(Path.GetFullPath(root));
        while (remaining.TryPop(out string? folder))
        {
            if (File.Exists(Path.Combine(folder, "server-state.json"))) yield return folder;
            foreach (string child in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
            {
                if (ExcludedFolders.Contains(Path.GetFileName(child)) ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                remaining.Push(child);
            }
        }
    }
}
