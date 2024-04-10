using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TUnit.Engine.SourceGenerator.Extensions;

namespace TUnit.Engine.SourceGenerator.CodeGenerators;

/// <summary>
/// A sample source generator that creates C# classes based on the text file (in this case, Domain Driven Design ubiquitous language registry).
/// When using a simple text file as a baseline, we can create a non-incremental source generator.
/// </summary>
[Generator]
public class TestsSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        if (!Debugger.IsAttached)
        {
            // Debugger.Launch();
        }

        var testMethods = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => IsSyntaxTargetForGeneration(s),
                transform: static (ctx, _) => GetSemanticTargetForGeneration(ctx))
            .Where(static m => m is not null)
            .Collect();
        
        context.RegisterSourceOutput(testMethods, Execute);
    }
    
    static bool IsSyntaxTargetForGeneration(SyntaxNode node)
    {
        return node is ClassDeclarationSyntax;
    }

    static IEnumerable<ClassMethod> GetSemanticTargetForGeneration(GeneratorSyntaxContext context)
    {
        if (context.Node is not ClassDeclarationSyntax)
        {
            yield break;
        }

        var symbol = context.SemanticModel.GetDeclaredSymbol(context.Node);

        if (symbol is not INamedTypeSymbol namedTypeSymbol)
        {
            yield break;
        }

        if (namedTypeSymbol.IsAbstract)
        {
            yield break;
        }

        var methods = namedTypeSymbol
            .GetMembersIncludingBase()
            .OfType<IMethodSymbol>();

        foreach (var methodSymbol in methods)
        {
            var attributes = methodSymbol.GetAttributes();

            if (!attributes.Any(x =>
                    x.AttributeClass?.BaseType?.ToDisplayString(DisplayFormats.FullyQualifiedGenericWithGlobalPrefix)
                    == WellKnownFullyQualifiedClassNames.BaseTestAttribute))
            {
                continue;
            }

            yield return new ClassMethod(namedTypeSymbol, methodSymbol);
        }
    }

    private static void Execute(SourceProductionContext context, ImmutableArray<IEnumerable<ClassMethod>> classMethods)
    {
        foreach (var classMethod in classMethods.SelectMany(x => x))
        {
            var classSource = ProcessTests(classMethod);
                
            if (string.IsNullOrEmpty(classSource))
            {
                continue;
            }

            var className = $"{classMethod.MethodSymbol.Name}_{Guid.NewGuid():N}";
            context.AddSource($"{className}.g.cs", SourceText.From(WrapInClass(className, classSource), Encoding.UTF8));
        }
    }

    private static string WrapInClass(string className, string methodCode)
    {
        return $$"""
               // <auto-generated/>
               using System.Linq;
               using System.Reflection;
               using System.Runtime.CompilerServices;

               namespace TUnit.Engine;

               file class {{className}}
               {
                   [ModuleInitializer]
                   public static void Initialise()
                   {
                        {{methodCode}}
                   }
               } 
               """;
    }

    private static string ProcessTests(ClassMethod classMethod)
    {
        var sourceBuilder = new StringBuilder();
        
        foreach (var testInvocationCode in GetTestInvocationCode(classMethod))
        {
            sourceBuilder.AppendLine(testInvocationCode);
        }
        
        return sourceBuilder.ToString();
    }

    private static IEnumerable<string> GetTestInvocationCode(ClassMethod classMethod)
    {
        var writeableTests = WriteableTestsRetriever.GetWriteableTests(classMethod);
        
        foreach (var writeableTest in writeableTests)
        {
            yield return GenericTestInvocationGenerator.GenerateTestInvocationCode(writeableTest);
        }
    }
}

public record ClassMethod(INamedTypeSymbol NamedTypeSymbol, IMethodSymbol MethodSymbol);