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
}
