using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Commands;

/// <summary><c>pk font [status|list|install]</c>: Nerd Font status and a per-user install of Cascadia Code NF.</summary>
public sealed class FontCommand : PickleCommandBase
{
    public override string Name => "font";

    public override string Description => "Nerd Font status; install Cascadia Code NF for the prompt's icons";

    public override string Usage => "pk font [status | list | install]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (output.Pickle.Services.Get<IFontService>() is not { } fonts)
        {
            output.Context.WriteError("Font management is only available on Windows. Install a Nerd Font with your package manager (e.g. fonts-cascadia-code) and set it in your terminal.");
            return 1;
        }

        var sub = args.Count == 0 ? "status" : args[0].ToLowerInvariant();
        switch (sub)
        {
            case "status":
                Status(output, fonts);
                return 0;
            case "list" or "ls":
                var nerd = fonts.InstalledNerdFonts();
                if (nerd.Count == 0)
                {
                    output.Muted("No Nerd Fonts are installed. 'pk font install' installs " + fonts.RecommendedFont + ".");
                }

                foreach (var family in nerd)
                {
                    output.Line(family);
                }

                return 0;
            case "install":
                return await Install(output, fonts, cancellationToken).ConfigureAwait(false);
            default:
                return UsageError(output, $"Unknown subcommand '{args[0]}'.");
        }
    }

    private static void Status(CommandOutput output, IFontService fonts)
    {
        var config = output.Pickle.Config.Current;
        string Label(string text) => output.Dim(text.PadRight(16));

        var terminalFont = fonts.TerminalFont();
        var glyphs = fonts.TerminalHasNerdFont();
        output.Line(Label("terminal font") + (terminalFont ?? output.Dim("unknown (not Windows Terminal or the console window)")));
        output.Line(Label("nerd glyphs") + glyphs switch
        {
            true => output.Accent("yes"),
            false => output.Warn("no") + output.Dim(" — icons fall back to plain symbols"),
            null => output.Dim("unknown"),
        });
        output.Line(Label("prompt.icons") + config.Prompt.Icons);
        output.Line(Label("profile font") + (config.Terminal.FontFace ?? "(Terminal's default)")
            + (config.Terminal.FontFace is { } face && !fonts.IsInstalled(face) ? output.Warn("  not installed") : string.Empty));
        var nerd = fonts.InstalledNerdFonts();
        output.Line(Label("nerd fonts") + (nerd.Count == 0 ? output.Dim("none installed") : string.Join(", ", nerd)));
        if (!fonts.IsInstalled(fonts.RecommendedFont))
        {
            output.Muted($"'pk font install' installs {fonts.RecommendedFont} for you (no administrator rights needed).");
        }
    }

    private static async Task<int> Install(CommandOutput output, IFontService fonts, CancellationToken cancellationToken)
    {
        var result = await fonts.InstallRecommendedAsync(new Progress<string>(output.Status), cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            output.Failure(result.Message);
            return 1;
        }

        output.Success(result.Message);
        var config = output.Pickle.Config;
        if (config.Current.Terminal.FontFace is not { } face || !fonts.IsInstalled(face))
        {
            config.Update(c => c.Terminal.FontFace = fonts.RecommendedFont);
            output.Line($"terminal.fontFace = {fonts.RecommendedFont}");
        }
        else if (face != fonts.RecommendedFont)
        {
            output.Muted($"The profile keeps '{face}'; 'pk terminal set font {fonts.RecommendedFont}' switches to the new font.");
        }

        output.Muted("Restart Windows Terminal (close every window) to draw with the new font.");
        return 0;
    }
}
