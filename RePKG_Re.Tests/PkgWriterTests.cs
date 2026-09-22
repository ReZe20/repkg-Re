using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RePKG_Re.Application.Exceptions;
using RePKG_Re.Application.Package;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Package.Interfaces;

namespace RePKG_Re.Tests
{
    [TestFixture]
    public class PkgWriterTests
    {
        [Test]
        public void TestWriteAndRead()
        {
            var package = new Package {Magic = "PKGV0005"};

            package.Entries.Add(new PackageEntry
            {
                Bytes = Encoding.ASCII.GetBytes("Hello world!"),
                FullPath = "hello_world.txt",
            });

            package.Entries.Add(new PackageEntry
            {
                Bytes = Encoding.ASCII.GetBytes("Test"),
                FullPath = "test.txt",
            });

            // Write
            IPackageWriter writer = new PackageWriter();
            var stream = new MemoryStream();
            using (var binaryWriter = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.WriteTo(binaryWriter, package);
            }

            // Read
            stream.Position = 0;
            var packageReader = new PackageReader {ReadEntryBytes = true};
            
            Package readPackage;
            using (var binaryReader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                readPackage = packageReader.ReadFrom(binaryReader);
            }

            // Verify
            Assert.AreEqual(package.Magic, readPackage.Magic);
            Assert.AreEqual(package.Entries.Count, readPackage.Entries.Count);

            for (var i = 0; i < package.Entries.Count; i++)
            {
                var entry = package.Entries[i];
                var readEntry = readPackage.Entries[i];

                Assert.AreEqual(entry.Bytes, readEntry.Bytes);
                Assert.AreEqual(entry.Extension, readEntry.Extension);
                Assert.AreEqual(entry.Length, readEntry.Length);
                Assert.AreEqual(entry.Offset, readEntry.Offset);
            }
        }

        private static Package BuildNonAsciiPackage()
        {
            var package = new Package {Magic = "PKGV0005"};

            // 用 \u 转义而不是字面量，避免源码编码影响。三类长度分歧：
            // CJK（1 个 code unit / 3 字节）、代理对（2 / 4）、以及带空格引号反斜杠的路径
            var names = new[]
            {
                "hello_world.txt",
                "materials/\u80CC\u666F.tex",                          // 背景
                "models/\u964B\u77F3/\u964B\u77F3.mdl",                // 陨石
                "materials/\U0001F600icon.tex",                        // emoji
                "sounds/obj_\"quoted\" \\back slash #1.mp3",
            };

            foreach (var name in names)
                package.Entries.Add(new PackageEntry
                {
                    FullPath = name,
                    Bytes = Encoding.UTF8.GetBytes(name),
                });

            return package;
        }

        [Test]
        public void TestWriteNameLengthField_IsUtf8ByteCount()
        {
            var expected = BuildNonAsciiPackage().Entries;

            var stream = new MemoryStream();
            using (var binaryWriter = new BinaryWriter(stream, Encoding.UTF8, true))
                new PackageWriter().WriteTo(binaryWriter, BuildNonAsciiPackage());

            var raw = stream.ToArray();
            var pos = 0;
            pos += 4 + BitConverter.ToInt32(raw, pos); // 魔数的长度字段 + 魔数本身
            var count = BitConverter.ToInt32(raw, pos);
            pos += 4;

            Assert.AreEqual(expected.Count, count);
            var body = 0;
            for (var i = 0; i < count; i++)
            {
                var want = Encoding.UTF8.GetByteCount(expected[i].FullPath);
                var written = BitConverter.ToInt32(raw, pos);
                Assert.AreEqual(want, written, $"条目 {i} 的名字长度字段不是 UTF-8 字节数");
                Assert.AreEqual(expected[i].Bytes.Length, BitConverter.ToInt32(raw, pos + 4 + written + 4), $"条目 {i} 的数据长度字段不符");
                pos += 4 + written + 8; // 名字 + offset + length
                body += expected[i].Bytes.Length;
            }

            Assert.AreEqual(raw.Length, pos + body, "表尾 + Σ数据长度 != 文件大小");
        }

        [Test]
        public void TestWriteAndRead_NonAsciiNamesRoundTrip()
        {
            var package = BuildNonAsciiPackage();

            var stream = new MemoryStream();
            using (var binaryWriter = new BinaryWriter(stream, Encoding.UTF8, true))
                new PackageWriter().WriteTo(binaryWriter, package);

            stream.Position = 0;
            Package readPackage;
            using (var binaryReader = new BinaryReader(stream, Encoding.UTF8, true))
                readPackage = new PackageReader {ReadEntryBytes = true}.ReadFrom(binaryReader);

            Assert.AreEqual(package.Entries.Count, readPackage.Entries.Count);
            for (var i = 0; i < package.Entries.Count; i++)
            {
                var entry = package.Entries[i];
                var readEntry = readPackage.Entries[i];

                Assert.AreEqual(entry.FullPath, readEntry.FullPath, $"条目 {i} 的名字回读不一致");
                Assert.AreEqual(entry.Offset, readEntry.Offset, $"条目 {i} 的偏移回读不一致");
                Assert.AreEqual(entry.Length, readEntry.Length, $"条目 {i} 的长度回读不一致");
                Assert.AreEqual(entry.Bytes, readEntry.Bytes, $"条目 {i} 的数据回读不一致");
            }
        }

        [Test]
        public void TestRead_NameOverLegacy255Clamp_RoundTrips()
        {
            // 旧的读侧把长度字段 Math.Min 到 255 字节，这条 ~296 字节的中文路径会被砍短，
            // 剩下没读的字节又被当成后面的 offset/length，于是从这条起整张表错位且不报错
            var longName = "materials/" + new string('\u80CC', 94) + ".tex";
            Assert.Greater(Encoding.UTF8.GetByteCount(longName), 255);

            var package = new Package {Magic = "PKGV0005"};
            package.Entries.Add(new PackageEntry {FullPath = longName, Bytes = Encoding.UTF8.GetBytes("payload")});
            package.Entries.Add(new PackageEntry {FullPath = "second.txt", Bytes = Encoding.UTF8.GetBytes("second")});

            var read = RoundTrip(package);

            Assert.AreEqual(longName, read.Entries[0].FullPath);
            Assert.AreEqual("second.txt", read.Entries[1].FullPath);
            Assert.AreEqual(Encoding.UTF8.GetBytes("payload"), read.Entries[0].Bytes);
            Assert.AreEqual(Encoding.UTF8.GetBytes("second"), read.Entries[1].Bytes);
        }

        [Test]
        public void TestRead_OversizedNameLength_ThrowsInsteadOfTruncating()
        {
            var magic = Encoding.ASCII.GetBytes("PKGV0005");
            var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                w.Write(magic.Length);
                w.Write(magic);
                w.Write(1);          // 条目数
                w.Write(4096);       // 声明的名字长度远超任何合法包
                w.Write(Encoding.UTF8.GetBytes(new string('x', 4096)));
                w.Write(0);          // offset
                w.Write(4);          // length
                w.Write(Encoding.UTF8.GetBytes("data"));
            }

            Assert.Throws<UnsafePkgException>(() => ReadPackage(stream));
        }

        [Test]
        public void TestRead_OversizedMagicLength_Throws()
        {
            // 魔数字段是同一个缺陷：截断读取会把剩下的字节留给后面的字段，整次读取全部带偏
            var magic = Encoding.ASCII.GetBytes(new string('M', 40));
            var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                w.Write(magic.Length);
                w.Write(magic);
                w.Write(0);
            }

            Assert.Throws<UnsafePkgException>(() => ReadPackage(stream));
        }

        private static Package RoundTrip(Package package)
        {
            var stream = new MemoryStream();
            using (var binaryWriter = new BinaryWriter(stream, Encoding.UTF8, true))
                new PackageWriter().WriteTo(binaryWriter, package);

            return ReadPackage(stream, readEntryBytes: true);
        }

        private static Package ReadPackage(MemoryStream stream, bool readEntryBytes = false)
        {
            stream.Position = 0;
            using (var binaryReader = new BinaryReader(stream, Encoding.UTF8, true))
                return new PackageReader {ReadEntryBytes = readEntryBytes}.ReadFrom(binaryReader);
        }
    }
}