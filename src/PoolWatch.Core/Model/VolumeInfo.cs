namespace PoolWatch.Core.Model;

public sealed record VolumeInfo(string Root, string FileSystem, string Label, bool? IsDevDrive, string DriveType);
