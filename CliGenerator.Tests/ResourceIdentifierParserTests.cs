using Console.Cli.Shared;

namespace CliGenerator.Tests;

[TestClass]
public class ResourceIdentifierParserTests
{
    // -----------------------------------------------------------------------
    // Parse — basic positional forms
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Parse_BareName_ReturnsNameOnly()
    {
        var r = ResourceIdentifierParser.Parse("myvault");
        Assert.IsNull(r.SubscriptionSegment);
        Assert.IsNull(r.ResourceGroupSegment);
        Assert.AreEqual("myvault", r.ResourceNameSegment);
    }

    [TestMethod]
    public void Parse_RgSlashName_ReturnsRgAndName()
    {
        var r = ResourceIdentifierParser.Parse("rg/kv");
        Assert.IsNull(r.SubscriptionSegment);
        Assert.AreEqual("rg", r.ResourceGroupSegment);
        Assert.AreEqual("kv", r.ResourceNameSegment);
    }

    [TestMethod]
    public void Parse_SubSlashRgSlashName_ReturnsAll()
    {
        var r = ResourceIdentifierParser.Parse("prod/rg/kv");
        Assert.AreEqual("prod", r.SubscriptionSegment);
        Assert.AreEqual("rg", r.ResourceGroupSegment);
        Assert.AreEqual("kv", r.ResourceNameSegment);
    }

    // -----------------------------------------------------------------------
    // Parse — /s/ short prefix
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Parse_ShortSubPrefix_ExtractsAllSegments()
    {
        var r = ResourceIdentifierParser.Parse("/s/prod/my-rg/kv");
        Assert.AreEqual("/s/prod", r.SubscriptionSegment);
        Assert.AreEqual("my-rg", r.ResourceGroupSegment);
        Assert.AreEqual("kv", r.ResourceNameSegment);
    }

    [TestMethod]
    public void Parse_ShortSubPrefix_WithGuid()
    {
        var guid = "00000000-0000-0000-0000-000000000001";
        var r = ResourceIdentifierParser.Parse($"/s/{guid}/my-rg/my-vault");
        Assert.AreEqual($"/s/{guid}", r.SubscriptionSegment);
        Assert.AreEqual("my-rg", r.ResourceGroupSegment);
        Assert.AreEqual("my-vault", r.ResourceNameSegment);
    }

    // -----------------------------------------------------------------------
    // Parse — /subscriptions/ long prefix
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Parse_FullSubscriptionsPrefix_ExtractsAllSegments()
    {
        var guid = "00000000-0000-0000-0000-000000000002";
        var r = ResourceIdentifierParser.Parse($"/subscriptions/{guid}/rg/kv");
        Assert.AreEqual($"/subscriptions/{guid}", r.SubscriptionSegment);
        Assert.AreEqual("rg", r.ResourceGroupSegment);
        Assert.AreEqual("kv", r.ResourceNameSegment);
    }

    // -----------------------------------------------------------------------
    // Parse — resource-type short prefix (e.g. /kv/)
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Parse_TypeShortPrefix_StripsPrefixAndReturnsBareNameOnly()
    {
        // /kv/myvault → the /kv/ leading prefix is stripped → bare name "myvault"
        var r = ResourceIdentifierParser.Parse("/kv/myvault");
        Assert.IsNull(r.SubscriptionSegment);
        Assert.IsNull(r.ResourceGroupSegment);
        Assert.AreEqual("myvault", r.ResourceNameSegment);
    }

    // -----------------------------------------------------------------------
    // NormalizeSubscriptionSegment
    // -----------------------------------------------------------------------

    [TestMethod]
    public void NormalizeSubscriptionSegment_ShortPrefix_ConvertsToLong()
    {
        var guid = "00000000-0000-0000-0000-000000000003";
        Assert.AreEqual(
            $"/subscriptions/{guid}",
            ResourceIdentifierParser.NormalizeSubscriptionSegment($"/s/{guid}")
        );
    }

    [TestMethod]
    public void NormalizeSubscriptionSegment_LongPrefix_PassesThrough()
    {
        var guid = "00000000-0000-0000-0000-000000000004";
        var value = $"/subscriptions/{guid}";
        Assert.AreEqual(value, ResourceIdentifierParser.NormalizeSubscriptionSegment(value));
    }

    [TestMethod]
    public void NormalizeSubscriptionSegment_Guid_PassesThrough()
    {
        var guid = "00000000-0000-0000-0000-000000000005";
        Assert.AreEqual(guid, ResourceIdentifierParser.NormalizeSubscriptionSegment(guid));
    }

    [TestMethod]
    public void NormalizeSubscriptionSegment_DisplayName_PassesThrough()
    {
        Assert.AreEqual("my-sub", ResourceIdentifierParser.NormalizeSubscriptionSegment("my-sub"));
    }

    [TestMethod]
    public void NormalizeSubscriptionSegment_Null_ReturnsNull()
    {
        Assert.IsNull(ResourceIdentifierParser.NormalizeSubscriptionSegment(null));
    }

    // -----------------------------------------------------------------------
    // NormalizeResourceGroupSegment
    // -----------------------------------------------------------------------

    [TestMethod]
    public void NormalizeResourceGroupSegment_RgPrefix_Stripped()
    {
        Assert.AreEqual(
            "my-rg",
            ResourceIdentifierParser.NormalizeResourceGroupSegment("/rg/my-rg")
        );
    }

    [TestMethod]
    public void NormalizeResourceGroupSegment_PlainName_PassesThrough()
    {
        Assert.AreEqual("my-rg", ResourceIdentifierParser.NormalizeResourceGroupSegment("my-rg"));
    }

    [TestMethod]
    public void NormalizeResourceGroupSegment_Null_ReturnsNull()
    {
        Assert.IsNull(ResourceIdentifierParser.NormalizeResourceGroupSegment(null));
    }

    // -----------------------------------------------------------------------
    // Edge cases
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Parse_EmptyString_Throws()
    {
        bool threw = false;
        try
        {
            ResourceIdentifierParser.Parse("");
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Assert.IsTrue(threw, "Expected ArgumentException for empty string");
    }

    [TestMethod]
    public void Parse_RgSegmentWithRgPrefix_NormalisesRg()
    {
        // "/rg/my-rg/my-vault" → leading /rg/ prefix stripped → "my-rg/my-vault"
        var r = ResourceIdentifierParser.Parse("/rg/my-rg/my-vault");
        Assert.IsNull(r.SubscriptionSegment);
        Assert.AreEqual("my-rg", r.ResourceGroupSegment);
        Assert.AreEqual("my-vault", r.ResourceNameSegment);
    }
}
