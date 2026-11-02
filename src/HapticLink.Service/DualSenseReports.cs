namespace HapticLink.Service;

/// <summary>
/// Report conversions for the virtual wired DualSense bridge, after the Linux hid-playstation driver and SAxense's
/// Bluetooth haptics (facts verified on the owner's DualSense, 2026-11-01).
///   Bluetooth input 0x31 (78 bytes) → USB input 0x01 (64 bytes): the same fields, one byte earlier.
///   USB output 0x02 (48 bytes) → Bluetooth output 0x31 (78 bytes): seq/tag header, the same 47-byte body, CRC.
///   Haptics: Bluetooth report 0x32 (142 bytes) carries 64 signed 8-bit samples, 32 stereo frames at 3 kHz.
///   Speaker: Bluetooth report 0x36 (398 bytes) carries the same haptics block plus one Opus frame.
/// Bluetooth output reports end in a CRC-32 over the HID transaction header 0xA2 and the report.
/// </summary>
public static class DualSenseReports
{
    public const int BtInputLength = 78, UsbInputLength = 64, UsbOutputLength = 48, BtOutputLength = 78, HapticsLength = 142;
    public const int HapticsSamples = 64;

    /// <summary>Bluetooth 0x31 → USB 0x01. False for anything else (basic 0x01 reports, short reads).</summary>
    public static bool ToUsbInput(ReadOnlySpan<byte> bt, Span<byte> usb)
    {
        if (bt.Length < 65 || bt[0] != 0x31 || usb.Length < UsbInputLength) return false;
        usb[0] = 0x01;
        bt.Slice(2, 63).CopyTo(usb[1..]);
        return true;
    }

    /// <summary>USB 0x02 (what games send the virtual pad) → Bluetooth 0x31 for the real one.</summary>
    public static bool ToBtOutput(ReadOnlySpan<byte> usb, byte sequence, Span<byte> bt)
    {
        if (usb.Length < UsbOutputLength || usb[0] != 0x02 || bt.Length < BtOutputLength) return false;
        bt[..BtOutputLength].Clear();
        bt[0] = 0x31;
        bt[1] = (byte)((sequence & 0x0F) << 4);
        bt[2] = 0x10;
        usb.Slice(1, 47).CopyTo(bt[3..]);
        WriteCrc(bt[..BtOutputLength]);
        return true;
    }

    /// <summary>
    /// Releases the light bar: until a Bluetooth pad gets valid_flag2 0x02 with lightbar_setup 0x02 (as hid-playstation sends
    /// when it connects) it ignores colour writes.
    /// </summary>
    public static void LightbarRelease(byte sequence, Span<byte> bt)
    {
        bt[..BtOutputLength].Clear();
        bt[0] = 0x31;
        bt[1] = (byte)((sequence & 0x0F) << 4);
        bt[2] = 0x10;
        bt[3 + 38] = 0x02; // valid_flag2: lightbar setup
        bt[3 + 41] = 0x02; // lightbar_setup: light out (release)
        WriteCrc(bt[..BtOutputLength]);
    }

    /// <summary>
    /// Report 0x32: sub-packet 0x11 (91 07 FE 00 00 00 00 FF counter), sub-packet 0x12 (92 40 + 64 samples, left/right
    /// interleaved), zeros, CRC over bytes 0..137 at 138..141.
    /// </summary>
    public static void Haptics(ReadOnlySpan<sbyte> samples, byte counter, Span<byte> report)
    {
        report[..HapticsLength].Clear();
        report[0] = 0x32;
        report[2] = 0x91; report[3] = 7; report[4] = AudioMask; report[9] = 0xFF; report[10] = counter;
        report[11] = 0x92; report[12] = HapticsSamples;
        for (int i = 0; i < HapticsSamples && i < samples.Length; i++) report[13 + i] = (byte)samples[i];
        WriteCrc(report[..HapticsLength]);
    }

    public const int AudioLength = 398, OpusFrameLength = 200, AudioStateLength = 63;

    /// <summary>
    /// Report 0x36: haptics and one Opus frame for the speaker (or the headphone jack) together, after LinuxAudio4Dualsense5
    /// and DS5_Bridge. Sub-packets, each with bit 7 set and a length byte: 0x11 config at 2, 0x10 audio state at 11 (the
    /// layout of the USB 0x02 body; only the fields its valid flags name are applied), 0x12 haptics at 76, 0x13 speaker
    /// (0x16 headphones) at 142 with 200 bytes of Opus (48 kHz stereo, 10 ms, CBR 160 kbit/s), CRC at 394..397.
    /// </summary>
    public static void Audio(ReadOnlySpan<sbyte> haptics, ReadOnlySpan<byte> opus, ReadOnlySpan<byte> state, bool headphones,
        byte sequence, byte counter, Span<byte> report)
    {
        report[..AudioLength].Clear();
        report[0] = 0x36;
        report[1] = (byte)((sequence & 0x0F) << 4);
        report[2] = 0x91; report[3] = 7; report[4] = AudioMask;
        report[5] = report[6] = report[7] = report[8] = report[9] = HapticsSamples;
        report[10] = counter;
        report[11] = 0x90; report[12] = AudioStateLength;
        state[..Math.Min(state.Length, AudioStateLength)].CopyTo(report[13..]);
        report[76] = 0x92; report[77] = HapticsSamples;
        for (int i = 0; i < HapticsSamples && i < haptics.Length; i++) report[78 + i] = (byte)haptics[i];
        report[142] = (byte)(headphones ? 0x96 : 0x93); report[143] = OpusFrameLength;
        opus[..Math.Min(opus.Length, OpusFrameLength)].CopyTo(report[144..]);
        WriteCrc(report[..AudioLength]);
    }

    /// <summary>
    /// The audio state sub-packet: the USB 0x02 body with only the audio fields valid (headphone and speaker volume,
    /// output path, preamp), so rumble, triggers and lights stay as the game's own reports set them.
    /// </summary>
    public static void AudioState(byte headphoneVolume, byte speakerVolume, byte audioControl, byte audioControl2, Span<byte> state)
    {
        state[..AudioStateLength].Clear();
        state[0] = 0x80 | 0x20 | 0x10; // valid_flag0: audio control, speaker volume, headphone volume
        state[1] = 0x80;               // valid_flag1: audio control 2
        state[4] = headphoneVolume;
        state[5] = speakerVolume;
        state[7] = audioControl;
        state[37] = audioControl2;
    }

    // The audio config's mask (0x11 sub-packet, byte 4). Bit 0 clear: the pad doesn't stream its microphone back (set, it
    // sends Opus frames inside input reports 0x31; see the mic-wip branch).
    private const byte AudioMask = 0xFE;

    /// <summary>Headphones are plugged into the pad (USB input 0x01, byte 54 bit 0).</summary>
    public static bool HeadphonesPlugged(ReadOnlySpan<byte> usbInput) => usbInput.Length > 54 && (usbInput[54] & 0x01) != 0;

    /// <summary>CRC-32 (zlib polynomial) seeded with the transaction header byte 0xA2.</summary>
    public static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = ~0xEADA2D49u; // the state after hashing 0xA2
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static void WriteCrc(Span<byte> report)
    {
        uint crc = Crc(report[..^4]);
        report[^4] = (byte)crc; report[^3] = (byte)(crc >> 8); report[^2] = (byte)(crc >> 16); report[^1] = (byte)(crc >> 24);
    }
}
