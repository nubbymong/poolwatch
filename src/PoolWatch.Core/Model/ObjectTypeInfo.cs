namespace PoolWatch.Core.Model;

/// <summary>An object-manager type (File, Event, Key, ...) from NtQueryObject(ObjectTypesInformation).</summary>
public readonly record struct ObjectTypeInfo(int TypeIndex, string Name, uint TotalObjects, uint TotalHandles);
