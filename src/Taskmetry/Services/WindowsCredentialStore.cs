using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Taskmetry.Services;

internal interface ISecureCredentialStore
{
    bool Contains(string targetName);

    string? Read(string targetName);

    void Write(string targetName, string secret);

    void Delete(string targetName);
}

internal sealed class WindowsCredentialStore : ISecureCredentialStore
{
    private const uint GenericCredential = 1;
    private const uint PersistForLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobSize = 2560;

    public bool Contains(string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        if (CredRead(targetName, GenericCredential, 0, out var credentialPointer))
        {
            CredFree(credentialPointer);
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound)
        {
            return false;
        }

        throw new Win32Exception(error);
    }

    public string? Read(string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        if (!CredRead(targetName, GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error);
        }

        byte[]? secretBytes = null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            secretBytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
            return Encoding.UTF8.GetString(secretBytes);
        }
        finally
        {
            if (secretBytes is not null)
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }

            CredFree(credentialPointer);
        }
    }

    public void Write(string targetName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        if (secretBytes.Length > MaximumCredentialBlobSize)
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                $"資格情報は{MaximumCredentialBlobSize}バイト以内で指定してください。");
        }

        var secretPointer = Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetName,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = secretPointer,
                Persist = PersistForLocalMachine,
                UserName = "claude.ai",
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            var zeroes = new byte[secretBytes.Length];
            Marshal.Copy(zeroes, 0, secretPointer, zeroes.Length);
            Marshal.FreeHGlobal(secretPointer);
        }
    }

    public void Delete(string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        if (CredDelete(targetName, GenericCredential, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public NativeFileTime LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }
}
