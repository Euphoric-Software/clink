using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Clink.Services;

/// <summary>WinVerifyTrust for one file: is its Authenticode signature valid, chaining to a trusted root? No UI, no network revocation lookup.</summary>
internal static class WinTrust
{
    private static readonly Guid s_genericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2

    /// <summary>True when <paramref name="file"/> (read through <paramref name="handle"/> when given) has a valid Authenticode signature.</summary>
    internal static bool IsValid(string file, SafeFileHandle? handle)
    {
        nint path = Marshal.StringToHGlobalUni(file);
        nint fileInfo = 0, data = 0;
        bool addedRef = false;
        try
        {
            if (handle != null) handle.DangerousAddRef(ref addedRef);
            var fi = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = path,
                hFile = handle != null ? handle.DangerousGetHandle() : 0,
            };
            fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fi, fileInfo, false);
            var wd = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfo,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE,
            };
            data = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(wd, data, false);
            var action = s_genericVerifyV2;
            int result = WinVerifyTrust(-1, ref action, data);
            Marshal.WriteInt32(data, Marshal.OffsetOf<WINTRUST_DATA>(nameof(WINTRUST_DATA.dwStateAction)).ToInt32(), (int)WTD_STATEACTION_CLOSE);
            WinVerifyTrust(-1, ref action, data);
            return result == 0;
        }
        finally
        {
            if (data != 0) Marshal.FreeHGlobal(data);
            if (fileInfo != 0) Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(path);
            if (addedRef) handle!.DangerousRelease();
        }
    }

    private const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint hwnd, ref Guid action, nint data);
}
