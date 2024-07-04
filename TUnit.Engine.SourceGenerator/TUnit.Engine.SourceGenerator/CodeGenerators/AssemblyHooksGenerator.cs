﻿using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Engine.SourceGenerator.Enums;
using TUnit.Engine.SourceGenerator.Models;

namespace TUnit.Engine.SourceGenerator.CodeGenerators;

[Generator]
internal class AssemblyHooksGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var setUpMethods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                WellKnownFullyQualifiedClassNames.AssemblySetUpAttribute.WithoutGlobalPrefix,
                predicate: static (s, _) => IsSyntaxTargetForGeneration(s),
                transform: static (ctx, _) => GetSemanticTargetForGeneration(ctx))
            .Where(static m => m is not null);
        
        var cleanUpMethods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                WellKnownFullyQualifiedClassNames.AssemblyCleanUpAttribute.WithoutGlobalPrefix,
                predicate: static (s, _) => IsSyntaxTargetForGeneration(s),
                transform: static (ctx, _) => GetSemanticTargetForGeneration(ctx))
            .Where(static m => m is not null);
        
        context.RegisterSourceOutput(setUpMethods,
            (productionContext, model) => Execute(productionContext, model, HookType.SetUp));
        context.RegisterSourceOutput(cleanUpMethods,
            (productionContext, model) => Execute(productionContext, model, HookType.CleanUp));
    }

    static bool IsSyntaxTargetForGeneration(SyntaxNode node)
    {
        return node is MethodDeclarationSyntax;
    }

    static AssemblyHooksDataModel? GetSemanticTargetForGeneration(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IMethodSymbol methodSymbol)
        {
            return null;
        }

        if (!methodSymbol.IsStatic)
        {
            return null;
        }

        return new AssemblyHooksDataModel
        {
            MethodName = methodSymbol.Name,
            FullyQualifiedTypeName = methodSymbol.ContainingType.ToDisplayString(DisplayFormats.FullyQualifiedGenericWithGlobalPrefix),
            MinimalTypeName = methodSymbol.ContainingType.Name,
            HasParameters = !methodSymbol.Parameters.IsDefaultOrEmpty
        };
    }

    private void Execute(SourceProductionContext context, AssemblyHooksDataModel? model, HookType hookType)
    {
        if (model is null)
        {
            return;
        }
        
        var className = $"AssemblyHooks_{model.MinimalTypeName}_{Guid.NewGuid():N}";

        using var sourceBuilder = new SourceCodeWriter();
                
        sourceBuilder.WriteLine("// <auto-generated/>");
        sourceBuilder.WriteLine("using System.Linq;");
        sourceBuilder.WriteLine("using System.Reflection;");
        sourceBuilder.WriteLine("using System.Runtime.CompilerServices;");
        sourceBuilder.WriteLine();
        sourceBuilder.WriteLine("namespace TUnit.Engine;");
        sourceBuilder.WriteLine();
        sourceBuilder.WriteLine("[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]");
        sourceBuilder.WriteLine($"file class {className}");
        sourceBuilder.WriteLine("{");
        sourceBuilder.WriteLine("[ModuleInitializer]");
        sourceBuilder.WriteLine("public static void Initialise()");
        sourceBuilder.WriteLine("{");

        if (hookType == HookType.SetUp)
        {
            sourceBuilder.WriteLine(
                $"global::TUnit.Engine.Hooks.AssemblyHookOrchestrators.RegisterSetUp(() => global::TUnit.Core.Helpers.RunHelpers.RunAsync(() => {model.FullyQualifiedTypeName}.{model.MethodName}({GenerateContextObject(model)})));");
        }
        else if (hookType == HookType.CleanUp)
        {
            sourceBuilder.WriteLine(
                $"global::TUnit.Engine.Hooks.AssemblyHookOrchestrators.RegisterCleanUp(() => global::TUnit.Core.Helpers.RunHelpers.RunAsync(() => {model.FullyQualifiedTypeName}.{model.MethodName}({GenerateContextObject(model)})));");
        }

        sourceBuilder.WriteLine("}");
        sourceBuilder.WriteLine("}");

        context.AddSource($"{className}.Generated.cs", sourceBuilder.ToString());
    }
    
    private string GenerateContextObject(AssemblyHooksDataModel model)
    {
        if (!model.HasParameters)
        {
            return string.Empty;
        }

        return $"TUnit.Engine.Hooks.ClassHookOrchestrator.GetAssemblyHookContext(typeof({model.FullyQualifiedTypeName}))";
    }
}