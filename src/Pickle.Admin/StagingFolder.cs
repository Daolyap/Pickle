namespace Pickle.Admin;

/// <summary>
/// A private (0700) folder under the temp directory where a privileged command's input file is staged. Files stay until
/// the next use after a day, because a command handed to the shell for a sudo password still needs them.
/// </summary>
internal static class StagingFolder
{
    public static DirectoryInfo Create(string? directory, string prefix)
    {
        var folder = Directory.CreateDirectory(directory ?? Path.Combine(Path.GetTempPath(), prefix + Environment.UserName));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        foreach (var old in folder.EnumerateFiles().Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)))
        {
            try
            {
                old.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return folder;
    }
}
