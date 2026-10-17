using System.Collections;
using System.IO;
using System.Resources;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// The window/taskbar icon is the WPF resource Assets/app.ico (drawn by tools/make-icon.ps1; the same file is the exe icon).
/// A missing or broken frame shows up as a blurry or blank icon somewhere in Windows, so every size is checked.
/// </summary>
public class AppIconTests
{
    private static readonly int[] RequiredSizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static byte[] ReadEmbeddedIcon()
    {
        var assembly = typeof(App).Assembly;
        using var resources = assembly.GetManifestResourceStream(assembly.GetName().Name + ".g.resources");
        Assert.NotNull(resources);
        using var reader = new ResourceReader(resources!);
        foreach (DictionaryEntry entry in reader)
        {
            if ((string)entry.Key == "assets/app.ico")
            {
                using var buffer = new MemoryStream();
                ((Stream)entry.Value!).CopyTo(buffer);
                return buffer.ToArray();
            }
        }
        throw new Xunit.Sdk.XunitException("Assets/app.ico is not embedded in the app");
    }

    [Fact]
    public void Icon_HasEveryWindowsSize_AndEachFrameIsValid()
    {
        var ico = ReadEmbeddedIcon();
        Assert.Equal(0, BitConverter.ToUInt16(ico, 0));
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2)); // 1 = icon
        var count = BitConverter.ToUInt16(ico, 4);

        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            var size = ico[entry] == 0 ? 256 : ico[entry];
            Assert.Equal(ico[entry], ico[entry + 1]); // square
            Assert.Equal(32, BitConverter.ToUInt16(ico, entry + 6)); // 32 bpp with alpha
            var length = BitConverter.ToInt32(ico, entry + 8);
            var offset = BitConverter.ToInt32(ico, entry + 12);
            Assert.InRange(offset, 6 + 16 * count, ico.Length - length);
            var data = ico.AsSpan(offset, length);

            if (data.StartsWith(PngSignature))
            {
                Assert.Equal(size, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[16..])); // IHDR width
            }
            else
            {
                Assert.Equal(40, BitConverter.ToInt32(data[..4])); // BITMAPINFOHEADER
                Assert.Equal(size, BitConverter.ToInt32(data[4..8]));
                Assert.Equal(size * 2, BitConverter.ToInt32(data[8..12])); // colour rows + AND mask rows
                Assert.Equal(32, BitConverter.ToUInt16(data[14..16]));
                var maskRow = (size + 31) / 32 * 4;
                Assert.Equal(40 + size * size * 4 + maskRow * size, length);
            }
            sizes.Add(size);
        }

        Assert.Equal(RequiredSizes, sizes.OrderBy(s => s));
    }
}
