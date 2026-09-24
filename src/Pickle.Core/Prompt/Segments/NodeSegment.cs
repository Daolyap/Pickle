using System.ComponentModel;
using System.Diagnostics;
using Pickle.Abstractions;

namespace Pickle.Core.Prompt.Segments;

/// <summary>Node.js version, shown inside projects (package.json in the directory or an ancestor).</summary>
public sealed class NodeSegment(SegmentEnvironment environment) : IPromptSegment
{
    public string Type => "node";

    public async ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken)
    {
        if (!environment.IsNodeProject(context.Cwd))
        {
            return null;
        }

        var version = await environment.GetNodeVersion(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(version) ? null : new PromptSegmentOutput(version);
    }
}

/// <summary>Runs <c>node --version</c> at most once per session.</summary>
public sealed class NodeVersionProbe
{
    private readonly Lazy<Task<string?>> _version;

    public NodeVersionProbe(string executable = "node", TimeSpan? timeout = null) =>
        _version = new Lazy<Task<string?>>(() => Task.Run(() => RunAsync(executable, timeout ?? TimeSpan.FromSeconds(3))));

    /// <summary>The probe itself is never cancelled (the result is shared); only this caller's wait is.</summary>
    public Task<string?> GetVersionAsync(CancellationToken cancellationToken) => _version.Value.WaitAsync(cancellationToken);

    public static string? ParseVersion(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return line is { Length: > 0 and < 40 } && (line[0] == 'v' || char.IsAsciiDigit(line[0])) ? SegmentText.Sanitize(line) : null;
    }

    private static async Task<string?> RunAsync(string executable, TimeSpan timeout)
    {
        if (Commands.ExecutableLocator.Find(executable) is not { } path)
        {
            return null;
        }

        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--version");

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            using var cts = new CancellationTokenSource(timeout);
            var output = process.StandardOutput.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return ParseVersion(await output.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
