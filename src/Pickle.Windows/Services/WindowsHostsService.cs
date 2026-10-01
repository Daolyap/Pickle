using System.Runtime.Versioning;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Services;

/// <summary>The hosts file: readable by everyone, written by the elevated helper after <see cref="HostsDocument.Validate"/>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHostsService(IElevationBroker broker, string? path = null) : IHostsService
{
    public string Path { get; } = path ?? System.IO.Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    public async Task<HostsDocument> ReadAsync(CancellationToken cancellationToken = default) =>
        HostsDocument.Parse(File.Exists(Path) ? await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false) : string.Empty);

    public async Task<ServiceOperationResult> WriteAsync(HostsDocument document, CancellationToken cancellationToken = default)
    {
        var text = document.Serialize();
        HostsDocument.Validate(text);
        try
        {
            var responses = await broker.RunAsync([new ElevatedRequest(ElevatedOperationKind.HostsFileWrite, [text])], null, cancellationToken).ConfigureAwait(false);
            var response = responses.FirstOrDefault();
            return new ServiceOperationResult(response?.Success ?? false, response?.Message ?? "No answer from the elevated helper.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ServiceOperationResult(false, "Administrator approval was declined.");
        }
    }
}
