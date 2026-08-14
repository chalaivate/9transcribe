using NineTranscribe.Hotkeys;
using NineTranscribe.Settings;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class HotkeyDisplayTests
{
    private const ushort VkNone = 0x00;
    private const ushort VkCapsLock = 0x14;
    private const ushort VkSpace = 0x20;
    private const ushort VkD = 0x44;
    private const ushort VkH = 0x48;
    private const ushort VkLeftWin = 0x5B;
    private const ushort VkF1 = 0x70;
    private const ushort VkF5 = 0x74;
    private const ushort VkF8 = 0x77;
    private const ushort VkF9 = 0x78;
    private const ushort VkLeftShift = 0xA0;
    private const ushort VkRightShift = 0xA1;
    private const ushort VkRightControl = 0xA3;
    private const ushort VkOem3 = 0xC0;

    [Fact]
    public void Describe_BareModifierBinding_UsesTheSideSpecificName()
    {
        Assert.Equal("Right Ctrl", HotkeyDisplay.Describe(HotkeyBinding.RightControl()));
    }

    [Fact]
    public void Describe_ComboBinding_ListsModifiersThenTheMainKey()
    {
        var binding = new HotkeyBinding
        {
            Vk = VkSpace,
            Mods = (int)(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt),
        };

        Assert.Equal("Ctrl + Alt + Space", HotkeyDisplay.Describe(binding));
    }

    [Fact]
    public void Describe_UnsetBinding_ReturnsTheThaiPlaceholder()
    {
        Assert.Equal("(ไม่ได้ตั้ง)", HotkeyDisplay.Describe(HotkeyBinding.None()));
        Assert.Equal("(ไม่ได้ตั้ง)", HotkeyDisplay.Describe(VkNone, HotkeyModifiers.None));
    }

    [Fact]
    public void Describe_FunctionKey_IsStableWithoutTheKeyboardLayout()
    {
        Assert.Equal("F8", HotkeyDisplay.Describe(HotkeyBinding.F8()));
        Assert.Equal("Win + H", HotkeyDisplay.Describe(VkH, HotkeyModifiers.Win));
    }

    [Fact]
    public void Describe_ModifiersOnly_OmitsTheMainKey()
    {
        Assert.Equal("Ctrl + Shift", HotkeyDisplay.Describe(VkNone, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift));
    }

    [Theory]
    [InlineData(VkOem3, HotkeyModifiers.None)]
    [InlineData(VkSpace, HotkeyModifiers.Win)]
    [InlineData(VkH, HotkeyModifiers.Win)]
    [InlineData(VkSpace, HotkeyModifiers.Ctrl)]
    [InlineData(VkF1, HotkeyModifiers.None)]
    [InlineData(VkF5, HotkeyModifiers.None)]
    [InlineData(VkF9, HotkeyModifiers.None)]
    [InlineData(VkD, HotkeyModifiers.None)]
    [InlineData(VkCapsLock, HotkeyModifiers.None)]
    [InlineData(VkLeftShift, HotkeyModifiers.Alt)]
    [InlineData(VkRightShift, HotkeyModifiers.Ctrl)]
    public void IsRisky_FlagsTheKnownCollisions(ushort vk, HotkeyModifiers mods)
    {
        Assert.True(HotkeyDisplay.IsRisky(vk, mods, out string warning));
        Assert.NotEmpty(warning);
    }

    [Theory]
    [InlineData(VkF8, HotkeyModifiers.None)]
    [InlineData(VkD, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)]
    [InlineData(VkRightControl, HotkeyModifiers.None)]
    [InlineData(VkF9, HotkeyModifiers.Ctrl)]
    public void IsRisky_LeavesSafeBindingsAlone(ushort vk, HotkeyModifiers mods)
    {
        Assert.False(HotkeyDisplay.IsRisky(vk, mods, out string warning));
        Assert.Equal(string.Empty, warning);
    }

    [Theory]
    [InlineData(VkRightControl, true)]
    [InlineData(VkLeftShift, true)]
    [InlineData(VkLeftWin, true)]
    [InlineData(VkF8, false)]
    [InlineData(VkSpace, false)]
    [InlineData(VkCapsLock, false)]
    public void IsModifierKey_CoversBothSidesOfEveryModifier(ushort vk, bool expected)
    {
        Assert.Equal(expected, HotkeyDisplay.IsModifierKey(vk));
    }

    [Theory]
    [InlineData(VkRightControl, HotkeyModifiers.None, false)]
    [InlineData(VkF8, HotkeyModifiers.None, true)]
    [InlineData(VkSpace, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, true)]
    [InlineData(VkNone, HotkeyModifiers.None, false)]
    public void ShouldSwallowByDefault_NeverHidesALoneModifier(ushort vk, HotkeyModifiers mods, bool expected)
    {
        Assert.Equal(expected, HotkeyDisplay.ShouldSwallowByDefault(vk, mods));
    }
}
