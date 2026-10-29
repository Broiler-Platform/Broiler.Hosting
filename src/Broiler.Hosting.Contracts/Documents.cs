namespace Broiler.Hosting;

/// <summary>Bytes and a display filename; Name is never a host filesystem path.</summary>
public sealed record DocumentContent(string Name, string ContentType, ReadOnlyMemory<byte> Bytes);
public sealed record DocumentOpenOptions(string Accept, long MaximumBytes = 16 * 1024 * 1024);
public enum DocumentOperationStatus { Opened, Saved, DownloadInitiated, Cancelled, Failed }
public sealed record DocumentOperationResult(DocumentOperationStatus Status, DocumentContent? Document = null, string? Error = null);

/// <summary>Hosts own pickers and durable writes. A download request is not a confirmed save.</summary>
public interface IDocumentPort
{
    ValueTask<DocumentOperationResult> OpenAsync(DocumentOpenOptions options, CancellationToken cancellationToken = default);
    ValueTask<DocumentOperationResult> ExportAsync(DocumentContent document, CancellationToken cancellationToken = default);
}

/// <summary>Normalizes untrusted picker names without giving them path authority.</summary>
public static class DocumentNames
{
    public static string DisplayName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string leaf = name.Replace('\\', '/').Split('/')[^1];
        if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or ".." || leaf.Any(char.IsControl))
            throw new ArgumentException("A document needs a display filename.", nameof(name));
        return leaf;
    }
}
