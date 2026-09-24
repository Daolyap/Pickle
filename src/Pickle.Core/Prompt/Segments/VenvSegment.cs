using Pickle.Abstractions;

namespace Pickle.Core.Prompt.Segments;

/// <summary>
/// Active Python environment: VIRTUAL_ENV (named after its project folder when the venv is called venv/.venv/env)
/// or CONDA_DEFAULT_ENV. Option <c>condaBase</c> (default false) also shows conda's "base" environment.
/// </summary>
public sealed class VenvSegment(SegmentEnvironment environment) : IPromptSegment
{
    private static readonly string[] GenericNames = ["venv", ".venv", "env", ".env", "virtualenv"];

    public string Type => "venv";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        Resolve(environment.GetEnvironmentVariable, style.OptionBool("condaBase", false)) is { } name
            ? SegmentText.Show(name)
            : SegmentText.Hide();

    public static string? Resolve(Func<string, string?> getEnv, bool showCondaBase = false)
    {
        if (getEnv("VIRTUAL_ENV") is { Length: > 0 } venv)
        {
            // Python 3.10+ activation scripts export the prompt name chosen with `python -m venv --prompt`.
            var prompt = getEnv("VIRTUAL_ENV_PROMPT")?.Trim().Trim('(', ')').Trim();
            if (!string.IsNullOrEmpty(prompt))
            {
                return SegmentText.Sanitize(prompt);
            }

            var parts = venv.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            var name = parts[^1];
            if (parts.Length > 1 && GenericNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                name = parts[^2];
            }

            return SegmentText.Sanitize(name);
        }

        if (getEnv("CONDA_DEFAULT_ENV") is { Length: > 0 } conda && (showCondaBase || conda != "base"))
        {
            return SegmentText.Sanitize(conda.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? conda);
        }

        return null;
    }
}
