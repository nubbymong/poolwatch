using System.Buffers.Binary;
using System.Text;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Parsing;

/// <summary>
/// Parses the SYSTEM_PROCESS_INFORMATION chain (x64) returned by NtQuerySystemInformation(SystemProcessInformation).
/// The ImageName buffer pointer is an absolute address inside the caller's buffer, so the caller passes the
/// buffer's base address to translate it into an offset.
/// </summary>
public static class ProcessInfoParser
{
    // x64 offsets inside SYSTEM_PROCESS_INFORMATION.
    internal const int NextEntryOffset = 0x00;
    internal const int NumberOfThreads = 0x04;
    internal const int WorkingSetPrivateSize = 0x08;
    internal const int CreateTime = 0x20;
    internal const int UserTime = 0x28;
    internal const int KernelTime = 0x30;
    internal const int ImageNameLength = 0x38;
    internal const int ImageNameBuffer = 0x40;
    internal const int UniqueProcessId = 0x50;
    internal const int InheritedFromUniqueProcessId = 0x58;
    internal const int HandleCount = 0x60;
    internal const int SessionId = 0x64;
    internal const int PageFaultCount = 0x80;
    internal const int WorkingSetSize = 0x90;
    internal const int QuotaPagedPoolUsage = 0xA0;
    internal const int QuotaNonPagedPoolUsage = 0xB0;
    internal const int PrivatePageCount = 0xC8;
    internal const int ReadOperationCount = 0xD0;
    internal const int WriteOperationCount = 0xD8;
    internal const int OtherOperationCount = 0xE0;
    internal const int ReadTransferCount = 0xE8;
    internal const int WriteTransferCount = 0xF0;
    internal const int OtherTransferCount = 0xF8;
    internal const int StructSize = 0x100;

    public static List<ProcessSample> Parse(ReadOnlySpan<byte> buffer, ulong baseAddress)
    {
        var list = new List<ProcessSample>(512);
        int offset = 0;
        while (true)
        {
            if (offset < 0 || offset + StructSize > buffer.Length) throw new FormatException($"Process entry at {offset} exceeds buffer");
            var e = buffer[offset..];
            uint next = U32(e, NextEntryOffset);
            int pid = (int)U64(e, UniqueProcessId);
            string name = ReadImageName(buffer, e, baseAddress) ?? (pid == 0 ? "Idle" : pid == 4 ? "System" : "");
            list.Add(new ProcessSample
            {
                Pid = pid,
                ParentPid = (int)U64(e, InheritedFromUniqueProcessId),
                CreateTime = I64(e, CreateTime),
                ImageName = name,
                ThreadCount = (int)U32(e, NumberOfThreads),
                HandleCount = (int)U32(e, HandleCount),
                SessionId = (int)U32(e, SessionId),
                UserTime = I64(e, UserTime),
                KernelTime = I64(e, KernelTime),
                WorkingSet = U64(e, WorkingSetSize),
                PageFaultCount = U32(e, PageFaultCount),
                QuotaPagedPoolUsage = U64(e, QuotaPagedPoolUsage),
                QuotaNonPagedPoolUsage = U64(e, QuotaNonPagedPoolUsage),
                PrivateBytes = U64(e, PrivatePageCount),
                ReadOperationCount = I64(e, ReadOperationCount),
                WriteOperationCount = I64(e, WriteOperationCount),
                OtherOperationCount = I64(e, OtherOperationCount),
                ReadTransferCount = I64(e, ReadTransferCount),
                WriteTransferCount = I64(e, WriteTransferCount),
                OtherTransferCount = I64(e, OtherTransferCount),
            });
            if (next == 0) break;
            offset += (int)next;
        }
        return list;
    }

    private static string? ReadImageName(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> e, ulong baseAddress)
    {
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(e[ImageNameLength..]);
        ulong ptr = U64(e, ImageNameBuffer);
        if (len == 0 || ptr == 0 || ptr < baseAddress) return null;
        ulong rel = ptr - baseAddress;
        if (rel + len > (ulong)buffer.Length) return null;
        return Encoding.Unicode.GetString(buffer.Slice((int)rel, len));
    }

    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
    private static ulong U64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt64LittleEndian(s[o..]);
    private static long I64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt64LittleEndian(s[o..]);
}
