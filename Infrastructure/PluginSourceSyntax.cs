// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Reads a plugin source file the way the game server's compiler will, as far as reading alone can tell: as C# 7.3, ignoring everything that
/// needs the game's own libraries. Nothing is run.
/// </summary>
/// <remarks>
/// <para>
/// What it catches is what actually breaks a plugin in practice: a typo that leaves the file unparseable, and modern syntax (a switch
/// expression, <c>using var</c>, <c>??=</c>, a nullable annotation, a range) that the game server's older compiler refuses. Either one makes
/// Carbon unload the plugin and leave it dead, which is why the Updater exists - and why it is better to refuse the file here, at upload,
/// than to find out on every server that took it.
/// </para>
/// <para>
/// How: the parser finds the syntax errors; the language-version errors ("Feature 'x' is not available in C# 7.3") come from the compiler
/// proper, so the file is compiled against nothing but the .NET core library (enough for it to know what a string is, which some of the
/// checks need) and only those errors are kept. The flood of "the game's type is not defined" complaints that produces is exactly the part
/// this cannot judge, and is thrown away.
/// </para>
/// <para>
/// What it cannot catch: a call to something that does not exist in the game's own code (a type Rust renamed this month), because that needs
/// the game's libraries, which are not here. That is checked against the real game files by the plugin's own tests, and a version that still
/// fails on a server is put back by the Updater's rollback.
/// </para>
/// </remarks>
public static class PluginSourceSyntax
{
    /// <summary>How many problems are worth listing. The first few are what the author needs; the rest are usually the same mistake again.</summary>
    public const int MaxListed = 5;

    // The core library only: the language needs it to know object, string and int, and no other reference is meaningful here.
    private static readonly MetadataReference[] CoreLibrary = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

    /// <summary>
    /// The problems that would stop <paramref name="source"/> compiling as C# 7.3, each as "line N: what is wrong", at most
    /// <see cref="MaxListed"/>, in the order they occur. Empty when it reads cleanly. <paramref name="total"/> is how many there are altogether.
    /// </summary>
    public static IReadOnlyList<string> Problems(string source, out int total)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.CSharp7_3, DocumentationMode.None, SourceCodeKind.Regular));

        var compilation = CSharpCompilation.Create(
            "PluginSyntaxCheck", [tree], CoreLibrary, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var problems = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Concat(compilation.GetDiagnostics().Where(IsLanguageVersionError))
            .Distinct()
            .OrderBy(d => d.Location.SourceSpan.Start)
            .ToList();
        total = problems.Count;

        return problems
            .Take(MaxListed)
            .Select(d => $"line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage(CultureInfo.InvariantCulture)}")
            .ToList();
    }

    // "Feature 'x' is not available in C# 7.3. Please use language version 8.0 or greater." (CS8370, and older ids for older features).
    private static bool IsLanguageVersionError(Diagnostic d) =>
        d.Severity == DiagnosticSeverity.Error
        && (d.Id == "CS8370" || d.GetMessage(CultureInfo.InvariantCulture).Contains("is not available in C# ", StringComparison.Ordinal));
}
