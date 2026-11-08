using System.Text;

namespace MeleMcp.Core.Tests;

/// <summary>
/// AtomicFile, the one way the server writes a whole file (src/BannedSymbols.txt): it keeps File.WriteAllText's bytes, and a
/// write that fails or is refused leaves the old file as it was, with no temporary file beside it.
/// </summary>
[TestClass]
public class AtomicFileTests
{
    [TestMethod]
    public void Write_ReplacesTheFile_AndLeavesNoTemporaryFile()
    {
        var dir = TestSetup.NewFolder("atomic-replace");
        var path = Path.Combine(dir, "a.ini");
        File.WriteAllText(path, "old");

        Assert.IsTrue(AtomicFile.WriteAllText(path, "new"));

        Assert.AreEqual("new", File.ReadAllText(path));
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(dir));
    }

    [TestMethod]
    public void Text_HasFileWriteAllTextsBytes_WithAndWithoutAByteOrderMark()
    {
        var dir = TestSetup.NewFolder("atomic-text");
        var plain = Path.Combine(dir, "plain.txt");
        var bom = Path.Combine(dir, "bom.txt");
        var latin = Path.Combine(dir, "latin.ini");

        AtomicFile.WriteAllText(plain, "é");
        AtomicFile.WriteAllText(bom, "é", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        AtomicFile.WriteAllText(latin, "é", Encoding.Latin1);

        CollectionAssert.AreEqual(new byte[] { 0xC3, 0xA9 }, File.ReadAllBytes(plain));
        CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF, 0xC3, 0xA9 }, File.ReadAllBytes(bom));
        CollectionAssert.AreEqual(new byte[] { 0xE9 }, File.ReadAllBytes(latin));
    }

    [TestMethod]
    public void FailingWriter_LeavesTheOldFile_AndNoTemporaryFile()
    {
        var dir = TestSetup.NewFolder("atomic-fail");
        var path = Path.Combine(dir, "a.tlk");
        File.WriteAllText(path, "old");

        Assert.ThrowsExactly<IOException>(() => AtomicFile.Write(path, temporary =>
        {
            File.WriteAllText(temporary, "half");
            throw new IOException("disk full");
        }));

        Assert.AreEqual("old", File.ReadAllText(path));
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(dir));
    }

    [TestMethod]
    public void RefusedBeforeTheMove_LeavesTheOldFile_AndReturnsFalse()
    {
        var dir = TestSetup.NewFolder("atomic-refused");
        var path = Path.Combine(dir, "session.json");
        File.WriteAllText(path, "old");
        string? seen = null;

        var replaced = AtomicFile.WriteAllText(path, "new", beforeMove: temporary =>
        {
            seen = File.ReadAllText(temporary);
            return false;
        });

        Assert.IsFalse(replaced);
        Assert.AreEqual("new", seen, "the check runs with the new content written and closed");
        Assert.AreEqual("old", File.ReadAllText(path));
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(dir));
    }
}
