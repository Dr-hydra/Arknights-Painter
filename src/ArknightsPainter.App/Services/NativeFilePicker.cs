using System.Runtime.InteropServices;

namespace ArknightsPainter.App.Services;

internal static class NativeFilePicker
{
    private static readonly Guid FileOpenDialogClassId = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid FileOpenDialogInterfaceId = new("D57C7288-D4AD-4768-BE02-9D969532D960");
    private const int ClassContextInProcessServer = 0x1;
    private const int CancelledHResult = unchecked((int)0x800704C7);

    public static string? PickImagePath(IntPtr ownerWindow)
    {
        var classId = FileOpenDialogClassId;
        var interfaceId = FileOpenDialogInterfaceId;
        Marshal.ThrowExceptionForHR(CoCreateInstance(
            ref classId,
            IntPtr.Zero,
            ClassContextInProcessServer,
            ref interfaceId,
            out var dialogPointer));

        IFileOpenDialog? dialog = null;
        IShellItem? result = null;
        try
        {
            dialog = (IFileOpenDialog)Marshal.GetObjectForIUnknown(dialogPointer);
            dialog.GetOptions(out var options);
            dialog.SetOptions(options |
                FileDialogOptions.FileMustExist |
                FileDialogOptions.PathMustExist |
                FileDialogOptions.ForceFileSystem |
                FileDialogOptions.NoChangeDirectory);
            dialog.SetFileTypes(
                2,
                [
                    new FileTypeFilter(
                        "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.webp)",
                        "*.png;*.jpg;*.jpeg;*.bmp;*.webp"),
                    new FileTypeFilter("所有文件 (*.*)", "*.*")
                ]);
            dialog.SetFileTypeIndex(1);
            dialog.SetTitle("打开图片");
            dialog.SetDefaultExtension("png");

            var showResult = dialog.Show(ownerWindow);
            if (showResult == CancelledHResult)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(showResult);
            dialog.GetResult(out result);
            result.GetDisplayName(ShellItemDisplayName.FileSystemPath, out var pathPointer);
            try
            {
                return Marshal.PtrToStringUni(pathPointer)
                    ?? throw new InvalidOperationException("系统文件选择器没有返回文件路径。");
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
        finally
        {
            if (result is not null)
            {
                Marshal.FinalReleaseComObject(result);
            }

            if (dialog is not null)
            {
                Marshal.FinalReleaseComObject(dialog);
            }

            Marshal.Release(dialogPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct FileTypeFilter(string name, string pattern)
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public readonly string Name = name;

        [MarshalAs(UnmanagedType.LPWStr)]
        public readonly string Pattern = pattern;
    }

    [Flags]
    private enum FileDialogOptions : uint
    {
        NoChangeDirectory = 0x00000008,
        FileMustExist = 0x00001000,
        PathMustExist = 0x00000800,
        ForceFileSystem = 0x00000040
    }

    private enum ShellItemDisplayName : uint
    {
        FileSystemPath = 0x80058000
    }

    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);

        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FileTypeFilter[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(FileDialogOptions options);
        void GetOptions(out FileDialogOptions options);
        void SetDefaultFolder(IntPtr shellItem);
        void SetFolder(IntPtr shellItem);
        void GetFolder(out IntPtr shellItem);
        void GetCurrentSelection(out IntPtr shellItem);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IntPtr shellItem, int placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int hResult);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog : IFileDialog
    {
        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(ShellItemDisplayName displayName, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem shellItem, uint hint, out int order);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        int classContext,
        ref Guid interfaceId,
        out IntPtr instance);
}
