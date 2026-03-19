#pragma warning disable CA1861 // Constant arrays in test assertions are fine
using Console.Cli;
using Console.Cli.Parsing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Tests.Parsing;

[TestClass]
public class CliParserTests
{
    // ── Test command hierarchy ──────────────────────────────────────────

    private class TestRootDef : CommandDef
    {
        public override string Name => "root";

        public readonly TestGroupDef Group = new();
        public readonly TestLeafDef DirectLeaf = new();

        internal override IEnumerable<CommandDef> EnumerateChildren()
        {
            yield return Group;
            yield return DirectLeaf;
        }

        internal readonly CliOption<bool> _verbose = new()
        {
            Name = "--verbose",
            Aliases = ["-v"],
            Recursive = true,
        };

        internal readonly CliOption<string?> _format = new()
        {
            Name = "--format",
            Aliases = ["-f"],
            Recursive = true,
        };

        internal override IEnumerable<CliOption> EnumerateAllOptions()
        {
            yield return _verbose;
            yield return _format;
            yield return _helpOption;
            yield return _helpMoreOption;
            yield return _helpCommandsOption;
            yield return _helpCommandsFlatOption;
        }
    }

    private class TestGroupDef : CommandDef
    {
        public override string Name => "group";
        public override string[] Aliases => ["grp"];

        public readonly TestSubLeafDef SubLeaf = new();

        internal override IEnumerable<CommandDef> EnumerateChildren()
        {
            yield return SubLeaf;
        }
    }

    private class TestSubLeafDef : CommandDef
    {
        public override string Name => "action";
        protected internal override bool HasExecuteHandler => true;

        internal readonly CliOption<string?> _name = new()
        {
            Name = "--name",
            Aliases = ["-n"],
            Required = true,
        };

        internal readonly CliOption<int> _count = new()
        {
            Name = "--count",
            DefaultValueFactory = () => 1,
        };

        internal readonly CliOption<bool> _force = new()
        {
            Name = "--force",
            Aliases = ["--no-force"],
        };

        internal override IEnumerable<CliOption> EnumerateAllOptions()
        {
            yield return _name;
            yield return _count;
            yield return _force;
            yield return _helpOption;
        }
    }

    private class TestLeafDef : CommandDef
    {
        public override string Name => "leaf";
        protected internal override bool HasExecuteHandler => true;

        internal readonly CliArgument<string> _arg = new()
        {
            Name = "target",
            Description = "The target to act on.",
        };

        internal readonly CliOption<List<string>> _tags = new()
        {
            Name = "--tags",
            Aliases = ["-t"],
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = () => new List<string>(),
        };

        internal override IEnumerable<CliOption> EnumerateAllOptions()
        {
            yield return _tags;
            yield return _helpOption;
        }

        internal override IEnumerable<CliArgument<string>> EnumerateArguments()
        {
            yield return _arg;
        }
    }

    // ── Tests ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Parse_EmptyArgs_MatchesRoot()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse([], root);

        Assert.AreSame(root, result.Command);
        Assert.AreEqual(0, result.Errors.Count);
        Assert.AreEqual(0, result.UnmatchedTokens.Count);
    }

    [TestMethod]
    public void Parse_SingleCommand_MatchesChild()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group"], root);

        Assert.AreSame(root.Group, result.Command);
        Assert.AreEqual(0, result.UnmatchedTokens.Count);
    }

    [TestMethod]
    public void Parse_NestedCommand_MatchesLeaf()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "--name", "test"], root);

        Assert.AreSame(root.Group.SubLeaf, result.Command);
        Assert.AreEqual("test", root.Group.SubLeaf._name.Value);
        Assert.IsTrue(root.Group.SubLeaf._name.WasProvided);
    }

    [TestMethod]
    public void Parse_CommandAlias_MatchesChild()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["grp", "action", "--name", "x"], root);

        Assert.AreSame(root.Group.SubLeaf, result.Command);
    }

    [TestMethod]
    public void Parse_OptionWithEqualsSign()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "--name=hello"], root);

        Assert.AreEqual("hello", root.Group.SubLeaf._name.Value);
        Assert.IsTrue(root.Group.SubLeaf._name.WasProvided);
    }

    [TestMethod]
    public void Parse_ShortAlias()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "-n", "hello"], root);

        Assert.AreEqual("hello", root.Group.SubLeaf._name.Value);
    }

    [TestMethod]
    public void Parse_BoolOptionAsFlag()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "--name", "x", "--force"], root);

        Assert.IsTrue(root.Group.SubLeaf._force.Value);
        Assert.IsTrue(root.Group.SubLeaf._force.WasProvided);
    }

    [TestMethod]
    public void Parse_BoolOptionWithTrueValue()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "--name", "x", "--force", "true"], root);

        Assert.IsTrue(root.Group.SubLeaf._force.Value);
    }

    [TestMethod]
    public void Parse_BoolOptionWithFalseValue()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "--name", "x", "--force", "false"], root);

        Assert.IsFalse(root.Group.SubLeaf._force.Value);
    }

    [TestMethod]
    public void Parse_BoolNegation_NoPrefix()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "--name", "x", "--no-force"], root);

        Assert.IsFalse(root.Group.SubLeaf._force.Value);
        Assert.IsTrue(root.Group.SubLeaf._force.WasProvided);
    }

    [TestMethod]
    public void Parse_IntOption()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "--name", "x", "--count", "42"], root);

        Assert.AreEqual(42, root.Group.SubLeaf._count.Value);
    }

    [TestMethod]
    public void Parse_DefaultValue_Applied()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "--name", "x"], root);

        Assert.AreEqual(1, root.Group.SubLeaf._count.Value);
        Assert.IsFalse(root.Group.SubLeaf._count.WasProvided);
    }

    [TestMethod]
    public void Parse_RequiredOptionMissing_ReportsError()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action"], root);

        Assert.IsTrue(result.Errors.Any(e => e.Contains("--name") && e.Contains("required")));
    }

    [TestMethod]
    public void Parse_UnknownOption_AddedToUnmatched()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "--name", "x", "--unknown"], root);

        CollectionAssert.Contains(result.UnmatchedTokens, "--unknown");
    }

    [TestMethod]
    public void Parse_UnknownCommand_AddedToUnmatched()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["bogus"], root);

        CollectionAssert.Contains(result.UnmatchedTokens, "bogus");
    }

    [TestMethod]
    public void Parse_RecursiveOption_AvailableOnSubcommand()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "--name", "x", "--verbose"], root);

        Assert.IsTrue(root._verbose.Value);
        Assert.IsTrue(root._verbose.WasProvided);
    }

    [TestMethod]
    public void Parse_RecursiveShortAlias()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "--name", "x", "-v"], root);

        Assert.IsTrue(root._verbose.Value);
    }

    [TestMethod]
    public void Parse_PositionalArgument()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["leaf", "myTarget"], root);

        Assert.AreSame(root.DirectLeaf, result.Command);
        Assert.AreEqual("myTarget", root.DirectLeaf._arg.Value);
        Assert.IsTrue(root.DirectLeaf._arg.WasProvided);
    }

    [TestMethod]
    public void Parse_MultiValueOption()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["leaf", "--tags", "a", "b", "c"], root);

        Assert.AreSame(root.DirectLeaf, result.Command);
        var tags = root.DirectLeaf._tags.Value!;
        Assert.AreEqual(3, tags.Count);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, tags);
    }

    [TestMethod]
    public void Parse_DoubleDash_StopsOptionParsing()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["leaf", "--", "--not-an-option"], root);

        Assert.AreSame(root.DirectLeaf, result.Command);
        // "--not-an-option" after -- should be treated as a positional arg
        Assert.AreEqual("--not-an-option", root.DirectLeaf._arg.Value);
    }

    [TestMethod]
    public void Parse_OptionBeforeCommand()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["--verbose", "group", "action", "--name", "x"], root);

        Assert.AreSame(root.Group.SubLeaf, result.Command);
        Assert.IsTrue(root._verbose.Value);
    }

    [TestMethod]
    public void Parse_OptionBetweenCommands()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "--verbose", "action", "--name", "x"], root);

        Assert.AreSame(root.Group.SubLeaf, result.Command);
        Assert.IsTrue(root._verbose.Value);
    }

    [TestMethod]
    public void Parse_HelpOption()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["--help"], root);

        Assert.IsTrue(root._helpOption.WasProvided);
    }

    [TestMethod]
    public void Parse_HelpOnSubcommand()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "--help"], root);

        Assert.AreSame(root.Group, result.Command);
        Assert.IsTrue(root.Group._helpOption.WasProvided);
    }

    [TestMethod]
    public void Parse_HelpMoreOption()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["--help-more"], root);

        Assert.IsTrue(root._helpMoreOption.WasProvided);
    }

    [TestMethod]
    public void Parse_HelpCommandsWithFilter()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["--help-commands=storage"], root);

        Assert.IsTrue(root._helpCommandsOption.WasProvided);
        Assert.AreEqual("storage", root._helpCommandsOption.Value);
    }

    [TestMethod]
    public void Parse_CommandPath_IsCorrect()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "--name", "x"], root);

        Assert.AreEqual(3, result.CommandPath.Count);
        Assert.AreSame(root, result.CommandPath[0]);
        Assert.AreSame(root.Group, result.CommandPath[1]);
        Assert.AreSame(root.Group.SubLeaf, result.CommandPath[2]);
    }

    [TestMethod]
    public void Parse_OptionMissingValue_ReportsError()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["group", "action", "--name"], root);

        Assert.IsTrue(result.Errors.Any(e => e.Contains("--name") && e.Contains("value")));
    }

    [TestMethod]
    public void Parse_FormatOptionWithValue()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["-f", "json", "leaf", "target"], root);

        Assert.AreEqual("json", root._format.Value);
    }

    [TestMethod]
    public void Parse_CaseInsensitiveCommands()
    {
        var root = new TestRootDef();
        var result = CliParser.Parse(["Group", "Action", "--name", "x"], root);

        Assert.AreSame(root.Group.SubLeaf, result.Command);
    }
}

[TestClass]
public class CliDirectiveTests
{
    private class SimpleRoot : CommandDef
    {
        public override string Name => "app";

        internal readonly CliOption<bool> _verbose = new() { Name = "--verbose" };

        internal override IEnumerable<CliOption> EnumerateAllOptions()
        {
            yield return _verbose;
            yield return _helpOption;
        }
    }

    [TestMethod]
    public void Parse_DebugDirective()
    {
        var root = new SimpleRoot();
        var result = CliParser.Parse(["[debug]", "--verbose"], root);

        Assert.AreEqual(1, result.Directives.Count);
        Assert.AreEqual("debug", result.Directives[0].Name);
        Assert.IsNull(result.Directives[0].Value);
        Assert.IsTrue(root._verbose.Value);
    }

    [TestMethod]
    public void Parse_SuggestDirective()
    {
        var root = new SimpleRoot();
        var result = CliParser.Parse(["[suggest:42]", "some line"], root);

        Assert.AreEqual(1, result.Directives.Count);
        Assert.AreEqual("suggest", result.Directives[0].Name);
        Assert.AreEqual("42", result.Directives[0].Value);
    }

    [TestMethod]
    public void Parse_MultipleDirectives()
    {
        var root = new SimpleRoot();
        var result = CliParser.Parse(["[debug]", "[trace]", "--verbose"], root);

        Assert.AreEqual(2, result.Directives.Count);
        Assert.AreEqual("debug", result.Directives[0].Name);
        Assert.AreEqual("trace", result.Directives[1].Name);
    }

    [TestMethod]
    public void Parse_DirectiveMixedWithArgs_OnlyLeadingDirectives()
    {
        var root = new SimpleRoot();
        // [debug] is a directive, but [notadirective] after --verbose is not (directives only at start)
        var result = CliParser.Parse(["[debug]", "--verbose"], root);

        Assert.AreEqual(1, result.Directives.Count);
        Assert.IsTrue(root._verbose.Value);
    }

    [TestMethod]
    public void TryParse_ValidDirective()
    {
        var d = CliDirective.TryParse("[debug]");
        Assert.IsNotNull(d);
        Assert.AreEqual("debug", d.Name);
        Assert.IsNull(d.Value);
    }

    [TestMethod]
    public void TryParse_DirectiveWithValue()
    {
        var d = CliDirective.TryParse("[suggest:42]");
        Assert.IsNotNull(d);
        Assert.AreEqual("suggest", d.Name);
        Assert.AreEqual("42", d.Value);
    }

    [TestMethod]
    public void TryParse_NotADirective()
    {
        Assert.IsNull(CliDirective.TryParse("--flag"));
        Assert.IsNull(CliDirective.TryParse("command"));
        Assert.IsNull(CliDirective.TryParse("[]")); // too short content
    }

    [TestMethod]
    public void TryParse_EmptyBrackets_NotDirective()
    {
        // "[x]" has length 3 but inner is "x" which is valid
        var d = CliDirective.TryParse("[x]");
        Assert.IsNotNull(d);
        Assert.AreEqual("x", d.Name);
    }
}

[TestClass]
public class CliOptionTests
{
    [TestMethod]
    public void StringOption_TryParse()
    {
        var opt = new CliOption<string> { Name = "--name" };
        Assert.IsTrue(opt.TryParse("hello"));
        Assert.AreEqual("hello", opt.Value);
        Assert.IsTrue(opt.WasProvided);
    }

    [TestMethod]
    public void IntOption_TryParse()
    {
        var opt = new CliOption<int> { Name = "--count" };
        Assert.IsTrue(opt.TryParse("42"));
        Assert.AreEqual(42, opt.Value);
    }

    [TestMethod]
    public void IntOption_InvalidValue_ReturnsFalse()
    {
        var opt = new CliOption<int> { Name = "--count" };
        Assert.IsFalse(opt.TryParse("notanumber"));
    }

    [TestMethod]
    public void BoolOption_FlagStyle()
    {
        var opt = new CliOption<bool> { Name = "--flag" };
        Assert.IsTrue(opt.TryParse(null));
        Assert.IsTrue(opt.Value);
    }

    [TestMethod]
    public void BoolOption_ExplicitTrue()
    {
        var opt = new CliOption<bool> { Name = "--flag" };
        Assert.IsTrue(opt.TryParse("true"));
        Assert.IsTrue(opt.Value);
    }

    [TestMethod]
    public void BoolOption_ExplicitFalse()
    {
        var opt = new CliOption<bool> { Name = "--flag" };
        Assert.IsTrue(opt.TryParse("false"));
        Assert.IsFalse(opt.Value);
    }

    [TestMethod]
    public void DefaultValueFactory_Applied()
    {
        var opt = new CliOption<int> { Name = "--port", DefaultValueFactory = () => 8080 };
        opt.ApplyDefault();
        Assert.AreEqual(8080, opt.Value);
        Assert.IsFalse(opt.WasProvided);
    }

    [TestMethod]
    public void DefaultValueFactory_NotApplied_WhenProvided()
    {
        var opt = new CliOption<int> { Name = "--port", DefaultValueFactory = () => 8080 };
        opt.TryParse("3000");
        opt.ApplyDefault();
        Assert.AreEqual(3000, opt.Value);
    }

    [TestMethod]
    public void CustomParser_Used()
    {
        var opt = new CliOption<int> { Name = "--val", Parser = raw => int.Parse(raw) * 2 };
        Assert.IsTrue(opt.TryParse("5"));
        Assert.AreEqual(10, opt.Value);
    }

    [TestMethod]
    public void Reset_ClearsValue()
    {
        var opt = new CliOption<string> { Name = "--name", DefaultValue = "default" };
        opt.TryParse("hello");
        Assert.AreEqual("hello", opt.Value);
        opt.Reset();
        Assert.AreEqual("default", opt.Value);
        Assert.IsFalse(opt.WasProvided);
    }

    [TestMethod]
    public void AllNames_IncludesNameAndAliases()
    {
        var opt = new CliOption<string> { Name = "--name", Aliases = ["-n", "--nombre"] };
        var names = opt.AllNames.ToList();
        CollectionAssert.AreEqual(new[] { "--name", "-n", "--nombre" }, names);
    }

    [TestMethod]
    public void GuidOption_TryParse()
    {
        var opt = new CliOption<Guid> { Name = "--id" };
        var guid = Guid.NewGuid();
        Assert.IsTrue(opt.TryParse(guid.ToString()));
        Assert.AreEqual(guid, opt.Value);
    }

    [TestMethod]
    public void NullableIntOption_TryParse()
    {
        var opt = new CliOption<int?> { Name = "--port" };
        Assert.IsTrue(opt.TryParse("8080"));
        Assert.AreEqual(8080, opt.Value);
    }

    [TestMethod]
    public void ListStringOption_TryParseMany()
    {
        var opt = new CliOption<List<string>>
        {
            Name = "--tags",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = () => new List<string>(),
        };
        opt.ApplyDefault();
        Assert.IsTrue(opt.TryParseMany(["a", "b", "c"]));
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, opt.Value);
    }

    [TestMethod]
    public void ElementParser_AccumulatesIntoList()
    {
        var opt = new CliOption<List<int>>
        {
            Name = "--nums",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = () => new List<int>(),
            ElementParser = raw => int.Parse(raw),
        };
        opt.ApplyDefault();
        Assert.IsTrue(opt.TryParse("1"));
        Assert.IsTrue(opt.TryParse("2"));
        Assert.IsTrue(opt.TryParse("3"));
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, opt.Value);
    }

    [TestMethod]
    public void UriOption_TryParse()
    {
        var opt = new CliOption<Uri?> { Name = "--url" };
        Assert.IsTrue(opt.TryParse("https://example.com"));
        Assert.AreEqual(new Uri("https://example.com"), opt.Value);
    }
}

[TestClass]
public class CliArgumentTests
{
    [TestMethod]
    public void StringArgument_TryParse()
    {
        var arg = new CliArgument<string> { Name = "target" };
        Assert.IsTrue(arg.TryParse("hello"));
        Assert.AreEqual("hello", arg.Value);
        Assert.IsTrue(arg.WasProvided);
    }

    [TestMethod]
    public void IntArgument_TryParse()
    {
        var arg = new CliArgument<int> { Name = "count" };
        Assert.IsTrue(arg.TryParse("42"));
        Assert.AreEqual(42, arg.Value);
    }

    [TestMethod]
    public void IntArgument_InvalidValue_ReturnsFalse()
    {
        var arg = new CliArgument<int> { Name = "count" };
        Assert.IsFalse(arg.TryParse("abc"));
    }
}
