using HapticLink.Service;

namespace HapticLink.Tests;

/// <summary>
/// The report conversions between the real Bluetooth pad and the virtual wired
/// one, and the shared-memory layout the service and the C driver must agree on.
/// </summary>
public class DualSenseBridgeTests
{
    /// <summary>The CRC every Bluetooth output report ends in: zlib CRC-32 over the header byte 0xA2, then the report.</summary>
    private static uint ReferenceCrc(ReadOnlySpan<byte> report)
    {
        uint crc = 0xFFFFFFFF;
        void Add(byte b)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        Add(0xA2);
        foreach (var b in report) Add(b);
        return ~crc;
    }

    [Fact]
    public void Crc_is_seeded_with_the_transaction_header()
    {
        Assert.Equal(0xEADA2D49u, DualSenseReports.Crc([]));
        byte[] data = [0x31, 0x00, 0x10, 0x03, 0x00, 0xC8, 0xC8];
        Assert.Equal(ReferenceCrc(data), DualSenseReports.Crc(data));
    }

    [Fact]
    public void Bluetooth_input_becomes_the_usb_report_one_byte_earlier()
    {
        var bt = new byte[DualSenseReports.BtInputLength];
        bt[0] = 0x31;
        bt[1] = 0xA0; // Bluetooth-only header byte (sequence)
        for (int i = 2; i < 65; i++) bt[i] = (byte)i;
        var usb = new byte[DualSenseReports.UsbInputLength];

        Assert.True(DualSenseReports.ToUsbInput(bt, usb));
        Assert.Equal(0x01, usb[0]);
        for (int i = 1; i < 64; i++) Assert.Equal(bt[i + 1], usb[i]);
    }

    [Fact]
    public void Basic_bluetooth_reports_are_not_forwarded()
    {
        var basic = new byte[DualSenseReports.BtInputLength];
        basic[0] = 0x01; // Windows pads basic reports to the full length; they hold no full input
        Assert.False(DualSenseReports.ToUsbInput(basic, new byte[64]));
    }

    [Fact]
    public void Game_output_becomes_a_bluetooth_report_with_header_and_crc()
    {
        var usb = new byte[DualSenseReports.UsbOutputLength];
        usb[0] = 0x02;
        usb[1] = 0x0C; usb[2] = 0x57; usb[39] = 0x08;   // flags: triggers, lights…
        usb[11] = 0x25; usb[12] = 0x40;                  // R2 weapon effect (seen from Stellar Blade)
        usb[45] = 0x80; usb[47] = 0xFF;                  // light bar colour
        var bt = new byte[DualSenseReports.BtOutputLength];

        Assert.True(DualSenseReports.ToBtOutput(usb, 0x13, bt));
        Assert.Equal(new byte[] { 0x31, 0x30, 0x10 }, bt[..3]); // sequence wraps at 16: 0x13 -> 3
        Assert.Equal(usb[1..48], bt[3..50]);
        Assert.All(bt[50..74], b => Assert.Equal(0, b));
        Assert.Equal(ReferenceCrc(bt.AsSpan(0, 74)), BitConverter.ToUInt32(bt, 74));
    }

    [Fact]
    public void Light_bar_release_marks_the_setup_field()
    {
        var bt = new byte[DualSenseReports.BtOutputLength];
        DualSenseReports.LightbarRelease(0, bt);
        Assert.Equal(0x31, bt[0]);
        Assert.Equal(0x02, bt[3 + 38]); // valid_flag2: lightbar setup
        Assert.Equal(0x02, bt[3 + 41]); // lightbar_setup: release
        Assert.Equal(ReferenceCrc(bt.AsSpan(0, 74)), BitConverter.ToUInt32(bt, 74));
    }

    [Fact]
    public void Haptics_report_matches_the_layout_the_pad_accepted()
    {
        // Verified on the owner's DualSense, 2026-11-01: 142 bytes; the first version (141, CRC one byte early) did nothing.
        var samples = new sbyte[DualSenseReports.HapticsSamples];
        for (int i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i % 2 == 0 ? 100 - i : -50);
        var report = new byte[DualSenseReports.HapticsLength];

        DualSenseReports.Haptics(samples, 7, report);

        Assert.Equal(142, report.Length);
        Assert.Equal(new byte[] { 0x32, 0x00, 0x91, 0x07, 0xFE, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x07, 0x92, 0x40 }, report[..13]);
        for (int i = 0; i < 64; i++) Assert.Equal((byte)samples[i], report[13 + i]);
        Assert.All(report[77..138], b => Assert.Equal(0, b));
        Assert.Equal(ReferenceCrc(report.AsSpan(0, 138)), BitConverter.ToUInt32(report, 138));
    }

    [Fact]
    public void Audio_report_matches_the_known_layout()
    {
        // LinuxAudio4Dualsense5 / DS5_Bridge: 398 bytes, sub-packets at 2, 11, 76 and 142, CRC over the first 394.
        var haptics = new sbyte[DualSenseReports.HapticsSamples];
        for (int i = 0; i < haptics.Length; i++) haptics[i] = (sbyte)(i - 32);
        var opus = new byte[DualSenseReports.OpusFrameLength];
        for (int i = 0; i < opus.Length; i++) opus[i] = (byte)(i * 7);
        var state = new byte[DualSenseReports.AudioStateLength];
        DualSenseReports.AudioState(0x7F, 0x64, 0x30, 0x02, state);
        var report = new byte[DualSenseReports.AudioLength];

        DualSenseReports.Audio(haptics, opus, state, headphones: false, sequence: 3, counter: 9, report);

        Assert.Equal(new byte[] { 0x36, 0x30, 0x91, 0x07, 0xFE, 64, 64, 64, 64, 64, 9, 0x90, 63 }, report[..13]);
        Assert.Equal(new byte[] { 0xB0, 0x80, 0, 0, 0x7F, 0x64, 0, 0x30 }, report[13..21]);
        Assert.Equal(0x02, report[13 + 37]);
        Assert.Equal(new byte[] { 0x92, 64 }, report[76..78]);
        for (int i = 0; i < 64; i++) Assert.Equal((byte)haptics[i], report[78 + i]);
        Assert.Equal(new byte[] { 0x93, 200 }, report[142..144]);
        Assert.Equal(opus, report[144..344]);
        Assert.All(report[344..394], b => Assert.Equal(0, b));
        Assert.Equal(ReferenceCrc(report.AsSpan(0, 394)), BitConverter.ToUInt32(report, 394));

        DualSenseReports.Audio(haptics, opus, state, headphones: true, sequence: 0, counter: 0, report);
        Assert.Equal(0x96, report[142]);
    }

    [Fact]
    public void Shared_memory_layout_matches_the_driver_header()
    {
        Assert.Equal(VirtualPad.OffOutput + VirtualPad.RingSlots * VirtualPad.SlotSize, VirtualPad.OffFeatureMask);
        Assert.Equal(1432, VirtualPad.OffFeature20 + 64);

        // The C driver's PadShared.h states the same offsets; a one-sided edit must fail here.
        var header = File.ReadAllText(Path.Combine(RepoRoot(), "drivers", "HapticLink.Pad", "PadShared.h"));
        Assert.Contains("sizeof(PHPAD_SLOT) == 72", header);
        Assert.Contains("sizeof(PHPAD_SHARED) == 1432", header);
        Assert.Contains($"L\"Global\\\\HapticLinkPad0\"", header);
        foreach (var (field, offset) in new[] { ("Input[64]", VirtualPad.OffInput), ("OutputHead", VirtualPad.OffOutputHead),
                     ("FeatureMask", VirtualPad.OffFeatureMask), ("Feature05[64]", VirtualPad.OffFeature05), ("Feature20[64]", VirtualPad.OffFeature20) })
        {
            // e.g. "    uint8_t           Input[64];   //   12, USB input report 0x01, id included"
            var line = header.Split('\n').Single(l => l.Contains(field + ";"));
            var stated = System.Text.RegularExpressions.Regex.Match(line, @"//\s*(\d+)").Groups[1].Value;
            Assert.Equal(offset.ToString(), stated);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HapticLink.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void HidHide_undo_survives_a_crash_file_from_an_older_service()
    {
        var undo = new HidHideControl.Undo(@"HID\PAD", Unhide: false, CloakOff: true, InverseOn: true);
        Assert.Equal(undo, HidHideControl.Undo.Deserialize(undo.Serialize()));
        // Services before 1.2.1 wrote three lines (no inverse mode).
        Assert.Equal(new HidHideControl.Undo(@"HID\PAD", true, false, false), HidHideControl.Undo.Deserialize(@"HID\PAD" + "\n1\n0\n"));
        Assert.False(new HidHideControl.Undo(@"HID\PAD", false, false).Changed);
        Assert.Null(HidHideControl.Undo.Deserialize(""));
    }

    [Fact]
    public void Instance_id_comes_from_the_device_path()
    {
        const string path = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#b&311692c8&11&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        Assert.Equal(@"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\B&311692C8&11&0000", PadConnection.InstanceIdOf(path));
    }

    [Fact]
    public void Steam_driver_entries_are_read_from_a_streamed_zip()
    {
        // Written to a non-seekable stream, entries get data descriptors like Steam's drivers.exe; a few junk bytes
        // in front stand in for the exe part.
        var inf = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("[Version] Signature=$Windows NT$\r\n", 50)));
        var sys = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        var buffer = new MemoryStream();
        buffer.Write([0x4D, 0x5A, 0x90, 0x00]);
        using (var zip = new System.IO.Compression.ZipArchive(new NonSeekable(buffer), System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in new[] { ("drivers/Windows10/x64/SteamStreamingSpeakers.inf", inf), ("drivers/Windows10/x64/SteamStreamingSpeakers.sys", sys) })
                using (var entry = zip.CreateEntry(name).Open()) entry.Write(data);
        }
        var bytes = buffer.ToArray();
        Assert.Equal(inf, SteamSpeakerDriver.Extract(bytes, "drivers/windows10/x64/steamstreamingspeakers.inf"));
        Assert.Equal(sys, SteamSpeakerDriver.Extract(bytes, "drivers/Windows10/x64/SteamStreamingSpeakers.sys"));
        Assert.Null(SteamSpeakerDriver.Extract(bytes, "drivers/Windows10/x64/steamstreamingspeakers.cat"));
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
