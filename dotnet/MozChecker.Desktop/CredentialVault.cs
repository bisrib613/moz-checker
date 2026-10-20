using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using MozChecker.Core;

namespace MozChecker.Desktop;

internal static class CredentialVault
{
    // Same target/user convention as Python keyring.backends.Windows.WinVaultKeyring.
    private static string Target(string name) => name + "@MozChecker";
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags; public uint Type; public string TargetName; public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist;
        public uint AttributeCount; public IntPtr Attributes; public string? TargetAlias; public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);
    private static (string User, string Value)? ReadTarget(string target)
    {
        if (!CredRead(target, 1, 0, out var ptr))
        {
            if (Marshal.GetLastWin32Error() == 1168)
                return null;
            throw new Win32Exception();
        }
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            return (cred.UserName, Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2) ?? "");
        }
        finally { CredFree(ptr); }
    }
    public static string Read(string name)
    {
        if (ReadTarget("MozChecker") is { } legacy && legacy.User == name)
            return legacy.Value;
        return ReadTarget(Target(name))?.Value ?? "";
    }
    public static void Save(string name, string value)
    {
        if (name is not ("opr_key" or "moz_id" or "moz_secret"))
            throw new ArgumentException("Kredensial tidak dikenal");
        var legacyMatches = ReadTarget("MozChecker")?.User == name;
        if (value.Length == 0)
        {
            foreach (var target in legacyMatches ? new[] { "MozChecker", Target(name) } : new[] { Target(name) })
                if (!CredDelete(target, 1, 0) && Marshal.GetLastWin32Error() != 1168)
                    throw new Win32Exception();
            return;
        }
        if (Encoding.Unicode.GetByteCount(value) > 2560)
            throw new ArgumentException("Kunci terlalu panjang untuk Credential Manager.");
        var ptr = Marshal.StringToCoTaskMemUni(value);
        try
        {
            foreach (var target in legacyMatches ? new[] { "MozChecker", Target(name) } : new[] { Target(name) })
            {
                var cred = new Credential { Type = 1, TargetName = target, UserName = name, CredentialBlobSize = (uint)Encoding.Unicode.GetByteCount(value), CredentialBlob = ptr, Persist = 3 };
                if (!CredWrite(ref cred, 0))
                    throw new Win32Exception();
            }
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(ptr); }
    }
    public static ApiKeys Load() => new(Read("opr_key"), Read("moz_id"), Read("moz_secret"));
}
