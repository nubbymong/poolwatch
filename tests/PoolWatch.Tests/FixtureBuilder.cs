using System.Buffers.Binary;
using System.Text;

namespace PoolWatch.Tests;

/// <summary>Builds native-layout byte fixtures (x64) for the parser tests.</summary>
internal static class FixtureBuilder
{
    public static byte[] PoolTags(params (string tag, uint pa, uint pf, ulong pu, uint na, uint nf, ulong nu)[] entries)
    {
        var b = new byte[8 + entries.Length * 40];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            var s = b.AsSpan(8 + i * 40);
            Encoding.Latin1.GetBytes(entries[i].tag.PadRight(4)).CopyTo(s);
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], entries[i].pa);
            BinaryPrimitives.WriteUInt32LittleEndian(s[8..], entries[i].pf);
            BinaryPrimitives.WriteUInt64LittleEndian(s[16..], entries[i].pu);
            BinaryPrimitives.WriteUInt32LittleEndian(s[24..], entries[i].na);
            BinaryPrimitives.WriteUInt32LittleEndian(s[28..], entries[i].nf);
            BinaryPrimitives.WriteUInt64LittleEndian(s[32..], entries[i].nu);
        }
        return b;
    }

    public sealed record Proc(int Pid, int Ppid, long Create, string Name, int Handles, ulong QuotaPaged, ulong QuotaNonPaged, long ReadOps, long OtherOps, int Threads = 3, uint PageFaults = 0, long KernelTime = 0, long UserTime = 0);

    /// <summary>A SYSTEM_PROCESS_INFORMATION chain; each entry has Threads x 0x50 bytes of thread records and its name after them.</summary>
    public static byte[] Processes(ulong baseAddress, params Proc[] procs)
    {
        var buf = new List<byte>();
        var offsets = new List<int>();
        foreach (var p in procs)
        {
            int start = buf.Count;
            offsets.Add(start);
            var nameBytes = Encoding.Unicode.GetBytes(p.Name);
            int size = 0x100 + p.Threads * 0x50 + nameBytes.Length + 8;
            size = (size + 7) & ~7;
            var e = new byte[size];
            var s = e.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(s[0x04..], (uint)p.Threads);
            BinaryPrimitives.WriteInt64LittleEndian(s[0x20..], p.Create);
            BinaryPrimitives.WriteUInt16LittleEndian(s[0x38..], (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(s[0x3A..], (ushort)(nameBytes.Length + 2));
            int nameOff = 0x100 + p.Threads * 0x50;
            if (nameBytes.Length > 0)
                BinaryPrimitives.WriteUInt64LittleEndian(s[0x40..], baseAddress + (ulong)(start + nameOff));
            nameBytes.CopyTo(s[nameOff..]);
            BinaryPrimitives.WriteUInt64LittleEndian(s[0x50..], (ulong)p.Pid);
            BinaryPrimitives.WriteUInt64LittleEndian(s[0x58..], (ulong)p.Ppid);
            BinaryPrimitives.WriteUInt32LittleEndian(s[0x60..], (uint)p.Handles);
            BinaryPrimitives.WriteInt64LittleEndian(s[0x28..], p.UserTime);
            BinaryPrimitives.WriteInt64LittleEndian(s[0x30..], p.KernelTime);
            BinaryPrimitives.WriteUInt32LittleEndian(s[0x80..], p.PageFaults);
            BinaryPrimitives.WriteUInt64LittleEndian(s[0xA0..], p.QuotaPaged);
            BinaryPrimitives.WriteUInt64LittleEndian(s[0xB0..], p.QuotaNonPaged);
            BinaryPrimitives.WriteUInt64LittleEndian(s[0xC8..], 12345678);
            BinaryPrimitives.WriteInt64LittleEndian(s[0xD0..], p.ReadOps);
            BinaryPrimitives.WriteInt64LittleEndian(s[0xD8..], 7);
            BinaryPrimitives.WriteInt64LittleEndian(s[0xE0..], p.OtherOps);
            buf.AddRange(e);
        }
        var arr = buf.ToArray();
        for (int i = 0; i < offsets.Count - 1; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(arr.AsSpan(offsets[i]), (uint)(offsets[i + 1] - offsets[i]));
        return arr;
    }

    public static byte[] Handles(params (int pid, ushort type)[] handles)
    {
        var b = new byte[16 + handles.Length * 40];
        BinaryPrimitives.WriteUInt64LittleEndian(b, (ulong)handles.Length);
        for (int i = 0; i < handles.Length; i++)
        {
            var s = b.AsSpan(16 + i * 40);
            BinaryPrimitives.WriteUInt64LittleEndian(s[8..], (ulong)handles[i].pid);
            BinaryPrimitives.WriteUInt64LittleEndian(s[16..], (ulong)(4 * (i + 1)));
            BinaryPrimitives.WriteUInt16LittleEndian(s[30..], handles[i].type);
        }
        return b;
    }

    public static byte[] ObjectTypes(ulong baseAddress, params (string name, byte index, uint objects, uint handles)[] types)
    {
        var buf = new List<byte>(new byte[8]);
        BinaryPrimitives.WriteUInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buf), (uint)types.Length);
        foreach (var t in types)
        {
            var name = Encoding.Unicode.GetBytes(t.name);
            int max = name.Length + 2;
            var e = new byte[0x68];
            BinaryPrimitives.WriteUInt16LittleEndian(e, (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), (ushort)max);
            BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(8), baseAddress + (ulong)(buf.Count + 0x68));
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x10), t.objects);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x14), t.handles);
            e[0x5A] = t.index;
            buf.AddRange(e);
            buf.AddRange(name);
            buf.AddRange(new byte[2]);
            while (buf.Count % 8 != 0) buf.Add(0);
        }
        return buf.ToArray();
    }
}
