using System.Runtime.InteropServices;

namespace VirtualDisplayManager.Core.Interop;

internal static class SignatureVerifier
{
    private static readonly Guid WintrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static (bool IsValid, int ErrorCode) VerifyEmbeddedOrCatalogSignature(string filePath)
    {
        var fileInfo = new WinTrustFileInfo(filePath);
        var data = new WinTrustData(fileInfo);
        try
        {
            var action = WintrustActionGenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return (result == 0, result);
        }
        finally
        {
            data.Dispose();
            fileInfo.Dispose();
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [In] ref Guid pgActionId,
        [In] ref WinTrustData pWvtData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeWinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    private sealed class WinTrustFileInfo : IDisposable
    {
        public IntPtr Pointer { get; }

        public WinTrustFileInfo(string filePath)
        {
            var native = new NativeWinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<NativeWinTrustFileInfo>(),
                FilePath = Marshal.StringToCoTaskMemUni(filePath)
            };
            Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<NativeWinTrustFileInfo>());
            Marshal.StructureToPtr(native, Pointer, false);
        }

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero) return;
            var native = Marshal.PtrToStructure<NativeWinTrustFileInfo>(Pointer);
            Marshal.FreeCoTaskMem(native.FilePath);
            Marshal.FreeCoTaskMem(Pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData : IDisposable
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPtr;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;

        public WinTrustData(WinTrustFileInfo fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2; // WTD_UI_NONE
            RevocationChecks = 0;
            UnionChoice = 1; // WTD_CHOICE_FILE
            FileInfoPtr = fileInfo.Pointer;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0;
            UiContext = 0;
        }

        public void Dispose() { }
    }
}
