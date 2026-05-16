using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Console.Cli.Shared;
using Console.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;
#pragma warning disable IDE0028 // Collection initialization can be simplified


namespace Console.Tests
{
    [TestClass]
    public class ArgCompletionHelperTests
    {
        private sealed class FakeArgClient : IArgClient
        {
            public IReadOnlyList<string>? LastSubscriptions;
            public string? LastKql;
            private readonly IReadOnlyList<ArgResource> _results;

            public FakeArgClient(IReadOnlyList<ArgResource> results)
            {
                _results = results;
            }

            public Task<IReadOnlyList<ArgResource>> QueryAsync(string kql, IEnumerable<string>? subscriptions, CancellationToken ct)
            {
                LastKql = kql;
                LastSubscriptions = subscriptions?.ToList();
                return Task.FromResult(_results);
            }
        }

        private static void SetMazConfig(MazConfig cfg)
        {
            var prop = typeof(MazConfig).GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            prop!.SetValue(null, cfg);
        }

        [TestMethod]
        public async Task QueryCompletionCandidates_FiltersByPrefix()
        {
            var results = new List<ArgResource>
            {
                new ArgResource("11111111-1111-1111-1111-111111111111", "rg1", "foo"),
                new ArgResource("11111111-1111-1111-1111-111111111111", "rg1", "bar"),
                new ArgResource("11111111-1111-1111-1111-111111111111", "rg1", "fizz"),
            };

            var fake = new FakeArgClient(results);
            SetMazConfig(new MazConfig());
            var armClient = new ArmClient(new DefaultAzureCredential());

            var candidates = (await ArgCompletionHelper.QueryCompletionCandidatesAsync(
                armClient,
                new DefaultAzureCredential(),
                "Microsoft.Fake/fakes",
                null,
                null,
                "f",
                argClient: fake
            )).ToList();

            CollectionAssert.AreEquivalent(new List<string> { "foo", "fizz" }, candidates);
        }

        [TestMethod]
        public async Task QueryCompletionCandidates_RespectsResolutionFilterForRg()
        {
            var results = new List<ArgResource> { new ArgResource("sub-A", "my-rg", "name1") };
            var fake = new FakeArgClient(results);

            SetMazConfig(new MazConfig { ResolutionFilter = new List<ResolutionFilterEntry> { new ResolutionFilterEntry("sub-A", new List<string> { "my-rg" }) } });
            var armClient = new ArmClient(new DefaultAzureCredential());

            var candidates = (await ArgCompletionHelper.QueryCompletionCandidatesAsync(
                armClient,
                new DefaultAzureCredential(),
                "Microsoft.Fake/fakes",
                null,
                "my-rg",
                "",
                argClient: fake
            )).ToList();

            // Fake client should have been called with subscription scope set to ["sub-A"]
            Assert.IsNotNull(fake.LastSubscriptions);
            CollectionAssert.Contains(fake.LastSubscriptions.ToList(), "sub-A");
        }

        [TestMethod]
        public async Task QueryCompletionCandidates_AppliesAllowedAndDeniedFilters()
        {
            var results = new List<ArgResource>
            {
                new ArgResource("11111111-1111-1111-1111-111111111111", "rg1", "keep"),
                new ArgResource("22222222-2222-2222-2222-222222222222", "rg1", "drop"),
            };

            var fake = new FakeArgClient(results);

            SetMazConfig(new MazConfig
            {
                AllowedSubscriptions = new List<string> { "11111111-1111-1111-1111-111111111111" },
                DisallowedSubscriptions = new List<string> { "22222222-2222-2222-2222-222222222222" },
                DeniedResourceIds = new List<string> { "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg1/providers/Microsoft.Fake/fakes/keep" }
            });

            var armClient = new ArmClient(new DefaultAzureCredential());

            var candidates = (await ArgCompletionHelper.QueryCompletionCandidatesAsync(
                armClient,
                new DefaultAzureCredential(),
                "Microsoft.Fake/fakes",
                null,
                null,
                "",
                argClient: fake
            )).ToList();

            // 'keep' should be removed because it's explicitly denied; 'drop' is disallowed by subscription
            Assert.IsFalse(candidates.Contains("keep"));
            Assert.IsFalse(candidates.Contains("drop"));
        }

        [TestMethod]
        public async Task QueryCompletionCandidates_NormalizesDisplayNameHints()
        {
            var results = new List<ArgResource>
            {
                new ArgResource("sub-prod", "rg1", "myres")
            };

            var fake = new FakeArgClient(results);
            SetMazConfig(new MazConfig
            {
                AllowedSubscriptions = new List<string> { "prod" }
            });

            var armClient = new ArmClient(new DefaultAzureCredential());

            var candidates = (await ArgCompletionHelper.QueryCompletionCandidatesAsync(
                armClient,
                new DefaultAzureCredential(),
                "Microsoft.Fake/fakes",
                null,
                null,
                "",
                argClient: fake,
                normalizeSubscriptionHint: (ac, hint) => Task.FromResult(hint == "prod" ? "sub-prod" : null)
            )).ToList();

            CollectionAssert.Contains(candidates, "myres");
        }
    }
}
#pragma warning restore IDE0028
