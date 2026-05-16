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

namespace Console.Tests
{
    [TestClass]
    public class ArgCompletionHelperEdgeCasesTests
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
        public async Task PrefixEscape_Apostrophe_IsEscaped()
        {
            var results = new List<ArgResource>();
            var fake = new FakeArgClient(results);
            SetMazConfig(new MazConfig());
            var armClient = new ArmClient(new DefaultAzureCredential());

            await ArgCompletionHelper.QueryCompletionCandidatesAsync(
                armClient,
                new DefaultAzureCredential(),
                "Microsoft.Fake/fakes",
                null,
                null,
                "o'BRIEN",
                argClient: fake
            );

            Assert.IsNotNull(fake.LastKql);
            StringAssert.Contains(fake.LastKql.ToLowerInvariant(), "o''brien");
        }

        [TestMethod]
        public async Task DeniedResourceIds_AreCaseInsensitiveAndTrimmed()
        {
            var results = new List<ArgResource>
            {
                new ArgResource("mysub", "RG1", "RES")
            };

            var fake = new FakeArgClient(results);
            SetMazConfig(new MazConfig
            {
                DeniedResourceIds = ["/SUBSCRIPTIONS/MYSUB/RESOURCEGROUPS/RG1/PROVIDERS/MICROSOFT.FAKE/FAKES/RES/"]
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

            Assert.IsFalse(candidates.Any(c => string.Equals(c, "RES", StringComparison.OrdinalIgnoreCase)));
        }

        [TestMethod]
        public async Task AllowedResourceGroups_Filtering_Works()
        {
            var results = new List<ArgResource>
            {
                new ArgResource("s1", "rg1", "name")
            };

            var fake = new FakeArgClient(results);
            SetMazConfig(new MazConfig
            {
                AllowedResourceGroups = ["other"]
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

            Assert.IsFalse(candidates.Contains("name"));
        }
    }
}
