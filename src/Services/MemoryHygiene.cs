using System;

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
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers(); // release GDI bitmap finalizers
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
