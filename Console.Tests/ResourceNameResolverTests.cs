using Azure.ResourceManager;
using Console.Cli.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Tests;

/// <summary>
/// Tests for <see cref="ResourceNameResolver"/> covering the sync/conflict paths
/// that require no ARM network calls.
/// </summary>
[TestClass]
public class ResourceNameResolverTests
{
    // Placeholder ArmClient — should never be called in the non-ARM-lookup paths.
    private static ArmClient NoCallArmClient => new(new Azure.Identity.DefaultAzureCredential());

    // ── S/G/N combined format ─────────────────────────────────────────────────

    [TestMethod]
    public async Task SubRgName_CombinedForm_ReturnsParsedSegments()
    {
        var (sub, rg, name) = await ResourceNameResolver.ResolveAsync(
            "mySub/myRg/myCluster",
            explicitSubscriptionId: null,
            explicitResourceGroupName: null,
            NoCallArmClient,
            "Microsoft.ContainerService/managedClusters"
        );

        Assert.AreEqual("mySub", sub);
        Assert.AreEqual("myRg", rg);
        Assert.AreEqual("myCluster", name);
    }

    [TestMethod]
    public async Task SubscriptionsGuid_CombinedForm_ExtractsGuid()
    {
        var guid = "12345678-1234-1234-1234-123456789abc";
        var (sub, rg, name) = await ResourceNameResolver.ResolveAsync(
            $"/subscriptions/{guid}/myRg/myCluster",
            explicitSubscriptionId: null,
            explicitResourceGroupName: null,
            NoCallArmClient,
            "Microsoft.ContainerService/managedClusters"
        );

        // /subscriptions/{guid} is normalised to the bare GUID for use in URL paths
        Assert.AreEqual(guid, sub);
        Assert.AreEqual("myRg", rg);
        Assert.AreEqual("myCluster", name);
    }

    // ── G/N combined form ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task RgName_CombinedForm_UsesExplicitSubId()
    {
        var (sub, rg, name) = await ResourceNameResolver.ResolveAsync(
            "myRg/myCluster",
            explicitSubscriptionId: "explicit-sub-id",
            explicitResourceGroupName: null,
            NoCallArmClient,
            "Microsoft.ContainerService/managedClusters"
        );

        Assert.AreEqual("explicit-sub-id", sub);
        Assert.AreEqual("myRg", rg);
        Assert.AreEqual("myCluster", name);
    }

    // ── bare name with explicit --resource-group ──────────────────────────────

    [TestMethod]
    public async Task Name_WithExplicitRg_UsesExplicitSubAndRg()
    {
        var (sub, rg, name) = await ResourceNameResolver.ResolveAsync(
            "myCluster",
            explicitSubscriptionId: "explicit-sub-id",
            explicitResourceGroupName: "explicit-rg",
            NoCallArmClient,
            "Microsoft.ContainerService/managedClusters"
        );

        Assert.AreEqual("explicit-sub-id", sub);
        Assert.AreEqual("explicit-rg", rg);
        Assert.AreEqual("myCluster", name);
    }

    // ── Conflict detection ────────────────────────────────────────────────────

    [TestMethod]
    public async Task RgName_PlusExplicitRg_ThrowsAmbiguousRg()
    {
        await Assert.ThrowsExceptionAsync<InvocationException>(async () =>
            await ResourceNameResolver.ResolveAsync(
                "myRg/myCluster",
                explicitSubscriptionId: null,
                explicitResourceGroupName: "otherRg",
                NoCallArmClient,
                "Microsoft.ContainerService/managedClusters"
            )
        );
    }

    [TestMethod]
    public async Task SubRgName_PlusExplicitSub_ThrowsAmbiguousSub()
    {
        await Assert.ThrowsExceptionAsync<InvocationException>(async () =>
            await ResourceNameResolver.ResolveAsync(
                "mySub/myRg/myCluster",
                explicitSubscriptionId: "otherSub",
                explicitResourceGroupName: null,
                NoCallArmClient,
                "Microsoft.ContainerService/managedClusters"
            )
        );
    }

    [TestMethod]
    public async Task SubRgName_PlusBothExplicit_ThrowsAmbiguousSub()
    {
        // When both sub and rg segments conflict, the sub check fires first.
        await Assert.ThrowsExceptionAsync<InvocationException>(async () =>
            await ResourceNameResolver.ResolveAsync(
                "mySub/myRg/myCluster",
                explicitSubscriptionId: "otherSub",
                explicitResourceGroupName: "otherRg",
                NoCallArmClient,
                "Microsoft.ContainerService/managedClusters"
            )
        );
    }

    // ── Missing subscription ──────────────────────────────────────────────────

    [TestMethod]
    public async Task RgName_NullSub_NoEnvVar_Throws()
    {
        // Temporarily ensure the env var is unset for this test.
        var saved = Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_SUBSCRIPTION_ID", null);
            await Assert.ThrowsExceptionAsync<InvocationException>(async () =>
                await ResourceNameResolver.ResolveAsync(
                    "myRg/myCluster",
                    explicitSubscriptionId: null,
                    explicitResourceGroupName: null,
                    NoCallArmClient,
                    "Microsoft.ContainerService/managedClusters"
                )
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_SUBSCRIPTION_ID", saved);
        }
    }
}
