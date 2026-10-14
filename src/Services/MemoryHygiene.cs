using System;
using System.Runtime;
using System.Runtime.InteropServices;

namespace WinLens.Services;

/// <summary>
/// One-shot memory reclaim run at the end of a translation cycle.
/// A full-screen capture is upscaled ~2x before OCR, so each cycle churns tens of MB of
/// short-lived buffers that land on the Large Object Heap. The LOH is not compacted and its
/// segments are not returned to the OS by default, which leaves the working set inflated
/// after the overlay closes. A single compacting gen-2 collection here gives that memory
/// back. This runs once per user-triggered translation, never in a hot loop.
/// </summary>
public static class MemoryHygiene
{
    public static void Trim()
    {
        // compacting: true compacts the small-object heaps only; the LOH, where the
        // screenshot-sized buffers live, needs this one-shot opt-in (reset by the next GC).
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers(); // release GDI bitmap finalizers
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Compacting the managed heap frees the pages, but Windows keeps them resident in
        // the working set (which is what Task Manager reports). After a translation burst
        // this leaves the process looking far heavier than it is. Asking the OS to trim the
        // working set hands those pages back; they fault back in only if actually needed.
        try { SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1)); }
        catch { /* best effort */ }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
}
