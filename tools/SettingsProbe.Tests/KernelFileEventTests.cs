using System.Buffers.Binary;
using System.Text;
using SettingsProbe.Etw;

namespace SettingsProbe.Tests;

public class KernelFileEventTests
{
    private const string NtName = @"\Device\HarddiskVolume3\Scratch\Contoso\settings.json";

    private static byte[] Payload(params object[] fields)
    {
        var bytes = new List<byte>();
        foreach (object f in fields)
        {
            switch (f)
            {
                case ulong u: bytes.AddRange(BitConverter.GetBytes(u)); break;
                case uint u: bytes.AddRange(BitConverter.GetBytes(u)); break;
                case string s: bytes.AddRange(Encoding.Unicode.GetBytes(s + "\0")); break;
                default: throw new ArgumentException("Unsupported field type.");
            }
        }
        return bytes.ToArray();
    }

    [Fact]
    public void Create_v1_with_64_bit_pointers()
    {
        // Irp, FileObject, IssuingThreadId, CreateOptions, CreateAttributes, ShareAccess, FileName
        byte[] p = Payload(0x1111UL, 0xFFFF_A000_0000_0042UL, 77u, 0x0500_0060u, 0x80u, 7u, NtName);

        var c = KernelFileEvents.ParseCreate(p, version: 1, pointerSize: 8);

        Assert.NotNull(c);
        Assert.Equal(0xFFFF_A000_0000_0042UL, c!.Value.FileObject);
        Assert.Equal(5u, c.Value.Disposition); // FILE_OVERWRITE_IF
        Assert.Equal(NtName, c.Value.FileName);
    }

    [Fact]
    public void Create_v0_has_a_thread_pointer_before_the_file_object()
    {
        // Irp, ThreadId (pointer), FileObject, CreateOptions, CreateAttributes, ShareAccess, FileName
        byte[] p = Payload(0x1111UL, 0x2222UL, 0x3333UL, 0x0200_0000u, 0u, 0u, NtName);

        var c = KernelFileEvents.ParseCreate(p, version: 0, pointerSize: 8);

        Assert.Equal(0x3333UL, c!.Value.FileObject);
        Assert.Equal(2u, c.Value.Disposition); // FILE_CREATE
        Assert.Equal(NtName, c.Value.FileName);
    }

    [Fact]
    public void Create_v1_with_32_bit_pointers()
    {
        byte[] p = Payload(0u, 0xABCDu, 1u, 0x0100_0000u, 0u, 0u, NtName);
        var c = KernelFileEvents.ParseCreate(p, version: 1, pointerSize: 4);
        Assert.Equal(0xABCDUL, c!.Value.FileObject);
        Assert.Equal(NtName, c.Value.FileName);
    }

    [Fact]
    public void Write_v1_gives_file_object_and_size()
    {
        // ByteOffset, Irp, FileObject, FileKey, IssuingThreadId, IOSize, IOFlags, ExtraFlags
        byte[] p = Payload(0UL, 0x1UL, 0x4242UL, 0x9UL, 12u, 262144u, 0u, 0u);
        var w = KernelFileEvents.ParseWrite(p, version: 1, pointerSize: 8);
        Assert.Equal(0x4242UL, w!.Value.FileObject);
        Assert.Equal(262144u, w.Value.IoSize);
    }

    [Fact]
    public void Write_v0_has_a_thread_pointer_before_the_file_object()
    {
        // ByteOffset, Irp, ThreadId, FileObject, FileKey, IOSize, IOFlags
        byte[] p = Payload(0UL, 0x1UL, 0x2UL, 0x4242UL, 0x9UL, 44u, 0u);
        var w = KernelFileEvents.ParseWrite(p, version: 0, pointerSize: 8);
        Assert.Equal(0x4242UL, w!.Value.FileObject);
        Assert.Equal(44u, w.Value.IoSize);
    }

    [Fact]
    public void Delete_and_rename_paths_v1_and_v0()
    {
        byte[] v1 = Payload(0x1UL, 0x2UL, 0x3UL, 0x4UL, 5u, 13u, NtName);
        byte[] v0 = Payload(0x1UL, 0x2UL, 0x3UL, 0x4UL, 0x5UL, 13u, NtName);
        Assert.Equal(NtName, KernelFileEvents.ParsePathEvent(v1, version: 1, pointerSize: 8));
        Assert.Equal(NtName, KernelFileEvents.ParsePathEvent(v0, version: 0, pointerSize: 8));
    }

    [Fact]
    public void Truncated_payloads_return_null()
    {
        Assert.Null(KernelFileEvents.ParseCreate(new byte[10], 1, 8));
        Assert.Null(KernelFileEvents.ParseWrite(new byte[10], 1, 8));
        Assert.Null(KernelFileEvents.ParsePathEvent(new byte[10], 1, 8));
    }

    [Fact]
    public void A_name_without_a_terminator_stops_at_the_end()
    {
        byte[] p = Encoding.Unicode.GetBytes("abc");
        Assert.Equal("abc", KernelFileEvents.ReadUnicodeZ(p));
    }

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\Users\me\.probe-scratch\Contoso\settings.json", @"Contoso\settings.json")]
    [InlineData(@"\DEVICE\HARDDISKVOLUME3\USERS\ME\.PROBE-SCRATCH\a.ini", @"a.ini")]
    [InlineData(@"\Device\HarddiskVolume3\Users\me\.probe-scratch\dir\", @"dir")]
    [InlineData(@"\Device\HarddiskVolume3\Users\me\.probe-scratch", null)]
    [InlineData(@"\Device\HarddiskVolume3\Users\me\.probe-scratch2\a.ini", null)]
    [InlineData(@"\Device\HarddiskVolume3\Users\me\Documents\private.docx", null)]
    [InlineData(@"\Device\HarddiskVolume4\Users\me\.probe-scratch\a.ini", null)]
    public void Nt_paths_are_kept_only_under_the_scope(string ntPath, string? expected)
    {
        var scope = new NtPathScope(@"\Device\HarddiskVolume3", @"C:\Users\me\.probe-scratch\");
        Assert.Equal(expected, scope.Relative(ntPath));
    }

    [Fact]
    public void Keywords_cover_create_write_delete_and_rename_only()
    {
        Assert.Equal(0x1E80UL, KernelFileEvents.Keywords);
        Assert.Equal(new Guid("edd08927-9cc4-4e65-b970-c2560fb5c289"), KernelFileEvents.ProviderId);
    }
}
