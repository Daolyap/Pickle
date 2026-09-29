namespace Pickle.Abstractions;

/// <summary>
/// A theme (themes/*.json). Colors are strings: "#RRGGBB", an ANSI name ("red", "brightBlack"), or
/// null/"default" for the terminal default. See <see cref="PickleColor"/>.
/// </summary>
public sealed class Theme
{
    public string Name { get; set; } = "unnamed";
    public string? Description { get; set; }
    public TerminalPalette Terminal { get; set; } = new();
    public SyntaxColors Syntax { get; set; } = new();
    public UiColors Ui { get; set; } = new();
    public PromptTheme Prompt { get; set; } = new();
}

/// <summary>The 16-color terminal palette. Used for the Windows Terminal color scheme.</summary>
public sealed class TerminalPalette
{
    public string Background { get; set; } = "#1B1F1A";
    public string Foreground { get; set; } = "#DDE5D6";
    public string CursorColor { get; set; } = "#A6E22E";
    public string SelectionBackground { get; set; } = "#3A4A32";
    public string Black { get; set; } = "#1B1F1A";
    public string Red { get; set; } = "#E0605A";
    public string Green { get; set; } = "#8FC34A";
    public string Yellow { get; set; } = "#E5C07B";
    public string Blue { get; set; } = "#61AFEF";
    public string Purple { get; set; } = "#C678DD";
    public string Cyan { get; set; } = "#56B6C2";
    public string White { get; set; } = "#DDE5D6";
    public string BrightBlack { get; set; } = "#5C6A55";
    public string BrightRed { get; set; } = "#FF7A72";
    public string BrightGreen { get; set; } = "#B5E36B";
    public string BrightYellow { get; set; } = "#F2D68F";
    public string BrightBlue { get; set; } = "#82C4FF";
    public string BrightPurple { get; set; } = "#DA9BEF";
    public string BrightCyan { get; set; } = "#7FD4DE";
    public string BrightWhite { get; set; } = "#FFFFFF";
}

public sealed class SyntaxColors
{
    public string? Default { get; set; }
    public string? Command { get; set; } = "green";
    public string? UnknownCommand { get; set; } = "red";
    public string? Parameter { get; set; } = "brightBlack";
    public string? String { get; set; } = "yellow";
    public string? Number { get; set; } = "brightPurple";
    public string? Variable { get; set; } = "cyan";
    public string? Operator { get; set; } = "brightBlack";
    public string? Keyword { get; set; } = "purple";
    public string? Comment { get; set; } = "brightBlack";
    public string? Type { get; set; } = "blue";
    public string? Member { get; set; } = "white";
    public string? Error { get; set; } = "brightRed";
    public string? Suggestion { get; set; } = "brightBlack";
    public string? SelectionBackground { get; set; } = "#3A4A32";
    public string? Translated { get; set; } = "brightBlack";
}

public sealed class UiColors
{
    public string Accent { get; set; } = "#8FC34A";
    public string Muted { get; set; } = "#5C6A55";
    public string Success { get; set; } = "#8FC34A";
    public string Warning { get; set; } = "#E5C07B";
    public string Error { get; set; } = "#E0605A";
    public string Info { get; set; } = "#61AFEF";
    public string PanelBackground { get; set; } = "#1B1F1A";
    public string PanelForeground { get; set; } = "#DDE5D6";
    public string PanelBorder { get; set; } = "#3A4A32";
    public string HighlightBackground { get; set; } = "#3A4A32";
    public string HighlightForeground { get; set; } = "#FFFFFF";
    public string MenuBackground { get; set; } = "#232922";
    public string MenuForeground { get; set; } = "#DDE5D6";
    public string MenuSelectedBackground { get; set; } = "#8FC34A";
    public string MenuSelectedForeground { get; set; } = "#1B1F1A";
    public string MenuDescription { get; set; } = "#7D8C75";
    public string MatchHighlight { get; set; } = "#E5C07B";
}

public enum SeparatorStyle
{
    Plain,
    Powerline,
    Round,
    Slant,
    None,
}

public sealed class PromptTheme
{
    public List<SegmentStyle> Left { get; set; } = [];
    public List<SegmentStyle> Right { get; set; } = [];

    public SeparatorStyle Separator { get; set; } = SeparatorStyle.Plain;

    /// <summary>Put the input on its own line below the segments.</summary>
    public bool NewlineBeforeInput { get; set; } = false;

    public string PromptChar { get; set; } = "❯";
    public string? PromptCharColor { get; set; } = "green";
    public string? PromptCharErrorColor { get; set; } = "red";
    public string ContinuationPrompt { get; set; } = "∙ ";

    /// <summary>Template for the transient prompt left in scrollback after a command is accepted. Tokens: {promptChar}, {cwd}, {time}.</summary>
    public string TransientTemplate { get; set; } = "{promptChar} ";

    /// <summary>Moving colors and prompt characters while Pickle waits for input; null for a static prompt.</summary>
    public PromptAnimation? Animation { get; set; }
}

/// <summary>
/// Animates the prompt while it waits for input. Effects recolor each segment (its background when it has one, else
/// its text) and the prompt character, offset by <see cref="Spread"/> per segment so the motion travels along the prompt:
/// <c>wave</c> scrolls <see cref="Colors"/> as a gradient, <c>rainbow</c> rotates each color's hue, <c>pulse</c> breathes
/// toward <see cref="Colors"/>[0] (or a lighter shade), <c>shimmer</c> sweeps a bright band across. <see cref="PromptChars"/>
/// cycles the prompt character (e.g. a spinner) with any effect, including <c>none</c>. The status and admin segments keep
/// their colors, as does any segment with the option <c>"animate": "false"</c>.
/// </summary>
public sealed class PromptAnimation
{
    /// <summary>none, wave, rainbow, pulse or shimmer.</summary>
    public string Effect { get; set; } = "wave";

    /// <summary>Gradient stops for wave (cycled) and the target color for pulse; empty uses the segments' own colors.</summary>
    public List<string> Colors { get; set; } = [];

    /// <summary>Milliseconds per frame (40–2000).</summary>
    public int FrameMs { get; set; } = 120;

    /// <summary>Milliseconds for one full cycle of the effect.</summary>
    public int PeriodMs { get; set; } = 4000;

    /// <summary>Phase offset between neighbouring segments, as a fraction of the cycle.</summary>
    public double Spread { get; set; } = 0.15;

    /// <summary>How far pulse and shimmer move a color toward their target (0–1).</summary>
    public double Intensity { get; set; } = 0.6;

    /// <summary>"auto" (backgrounds of block segments, text of the others), "background" or "foreground".</summary>
    public string Target { get; set; } = "auto";

    /// <summary>Whether the prompt character's color takes part (its error color never does).</summary>
    public bool PromptChar { get; set; } = true;

    /// <summary>Prompt characters shown in turn instead of <see cref="PromptTheme.PromptChar"/>; padded to the widest.</summary>
    public List<string> PromptChars { get; set; } = [];

    /// <summary>Frames each of <see cref="PromptChars"/> stays on screen.</summary>
    public int PromptCharFrames { get; set; } = 1;
}

public sealed class SegmentStyle
{
    /// <summary>Segment type id: cwd, git, status, duration, time, user, host, admin, venv, node, k8s, jobs, text, or a plugin segment id.</summary>
    public string Type { get; set; } = "text";
    public string? Foreground { get; set; }
    public string? Background { get; set; }

    /// <summary>Format template; <c>{value}</c> is the segment's text, <c>{icon}</c> its icon.</summary>
    public string? Template { get; set; }

    public string? Icon { get; set; }

    /// <summary>Segment-specific options, e.g. cwd: {"maxDepth": "3"}, time: {"format": "HH:mm"}.</summary>
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface IThemeProvider
{
    Theme Current { get; }

    event EventHandler<Theme>? ThemeChanged;

    IReadOnlyList<string> Available { get; }

    Theme? Load(string name);

    /// <summary>Switch themes (persists <see cref="PickleConfig.Theme"/>).</summary>
    void Apply(string name);
}

/// <summary>Sample prompts for any theme, as ANSI lines (the theme gallery, <c>pk theme preview</c>).</summary>
public interface IThemePreviewer
{
    /// <summary>
    /// A fixed sample prompt (a git repo, a failed command's status, a clock) for <paramref name="theme"/> at
    /// <paramref name="width"/> columns; for an animated theme, as it looks at <paramref name="frame"/> (null: its own colors).
    /// </summary>
    IReadOnlyList<string> Preview(Theme theme, int width, long? frame = null);
}
