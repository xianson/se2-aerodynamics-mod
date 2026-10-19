// MODVAR1: a 'var' whose type is an array of a type declared in the mod. The game's whitelist analyzer bans that
// (VRS1001 "The symbol X[] is banned for use in scripts": it checks the symbol behind 'var', and an array type of a
// mod type is in no whitelist) and the world then fails to load; ModCheck's run of the same analyzer did not flag
// every such case (2026-10-02: `var snap = Volatile.Read(ref _siteSnap)` passed offline, failed in game).
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class VarArrayAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor("MODVAR1",
        "var of an array of a mod type", "'var' here is {0}: the game bans that symbol (VRS1001) - write the type out as IEnumerable<T>/List<T>, or avoid the array",
        "ModCheck", DiagnosticSeverity.Error, isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.NotConfigurable);   // (the mods' files open with '#pragma warning disable': the game does not let that hide this)
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Check, SyntaxKind.IdentifierName);
    }

    static void Check(SyntaxNodeAnalysisContext c)
    {
        var id = (IdentifierNameSyntax)c.Node;
        if (!id.IsVar) return;
        if (c.SemanticModel.GetSymbolInfo(id).Symbol is not IArrayTypeSymbol arr) return;
        ITypeSymbol e = arr.ElementType;
        while (e is IArrayTypeSymbol inner) e = inner.ElementType;
        if (e.IsTupleType) return;   // (System.ValueTuple: the game's own; its syntax only looks declared here)
        if (e.Locations.Length > 0 && e.Locations[0].IsInSource)
            c.ReportDiagnostic(Diagnostic.Create(Rule, id.GetLocation(), arr.ToDisplayString()));
    }
}
