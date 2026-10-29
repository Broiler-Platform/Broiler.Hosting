namespace Broiler.Hosting.WebAssembly;

/// <summary>Browser transport seam; the caller supplies its JS interop marshalling.</summary>
public sealed class BrowserDocumentPort(
    Func<DocumentOpenOptions, CancellationToken, ValueTask<DocumentOperationResult>> open,
    Func<DocumentContent, CancellationToken, ValueTask<DocumentOperationResult>> export) : IDocumentPort
{
    public ValueTask<DocumentOperationResult> OpenAsync(DocumentOpenOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.MaximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        return OpenCoreAsync(options, cancellationToken);
    }

    private async ValueTask<DocumentOperationResult> OpenCoreAsync(DocumentOpenOptions options, CancellationToken token)
    {
        var result = await open(options, token);
        token.ThrowIfCancellationRequested();
        if (result.Status != DocumentOperationStatus.Opened) return result;
        if (result.Document is null || result.Document.Bytes.Length > options.MaximumBytes)
            return new(DocumentOperationStatus.Failed, Error: "The selected document exceeds the size limit or has no content.");
        return result with { Document = result.Document with { Name = DocumentNames.DisplayName(result.Document.Name) } };
    }

    public ValueTask<DocumentOperationResult> ExportAsync(DocumentContent document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return export(document with { Name = DocumentNames.DisplayName(document.Name) }, cancellationToken);
    }
}
