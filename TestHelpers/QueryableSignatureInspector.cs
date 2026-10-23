// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Reflection helper of the queryable-surface guards: tells whether a type mentions IQueryable anywhere in its shape
/// (IQueryable/IOrderedQueryable or any type implementing IQueryable, as element of an array or by-ref, as generic
/// argument at any depth - Task, tuple, Func, Expression) and lists the members of a type whose signature does.
/// </summary>

using System.Reflection;
using System.Runtime.CompilerServices;

namespace Klacks.UnitTest.TestHelpers;

public static class QueryableSignatureInspector
{
    private const char GenericArityMarker = '`';

    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <param name="type">Any type of a signature</param>
    /// <returns>True when the type is or contains a queryable</returns>
    public static bool CarriesQueryable(Type type)
    {
        if (type.IsByRef || type.IsArray || type.IsPointer)
        {
            return CarriesQueryable(type.GetElementType()!);
        }

        if (type.IsGenericParameter)
        {
            return false;
        }

        if (typeof(IQueryable).IsAssignableFrom(type)
            || (type.IsGenericType && type.GetGenericTypeDefinition().GetInterfaces().Contains(typeof(IQueryable)))
            || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>)))
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(CarriesQueryable);
    }

    /// <summary>
    /// Members declared by the type itself that other code can call or override (public, protected, protected
    /// internal) and whose return, parameter, property, field or event type carries a queryable.
    /// </summary>
    /// <param name="type">Declaring type</param>
    /// <returns>"SimpleTypeName.MemberName" per offending member, distinct</returns>
    public static List<string> ExposedQueryableMembers(Type type)
    {
        var members = new List<string>();
        var owner = SimpleName(type);

        foreach (var method in type.GetMethods(DeclaredMembers).Where(method => !method.IsSpecialName && IsExposed(method)))
        {
            if (CarriesQueryable(method.ReturnType) || method.GetParameters().Any(parameter => CarriesQueryable(parameter.ParameterType)))
            {
                members.Add($"{owner}.{method.Name}");
            }
        }

        foreach (var constructor in type.GetConstructors(DeclaredMembers).Where(IsExposed))
        {
            if (constructor.GetParameters().Any(parameter => CarriesQueryable(parameter.ParameterType)))
            {
                members.Add($"{owner}..ctor");
            }
        }

        foreach (var property in type.GetProperties(DeclaredMembers))
        {
            var accessors = property.GetAccessors(nonPublic: true).Where(IsExposed).ToList();
            if (accessors.Count > 0
                && (CarriesQueryable(property.PropertyType)
                    || property.GetIndexParameters().Any(parameter => CarriesQueryable(parameter.ParameterType))))
            {
                members.Add($"{owner}.{property.Name}");
            }
        }

        foreach (var field in type.GetFields(DeclaredMembers).Where(field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly))
        {
            if (CarriesQueryable(field.FieldType))
            {
                members.Add($"{owner}.{field.Name}");
            }
        }

        foreach (var eventInfo in type.GetEvents(DeclaredMembers))
        {
            var adder = eventInfo.GetAddMethod(nonPublic: true);
            if (adder != null && IsExposed(adder) && CarriesQueryable(eventInfo.EventHandlerType!))
            {
                members.Add($"{owner}.{eventInfo.Name}");
            }
        }

        if (type.BaseType != null && CarriesQueryable(type.BaseType))
        {
            members.Add($"{owner} : {SimpleName(type.BaseType)}");
        }

        return members.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <param name="type">Type to classify</param>
    /// <returns>True for closures, async state machines, anonymous types and other compiler-generated types</returns>
    public static bool IsCompilerGenerated(Type type)
    {
        for (var current = type; current != null; current = current.DeclaringType)
        {
            if (current.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || current.Name.Contains('<'))
            {
                return true;
            }
        }

        return false;
    }

    /// <param name="type">Any type</param>
    /// <returns>The type name without the generic arity suffix</returns>
    public static string SimpleName(Type type)
    {
        var name = type.Name;
        var marker = name.IndexOf(GenericArityMarker);
        return marker < 0 ? name : name[..marker];
    }

    private static bool IsExposed(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;
}
