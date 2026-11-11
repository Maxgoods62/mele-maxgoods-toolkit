using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core.Tests;

/// <summary>
/// What export_set and export_set_batch (PropertyEditor.Assign) take for an enum and for a string. An enum value was stored as
/// given: a typo, or the EnumType.VALUE text export_get prints, was saved as a name the enum does not have, and reported as a
/// success. A string pasted from export_get kept its quotes. LEC's table of enums is built into it, so no game is needed.
/// </summary>
[TestClass]
public class PropertyEditTests
{
    /// <summary>An empty LE1 package in memory: Assign asks it which game's enums to check against.</summary>
    private static IMEPackage Package()
    {
        LecHost.EnsureInitialized();
        return MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(TestSetup.Temp, "PropertyEdit.pcc"), MEGame.LE1);
    }

    /// <summary>A conversation reply's Category, an EReplyCategory.</summary>
    private static EnumProperty Category(string value = "REPLY_CATEGORY_DEFAULT") => new(value, "EReplyCategory", MEGame.LE1, "Category");

    [TestMethod]
    [DataRow("REPLY_CATEGORY_AGREE")]
    [DataRow("reply_category_agree")]
    [DataRow("EReplyCategory.REPLY_CATEGORY_AGREE")]
    [DataRow(" ereplycategory.Reply_Category_Agree ")]
    public void Enum_AValueItHas_IsStoredUnderTheEnumsOwnName(string text)
    {
        using var pcc = Package();
        var category = Category();

        Assert.IsNull(PropertyEditor.Assign(pcc, category, text));

        Assert.AreEqual("REPLY_CATEGORY_AGREE", category.Value.Instanced);
    }

    [TestMethod]
    public void Enum_ANameItLacks_IsRefused_WithTheNamesItHas()
    {
        using var pcc = Package();
        var category = Category();

        var e = Assert.Throws<ArgumentException>(() => PropertyEditor.Assign(pcc, category, "REPLY_CATEGORY_AGRE"));

        StringAssert.Contains(e.Message, "REPLY_CATEGORY_AGREE");
        StringAssert.Contains(e.Message, "REPLY_CATEGORY_INVESTIGATE");
        Assert.AreEqual("REPLY_CATEGORY_DEFAULT", category.Value.Instanced, "the refused name was stored");
    }

    [TestMethod]
    public void Enum_AValueOfAnotherEnum_IsRefused()
    {
        using var pcc = Package();
        var category = Category();

        var e = Assert.Throws<ArgumentException>(() => PropertyEditor.Assign(pcc, category, "ELightAffectsClassification.LAC_USER_SELECTED"));

        StringAssert.Contains(e.Message, "EReplyCategory");
        Assert.AreEqual("REPLY_CATEGORY_DEFAULT", category.Value.Instanced, "the refused name was stored");
    }

    /// <summary>An enum of a mod's own script class is not in LEC's table: it still has to be settable.</summary>
    [TestMethod]
    public void Enum_LecDoesNotKnow_IsStoredAsGiven_AndTheNoteSaysItWasNotChecked()
    {
        using var pcc = Package();
        var mode = new EnumProperty("None", "EMyModsOwnEnum", MEGame.LE1, "Mode");

        var note = PropertyEditor.Assign(pcc, mode, "EMyModsOwnEnum.MY_Value");

        Assert.AreEqual("MY_Value", mode.Value.Instanced);
        Assert.IsNotNull(note);
        StringAssert.Contains(note, "EMyModsOwnEnum");
    }

    [TestMethod]
    public void Enum_WhatExportGetPrints_GoesBackInUnchanged()
    {
        using var pcc = Package();
        var category = Category("REPLY_CATEGORY_AGREE");
        var shown = ExportInspector.FormatScalar(pcc, category);
        Assert.AreEqual("EReplyCategory.REPLY_CATEGORY_AGREE", shown);

        Assert.IsNull(PropertyEditor.Assign(pcc, category, shown));

        Assert.AreEqual("REPLY_CATEGORY_AGREE", category.Value.Instanced);
    }

    [TestMethod]
    public void String_InTheQuotesExportGetPrints_IsStoredWithoutThem()
    {
        var comment = new StrProperty("old", "m_sComment");
        Assert.IsNull(PropertyEditor.Assign(null!, comment, "plain text"));
        Assert.AreEqual("plain text", comment.Value);

        var shown = ExportInspector.FormatScalar(null!, comment);
        Assert.AreEqual("\"plain text\"", shown);
        var note = PropertyEditor.Assign(null!, comment, shown);
        Assert.AreEqual("plain text", comment.Value);
        Assert.IsNotNull(note);
        StringAssert.Contains(note, "quotes");

        // A pair that belongs to the text is given doubled; one quote alone is text.
        PropertyEditor.Assign(null!, comment, "\"\"kept\"\"");
        Assert.AreEqual("\"kept\"", comment.Value);
        Assert.IsNull(PropertyEditor.Assign(null!, comment, "\""));
        Assert.AreEqual("\"", comment.Value);
    }

    [TestMethod]
    public void OtherKinds_AreStoredAsBefore_WithNoNote()
    {
        var count = new IntProperty(1, "Count");
        var tag = new NameProperty("old", "Tag");

        Assert.IsNull(PropertyEditor.Assign(null!, count, "5"));
        Assert.IsNull(PropertyEditor.Assign(null!, tag, "hench_bonus"));

        Assert.AreEqual(5, count.Value);
        Assert.AreEqual("hench_bonus", tag.Value.Instanced);
    }
}
