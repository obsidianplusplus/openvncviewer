// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace OpenVNCViewer;

// DPAPI CurrentUser: only explicitly saved profile passwords are protected.
public static class PasswordProtector
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    static byte[] Transform(byte[] value, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("保存密码需要 Windows DPAPI");
        var input = new Blob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            bool ok = protect ? CryptProtectData(ref input, "OpenVNCViewer", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 无法保护或读取这个密码");
            byte[] result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (int i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
    public static string Protect(string password)
    {
        if (password.Length == 0) return "";
        byte[] raw = Encoding.UTF8.GetBytes(password);
        try { return Convert.ToBase64String(Transform(raw, true)); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
    public static string Unprotect(string token)
    {
        if (token.Length == 0) return "";
        try
        {
            byte[] raw = Transform(Convert.FromBase64String(token), false);
            try { return Encoding.UTF8.GetString(raw); }
            finally { CryptographicOperations.ZeroMemory(raw); }
        }
        catch (Exception ex) when (ex is FormatException or Win32Exception or PlatformNotSupportedException) { return ""; }
    }
}
