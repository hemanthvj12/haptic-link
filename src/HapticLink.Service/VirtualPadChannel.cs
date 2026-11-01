using System.IO.MemoryMappedFiles;

namespace HapticLink.Service;

/// <summary>
/// Names and layout shared by the service and the UMDF driver (drivers\HapticLink.Pad, whose PadShared.h holds the same
/// layout in C; a test checks the two agree).
/// </summary>
public static class VirtualPad
{
    /// <summary>The section the service creates and the driver opens by name (one pad for now).</summary>
    public const string SectionName = @"Global\HapticLinkPad0";
    public const int SectionSize = 4096;
    /// <summary>
    /// The virtual pad's container id, also stamped on its speaker endpoint: games pair a pad with the sound device that
    /// shares its container. ("HLNK")
    /// </summary>
    public static readonly Guid ContainerId = new("484c4e4b-0000-0000-0000-000000000000");

    public const uint Magic = 0x31504C48, LayoutVersion = 1; // "HLP1"
    public const int OffMagic = 0, OffVersion = 4, OffInputSeq = 8, OffInput = 12, OffHeartbeat = 76, OffOutputHead = 80,
        OffOutput = 84, SlotSize = 72, RingSlots = 16, OffFeatureMask = 1236, OffFeature05 = 1240, OffFeature09 = 1304,
        OffFeature20 = 1368;
}

/// <summary>
/// The shared memory of one virtual pad: the real pad's feature replies and input reports go in, the output reports
/// games send come out.
/// </summary>
public sealed unsafe class VirtualPadChannel : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private uint _readHead;

    public VirtualPadChannel(MemoryMappedFile file)
    {
        _file = file;
        _view = file.CreateViewAccessor(0, VirtualPad.SectionSize);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
        _readHead = Volatile.Read(ref *(uint*)(_base + VirtualPad.OffOutputHead));
    }

    /// <summary>
    /// Sets up a fresh section: the real pad's calibration (0x05) and firmware (0x20) replies when given, a neutral input
    /// report, then the magic that tells the driver it's ready.
    /// </summary>
    public void Initialize(ReadOnlySpan<byte> feature05, ReadOnlySpan<byte> feature20, ReadOnlySpan<byte> neutralInput)
    {
        uint mask = 0;
        if (feature05.Length > 0) { feature05[..Math.Min(64, feature05.Length)].CopyTo(Span(VirtualPad.OffFeature05, 64)); mask |= 1; }
        if (feature20.Length > 0) { feature20[..Math.Min(64, feature20.Length)].CopyTo(Span(VirtualPad.OffFeature20, 64)); mask |= 4; }
        *(uint*)(_base + VirtualPad.OffFeatureMask) = mask;
        *(uint*)(_base + VirtualPad.OffVersion) = VirtualPad.LayoutVersion;
        WriteInput(neutralInput);
        Thread.MemoryBarrier();
        Volatile.Write(ref *(uint*)(_base + VirtualPad.OffMagic), VirtualPad.Magic);
    }

    /// <summary>The driver's tick counter; it moves about 250 times a second while the virtual pad is running.</summary>
    public uint DriverHeartbeat => Volatile.Read(ref *(uint*)(_base + VirtualPad.OffHeartbeat));

    /// <summary>Publishes a USB input report 0x01 (64 bytes, id included) for games to read.</summary>
    public void WriteInput(ReadOnlySpan<byte> usbReport)
    {
        ref uint seq = ref *(uint*)(_base + VirtualPad.OffInputSeq);
        uint s = Volatile.Read(ref seq);
        Volatile.Write(ref seq, s + 1); // odd: being written
        Thread.MemoryBarrier();
        usbReport[..Math.Min(64, usbReport.Length)].CopyTo(Span(VirtualPad.OffInput, 64));
        Thread.MemoryBarrier();
        Volatile.Write(ref seq, s + 2);
    }

    public delegate void ReportHandler(ReadOnlySpan<byte> report);

    /// <summary>
    /// Hands each output report games wrote since the last call (USB 0x02, id included) to <paramref name="handle"/>, oldest
    /// first. If more than the ring holds piled up, only the newest are delivered.
    /// </summary>
    public int ReadOutputs(ReportHandler handle)
    {
        uint head = Volatile.Read(ref *(uint*)(_base + VirtualPad.OffOutputHead));
        if (head - _readHead > VirtualPad.RingSlots) _readHead = head - VirtualPad.RingSlots;
        int count = 0;
        for (; _readHead != head; _readHead++)
        {
            byte* slot = _base + VirtualPad.OffOutput + (int)(_readHead % VirtualPad.RingSlots) * VirtualPad.SlotSize;
            // The driver sets Seq last; a slot still being filled is retried briefly, then skipped.
            int spins = 0;
            while (Volatile.Read(ref *(uint*)slot) != _readHead + 1 && spins++ < 200) Thread.SpinWait(20);
            if (spins > 200) continue;
            int length = (int)Math.Min(*(uint*)(slot + 4), 64u);
            handle(new ReadOnlySpan<byte>(slot + 8, length));
            count++;
        }
        return count;
    }

    private Span<byte> Span(int offset, int length) => new(_base + offset, length);

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
