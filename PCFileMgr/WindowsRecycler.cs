using System.IO;
using System.Runtime.InteropServices;
using PCFileMgr.Core;

namespace PCFileMgr;

// Windows Shell recycle operation on an STA thread. A progress sink vetoes permanent deletion.
public sealed class WindowsRecycler(IntPtr owner) : IFileRecycler
{
    public void Recycle(string path)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("STA thread required.");
        IShellItem? shellItem = null;
        IFileOperation? operation = null;
        var sink = new RecycleOnlySink();
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!;
            operation = (IFileOperation)Activator.CreateInstance(type)!;
            // SILENT | NOCONFIRMATION | ALLOWUNDO | NOERRORUI | NO_CONNECTED_ELEMENTS |
            // RECYCLEONDELETE | EARLYFAILURE | ADDUNDORECORD. No elevation and no permanent-delete fallback.
            operation.SetOperationFlags(0x0004 | 0x0010 | 0x0040 | 0x0400 | 0x2000 | 0x00080000 | 0x00100000 | 0x20000000);
            operation.SetOwnerWindow(owner);
            var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out shellItem));
            operation.DeleteItem(shellItem, sink);
            try { operation.PerformOperations(); }
            catch (COMException) when (sink.PermanentDeleteBlocked)
            { throw new IOException("휴지통으로 이동할 수 없어 건너뛰었습니다. 영구 삭제는 수행하지 않습니다."); }
            operation.GetAnyOperationsAborted(out var aborted);
            if (sink.PermanentDeleteBlocked) throw new IOException("휴지통으로 이동할 수 없어 건너뛰었습니다. 영구 삭제는 수행하지 않습니다.");
            if (sink.Result < 0) Marshal.ThrowExceptionForHR(sink.Result);
            if (aborted) throw new OperationCanceledException("휴지통 이동이 취소되었습니다.");
            if (!sink.Completed || File.Exists(path)) throw new IOException("휴지통 이동을 확인할 수 없습니다. 파일 상태를 다시 확인하세요.");
        }
        finally
        {
            if (shellItem is not null) Marshal.ReleaseComObject(shellItem);
            if (operation is not null) Marshal.ReleaseComObject(operation);
            GC.KeepAlive(sink);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);
}

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
    void GetParent(out IShellItem parent);
    void GetDisplayName(uint displayName, out IntPtr name);
    void GetAttributes(uint mask, out uint attributes);
    void Compare(IShellItem other, uint hint, out int order);
}

[ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    void Advise(IFileOperationProgressSink sink, out uint cookie);
    void Unadvise(uint cookie);
    void SetOperationFlags(uint flags);
    void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
    void SetProgressDialog(IntPtr dialog);
    void SetProperties(IntPtr properties);
    void SetOwnerWindow(IntPtr owner);
    void ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] object item);
    void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
    void RenameItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
    void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
    void MoveItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
    void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.Interface)] object destination);
    void CopyItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
    void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.Interface)] object destination);
    void DeleteItem(IShellItem item, IFileOperationProgressSink sink);
    void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
    void NewItem([MarshalAs(UnmanagedType.Interface)] object destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IFileOperationProgressSink sink);
    void PerformOperations();
    void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}

[ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int result);
    [PreserveSig] int PreRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr newItem);
    [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr newItem);
    [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr newItem);
    [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
    [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr newItem);
    [PreserveSig] int PreNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IntPtr newItem);
    [PreserveSig] int UpdateProgress(uint total, uint soFar);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RecycleOnlySink : IFileOperationProgressSink
{
    public bool PermanentDeleteBlocked { get; private set; }
    public bool Completed { get; private set; }
    public int Result { get; private set; }
    public int PreDeleteItem(uint flags, IntPtr item)
    {
        // TSF_DELETE_RECYCLE_IF_POSSIBLE. Shell must explicitly signal a recycle operation.
        if ((flags & 0x80) != 0) return 0;
        PermanentDeleteBlocked = true;
        return unchecked((int)0x80004005);
    }
    public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr newItem) { Result = result; Completed = result >= 0; return 0; }
    public int StartOperations() => 0;
    public int FinishOperations(int result) { if (result < 0) Result = result; return 0; }
    public int PreRenameItem(uint flags, IntPtr item, string name) => 0;
    public int PostRenameItem(uint flags, IntPtr item, string name, int result, IntPtr newItem) => 0;
    public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, string name) => 0;
    public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, string name, int result, IntPtr newItem) => 0;
    public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, string name) => 0;
    public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, string name, int result, IntPtr newItem) => 0;
    public int PreNewItem(uint flags, IntPtr destination, string name) => 0;
    public int PostNewItem(uint flags, IntPtr destination, string name, string template, uint attributes, int result, IntPtr newItem) => 0;
    public int UpdateProgress(uint total, uint soFar) => 0;
    public int ResetTimer() => 0;
    public int PauseTimer() => 0;
    public int ResumeTimer() => 0;
}
