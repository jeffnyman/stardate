namespace Stardate.Core.Tests;

/// <summary>The keyboard's scan codes, without a program.</summary>
public class InputTests
{
    [Theory]
    [InlineData(0x04, 0x1E, false)] // A
    [InlineData(0x1D, 0x2C, false)] // Z
    [InlineData(0x27, 0x0B, false)] // 0
    [InlineData(0x29, 0x01, false)] // Esc
    [InlineData(0x2C, 0x39, false)] // Space
    [InlineData(0x38, 0x35, false)] // /
    [InlineData(0x43, 0x44, false)] // F10
    [InlineData(0x45, 0x58, false)] // F12
    [InlineData(0x52, 0x48, true)] // the up arrow, in the AT's own block
    [InlineData(0x60, 0x48, false)] // the keypad's 8, the same key on the XT's keyboard
    [InlineData(0x58, 0x1C, true)] // the keypad's Enter
    [InlineData(0x57, 0x4E, false)] // the keypad's +
    [InlineData(0xE4, 0x1D, true)] // right Ctrl
    [InlineData(0xE5, 0x36, false)] // right Shift
    public void Usb_usages_map_to_set_1_scan_codes(int usage, int code, bool extended)
    {
        Assert.True(ScanCodes.TryFromUsb(usage, out byte scanCode, out bool isExtended));
        Assert.Equal(code, scanCode);
        Assert.Equal(extended, isExtended);
    }

    [Theory]
    [InlineData(0xE3)] // the Windows key
    [InlineData(0x48)] // Pause
    [InlineData(0x00)]
    [InlineData(0x1000)]
    public void Keys_a_pc_of_the_time_lacked_have_no_code(int usage)
        => Assert.False(ScanCodes.TryFromUsb(usage, out _, out _));
}
