using DockCommander.Core.Files;
using Xunit;

namespace DockCommander.Tests;

public sealed class LinuxLocationsTests
{
    [Theory]
    [InlineData("/proc", "proc", false)]
    [InlineData("/mnt/wslg/distro", "ext4", false)]
    [InlineData("/run/user/1000", "tmpfs", false)]
    [InlineData("/snap/app/123", "squashfs", false)]
    [InlineData("/etc/hosts", "ext4", false)]
    [InlineData("/mnt/c", "9p", true)]
    [InlineData("/media/user/USB", "vfat", true)]
    [InlineData("/run/media/user/Data", "ext4", true)]
    [InlineData("/data", "ext4", true)]
    public void FiltersSystemMounts(string path, string format, bool expected) =>
        Assert.Equal(expected, LinuxLocations.IsUserMount(path, format));

    [Theory]
    [InlineData("/mnt/c/", "", "C:")]
    [InlineData("/mnt/d", "long label", "D:")]
    [InlineData("/media/user/01234", "Photos", "Photos")]
    [InlineData("/media/user/USB", "/media/user/USB", "USB")]
    public void UsesCompactNames(string path, string label, string expected) =>
        Assert.Equal(expected, LinuxLocations.ShortName(path, label));
}
