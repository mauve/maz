using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CliGenerator;

[Generator]
public class CliOptionGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static ctx =>
            ctx.AddSource(
                "CliAttributes.g.cs",
                SourceText.From(AttributeSources.Source, Encoding.UTF8)
            )
        );

        var provider = context
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) =>
                    node is ClassDeclarationSyntax c && c.Modifiers.Any(SyntaxKind.PartialKeyword),
                transform: static (ctx, ct) =>
                    ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node, ct)
                    as INamedTypeSymbol
            )
            .Collect()
            .SelectMany(
                static (classes, _) =>
                    classes
                        .Where(static c => c is not null)
                        .GroupBy(static c => c!.ToDisplayString())
                        .Select(static g => g.First())
                        .Select(static c => BuildClassModel(c!))
            );

        context.RegisterSourceOutput(
            provider,
            static (ctx, model) =>
            {
                if (model is null)
                    return;
                var source = Emit(model);
                ctx.AddSource($"{model.ClassName}.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        );
    }

    // ── Model ──────────────────────────────────────────────────────────────

    sealed class ClassModel
    {
        public string Namespace = "";
        public string ClassName = "";
        public bool IsCommandDef;
        public bool IsOptionPack;
        public bool HasExplicitDescriptionOverride;
        public bool HasExplicitDetailedDescriptionOverride;
        public string? XmlDescription;
        public string? XmlRemarks;
        public List<OptionPropModel> Options = [];
        public List<ChildModel> Children = [];
    }

    sealed class OptionPropModel
    {
        public string Name = "";
        public string OptionType = ""; // e.g. "Option<string?>"
        public string ValueType = ""; // T in Option<T>
        public string PrimaryAlias = "";
        public string[] ExtraAliases = Array.Empty<string>();
        public bool IsGlobal;
        public bool IsRequired;
        public bool IsAdvanced;
        public string Description = "";
        public string? EnvVar;
        public string? DefaultExpression;
        public string? EnvVarAccessorSuffix;
        public string? CustomParserExpr;
        public string? CompletionProviderTypeName;
        public bool AllowMultiple;
        public string? ArityExpr;
        public bool HasNullableAnnotation;
        public string? AllowedValuesText;  // joined display string for [allowed:]
        public string? DefaultText;        // pre-formatted display string for [default:]
    }

    sealed class ChildModel
    {
        public string Name = "";
        public bool IsCommandDef;
        public bool IsOptionPack;
        public string TypeName = "";
    }

    // ── Build model from symbol ─────────────────────────────────────────────

    static ClassModel? BuildClassModel(INamedTypeSymbol cls)
    {
        var model = new ClassModel
        {
            Namespace = cls.ContainingNamespace.IsGlobalNamespace
                ? ""
                : cls.ContainingNamespace.ToDisplayString(),
            ClassName = cls.Name,
        };

        // Determine base type
        for (var b = cls.BaseType; b != null; b = b.BaseType)
        {
            var bn = b.ToDisplayString();
            if (bn == "Console.Cli.CommandDef")
            {
                model.IsCommandDef = true;
                break;
            }
            if (bn == "Console.Cli.OptionPack")
            {
                model.IsOptionPack = true;
                break;
            }
        }

        if (!model.IsCommandDef && !model.IsOptionPack)
            return null;

        // XML doc description on class
        model.XmlDescription = GetXmlSummary(cls);
        model.XmlRemarks = GetXmlRemarks(cls);

        // An explicitly declared Description override takes precedence over XML summary.
        model.HasExplicitDescriptionOverride = cls.GetMembers("Description")
            .OfType<IPropertySymbol>()
            .Any(p => p.IsOverride && !p.IsStatic && p.Parameters.Length == 0);

        model.HasExplicitDetailedDescriptionOverride = cls.GetMembers("DetailedDescription")
            .OfType<IPropertySymbol>()
            .Any(p => p.IsOverride && !p.IsStatic && p.Parameters.Length == 0);

        // Options
        foreach (var prop in cls.GetMembers().OfType<IPropertySymbol>())
        {
            if (!HasCliOptionAttribute(prop))
                continue;
            model.Options.Add(BuildOptionModel(prop));
        }

        // Children: public instance fields of type CommandDef or OptionPack
        foreach (var member in cls.GetMembers().OfType<IFieldSymbol>())
        {
            if (!member.IsStatic && member.DeclaredAccessibility == Accessibility.Public)
            {
                var fieldTypeName = member.Type.ToDisplayString();
                bool isCD = IsCommandDefType(member.Type);
                bool isOP = IsOptionPackType(member.Type);
                if (isCD || isOP)
                {
                    model.Children.Add(
                        new ChildModel
                        {
                            Name = member.Name,
                            IsCommandDef = isCD,
                            IsOptionPack = isOP,
                            TypeName = fieldTypeName,
                        }
                    );
                }
            }
        }

        return model;
    }

    static bool HasCliOptionAttribute(IPropertySymbol prop) =>
        prop.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "Console.Cli.CliOptionAttribute");

    static OptionPropModel BuildOptionModel(IPropertySymbol prop)
    {
        var m = new OptionPropModel { Name = prop.Name };

        // [CliOption] attribute
        var attr = prop.GetAttributes()
            .FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "Console.Cli.CliOptionAttribute"
            );

        string[] aliases = Array.Empty<string>();
        if (attr != null)
        {
            // constructor arg is params string[] aliases
            var ctorArg = attr.ConstructorArguments.FirstOrDefault();
            if (ctorArg.Kind == TypedConstantKind.Array)
                aliases = ctorArg.Values.Select(v => v.Value?.ToString() ?? "").ToArray();
            else if (ctorArg.Value != null)
                aliases = new[] { ctorArg.Value.ToString()! };

            m.IsGlobal = GetNamedBool(attr, "Global");
            if (GetNamedBool(attr, "Required"))
                m.IsRequired = true;
            m.IsAdvanced = GetNamedBool(attr, "Advanced");
            m.EnvVar = GetNamedString(attr, "EnvVar");
            m.CompletionProviderTypeName = GetNamedType(attr, "CompletionProviderType");
        }

        m.PrimaryAlias = aliases.Length > 0 ? aliases[0] : KebabCase(prop.Name);
        m.ExtraAliases = aliases.Length > 1 ? aliases.Skip(1).ToArray() : Array.Empty<string>();

        // Description from XML doc
        m.Description = GetXmlSummary(prop) ?? "";

        // Type info
        var typeSymbol = prop.Type;
        m.HasNullableAnnotation =
            prop.NullableAnnotation == NullableAnnotation.Annotated
            || (
                typeSymbol is INamedTypeSymbol nt
                && nt.OriginalDefinition.SpecialType == SpecialType.None
                && nt.Name == "Nullable"
            );

        // Collection detection
        var (isCollection, elemType, elemTypeSymbol) = GetCollectionInfo(typeSymbol);
        bool hasDefault = m.DefaultExpression != null || GetDefaultExpression(prop) != null;

        // Determine value type for Option<T>
        string valueTypeStr = GetTypeDisplayString(typeSymbol);
        m.ValueType = valueTypeStr;
        m.OptionType = $"Option<{valueTypeStr}>";

        (string, string)[]? capturedEnumDescs = null;

        if (isCollection && elemType != null)
        {
            m.AllowMultiple = true;
            // [Arity] override?
            var arityAttr = prop.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "ArityAttribute");
            if (arityAttr != null && arityAttr.ConstructorArguments.Length == 2)
            {
                var mn = arityAttr.ConstructorArguments[0].Value;
                var mx = arityAttr.ConstructorArguments[1].Value;
                m.ArityExpr = $"new ArgumentArity({mn}, {mx})";
            }
            else if (!m.HasNullableAnnotation && !hasDefault)
                m.ArityExpr = "ArgumentArity.OneOrMore";
            else
                m.ArityExpr = "ArgumentArity.ZeroOrMore";

            // Custom parser for element type
            if (elemTypeSymbol != null)
            {
                var enumDescs = GetEnumDescriptions(elemTypeSymbol);
                if (enumDescs != null)
                {
                    capturedEnumDescs = enumDescs;
                    var elemTypeName = GetNonNullableTypeName(elemTypeSymbol);
                    m.CustomParserExpr = BuildEnumCollectionSwitchParser(elemTypeName, enumDescs);
                    m.AllowedValuesText = string.Join(", ", enumDescs.Select(d => d.Item2));
                }
                else
                {
                    var elemInner = GetParserInnerExpr(elemTypeSymbol, out _);
                    if (elemInner != null)
                        m.CustomParserExpr =
                            $"r => r.Tokens.Select(t => {string.Format(elemInner, "t.Value")}).ToList()";
                }
            }
        }
        else
        {
            bool isBool = typeSymbol.SpecialType == SpecialType.System_Boolean;

            // Required inference
            if (!m.IsRequired)
            {
                bool isValueType = typeSymbol.IsValueType && !IsNullableValueType(typeSymbol);
                // Required if: non-nullable reference type with no default OR non-nullable Guid-like with no default
                if (!m.HasNullableAnnotation && !hasDefault && !isBool)
                {
                    if (!isValueType || IsGuidLike(typeSymbol))
                        m.IsRequired = true;
                }
            }

            // Custom parser for non-collection type
            var enumDescs = GetEnumDescriptions(typeSymbol);
            if (enumDescs != null)
            {
                capturedEnumDescs = enumDescs;
                var typeName = GetNonNullableTypeName(typeSymbol);
                m.CustomParserExpr = BuildEnumSwitchParser(
                    typeName,
                    enumDescs,
                    m.HasNullableAnnotation
                );
                m.AllowedValuesText = string.Join(", ", enumDescs.Select(d => d.Item2));
            }
            else
            {
                var parser = GetSingleValueParserExpr(typeSymbol, m.HasNullableAnnotation);
                if (parser != null)
                    m.CustomParserExpr = string.Format(parser, "r.Tokens[0].Value");
            }

            // Non-nullable bool: add --no-{longAlias} negation alias and a toggle parser
            if (isBool)
            {
                var firstLongAlias = new[] { m.PrimaryAlias }
                    .Concat(m.ExtraAliases)
                    .FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal));
                if (firstLongAlias != null)
                {
                    var negation = "--no-" + firstLongAlias.Substring(2);
                    m.ExtraAliases = m.ExtraAliases.Append(negation).ToArray();
                    m.CustomParserExpr =
                        @"r => r.Tokens.Count > 0 ? bool.Parse(r.Tokens[0].Value) : !(r.Parent is global::System.CommandLine.Parsing.OptionResult __or && (__or.IdentifierToken?.Value ?? """").StartsWith(""--no-"", global::System.StringComparison.OrdinalIgnoreCase))";
                }
            }
        }

        // Read default from property initializer (e.g. = true, = ["a", "b"])
        if (m.DefaultExpression == null && hasDefault)
            m.DefaultExpression = GetDefaultExpression(prop);

        // Compute DefaultText for the registry
        if (m.DefaultExpression != null && m.DefaultExpression != "[]")
        {
            if (capturedEnumDescs != null)
            {
                // Map enum member name references in the expression to their description strings
                var mapped = capturedEnumDescs
                    .Where(d => m.DefaultExpression.Contains("." + d.Item1))
                    .Select(d => d.Item2)
                    .ToArray();
                m.DefaultText = mapped.Length > 0 ? string.Join("|", mapped) : m.DefaultExpression;
            }
            else
            {
                m.DefaultText = m.DefaultExpression;
            }
        }

        // EnvVar: build a property-accessor fallback suffix (no DefaultValueFactory, avoids [] in help).
        if (m.EnvVar != null && m.DefaultExpression == null)
        {
            var inner = GetParserInnerExpr(typeSymbol, out var nonNullTypeName);
            if (inner == null)
            {
                // string or other System.CommandLine-native type: null-coalesce with env var
                m.EnvVarAccessorSuffix =
                    $" ?? System.Environment.GetEnvironmentVariable(\"{m.EnvVar}\")";
            }
            else
            {
                // type needs conversion (e.g. Uri): wrap with null guard
                var converted = string.Format(inner, "__s");
                m.EnvVarAccessorSuffix =
                    $" ?? (System.Environment.GetEnvironmentVariable(\"{m.EnvVar}\") is string __s"
                    + $" ? ({nonNullTypeName}?){converted} : ({nonNullTypeName}?)null)";
            }
        }

        return m;
    }

    // ── Type helpers ───────────────────────────────────────────────────────

    static (bool isCollection, string? elemType, ITypeSymbol? elemTypeSymbol) GetCollectionInfo(
        ITypeSymbol typeSymbol
    )
    {
        if (typeSymbol is INamedTypeSymbol nt)
        {
            // List<T>, IEnumerable<T>, IReadOnlyList<T>
            var orig = nt.OriginalDefinition.ToDisplayString();
            if (
                (
                    orig == "System.Collections.Generic.List<T>"
                    || orig == "System.Collections.Generic.IEnumerable<T>"
                    || orig == "System.Collections.Generic.IReadOnlyList<T>"
                    || orig == "System.Collections.Generic.IReadOnlyCollection<T>"
                )
                && nt.TypeArguments.Length == 1
            )
            {
                var elem = nt.TypeArguments[0];
                return (true, GetTypeDisplayString(elem), elem);
            }
        }
        if (typeSymbol is IArrayTypeSymbol arr)
            return (true, GetTypeDisplayString(arr.ElementType), arr.ElementType);
        return (false, null, null);
    }

    static string GetTypeDisplayString(ITypeSymbol sym)
    {
        // Use minimal display but preserve nullability annotation
        var opts = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
        );
        return sym.ToDisplayString(opts);
    }

    static bool IsNullableValueType(ITypeSymbol sym)
    {
        return sym is INamedTypeSymbol nt
            && nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }

    static bool IsGuidLike(ITypeSymbol sym)
    {
        var name = sym.ToDisplayString();
        return name == "System.Guid" || name == "Guid";
    }

    static bool IsCommandDefType(ITypeSymbol sym)
    {
        for (var b = sym.BaseType; b != null; b = b.BaseType)
            if (b.ToDisplayString() == "Console.Cli.CommandDef")
                return true;
        return sym.ToDisplayString() == "Console.Cli.CommandDef";
    }

    static bool IsOptionPackType(ITypeSymbol sym)
    {
        for (var b = sym.BaseType; b != null; b = b.BaseType)
            if (b.ToDisplayString() == "Console.Cli.OptionPack")
                return true;
        return sym.ToDisplayString() == "Console.Cli.OptionPack";
    }

    // Returns a format string template "SomeType.Parse({0})" or "new SomeType({0})"
    // where {0} will be substituted with the token value expression.
    // Returns null if System.CommandLine handles the type natively.
    static string? GetParserInnerExpr(ITypeSymbol sym, out string nonNullTypeName)
    {
        // Unwrap nullable value type
        if (IsNullableValueType(sym) && sym is INamedTypeSymbol nnt)
            sym = nnt.TypeArguments[0];

        nonNullTypeName = "";

        // System.CommandLine handles these natively
        if (IsBuiltinParseable(sym))
            return null;

        // Use non-nullable display name for constructor expressions
        var typeNameFmt = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
        );
        var typeName = sym.WithNullableAnnotation(NullableAnnotation.None)
            .ToDisplayString(typeNameFmt);
        nonNullTypeName = typeName;

        // Check [CliParser] attribute on type
        var cliParserAttr = sym.GetAttributes()
            .FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "Console.Cli.CliParserAttribute"
            );
        if (cliParserAttr != null)
        {
            var converterType = cliParserAttr.ConstructorArguments[0].Value?.ToString();
            if (converterType != null)
                return $"({typeName})(new {converterType}().ConvertFromString({{0}}))";
        }

        // Check for static T Parse(string) method
        var parseMethod = sym.GetMembers("Parse")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m =>
                m.IsStatic
                && m.Parameters.Length == 1
                && m.Parameters[0].Type.SpecialType == SpecialType.System_String
                && !m.ReturnsVoid
            );
        if (parseMethod != null)
            return $"{typeName}.Parse({{0}})";

        // Check for T(string) constructor
        var stringCtor = sym.GetMembers(".ctor")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m =>
                m.Parameters.Length == 1
                && m.Parameters[0].Type.SpecialType == SpecialType.System_String
            );
        if (stringCtor != null)
            return $"new {typeName}({{0}})";

        return null;
    }

    static string? GetSingleValueParserExpr(ITypeSymbol sym, bool isNullable)
    {
        var inner = GetParserInnerExpr(sym, out var typeName);
        if (inner == null)
            return null;
        if (isNullable)
            return $"r => r.Tokens.Count > 0 ? {string.Format(inner, "r.Tokens[0].Value")} : ({typeName}?)null";
        return $"r => {string.Format(inner, "r.Tokens[0].Value")}";
    }

    // Returns (MemberName, DescriptionValue)[] if the type is an enum with
    // at least one [Description] attribute on its members; otherwise null.
    static (string memberName, string descValue)[]? GetEnumDescriptions(ITypeSymbol sym)
    {
        if (IsNullableValueType(sym) && sym is INamedTypeSymbol nnt)
            sym = nnt.TypeArguments[0];
        if (sym.TypeKind != TypeKind.Enum)
            return null;

        var pairs = sym.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.IsConst)
            .Select(f =>
            {
                var desc = f.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name == "DescriptionAttribute")
                    ?.ConstructorArguments.FirstOrDefault()
                    .Value?.ToString();
                return (f.Name, desc);
            })
            .Where(x => x.desc != null)
            .Select(x => (x.Name, x.desc!))
            .ToArray();

        return pairs.Length > 0 ? pairs : null;
    }

    static string BuildEnumSwitchParser(string typeName, (string, string)[] descs, bool isNullable)
    {
        var allowedList = string.Join(", ", descs.Select(d => d.Item2));
        var cases = string.Join(" ", descs.Select(d => $"\"{d.Item2}\" => {typeName}.{d.Item1},"));
        var throwExpr =
            $"var __s => throw new global::System.ArgumentException($\"Unknown value '{{__s}}'. Allowed: {allowedList}.\")";
        var switchExpr = $"r.Tokens[0].Value switch {{ {cases} {throwExpr} }}";

        if (isNullable)
            return $"r => r.Tokens.Count > 0 ? ({typeName}?)({switchExpr}) : ({typeName}?)null";
        return $"r => {switchExpr}";
    }

    static string BuildEnumCollectionSwitchParser(string typeName, (string, string)[] descs)
    {
        var allowedList = string.Join(", ", descs.Select(d => d.Item2));
        var cases = string.Join(" ", descs.Select(d => $"\"{d.Item2}\" => {typeName}.{d.Item1},"));
        var throwExpr =
            $"var __s => throw new global::System.ArgumentException($\"Unknown value '{{__s}}'. Allowed: {allowedList}.\")";
        return $"r => r.Tokens.Select(t => t.Value switch {{ {cases} {throwExpr} }}).ToList()";
    }

    static string GetNonNullableTypeName(ITypeSymbol sym)
    {
        if (IsNullableValueType(sym) && sym is INamedTypeSymbol nnt)
            sym = nnt.TypeArguments[0];
        var fmt = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
        );
        return sym.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(fmt);
    }

    static bool IsBuiltinParseable(ITypeSymbol sym)
    {
        switch (sym.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_String:
            case SpecialType.System_DateTime:
                return true;
        }
        if (sym.TypeKind == TypeKind.Enum)
            return true;
        var name = sym.ToDisplayString();
        if (name == "System.Guid" || name == "Guid")
            return true;
        if (name == "System.DateTimeOffset" || name == "DateTimeOffset")
            return true;
        return false;
    }

    // ── Syntax helpers ─────────────────────────────────────────────────────

    static string? GetDefaultExpression(IPropertySymbol prop)
    {
        var syn = prop.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (syn is PropertyDeclarationSyntax pds && pds.Initializer != null)
            return pds.Initializer.Value.ToFullString().Trim();
        return null;
    }

    static string? GetXmlSummary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        var start = xml!.IndexOf("<summary>", StringComparison.Ordinal);
        var end = xml.IndexOf("</summary>", StringComparison.Ordinal);
        if (start < 0 || end < 0)
            return null;
        var raw = xml.Substring(start + 9, end - start - 9);
        // Normalize whitespace
        var lines = raw.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(trimmed);
            }
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    static string? GetXmlRemarks(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        var start = xml!.IndexOf("<remarks>", StringComparison.Ordinal);
        var end = xml.IndexOf("</remarks>", StringComparison.Ordinal);
        if (start < 0 || end < 0)
            return null;
        var raw = xml.Substring(start + 9, end - start - 9);

        // Split into lines, remove common leading whitespace, trim surrounding blank lines
        var lines = raw.Split('\n');
        // Find minimum indent of non-empty lines
        int minIndent = int.MaxValue;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
                continue;
            int indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
                indent++;
            if (indent < minIndent)
                minIndent = indent;
        }
        if (minIndent == int.MaxValue)
            minIndent = 0;

        var result = new List<string>();
        foreach (var line in lines)
        {
            var stripped = line.Length >= minIndent ? line.Substring(minIndent) : line;
            result.Add(stripped.TrimEnd());
        }

        // Remove leading and trailing blank lines
        while (result.Count > 0 && result[0].Trim().Length == 0)
            result.RemoveAt(0);
        while (result.Count > 0 && result[result.Count - 1].Trim().Length == 0)
            result.RemoveAt(result.Count - 1);

        if (result.Count == 0)
            return null;
        return string.Join("\n", result);
    }

    static string? GetNamedString(AttributeData attr, string name)
    {
        var arg = attr.NamedArguments.FirstOrDefault(a => a.Key == name);
        return arg.Value.Value?.ToString();
    }

    static bool GetNamedBool(AttributeData attr, string name)
    {
        var arg = attr.NamedArguments.FirstOrDefault(a => a.Key == name);
        return arg.Value.Value is true;
    }

    static string? GetNamedType(AttributeData attr, string name)
    {
        var arg = attr.NamedArguments.FirstOrDefault(a => a.Key == name);
        if (arg.Value.Kind != TypedConstantKind.Type || arg.Value.Value is not ITypeSymbol sym)
            return null;
        return sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    static string KebabCase(string name)
    {
        var sb = new StringBuilder("--");
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                sb.Append('-');
            sb.Append(char.ToLower(name[i]));
        }
        return sb.ToString();
    }

    // ── Code emission ──────────────────────────────────────────────────────

    static string Emit(ClassModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591");
        sb.AppendLine("using System.CommandLine;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine();

        bool hasNs = !string.IsNullOrEmpty(model.Namespace);
        if (hasNs)
        {
            sb.AppendLine($"namespace {model.Namespace};").AppendLine();
        }

        sb.AppendLine($"partial class {model.ClassName}");
        sb.AppendLine("{");

        // Private option fields
        foreach (var opt in model.Options)
        {
            sb.Append(
                $"    private readonly {opt.OptionType} _opt_{opt.Name} = new({Quote(opt.PrimaryAlias)}, new string[] {{"
            );
            sb.Append(string.Join(", ", opt.ExtraAliases.Select(Quote)));
            sb.Append("})");

            var initParts = new List<string>();
            if (!string.IsNullOrEmpty(opt.Description))
                initParts.Add($"Description = {Quote(opt.Description)}");
            if (opt.IsRequired)
                initParts.Add("Required = true");
            if (opt.AllowMultiple)
                initParts.Add("AllowMultipleArgumentsPerToken = true");
            if (opt.ArityExpr != null)
                initParts.Add($"Arity = {opt.ArityExpr}");
            if (opt.DefaultExpression != null)
                initParts.Add($"DefaultValueFactory = _ => {opt.DefaultExpression}");
            if (opt.CustomParserExpr != null)
                initParts.Add($"CustomParser = {opt.CustomParserExpr}");
            if (opt.IsGlobal)
                initParts.Add("Recursive = true");

            if (initParts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("    {");
                foreach (var p in initParts)
                    sb.AppendLine($"        {p},");
                sb.Append("    }");
            }
            sb.AppendLine(";");
        }

        sb.AppendLine();

        // Partial property implementations
        foreach (var opt in model.Options)
        {
            string returnType = opt.ValueType;
            string nullSuppressor = opt.IsRequired ? "!" : "";
            string envSuffix = opt.EnvVarAccessorSuffix ?? "";
            if (opt.DefaultExpression != null)
            {
                // Use the `field` keyword so the declaring partial's `= value` initializer compiles (CS8050).
                // `field` holds the initializer value and is returned before ParseResult is available.
                sb.AppendLine($"    public partial {returnType} {opt.Name}");
                sb.AppendLine("    {");
                sb.AppendLine("        get");
                sb.AppendLine("        {");
                sb.AppendLine("            if (!HasParseResult) return field;");
                sb.AppendLine($"            return GetValue(_opt_{opt.Name});");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
            }
            else
            {
                sb.AppendLine(
                    $"    public partial {returnType} {opt.Name} => GetValue(_opt_{opt.Name}){nullSuppressor}{envSuffix};"
                );
            }
        }

        sb.AppendLine();

        // AddGeneratedOptions / AddOptionsTo
        if (model.IsCommandDef)
        {
            sb.AppendLine(
                "    protected override void AddGeneratedOptions(global::System.CommandLine.Command cmd)"
            );
            sb.AppendLine("    {");
            foreach (var opt in model.Options)
            {
                if (opt.IsAdvanced)
                    sb.AppendLine(
                        $"        global::Console.Cli.AdvancedOptionRegistry.Register(_opt_{opt.Name});"
                    );
                sb.AppendLine($"        cmd.Add(_opt_{opt.Name});");
                EmitMetadataRegistration(sb, opt);
                if (opt.CompletionProviderTypeName != null)
                {
                    var allAliases = new[] { opt.PrimaryAlias }.Concat(opt.ExtraAliases);
                    var aliasArgs = string.Join(", ", allAliases.Select(Quote));
                    sb.AppendLine(
                        $"        global::Console.Cli.CliCompletionProviderRegistry.Register(new[] {{ {aliasArgs} }}, typeof({opt.CompletionProviderTypeName}));"
                    );
                }
            }
            sb.AppendLine("    }");
        }
        else if (model.IsOptionPack)
        {
            // OptionPack subclasses override AddGeneratedOptions, called from AddOptionsTo
            sb.AppendLine(
                "    protected override void AddGeneratedOptions(global::System.CommandLine.Command cmd)"
            );
            sb.AppendLine("    {");
            foreach (var opt in model.Options)
            {
                if (opt.IsAdvanced)
                    sb.AppendLine(
                        $"        global::Console.Cli.AdvancedOptionRegistry.Register(_opt_{opt.Name});"
                    );
                sb.AppendLine($"        cmd.Add(_opt_{opt.Name});");
                EmitMetadataRegistration(sb, opt);
                if (opt.CompletionProviderTypeName != null)
                {
                    var allAliases = new[] { opt.PrimaryAlias }.Concat(opt.ExtraAliases);
                    var aliasArgs = string.Join(", ", allAliases.Select(Quote));
                    sb.AppendLine(
                        $"        global::Console.Cli.CliCompletionProviderRegistry.Register(new[] {{ {aliasArgs} }}, typeof({opt.CompletionProviderTypeName}));"
                    );
                }
            }
            sb.AppendLine("    }");
        }

        // AddGeneratedChildren (CommandDef) / AddChildPacksTo (OptionPack) for child fields
        var childPacks = model.Children.Where(c => c.IsOptionPack).ToList();
        var childCmds = model.Children.Where(c => c.IsCommandDef).ToList();

        if ((childCmds.Count > 0 || childPacks.Count > 0) && model.IsCommandDef)
        {
            sb.AppendLine();
            sb.AppendLine("    protected override bool HasGeneratedChildren => true;");
            sb.AppendLine();
            sb.AppendLine(
                "    protected override void AddGeneratedChildren(global::System.CommandLine.Command cmd)"
            );
            sb.AppendLine("    {");
            foreach (var child in childPacks)
                sb.AppendLine(
                    $"        ((global::Console.Cli.OptionPack){child.Name}).AddOptionsTo(cmd);"
                );
            foreach (var child in childCmds)
                sb.AppendLine(
                    $"        cmd.Add(((global::Console.Cli.CommandDef){child.Name}).Build());"
                );
            sb.AppendLine("    }");
        }
        else if (childPacks.Count > 0 && model.IsOptionPack)
        {
            sb.AppendLine();
            sb.AppendLine(
                "    protected override void AddChildPacksTo(global::System.CommandLine.Command cmd)"
            );
            sb.AppendLine("    {");
            foreach (var child in childPacks)
                sb.AppendLine(
                    $"        ((global::Console.Cli.OptionPack){child.Name}).AddOptionsTo(cmd);"
                );
            sb.AppendLine("    }");
        }

        // Description override for CommandDef
        if (
            model.IsCommandDef
            && !model.HasExplicitDescriptionOverride
            && !string.IsNullOrEmpty(model.XmlDescription)
        )
        {
            sb.AppendLine();
            sb.AppendLine(
                $"    public override string Description => {Quote(model.XmlDescription!)};"
            );
        }

        // DetailedDescription override for CommandDef
        if (
            model.IsCommandDef
            && !model.HasExplicitDetailedDescriptionOverride
            && !string.IsNullOrEmpty(model.XmlRemarks)
        )
        {
            sb.AppendLine();
            sb.AppendLine(
                $"    public override string? DetailedDescription => {Quote(model.XmlRemarks!)};"
            );
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    static void EmitMetadataRegistration(StringBuilder sb, OptionPropModel opt)
    {
        if (opt.EnvVar is null && opt.AllowedValuesText is null && opt.DefaultText is null)
            return;
        var envArg = opt.EnvVar is not null ? Quote(opt.EnvVar) : "null";
        var allowedArg = opt.AllowedValuesText is not null ? Quote(opt.AllowedValuesText) : "null";
        var defaultArg = opt.DefaultText is not null ? Quote(opt.DefaultText) : "null";
        sb.AppendLine(
            $"        global::Console.Cli.OptionMetadataRegistry.Register(_opt_{opt.Name},"
        );
        sb.AppendLine(
            $"            new global::Console.Cli.OptionMetadata({envArg}, {allowedArg}, {defaultArg}));"
        );
    }

    static string Quote(string s) =>
        $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")}\"";
}
