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
    public class ArgCompletionHelperMoreEdgeCasesTests
    {
        private sealed class FakeArgClient : IArgClient
        {
            public IReadOnlyList<string>? LastSubscriptions;
            public string? LastKql;
            private readonly IReadOnlyList<ArgResource> _results;
            public FakeArgClient(IReadOnlyList<ArgResource> results) { _results = results; }
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
        public async Task UnicodePrefix_Filtering_Works()
        {
            var results = new List<ArgResource>
            {
                new ArgResource("1111-1111", "rg", "ñame"),
                new ArgResource("1111-1111", "rg", "name"),
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
                "ñ",
                argClient: fake
            )).ToList();
            CollectionAssert.AreEquivalent(new List<string> { "ñame" }, candidates);
        }

        [TestMethod]
        public async Task DuplicateName_AcrossSubscriptions_AllowedSubscriptionFilters()
        {
            var results = new List<ArgResource>
            {
                new ArgResource("sub-1", "rg", "dup"),
                new ArgResource("sub-2", "rg", "dup"),
            };
            var fake = new FakeArgClient(results);
            SetMazConfig(new MazConfig
            {
                AllowedSubscriptions = new List<string> { "sub-2" }
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
            // Only subscription 'sub-2' is allowed; candidate 'dup' should still be present.
            CollectionAssert.Contains(candidates, "dup");
        }
    }
}
#pragma warning restore IDE0028