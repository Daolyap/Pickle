using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Pickle.Windows.Elevation;

[SupportedOSPlatform("windows")]
internal static partial class PipeNative
{
    /// <summary>A single-instance pipe whose DACL grants only the current user (no inherited ACEs).</summary>
    public static NamedPipeServerStream CreateServer(string pipeName)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Cannot determine the current user SID.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            ElevationProtocol.MaxMessageBytes,
            ElevationProtocol.MaxMessageBytes,
            security);
    }

    public static bool TryGetClientProcessId(NamedPipeServerStream server, out int processId)
    {
        processId = 0;
        if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var pid))
        {
            return false;
        }

        processId = unchecked((int)pid);
        return true;
    }

    public static bool TryGetServerProcessId(NamedPipeClientStream client, out int processId)
    {
        processId = 0;
        if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var pid))
        {
            return false;
        }

        processId = unchecked((int)pid);
        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
