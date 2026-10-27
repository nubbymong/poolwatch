using PoolWatch.Collectors;

namespace PoolWatch.AppTests;

/// <summary>
/// The write policy's volume rule: PoolWatch never writes its own files on a ReFS volume (every Dev Drive is one), whose
/// per-file metadata is the kernel pool it measures. It replaces a hard-coded drive letter. Pure: no volume is queried.
/// </summary>
public class VolumeRuleTests
{
    [Theory]
    [InlineData("ReFS", true)]
    [InlineData("refs", true)]
    [InlineData("NTFS", false)]
    [InlineData("FAT32", false)]
    [InlineData("exFAT", false)]
    [InlineData("", false)]
    public void OnlyReFSVolumesAreRefused(string fileSystem, bool refused)
    {
        string? why = VolumeIdentity.FileSystemRefusal(fileSystem);
        Assert.Equal(refused, why is not null);
        if (refused) Assert.Contains("ReFS", why);
    }
}
