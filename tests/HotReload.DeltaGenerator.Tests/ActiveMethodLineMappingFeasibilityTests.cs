// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace HotReload.DeltaGenerator.Tests;

public sealed class ActiveMethodLineMappingFeasibilityTests
{
    private const string BaselinePath = "/workspace/Calculator.cs";
    private const string MovedPath = "/workspace/MovedCalculator.cs";
    private const string ActiveText = "var result = input * 2;";
    private readonly ITestOutputHelper output;

    public ActiveMethodLineMappingFeasibilityTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    public static TheoryData<Scenario> Scenarios
    {
        get
        {
            var data = new TheoryData<Scenario>();
            data.Add(new Scenario("blank-line-before-active", BaselineSource.Replace(ActiveText, "\n        " + ActiveText, StringComparison.Ordinal), BaselinePath, MappingOutcome.ExactSyntaxAnchor));
            data.Add(new Scenario("comment-before-active", BaselineSource.Replace(ActiveText, "// inserted comment\n        " + ActiveText, StringComparison.Ordinal), BaselinePath, MappingOutcome.ExactSyntaxAnchor));
            data.Add(new Scenario("executable-before-active", BaselineSource.Replace(ActiveText, "var extra = seed - seed;\n        " + ActiveText, StringComparison.Ordinal), BaselinePath, MappingOutcome.ExactSyntaxAnchor));
            data.Add(new Scenario("executable-after-active", BaselineSource.Replace("return result;", "var observed = result;\n        return observed;", StringComparison.Ordinal), BaselinePath, MappingOutcome.ExactSyntaxAnchor));
            data.Add(new Scenario("delete-line-before-active", BaselineSource.Replace("        var seed = input + 1;\n", string.Empty, StringComparison.Ordinal), BaselinePath, MappingOutcome.ExactSyntaxAnchor));
            data.Add(new Scenario("expression-change-and-line-move", BaselineSource.Replace(ActiveText, "\n        var result = input * 3;", StringComparison.Ordinal), BaselinePath, MappingOutcome.ExactOrdinalStructure));
            data.Add(new Scenario("multiline-active-expression", BaselineSource.Replace(ActiveText, "var result =\n            input * 3;", StringComparison.Ordinal), BaselinePath, MappingOutcome.RestartRequired));
            data.Add(new Scenario("line-directive", BaselineSource.Replace(ActiveText, "#line 200 \"mapped.cs\"\n        " + ActiveText + "\n#line default", StringComparison.Ordinal), BaselinePath, MappingOutcome.RestartRequired));
            data.Add(new Scenario("document-move", BaselineSource, MovedPath, MappingOutcome.RestartRequired));
            data.Add(new Scenario("method-move", MethodMovedSource, BaselinePath, MappingOutcome.RestartRequired));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Corpus_ClassifiesOnlyExactUpdatedMethodMappings(Scenario scenario)
    {
        var evidence = Analyze(BaselineSource, BaselinePath, scenario.UpdatedSource, scenario.UpdatedPath);

        output.WriteLine(JsonSerializer.Serialize(new { scenario.Name, Evidence = evidence }));
        Assert.Equal(scenario.Expected, evidence.Outcome);
        Assert.Equal(0x06000001, evidence.OldMethodToken);
        Assert.NotEqual(0, evidence.NewMethodToken);

        if (scenario.Expected is MappingOutcome.ExactSyntaxAnchor or MappingOutcome.ExactOrdinalStructure)
        {
            Assert.NotNull(evidence.OldActivePoint);
            Assert.NotNull(evidence.NewActivePoint);
            Assert.Equal("LocalDeclarationStatement", evidence.OldActivePoint.SyntaxKind);
            Assert.Equal("LocalDeclarationStatement", evidence.NewActivePoint.SyntaxKind);
        }
    }

    private static MappingEvidence Analyze(
        string oldSource,
        string oldPath,
        string newSource,
        string newPath)
    {
        var oldImage = Compile(oldSource, oldPath);
        var newImage = Compile(newSource, newPath);
        var oldMethod = ReadMethod(oldImage, oldSource, oldPath, "Run");
        var newMethod = ReadMethod(newImage, newSource, newPath, "Run");
        var oldActiveIndex = FindIndex(oldMethod.Points, point => point.NormalizedSyntax == Normalize(ActiveText));
        var oldActive = oldActiveIndex >= 0 ? oldMethod.Points[oldActiveIndex] : null;

        MappingOutcome outcome;
        SequencePointEvidence? newActive = null;
        string reason;

        if (ContainsLineDirective(oldSource) || ContainsLineDirective(newSource))
        {
            outcome = MappingOutcome.RestartRequired;
            reason = "lineDirective";
        }
        else if (!StringComparer.Ordinal.Equals(oldPath, newPath))
        {
            outcome = MappingOutcome.RestartRequired;
            reason = "documentChanged";
        }
        else if (oldActive is null)
        {
            outcome = MappingOutcome.RestartRequired;
            reason = "oldActiveStatementMissing";
        }
        else
        {
            var syntaxMatches = newMethod.Points
                .Where(point => point.SyntaxKind == oldActive.SyntaxKind &&
                    point.NormalizedSyntax == oldActive.NormalizedSyntax)
                .ToArray();
            if (syntaxMatches.Length == 1)
            {
                outcome = MappingOutcome.ExactSyntaxAnchor;
                newActive = syntaxMatches[0];
                reason = "uniqueSyntaxAnchor";
            }
            else if (syntaxMatches.Length > 1)
            {
                outcome = MappingOutcome.RestartRequired;
                reason = "ambiguousSyntaxAnchor";
            }
            else if (oldActiveIndex < newMethod.Points.Length &&
                HasEqualVisibleStructure(oldMethod.Points, newMethod.Points))
            {
                outcome = MappingOutcome.ExactOrdinalStructure;
                newActive = newMethod.Points[oldActiveIndex];
                reason = "equalVisibleSequencePointStructure";
            }
            else
            {
                outcome = MappingOutcome.RestartRequired;
                reason = "updatedSequencePointStructureChanged";
            }
        }

        return new(
            outcome,
            reason,
            oldMethod.MethodToken,
            newMethod.MethodToken,
            oldMethod.Points.Length,
            newMethod.Points.Length,
            oldActive,
            newActive);
    }

    private static bool HasEqualVisibleStructure(
        ImmutableArray<SequencePointEvidence> oldPoints,
        ImmutableArray<SequencePointEvidence> newPoints)
    {
        if (oldPoints.Length != newPoints.Length)
        {
            return false;
        }

        for (var index = 0; index < oldPoints.Length; index++)
        {
            var oldPoint = oldPoints[index];
            var newPoint = newPoints[index];
            if (oldPoint.SyntaxKind != newPoint.SyntaxKind ||
                oldPoint.StartColumn != newPoint.StartColumn ||
                oldPoint.EndLine - oldPoint.StartLine != newPoint.EndLine - newPoint.StartLine)
            {
                return false;
            }
        }

        return true;
    }

    private static int FindIndex<T>(ImmutableArray<T> values, Func<T, bool> predicate)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (predicate(values[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static CompilationImage Compile(string source, string path)
    {
        var tree = CSharpSyntaxTree.ParseText(
            SourceText.From(source, Encoding.UTF8),
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
            path);
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? throw new InvalidOperationException("The trusted platform assembly list is unavailable.");
        var compilation = CSharpCompilation.Create(
            "ActiveMethodLineMappingFixture",
            [tree],
            trustedAssemblies.Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                deterministic: true));
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(
            pe,
            pdb,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return new(pe.ToArray(), pdb.ToArray());
    }

    private static MethodEvidence ReadMethod(
        CompilationImage image,
        string source,
        string path,
        string methodName)
    {
        using var peStream = new MemoryStream(image.Pe);
        using var peReader = new PEReader(peStream);
        var metadata = peReader.GetMetadataReader();
        var methodHandle = metadata.TypeDefinitions
            .SelectMany(handle => metadata.GetTypeDefinition(handle).GetMethods())
            .Single(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == methodName);
        var methodToken = MetadataTokens.GetToken(methodHandle);

        using var pdbProvider = MetadataReaderProvider.FromPortablePdbImage(image.Pdb.ToImmutableArray());
        var pdb = pdbProvider.GetMetadataReader();
        var information = pdb.GetMethodDebugInformation(
            MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(methodHandle)));
        var tree = CSharpSyntaxTree.ParseText(source, path: path);
        var text = tree.GetText();
        var root = tree.GetRoot();
        var points = information.GetSequencePoints()
            .Where(static point => !point.IsHidden)
            .Select(point => CreatePoint(point, text, root))
            .ToImmutableArray();
        return new(methodToken, points);
    }

    private static SequencePointEvidence CreatePoint(
        SequencePoint point,
        SourceText text,
        SyntaxNode root)
    {
        var lineIndex = Math.Clamp(point.StartLine - 1, 0, text.Lines.Count - 1);
        var line = text.Lines[lineIndex];
        var position = Math.Clamp(line.Start + Math.Max(point.StartColumn - 1, 0), line.Start, line.End);
        var token = root.FindToken(position);
        var syntax = token.Parent?.AncestorsAndSelf().FirstOrDefault(static node =>
            node is StatementSyntax or ArrowExpressionClauseSyntax or MethodDeclarationSyntax);
        return new(
            point.Offset,
            point.StartLine,
            point.StartColumn,
            point.EndLine,
            point.EndColumn,
            syntax?.Kind().ToString() ?? "Unknown",
            Normalize(syntax?.WithoutTrivia().ToFullString() ?? string.Empty));
    }

    private static bool ContainsLineDirective(string source) =>
        source.Contains("#line", StringComparison.Ordinal);

    private static string Normalize(string value) =>
        string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));

    public sealed record Scenario(
        string Name,
        string UpdatedSource,
        string UpdatedPath,
        MappingOutcome Expected)
    {
        public override string ToString() => Name;
    }

    public enum MappingOutcome
    {
        ExactSyntaxAnchor,
        ExactOrdinalStructure,
        RestartRequired
    }

    private sealed record CompilationImage(byte[] Pe, byte[] Pdb);

    private sealed record MethodEvidence(
        int MethodToken,
        ImmutableArray<SequencePointEvidence> Points);

    private sealed record MappingEvidence(
        MappingOutcome Outcome,
        string Reason,
        int OldMethodToken,
        int NewMethodToken,
        int OldVisibleSequencePointCount,
        int NewVisibleSequencePointCount,
        SequencePointEvidence? OldActivePoint,
        SequencePointEvidence? NewActivePoint);

    private sealed record SequencePointEvidence(
        int IlOffset,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn,
        string SyntaxKind,
        string NormalizedSyntax);

    private const string BaselineSource = """
        namespace DeltaFixture;

        public static class Calculator
        {
            public static int Run(int input)
            {
                var seed = input + 1;
                var result = input * 2;
                return result;
            }
        }
        """;

    private const string MethodMovedSource = """
        namespace DeltaFixture;

        public static class Calculator
        {
            public static int Run(int input)
            {
                return Helper(input);
            }

            private static int Helper(int input)
            {
                var seed = input + 1;
                var result = input * 2;
                return result;
            }
        }
        """;
}
