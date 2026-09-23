using System.Globalization;
using System.Text;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Prompt.Segments;

/// <summary>
/// Branch (or short sha when detached), operation, ahead/behind and change counts. Hidden outside repositories.
/// Asynchronous: the prompt engine caches it per directory and never waits longer than prompt.gitTimeoutMs.
/// Options: <c>counts</c> (default true) to show only the branch and operation when false.
/// </summary>
public sealed class GitSegment(SegmentEnvironment environment) : IPromptSegment
{
    public string Type => "git";

    public async ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken)
    {
        var status = await environment.GetGitStatus(context.Cwd, cancellationToken).ConfigureAwait(false);
        return status is null ? null : new PromptSegmentOutput(Format(status, style.OptionBool("counts", true)));
    }

    public static string Format(GitStatus status, bool counts = true)
    {
        var sb = new StringBuilder(SegmentText.Sanitize(Head(status)));
        if (!string.IsNullOrEmpty(status.Operation))
        {
            sb.Append(' ').Append(SegmentText.Sanitize(status.Operation));
        }

        if (!counts)
        {
            return sb.ToString();
        }

        Append(sb, "↑", status.Ahead);
        Append(sb, "↓", status.Behind);
        Append(sb, "+", status.StagedCount);
        Append(sb, "!", status.UnstagedCount);
        Append(sb, "?", status.UntrackedCount);
        Append(sb, "✖", status.ConflictCount);
        Append(sb, "≡", status.StashCount);
        return sb.ToString();
    }

    private static string Head(GitStatus status)
    {
        if (!status.IsDetached && !string.IsNullOrEmpty(status.Branch))
        {
            return status.Branch;
        }

        return string.IsNullOrEmpty(status.HeadSha) ? "(detached)" : status.HeadSha[..Math.Min(7, status.HeadSha.Length)];
    }

    private static void Append(StringBuilder sb, string symbol, int count)
    {
        if (count > 0)
        {
            sb.Append(' ').Append(symbol).Append(count.ToString(CultureInfo.InvariantCulture));
        }
    }
}
