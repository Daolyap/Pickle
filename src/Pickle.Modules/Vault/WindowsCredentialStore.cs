using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Pickle.Modules.Vault;

/// <summary>Windows Credential Manager (generic credentials named <c>Pickle:&lt;name&gt;</c>, kept for this Windows account only).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : ISecretStore
{
    private const string Prefix = "Pickle:";
    private const uint CredTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE.</summary>
    public const int MaxBytes = 2560;

    public string Id => "credential-manager";

    public string DisplayName => "Windows Credential Manager";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(OperatingSystem.IsWindows());

    public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        if (!CredRead(Prefix + name, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? Task.FromResult<string?>(null) : throw Failure("read", error);
        }

        try
        {
            return Task.FromResult<string?>(BlobText(Marshal.PtrToStructure<Credential>(pointer)));
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public unsafe Task SetAsync(string name, string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxBytes)
        {
            throw new SecretStoreException($"Credential Manager holds at most {MaxBytes} bytes per secret; this one is {bytes.Length}. Use the encrypted file instead: pk config set extensions.vault.backend file");
        }

        var target = Prefix + name;
        var user = Environment.UserName;
        fixed (char* targetPointer = target)
        fixed (char* userPointer = user)
        fixed (byte* blobPointer = bytes)
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = (nint)targetPointer,
                UserName = (nint)userPointer,
                BlobSize = (uint)bytes.Length,
                Blob = (nint)blobPointer,
                Persist = PersistLocalMachine,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw Failure("write", Marshal.GetLastPInvokeError());
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        if (CredDelete(Prefix + name, CredTypeGeneric, 0))
        {
            return Task.FromResult(true);
        }

        var error = Marshal.GetLastPInvokeError();
        return error == ErrorNotFound ? Task.FromResult(false) : throw Failure("delete", error);
    }

    public Task<IReadOnlyList<SecretInfo>?> ListAsync(CancellationToken cancellationToken)
    {
        if (!CredEnumerate(Prefix + "*", 0, out var count, out var array))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? Task.FromResult<IReadOnlyList<SecretInfo>?>([]) : throw Failure("list", error);
        }

        try
        {
            var list = new List<SecretInfo>();
            for (var i = 0; i < count; i++)
            {
                var credential = Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(array, i * IntPtr.Size));
                if (Marshal.PtrToStringUni(credential.TargetName) is { } target && target.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var ticks = ((long)credential.LastWrittenHigh << 32) | credential.LastWrittenLow;
                    list.Add(new SecretInfo(target[Prefix.Length..], ticks > 0 ? DateTimeOffset.FromFileTime(ticks) : null));
                }
            }

            return Task.FromResult<IReadOnlyList<SecretInfo>?>(list);
        }
        finally
        {
            CredFree(array);
        }
    }

    private static unsafe string? BlobText(Credential credential) =>
        credential.BlobSize == 0 || credential.Blob == 0
            ? string.Empty
            : Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)credential.Blob, (int)credential.BlobSize));

    private static SecretStoreException Failure(string action, int error) =>
        new($"Credential Manager could not {action} the secret: {new System.ComponentModel.Win32Exception(error).Message}");

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint BlobSize;
        public nint Blob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref Credential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredEnumerate(string? filter, uint flags, out uint count, out nint credentials);

    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void CredFree(nint buffer);
}
