using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Files;

/// <summary>Registers the file picker (Ctrl+T inserts paths) and the <see cref="EditorActionNames.FilePickerCd"/> action (Alt+C).</summary>
public sealed class FilesPanelPlugin : IPicklePlugin
{
    public const string PanelId = "files";

    public string Id => "pickle.files";

    public string DisplayName => "File picker";

    public string Description => "Fuzzy file finder with preview; inserts paths or changes directory.";

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(new PanelDescriptor
        {
            Id = PanelId,
            Title = "Files",
            Description = "Fuzzy-find files and insert their paths",
            DefaultKey = "Ctrl+T",
            CreateView = ctx => new FilesPanel(ctx),
        });

        context.KeyBindings.RegisterAction(EditorActionNames.FilePickerCd, "Pick a directory and change to it", (buffer, _) =>
        {
            buffer.ShowPanel(PanelId, FilesPanel.DirectoryModePrefix);
            return ValueTask.CompletedTask;
        });
    }
}
