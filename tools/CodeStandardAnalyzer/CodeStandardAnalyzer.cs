using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KLaniBot.CodeStandard;

// Use case: every project in this repository loads this analyzer, so the parts of the code standard in AGENTS.md that a
// machine can decide fail the build instead of waiting for a reviewer. It sees only the method it reports on: a throw in a
// called method is that method's finding, so a clean caller relies on clean callees.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeStandardAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "CodeStandard";

    public static readonly DiagnosticDescriptor NoTuple = new(
        "KLB001",
        "No tuples",
        "Use a named record instead of a tuple",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BoolMethodThrows = new(
        "KLB002",
        "A method returning bool never throws",
        "'{0}' returns bool and must report failure through its result instead of throwing",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UndeclaredThrow = new(
        "KLB003",
        "A thrown exception is named in the header",
        "'{0}' throws {1}, which its header comment does not name",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor IncompleteEnumSwitch = new(
        "KLB004",
        "A switch over a project enum names every value",
        "The switch over {0} does not name {1}; a discard arm hides a value added later",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor LongMethod = new(
        "KLB005",
        "A method stays short",
        "'{0}' has {1} lines of code; split it into named steps of at most {2}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // Lines that hold code; blank lines and comment-only lines do not count, so explaining a step costs nothing.
    private const int MaximumCodeLines = 40;

    public static readonly DiagnosticDescriptor TypeWithoutUseCase = new(
        "KLB006",
        "A type opens with its use case",
        "'{0}' ({1}) has no comment that says who uses it and when",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [NoTuple, BoolMethodThrows, UndeclaredThrow, IncompleteEnumSwitch, LongMethod, TypeWithoutUseCase];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(ReportTupleType, SyntaxKind.TupleType);
        context.RegisterSyntaxNodeAction(ReportTupleExpression, SyntaxKind.TupleExpression);
        context.RegisterSyntaxNodeAction(CheckEnumSwitch, SyntaxKind.SwitchExpression, SyntaxKind.SwitchStatement);
        context.RegisterSyntaxNodeAction(
            CheckThrowingMember,
            SyntaxKind.MethodDeclaration,
            SyntaxKind.ConstructorDeclaration,
            SyntaxKind.OperatorDeclaration,
            SyntaxKind.ConversionOperatorDeclaration,
            SyntaxKind.GetAccessorDeclaration,
            SyntaxKind.SetAccessorDeclaration,
            SyntaxKind.InitAccessorDeclaration,
            SyntaxKind.PropertyDeclaration,
            SyntaxKind.IndexerDeclaration);
        context.RegisterSyntaxNodeAction(
            CheckTypeHeader,
            SyntaxKind.ClassDeclaration,
            SyntaxKind.StructDeclaration,
            SyntaxKind.InterfaceDeclaration,
            SyntaxKind.RecordDeclaration,
            SyntaxKind.RecordStructDeclaration,
            SyntaxKind.EnumDeclaration,
            SyntaxKind.DelegateDeclaration);
        context.RegisterSyntaxNodeAction(
            CheckLength,
            SyntaxKind.MethodDeclaration,
            SyntaxKind.ConstructorDeclaration,
            SyntaxKind.OperatorDeclaration,
            SyntaxKind.ConversionOperatorDeclaration,
            SyntaxKind.GetAccessorDeclaration,
            SyntaxKind.SetAccessorDeclaration,
            SyntaxKind.InitAccessorDeclaration,
            SyntaxKind.PropertyDeclaration,
            SyntaxKind.IndexerDeclaration);
    }

    // A partial type needs the header on one of its parts only.
    private static void CheckTypeHeader(SyntaxNodeAnalysisContext context)
    {
        SyntaxNode type = context.Node;
        SyntaxToken identifier = TypeIdentifier(type);
        ISymbol? symbol = context.SemanticModel.GetDeclaredSymbol(type, context.CancellationToken);
        IEnumerable<SyntaxNode> parts = symbol?.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax(context.CancellationToken)) ?? [type];
        if (parts.Any(HasHeader))
        {
            return;
        }

        string placement = type.Parent is BaseTypeDeclarationSyntax ? "nested" : "top-level";
        context.ReportDiagnostic(Diagnostic.Create(TypeWithoutUseCase, identifier.GetLocation(), identifier.Text, placement));
    }

    private static SyntaxToken TypeIdentifier(SyntaxNode type)
    {
        return type switch
        {
            BaseTypeDeclarationSyntax declaration => declaration.Identifier,
            DelegateDeclarationSyntax declaration => declaration.Identifier,
            _ => type.GetFirstToken()
        };
    }

    // A comment above the type, or between its attributes and its name.
    private static bool HasHeader(SyntaxNode type)
    {
        SyntaxToken identifier = TypeIdentifier(type);

        return type.DescendantTokens()
            .TakeWhile(token => token.SpanStart <= identifier.SpanStart)
            .SelectMany(token => token.LeadingTrivia)
            .Any(IsComment);
    }

    // Local functions and lambdas are part of the member they are written in, so they count toward its length.
    private static void CheckLength(SyntaxNodeAnalysisContext context)
    {
        SyntaxNode member = context.Node;
        SyntaxNode? body = BodyOf(member);
        if (body == null)
        {
            return;
        }

        int codeLines = CodeLines(body);
        if (codeLines <= MaximumCodeLines)
        {
            return;
        }
        Location location = IdentifierLocation(member);
        context.ReportDiagnostic(Diagnostic.Create(LongMethod, location, NameOf(member), codeLines, MaximumCodeLines));
    }

    private static int CodeLines(SyntaxNode body)
    {
        var lines = new HashSet<int>();
        foreach (SyntaxToken token in body.DescendantTokens())
        {
            FileLinePositionSpan span = token.GetLocation().GetLineSpan();
            for (int line = span.StartLinePosition.Line; line <= span.EndLinePosition.Line; line++)
            {
                lines.Add(line);
            }
        }
        return lines.Count;
    }

    private static void ReportTupleType(SyntaxNodeAnalysisContext context)
    {
        context.ReportDiagnostic(Diagnostic.Create(NoTuple, context.Node.GetLocation()));
    }

    // The target of a deconstruction, as in (first, second) = pair or foreach ((first, second) in pairs), is written like a
    // tuple but creates none.
    private static void ReportTupleExpression(SyntaxNodeAnalysisContext context)
    {
        SyntaxNode tuple = context.Node;
        bool isDeconstructionTarget = tuple.Parent is AssignmentExpressionSyntax assignment && assignment.Left == tuple
            || tuple.Parent is ForEachVariableStatementSyntax loop && loop.Variable == tuple;
        bool isNestedDeconstructionTarget = tuple.Parent is ArgumentSyntax && tuple.Ancestors().OfType<AssignmentExpressionSyntax>()
            .Any(assignment => assignment.Left.Span.Contains(tuple.Span));
        if (isDeconstructionTarget || isNestedDeconstructionTarget)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(NoTuple, tuple.GetLocation()));
    }

    // Only enums declared in this repository: their values are ours to name, and a new one must be handled wherever the
    // enum is switched on. Flags enums combine values and are left out.
    private static void CheckEnumSwitch(SyntaxNodeAnalysisContext context)
    {
        ExpressionSyntax governing = context.Node switch
        {
            SwitchExpressionSyntax expression => expression.GoverningExpression,
            SwitchStatementSyntax statement => statement.Expression,
            _ => throw new System.InvalidOperationException("Registered only for switch expressions and statements.")
        };
        if (context.SemanticModel.GetTypeInfo(governing, context.CancellationToken).Type is not INamedTypeSymbol type
            || type.TypeKind != TypeKind.Enum
            || !type.Locations.Any(location => location.IsInSource)
            || type.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == "FlagsAttribute"))
        {
            return;
        }

        var named = new HashSet<string>();
        foreach (SyntaxNode node in context.Node.DescendantNodes(child => child is not SwitchExpressionSyntax and not SwitchStatementSyntax
            || child == context.Node))
        {
            ISymbol? symbol = node is ExpressionSyntax expression
                ? context.SemanticModel.GetSymbolInfo(expression, context.CancellationToken).Symbol
                : null;
            if (node is ExpressionSyntax value
                && symbol is IFieldSymbol { HasConstantValue: true } field
                && SymbolEqualityComparer.Default.Equals(field.ContainingType, type)
                && IsInPattern(value))
            {
                named.Add(field.Name);
            }
        }
        string[] missing = type.GetMembers().OfType<IFieldSymbol>()
            .Where(field => field.HasConstantValue && !named.Contains(field.Name))
            .Select(field => field.Name)
            .ToArray();
        if (missing.Length > 0)
        {
            string missingNames = string.Join(", ", missing);
            context.ReportDiagnostic(Diagnostic.Create(IncompleteEnumSwitch, governing.GetLocation(), type.Name, missingNames));
        }
    }

    private static bool IsInPattern(SyntaxNode value)
    {
        return value.Ancestors().TakeWhile(ancestor => ancestor is not SwitchExpressionArmSyntax and not SwitchSectionSyntax)
            .Any(ancestor => ancestor is PatternSyntax or CaseSwitchLabelSyntax)
            || value.Parent is CaseSwitchLabelSyntax;
    }

    private static void CheckThrowingMember(SyntaxNodeAnalysisContext context)
    {
        SyntaxNode member = context.Node;
        SyntaxNode? body = BodyOf(member);
        if (body == null)
        {
            return;
        }

        // UnreachableException marks a value that cannot occur; it is a programmer error, not a failure to announce or
        // to report through a bool.
        List<string> exceptionNames = DirectThrows(body)
            .Select(thrown => ThrownTypeName(context, thrown))
            .Distinct()
            .Where(name => name != "UnreachableException")
            .ToList();
        if (exceptionNames.Count == 0)
        {
            return;
        }
        string memberName = NameOf(member);
        Location location = IdentifierLocation(member);

        if (ReturnsBool(context, member))
        {
            context.ReportDiagnostic(Diagnostic.Create(BoolMethodThrows, location, memberName));
            return;
        }
        string header = HeaderComments(member);
        foreach (string exceptionName in exceptionNames)
        {
            if (!NamesWord(header, exceptionName))
            {
                context.ReportDiagnostic(Diagnostic.Create(UndeclaredThrow, location, memberName, exceptionName));
            }
        }
    }

    // A whole word only: a header naming InvalidDataException does not announce a plain Exception.
    private static bool NamesWord(string header, string word)
    {
        return Regex.IsMatch(header, @"\b" + Regex.Escape(word) + @"\b");
    }

    // Properties and indexers with accessor lists are checked per accessor; here only their expression bodies count.
    private static SyntaxNode? BodyOf(SyntaxNode member)
    {
        return member switch
        {
            BaseMethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody,
            AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody,
            BasePropertyDeclarationSyntax property => ExpressionBodyOf(property),
            _ => null
        };
    }

    private static SyntaxNode? ExpressionBodyOf(BasePropertyDeclarationSyntax property)
    {
        return property switch
        {
            PropertyDeclarationSyntax plain => plain.ExpressionBody,
            IndexerDeclarationSyntax indexer => indexer.ExpressionBody,
            _ => null
        };
    }

    // A local function runs as part of its member, so its throws are the member's. A lambda runs whenever its receiver
    // decides, so its throws belong to that receiver.
    private static IEnumerable<SyntaxNode> DirectThrows(SyntaxNode body)
    {
        return body.DescendantNodes(node => node is not AnonymousFunctionExpressionSyntax)
            .Where(node => node is ThrowStatementSyntax or ThrowExpressionSyntax);
    }

    private static bool ReturnsBool(SyntaxNodeAnalysisContext context, SyntaxNode member)
    {
        TypeSyntax? returnType = member switch
        {
            MethodDeclarationSyntax method => method.ReturnType,
            OperatorDeclarationSyntax operatorDeclaration => operatorDeclaration.ReturnType,
            BasePropertyDeclarationSyntax property => property.Type,
            AccessorDeclarationSyntax { Parent.Parent: BasePropertyDeclarationSyntax property }
                when member.IsKind(SyntaxKind.GetAccessorDeclaration) => property.Type,
            _ => null
        };
        if (returnType == null)
        {
            return false;
        }

        ITypeSymbol? type = context.SemanticModel.GetTypeInfo(returnType, context.CancellationToken).Type;
        return type?.SpecialType == SpecialType.System_Boolean;
    }

    // A rethrow (throw;) rethrows what its catch clause caught.
    private static string ThrownTypeName(SyntaxNodeAnalysisContext context, SyntaxNode thrown)
    {
        ExpressionSyntax? expression = thrown switch
        {
            ThrowStatementSyntax statement => statement.Expression,
            ThrowExpressionSyntax throwExpression => throwExpression.Expression,
            _ => null
        };
        if (expression == null)
        {
            CatchClauseSyntax? catchClause = thrown.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault();
            TypeSyntax? caughtType = catchClause?.Declaration?.Type;
            return caughtType == null ? "Exception" : TypeName(context, caughtType);
        }

        ITypeSymbol? type = context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type;
        return type?.Name ?? "Exception";
    }

    private static string TypeName(SyntaxNodeAnalysisContext context, TypeSyntax syntax)
    {
        ITypeSymbol? type = context.SemanticModel.GetTypeInfo(syntax, context.CancellationToken).Type;
        return type?.Name ?? syntax.ToString();
    }

    // The header is every comment above the declaration and between its attributes and its name; for an accessor it
    // also includes the header of the property it belongs to.
    private static string HeaderComments(SyntaxNode member)
    {
        IEnumerable<SyntaxTrivia> trivia = HeaderTokens(member).SelectMany(token => token.LeadingTrivia);
        if (member is AccessorDeclarationSyntax { Parent.Parent: { } property })
        {
            trivia = trivia.Concat(HeaderTokens(property).SelectMany(token => token.LeadingTrivia));
        }

        return string.Join("\n", trivia.Where(IsComment).Select(comment => comment.ToString()));
    }

    private static IEnumerable<SyntaxToken> HeaderTokens(SyntaxNode member)
    {
        SyntaxNode? body = BodyOf(member) ?? (member as BasePropertyDeclarationSyntax)?.AccessorList;
        return member.DescendantTokens().TakeWhile(token => body == null || token.SpanStart < body.SpanStart);
    }

    private static bool IsComment(SyntaxTrivia trivia)
    {
        return trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia);
    }

    private static string NameOf(SyntaxNode member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method.Identifier.Text,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
            PropertyDeclarationSyntax property => property.Identifier.Text,
            AccessorDeclarationSyntax { Parent.Parent: PropertyDeclarationSyntax property } => property.Identifier.Text,
            _ => member.Kind().ToString()
        };
    }

    private static Location IdentifierLocation(SyntaxNode member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method.Identifier.GetLocation(),
            ConstructorDeclarationSyntax constructor => constructor.Identifier.GetLocation(),
            PropertyDeclarationSyntax property => property.Identifier.GetLocation(),
            AccessorDeclarationSyntax accessor => accessor.Keyword.GetLocation(),
            _ => member.GetFirstToken().GetLocation()
        };
    }
}
