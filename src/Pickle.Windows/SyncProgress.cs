namespace Pickle.Windows;

/// <summary>An <see cref="IProgress{T}"/> that reports inline (unlike <see cref="Progress{T}"/>, which posts and can reorder).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
