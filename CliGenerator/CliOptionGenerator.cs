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

        // Secondary provider: find classes with [CliManualOptions] (non-partial option packs)
        var manualOptsProvider = context
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) =>
                    node is ClassDeclarationSyntax c && c.AttributeLists.Count > 0,
                transform: static (ctx, ct) =>
                {
                    if (ctx.Node is not ClassDeclarationSyntax)
                        return null;
                    var sym =
                        ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node, ct)
                        as INamedTypeSymbol;
                    if (sym is null)
                        return null;
                    var allAliases = new List<string>();
                    foreach (var attr in sym.GetAttributes())
                    {
                        if (
                            attr.AttributeClass?.ToDisplayString()
                            != "Console.Cli.CliManualOptionsAttribute"
                        )
                            continue;
                        var ctorArg = attr.ConstructorArguments.FirstOrDefault();
                        if (ctorArg.Kind == TypedConstantKind.Array)
                            allAliases.AddRange(
                                ctorArg
                                    .Values.Select(v => v.Value?.ToString() ?? "")
                                    .Where(s => s.Length > 0)
                            );
                        else if (ctorArg.Value is string sv && sv.Length > 0)
                            allAliases.Add(sv);
                    }
                    if (allAliases.Count == 0)
                        return null;
                    return new ManualOptsModel
                    {
                        TypeFullName = sym.ToDisplayString(),
                        Aliases = allAliases.ToArray(),
                    };
                }
            )
            .Where(static m => m is not null);

        // Batch step: emit the compile-time completion tree
        var allModels = provider.Collect();
        var allManualOpts = manualOptsProvider.Collect();
        var combined = allModels.Combine(allManualOpts);
        context.RegisterSourceOutput(
            combined,
            static (ctx, pair) =>
            {
                var (models, manualOpts) = pair;
                var source = EmitCompletionTree(
                    models.Where(m => m is not null).Select(m => m!).ToList(),
                    manualOpts.Where(m => m is not null).Select(m => m!).ToList()
                );
                if (source is not null)
                    ctx.AddSource("CompletionTree.g.cs", SourceText.From(source, Encoding.UTF8));
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
        public bool HasExecuteHandler;

        /// <summary>
        /// CLI name returned by the <c>Name</c> property override, if it is a simple string literal.
        /// Used to detect child commands whose CLI name collides with this command's name.
        /// </summary>
        public string? CliName;
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
        public string? AllowedValuesText; // joined display string for [allowed:]
        public string? DefaultText; // pre-formatted display string for [default:]
    }

    sealed class ChildModel
    {
        public string Name = "";
        public bool IsCommandDef;
        public bool IsOptionPack;
        public string TypeName = "";
        public string? Description;
    }

    sealed class ManualOptsModel
    {
        public string TypeFullName = "";
        public string[] Aliases = Array.Empty<string>();
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
                            Description = isCD ? GetXmlSummary(member.Type) : null,
                        }
                    );
                }
            }
        }

        // Detect if class overrides ExecuteAsync
        model.HasExecuteHandler = cls.GetMembers("ExecuteAsync")
            .OfType<IMethodSymbol>()
            .Any(static m => m.IsOverride && !m.IsStatic);

        // Extract CLI name from the Name property override (if it's a simple string literal).
        // Used to detect child commands whose name collides with the parent's name.
        if (model.IsCommandDef)
            model.CliName = GetCliNameLiteral(cls);

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

    /// <summary>
    /// Returns the string literal value of the <c>Name</c> property override, if it is a
    /// simple expression-bodied property returning a string literal (e.g. <c>=> "subscription"</c>).
    /// Returns <c>null</c> for hand-written classes or non-literal overrides.
    /// </summary>
    static string? GetCliNameLiteral(INamedTypeSymbol cls)
    {
        var nameProp = cls.GetMembers("Name")
            .OfType<IPropertySymbol>()
            .FirstOrDefault(static p => p.IsOverride && !p.IsStatic && p.Parameters.Length == 0);
        if (nameProp == null)
            return null;
        foreach (var syntaxRef in nameProp.DeclaringSyntaxReferences)
        {
            if (
                syntaxRef.GetSyntax() is PropertyDeclarationSyntax propDecl
                && propDecl.ExpressionBody?.Expression
                    is LiteralExpressionSyntax
                    {
                        Token: { RawKind: (int)SyntaxKind.StringLiteralToken } tok
                    }
            )
                return tok.ValueText;
        }
        return null;
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

    static string FieldToCliName(string fieldName)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < fieldName.Length; i++)
        {
            var c = fieldName[i];
            if (char.IsUpper(c) && i > 0)
            {
                var prevIsLower = i > 0 && char.IsLower(fieldName[i - 1]);
                var nextIsLower = i + 1 < fieldName.Length && char.IsLower(fieldName[i + 1]);
                if (prevIsLower || nextIsLower)
                    sb.Append('-');
            }
            sb.Append(char.ToLowerInvariant(c));
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
        sb.AppendLine("using System.Linq;");
        sb.AppendLine();

        bool hasNs = !string.IsNullOrEmpty(model.Namespace);
        if (hasNs)
        {
            sb.AppendLine($"namespace {model.Namespace};").AppendLine();
        }

        sb.AppendLine($"partial class {model.ClassName}");
        sb.AppendLine("{");

        // Private CliOption<T> fields
        foreach (var opt in model.Options)
        {
            sb.AppendLine(
                $"    private readonly global::Console.Cli.Parsing.CliOption<{opt.ValueType}> _opt_{opt.Name} = new()"
            );
            sb.AppendLine("    {");
            sb.AppendLine($"        Name = {Quote(opt.PrimaryAlias)},");
            if (opt.ExtraAliases.Length > 0)
                sb.AppendLine(
                    $"        Aliases = new string[] {{{string.Join(", ", opt.ExtraAliases.Select(Quote))}}},"
                );
            if (!string.IsNullOrEmpty(opt.Description))
                sb.AppendLine($"        Description = {Quote(opt.Description)},");
            if (opt.IsRequired)
                sb.AppendLine("        Required = true,");
            if (opt.AllowMultiple)
                sb.AppendLine("        AllowMultipleArgumentsPerToken = true,");
            if (opt.IsAdvanced)
                sb.AppendLine("        IsAdvanced = true,");
            if (opt.IsGlobal)
                sb.AppendLine("        Recursive = true,");
            if (!opt.IsRequired && (opt.HasNullableAnnotation || opt.DefaultExpression != null))
                sb.AppendLine("        ValueIsOptional = true,");
            if (opt.DefaultExpression != null)
                sb.AppendLine($"        DefaultValueFactory = () => {opt.DefaultExpression},");
            if (opt.CustomParserExpr != null)
            {
                var parserExpr = EmitCliOptionParser(opt);
                // For collection types, emit ElementParser instead of Parser
                var propName = opt.AllowMultiple ? "ElementParser" : "Parser";
                // Wrap in parens to avoid switch expression braces confusing the object initializer
                sb.AppendLine($"        {propName} = ({parserExpr}),");
            }
            if (
                opt.EnvVar is not null
                || opt.AllowedValuesText is not null
                || opt.DefaultText is not null
            )
            {
                var envArg = opt.EnvVar is not null ? Quote(opt.EnvVar) : "null";
                var allowedArg = opt.AllowedValuesText is not null
                    ? Quote(opt.AllowedValuesText)
                    : "null";
                var defaultArg = opt.DefaultText is not null ? Quote(opt.DefaultText) : "null";
                sb.AppendLine(
                    $"        Metadata = new global::Console.Cli.OptionMetadata({envArg}, {allowedArg}, {defaultArg}),"
                );
            }
            sb.AppendLine("    };");
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
                sb.AppendLine($"    public partial {returnType} {opt.Name}");
                sb.AppendLine("    {");
                sb.AppendLine("        get");
                sb.AppendLine("        {");
                sb.AppendLine($"            if (!_opt_{opt.Name}.WasProvided) return field;");
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

        // EnumerateOptions — yields this class's own CliOption instances
        if (model.Options.Count > 0)
        {
            sb.AppendLine(
                "    internal override System.Collections.Generic.IEnumerable<global::Console.Cli.Parsing.CliOption> EnumerateOptions()"
            );
            sb.AppendLine("    {");
            foreach (var opt in model.Options)
                sb.AppendLine($"        yield return _opt_{opt.Name};");
            sb.AppendLine("    }");
        }

        // Children
        var childPacks = model.Children.Where(c => c.IsOptionPack).ToList();
        var childCmds = model.Children.Where(c => c.IsCommandDef).ToList();

        if (model.IsCommandDef)
        {
            // EnumerateChildren — yields child CommandDefs
            if (childCmds.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("    protected override bool HasGeneratedChildren => true;");
                sb.AppendLine();
                sb.AppendLine(
                    "    internal override System.Collections.Generic.IEnumerable<global::Console.Cli.CommandDef> EnumerateChildren()"
                );
                sb.AppendLine("    {");
                foreach (var child in childCmds)
                {
                    var cliName = FieldToCliName(child.Name);
                    if (model.CliName != null && cliName == model.CliName)
                    {
                        // Inline children when names collide
                        sb.AppendLine($"        if ({child.Name} is not null)");
                        sb.AppendLine(
                            $"            foreach (var __c in {child.Name}.InlineChildren())"
                        );
                        sb.AppendLine($"                yield return __c;");
                    }
                    else
                    {
                        sb.AppendLine(
                            $"        if ({child.Name} is not null) yield return {child.Name};"
                        );
                    }
                }
                sb.AppendLine("    }");
            }

            // EnumerateOptionPacks — yields child OptionPacks
            if (childPacks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(
                    "    internal override System.Collections.Generic.IEnumerable<global::Console.Cli.OptionPack> EnumerateOptionPacks()"
                );
                sb.AppendLine("    {");
                foreach (var child in childPacks)
                    sb.AppendLine($"        yield return {child.Name};");
                sb.AppendLine("    }");
            }
        }
        else if (model.IsOptionPack)
        {
            // EnumerateChildPacks for OptionPack
            if (childPacks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(
                    "    internal override System.Collections.Generic.IEnumerable<global::Console.Cli.OptionPack> EnumerateChildPacks()"
                );
                sb.AppendLine("    {");
                foreach (var child in childPacks)
                    sb.AppendLine($"        yield return {child.Name};");
                sb.AppendLine("    }");
            }
        }

        // HasExecuteHandler override
        if (model.IsCommandDef && model.HasExecuteHandler)
        {
            sb.AppendLine();
            sb.AppendLine("    protected internal override bool HasExecuteHandler => true;");
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

    /// <summary>
    /// Converts a System.CommandLine CustomParser expression (ArgumentResult r => ...)
    /// to a simple string parser (string __raw => ...).
    /// </summary>
    static string EmitCliOptionParser(OptionPropModel opt)
    {
        if (opt.CustomParserExpr == null)
            return "null";

        // The old parser expressions use r.Tokens[0].Value or r.Tokens patterns.
        // We need to transform them to simple string parsers.
        var expr = opt.CustomParserExpr;

        // Bool toggle parser: special case — detect the --no- pattern
        if (expr.Contains("r.Parent is global::System.CommandLine.Parsing.OptionResult"))
        {
            // This is the bool toggle parser. In the new model, the parser just handles
            // true/false string values. The --no- negation is handled by CliParser directly.
            return "__raw => bool.Parse(__raw)";
        }

        // Collection switch parser: r => r.Tokens.Select(t => t.Value switch { ... }).ToList()
        if (expr.StartsWith("r => r.Tokens.Select(t => t.Value switch"))
        {
            var switchStart = expr.IndexOf("t.Value switch", StringComparison.Ordinal);
            var switchBody = expr.Substring(switchStart + "t.Value switch".Length);
            // Remove .ToList() suffix
            if (switchBody.EndsWith(".ToList()"))
                switchBody = switchBody.Substring(0, switchBody.Length - ".ToList()".Length);
            // Remove trailing ) from the Select(t => ...) call
            if (switchBody.EndsWith(")"))
                switchBody = switchBody.Substring(0, switchBody.Length - 1);
            // ElementParser returns object, so cast the result
            return $"__raw => (object)(__raw switch {switchBody.TrimStart()})";
        }

        // Simple switch parser: r => r.Tokens[0].Value switch { ... }
        if (expr.Contains("r.Tokens[0].Value switch"))
        {
            return expr.Replace("r => r.Tokens[0].Value switch", "__raw => __raw switch")
                .Replace("r => r.Tokens.Count > 0 ? ", "__raw => __raw != null ? ")
                .Replace("r.Tokens[0].Value", "__raw");
        }

        // Nullable check: r => r.Tokens.Count > 0 ? ... : null
        if (expr.Contains("r.Tokens.Count > 0"))
        {
            return expr.Replace("r => r.Tokens.Count > 0", "__raw => __raw != null")
                .Replace("r.Tokens[0].Value", "__raw");
        }

        // Simple value parser: r => SomeType.Parse(r.Tokens[0].Value) or similar
        if (expr.Contains("r.Tokens[0].Value"))
        {
            return expr.Replace("r => ", "__raw => ").Replace("r.Tokens[0].Value", "__raw");
        }

        // Collection parser with token iteration
        if (expr.Contains("r.Tokens.Select"))
        {
            var inner = expr;
            inner = inner.Replace("r => r.Tokens.Select(t => ", "__raw => (object)(");
            inner = inner.Replace("t.Value", "__raw");
            if (inner.EndsWith(").ToList()"))
                inner = inner.Substring(0, inner.Length - ".ToList()".Length);
            return inner;
        }

        // Fallback: just use as-is with substitution
        return expr.Replace("r => ", "__raw => ").Replace("r.Tokens[0].Value", "__raw");
    }

    static string Quote(string s) =>
        $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")}\"";

    // ── Completion tree emission ─────────────────────────────────────────────

    // Intermediate node representation used during tree construction.
    sealed class NodeData
    {
        public string CliName = "";
        public List<string> Options = [];
        public List<NodeData> Children = [];

        /// <summary>Full type name of the ClassModel this node came from (used to avoid cycles).</summary>
        public string TypeFullName = "";

        /// <summary>Fully-qualified type name of the static helper method key.</summary>
        public string MethodKey = "";
    }

    static string? EmitCompletionTree(List<ClassModel> models, List<ManualOptsModel> manualOpts)
    {
        // Build lookup dicts
        var dict = new Dictionary<string, ClassModel>(StringComparer.Ordinal);
        foreach (var m in models)
        {
            var key = m.Namespace.Length > 0 ? $"{m.Namespace}.{m.ClassName}" : m.ClassName;
            if (!dict.ContainsKey(key))
                dict[key] = m;
        }

        var manualDict = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var mo in manualOpts)
        {
            if (!manualDict.TryGetValue(mo.TypeFullName, out var list))
                manualDict[mo.TypeFullName] = list = [];
            foreach (var a in mo.Aliases)
                if (!list.Contains(a))
                    list.Add(a);
        }

        if (!dict.TryGetValue("Console.Cli.RootCommandDef", out var rootModel))
            return null;

        // Collect dynamic providers: alias → fully-qualified type name
        var dynamicProviders = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectDynamicProviders(rootModel, dict, dynamicProviders, new HashSet<string>());

        // Build the node tree
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var root = BuildNodeData(
            "Console.Cli.RootCommandDef",
            null,
            rootModel,
            dict,
            manualDict,
            visited
        );
        if (root is null)
            return null;

        // --help is added by System.CommandLine (recursive) and --help-more,
        // --help-commands, --help-commands-flat are added at runtime in
        // CommandDef.SetupHelpOptions(). Inject them into the completion tree
        // so auto-complete/suggestion works for these options.
        InjectHelpOptions(root);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace Console.Cli;");
        sb.AppendLine();
        sb.AppendLine("internal static class CompletionTree");
        sb.AppendLine("{");

        // DynamicProviders map
        sb.AppendLine(
            "    internal static readonly System.Collections.Generic.IReadOnlyDictionary<string, ICliCompletionProvider> DynamicProviders ="
        );
        sb.AppendLine(
            "        new System.Collections.Generic.Dictionary<string, ICliCompletionProvider>"
        );
        sb.AppendLine("        {");
        foreach (var kvp in dynamicProviders)
            sb.AppendLine($"            [{Quote(kvp.Key)}] = new {kvp.Value}(),");
        sb.AppendLine("        };");
        sb.AppendLine();

        // Root is built eagerly with all immediate children (service-level nodes).
        // Each service node carries a lazy factory for its subtree: the subtree is only
        // allocated when the completion handler actually walks into that service.
        sb.AppendLine("    internal static readonly global::Console.Cli.CompletionNode Root =");
        sb.AppendLine($"        new({Quote(root.CliName)},");
        sb.AppendLine($"            {InlineOptions(root.Options)},");
        sb.AppendLine("            new global::Console.Cli.CompletionNode[]");
        sb.AppendLine("            {");

        for (int si = 0; si < root.Children.Count; si++)
        {
            var svc = root.Children[si];
            string factory =
                svc.Children.Count == 0
                    ? "global::System.Array.Empty<global::Console.Cli.CompletionNode>()"
                    : $"Build_S{si}";
            sb.AppendLine(
                $"                new({Quote(svc.CliName)}, {InlineOptions(svc.Options)}, {factory}),"
            );
        }

        sb.AppendLine("            });");
        sb.AppendLine();

        // One lazy-initialised subtree builder per immediate child of root.
        // The subtree is collected in post-order so children always precede their parents
        // in the local nodes[] array — same guarantee as the global array in option 2.
        const int ChunkSize = 500;

        for (int si = 0; si < root.Children.Count; si++)
        {
            var svc = root.Children[si];
            if (svc.Children.Count == 0)
                continue;

            // Post-order subtree; svc itself is last — exclude it (already built above).
            var subList = new List<NodeData>();
            CollectAllNodes(svc, subList);
            int subN = subList.Count - 1;

            var localIdx = new Dictionary<NodeData, int>(subN);
            for (int i = 0; i < subN; i++)
                localIdx[subList[i]] = i;

            string mk = $"S{si}";

            sb.AppendLine($"    private static global::Console.Cli.CompletionNode[] Build_{mk}()");
            sb.AppendLine("    {");
            sb.AppendLine($"        var n = new global::Console.Cli.CompletionNode[{subN}];");

            int numChunks = (subN + ChunkSize - 1) / ChunkSize;
            for (int c = 0; c < numChunks; c++)
                sb.AppendLine($"        BuildSub_{mk}_{c}(n);");

            // Return the direct children of svc from the local array.
            sb.Append("        return new global::Console.Cli.CompletionNode[] {");
            sb.Append(string.Join(", ", svc.Children.Select(ch => $"n[{localIdx[ch]}]")));
            sb.AppendLine("};");
            sb.AppendLine("    }");
            sb.AppendLine();

            for (int c = 0; c < numChunks; c++)
            {
                int chunkStart = c * ChunkSize;
                int chunkEnd = Math.Min(chunkStart + ChunkSize, subN);

                sb.AppendLine(
                    $"    private static void BuildSub_{mk}_{c}(global::Console.Cli.CompletionNode[] n)"
                );
                sb.AppendLine("    {");

                for (int i = chunkStart; i < chunkEnd; i++)
                {
                    var node = subList[i];
                    sb.Append($"        n[{i}] = new(");
                    sb.Append(Quote(node.CliName));
                    sb.Append(", ");

                    sb.Append(InlineOptions(node.Options));
                    sb.Append(", ");

                    if (node.Children.Count == 0)
                        sb.Append(
                            "global::System.Array.Empty<global::Console.Cli.CompletionNode>()"
                        );
                    else
                    {
                        sb.Append("new[] {");
                        sb.Append(
                            string.Join(", ", node.Children.Select(ch => $"n[{localIdx[ch]}]"))
                        );
                        sb.Append('}');
                    }

                    sb.AppendLine(");");
                }

                sb.AppendLine("    }");
                sb.AppendLine();
            }
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    static string InlineOptions(List<string> options) =>
        options.Count == 0
            ? "global::System.Array.Empty<string>()"
            : "new[] {" + string.Join(", ", options.Select(Quote)) + "}";

    static void CollectAllNodes(NodeData node, List<NodeData> result)
    {
        // Post-order: children first, then parent
        foreach (var child in node.Children)
            CollectAllNodes(child, result);
        result.Add(node);
    }

    static NodeData? BuildNodeData(
        string fullTypeName,
        string? fieldName,
        ClassModel model,
        Dictionary<string, ClassModel> dict,
        Dictionary<string, List<string>> manualDict,
        HashSet<string> visited
    )
    {
        if (!visited.Add(fullTypeName))
            return null;

        var cliName =
            model.CliName
            ?? (fieldName is not null ? FieldToCliName(fieldName) : null)
            ?? model.ClassName.ToLowerInvariant();

        var options = CollectNodeOptions(model, dict, manualDict, new HashSet<string>());

        var children = new List<NodeData>();
        foreach (var child in model.Children)
        {
            if (!child.IsCommandDef)
                continue;

            var childCliName = FieldToCliName(child.Name);

            // Parent-child name collision: inline grandchildren directly
            if (model.CliName is not null && childCliName == model.CliName)
            {
                if (dict.TryGetValue(child.TypeName, out var inlinedModel))
                {
                    var inlinedChildren = BuildCommandDefChildren(
                        inlinedModel,
                        dict,
                        manualDict,
                        new HashSet<string>(visited)
                    );
                    children.AddRange(inlinedChildren);
                    // Merge the inlined command's options into ours
                    var inlinedOpts = CollectNodeOptions(
                        inlinedModel,
                        dict,
                        manualDict,
                        new HashSet<string>()
                    );
                    foreach (var o in inlinedOpts)
                        if (!options.Contains(o))
                            options.Add(o);
                }
                continue;
            }

            if (!dict.TryGetValue(child.TypeName, out var childModel))
                continue;

            var childNode = BuildNodeData(
                child.TypeName,
                child.Name,
                childModel,
                dict,
                manualDict,
                new HashSet<string>(visited)
            );
            if (childNode is not null)
                children.Add(childNode);
        }

        visited.Remove(fullTypeName);

        return new NodeData
        {
            TypeFullName = fullTypeName,
            CliName = cliName,
            Options = options,
            Children = children,
        };
    }

    static List<NodeData> BuildCommandDefChildren(
        ClassModel model,
        Dictionary<string, ClassModel> dict,
        Dictionary<string, List<string>> manualDict,
        HashSet<string> visited
    )
    {
        var result = new List<NodeData>();
        foreach (var child in model.Children)
        {
            if (!child.IsCommandDef)
                continue;
            if (!dict.TryGetValue(child.TypeName, out var childModel))
                continue;
            var childNode = BuildNodeData(
                child.TypeName,
                child.Name,
                childModel,
                dict,
                manualDict,
                new HashSet<string>(visited)
            );
            if (childNode is not null)
                result.Add(childNode);
        }
        return result;
    }

    /// <summary>
    /// Collects all non-advanced option aliases from a CommandDef or OptionPack model,
    /// recursively including its OptionPack children.
    /// </summary>
    static List<string> CollectNodeOptions(
        ClassModel model,
        Dictionary<string, ClassModel> dict,
        Dictionary<string, List<string>> manualDict,
        HashSet<string> visited
    )
    {
        var modelKey =
            model.Namespace.Length > 0 ? $"{model.Namespace}.{model.ClassName}" : model.ClassName;
        if (!visited.Add(modelKey))
            return [];

        var result = new List<string>();

        // Options declared on this class
        foreach (var opt in model.Options)
        {
            if (opt.IsAdvanced)
                continue;
            result.Add(opt.PrimaryAlias);
            foreach (var extra in opt.ExtraAliases)
                result.Add(extra);
        }

        // Recurse into OptionPack children
        foreach (var child in model.Children)
        {
            if (!child.IsOptionPack)
                continue;

            // From ClassModel (partial OptionPacks)
            if (dict.TryGetValue(child.TypeName, out var packModel))
            {
                var packOpts = CollectNodeOptions(packModel, dict, manualDict, visited);
                foreach (var o in packOpts)
                    if (!result.Contains(o))
                        result.Add(o);
            }

            // From [CliManualOptions] (non-partial OptionPacks)
            if (manualDict.TryGetValue(child.TypeName, out var manualAliases))
                foreach (var a in manualAliases)
                    if (!result.Contains(a))
                        result.Add(a);
        }

        return result;
    }

    /// <summary>
    /// Injects --help into every node and root-only help options (--help-more,
    /// --help-commands, --help-commands-flat) into the root node.
    /// These options are added at runtime by System.CommandLine / CommandDef.SetupHelpOptions()
    /// and are invisible to the source generator's model.
    /// </summary>
    static void InjectHelpOptions(NodeData root)
    {
        // Root-only help options
        foreach (var opt in new[] { "--help-more", "--help-commands", "--help-commands-flat" })
            if (!root.Options.Contains(opt))
                root.Options.Add(opt);

        // --help is recursive (appears on every command)
        AddHelpToAll(root);

        static void AddHelpToAll(NodeData node)
        {
            if (!node.Options.Contains("--help"))
                node.Options.Add("--help");
            foreach (var child in node.Children)
                AddHelpToAll(child);
        }
    }

    /// <summary>
    /// Walks the full command tree collecting dynamic provider registrations.
    /// </summary>
    static void CollectDynamicProviders(
        ClassModel model,
        Dictionary<string, ClassModel> dict,
        Dictionary<string, string> result,
        HashSet<string> visited
    )
    {
        var modelKey =
            model.Namespace.Length > 0 ? $"{model.Namespace}.{model.ClassName}" : model.ClassName;
        if (!visited.Add(modelKey))
            return;

        foreach (var opt in model.Options)
        {
            if (opt.IsAdvanced || opt.CompletionProviderTypeName is null)
                continue;
            var allAliases = new[] { opt.PrimaryAlias }.Concat(opt.ExtraAliases);
            foreach (var alias in allAliases)
                if (!result.ContainsKey(alias))
                    result[alias] = opt.CompletionProviderTypeName;
        }

        foreach (var child in model.Children)
        {
            if (dict.TryGetValue(child.TypeName, out var childModel))
                CollectDynamicProviders(childModel, dict, result, visited);
        }
    }
}
