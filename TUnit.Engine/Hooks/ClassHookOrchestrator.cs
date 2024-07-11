﻿using System.Collections.Concurrent;
using System.Reflection;
using TUnit.Core;
using TUnit.Core.Models;

namespace TUnit.Engine.Hooks;

#if !DEBUG
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
#endif
public static class ClassHookOrchestrator
{
    private static readonly ConcurrentDictionary<Type, List<Lazy<Task>>> SetUps = new();
    private static readonly ConcurrentDictionary<Type, List<Func<Task>>> CleanUps = new();
    
    private static readonly ConcurrentDictionary<Assembly, AssemblyHookContext> AssemblyHookContexts = new();
    private static readonly ConcurrentDictionary<Type, ClassHookContext> ClassHookContexts = new();

    private static readonly ConcurrentDictionary<Type, int> InstanceTrackers = new();
    
    public static void RegisterInstance(TestContext testContext)
    {
        var classType = testContext.TestDetails.ClassType;

        AssemblyHookOrchestrator.Increment(classType.Assembly);
        
        foreach (var type in GetTypesIncludingBase(classType))
        {
            var count = InstanceTrackers.GetOrAdd(type, _ => 0);
            InstanceTrackers[type] = count + 1;
        }

        var testInformation = testContext.TestDetails;
        
        foreach (var argument in testInformation.InternalTestClassArguments)
        {
            if (argument.InjectedDataType == InjectedDataType.SharedByKey)
            {
                TestDataContainer.IncrementKeyUsage(argument.StringKey!, argument.Type);
            }
            
            if (argument.InjectedDataType == InjectedDataType.SharedGlobally)
            {
                TestDataContainer.IncrementGlobalUsage(argument.Type);
            }
        }
        
        foreach (var argument in testInformation.InternalTestMethodArguments)
        {
            if (argument.InjectedDataType == InjectedDataType.SharedByKey)
            {
                TestDataContainer.IncrementKeyUsage(argument.StringKey!, argument.Type);
            }
            
            if (argument.InjectedDataType == InjectedDataType.SharedGlobally)
            {
                TestDataContainer.IncrementGlobalUsage(argument.Type);
            }
        }
    }
    
    public static void RegisterSetUp(Type type, StaticMethod staticMethod)
    {
        var taskFunctions = SetUps.GetOrAdd(type, _ => []);

        taskFunctions.Add(Convert(staticMethod));
    }
    
    public static void RegisterCleanUp(Type type, StaticMethod staticMethod)
    {
        var taskFunctions = CleanUps.GetOrAdd(type, _ => []);

        taskFunctions.Add(() =>
        {
            var cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(EngineCancellationToken.Token);
            var timeout = staticMethod.Timeout;

            if (timeout != null)
            {
                cancellationToken.CancelAfter(timeout.Value);
            }

            return staticMethod.Body(cancellationToken.Token);
        });
    }
    
    public static void RegisterTestContext(Type type, TestContext testContext)
    {
        var classHookContext = ClassHookContexts.GetOrAdd(type, _ => new ClassHookContext
            {
                ClassType = type
            });

        classHookContext.Tests.Add(testContext);

        var assemblyHookContext = AssemblyHookContexts.GetOrAdd(type.Assembly, _ => new AssemblyHookContext
        {
            Assembly = type.Assembly
        });

        assemblyHookContext.TestClasses.Add(classHookContext);
    }
    
    public static ClassHookContext GetClassHookContext(Type type)
    {
        lock (type)
        {
            return ClassHookContexts.GetOrAdd(type, _ => new ClassHookContext
            {
                ClassType = type
            });
        }
    }

    public static AssemblyHookContext GetAssemblyHookContext(Type type)
    {
        lock (type)
        {
            return AssemblyHookContexts.GetOrAdd(type.Assembly, _ => new AssemblyHookContext
            {
                Assembly = type.Assembly
            });
        }
    }

    public static IEnumerable<AssemblyHookContext> GetAllAssemblyHookContexts() => AssemblyHookContexts.Values;
    
    public static async Task ExecuteSetups(Type testClassType)
    {
        await AssemblyHookOrchestrator.ExecuteSetups(testClassType.Assembly);

        // Reverse so base types are first - We'll run those ones first
        var typesIncludingBase = GetTypesIncludingBase(testClassType)
            .Reverse();

        foreach (var type in typesIncludingBase)
        {
            if (!SetUps.TryGetValue(type, out var setUpsForType))
            {
                return;
            }

            foreach (var setUp in setUpsForType)
            {
                // As these are lazy we should always get the same Task
                // So we await the same Task to ensure it's finished first
                // and also gives the benefit of rethrowing the same exception if it failed
                await setUp.Value;
            }
        }
    }
    
    public static async Task ExecuteCleanUpsIfLastInstance(Type testClassType,
        List<Exception> cleanUpExceptions)
    {
        var typesIncludingBase = GetTypesIncludingBase(testClassType);

        foreach (var type in typesIncludingBase)
        {
            var instanceCount = DecreaseCount(type);

            if (instanceCount != 0)
            {
                // Only run one time clean down's when no instances are left!
                continue;
            }

            await TestDataContainer.OnLastInstance(testClassType);
            
            if (!CleanUps.TryGetValue(type, out var cleanUpsForType))
            {
                return;
            }

            foreach (var cleanUp in cleanUpsForType)
            {
                await RunHelpers.RunSafelyAsync(cleanUp, cleanUpExceptions);
            }
        }
    }

    public static IEnumerable<TestContext> GetTestsForType(Type type)
    {
        var context = ClassHookContexts.GetOrAdd(type, new ClassHookContext
        {
            ClassType = type
        });

        return context.Tests;
    }

    private static IEnumerable<Type> GetTypesIncludingBase(Type testClassType)
    {
        var type = testClassType;
        
        while (type != null && type != typeof(object))
        {
            yield return type;
            type = type.BaseType;
        }
    }
    
    private static int DecreaseCount(Type type)
    {
        lock (type)
        {
            var count = InstanceTrackers[type];
            return InstanceTrackers[type] = count - 1;
        }
    }
    
    private static Lazy<Task> Convert(StaticMethod staticMethod)
    {
        return new Lazy<Task>(() =>
        {
            var cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(EngineCancellationToken.Token);
            var timeout = staticMethod.Timeout;

            if (timeout != null)
            {
                cancellationToken.CancelAfter(timeout.Value);
            }
            
            return staticMethod.Body(cancellationToken.Token);
        });
    }
}