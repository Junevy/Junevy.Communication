using System.Reflection;

namespace Junevy.Communication.Architecture.Tests;

/// <summary>
/// 遍历一个程序集导出类型在程序集外可见的签名，返回其中出现的每一个类型（已展开数组、指针、引用与泛型实参）。
/// 只看程序集外可见的成员：<c>Sdk/</c> 命名空间里的内部类型使用 SDK 类型是允许的，公开类型的私有成员同样不算对外暴露。
/// </summary>
internal static class SdkPublicApiWalker
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>导出类型的公开 API 中出现过的全部类型，含自身、基类、接口与可见成员的签名。</summary>
    public static IEnumerable<Type> ExposedTypes(Assembly assembly) =>
        assembly.GetExportedTypes().SelectMany(ExposedTypesOf);

    private static IEnumerable<Type> ExposedTypesOf(Type type) =>
        Bases(type).Concat(VisibleMembers(type).SelectMany(MemberTypes));

    private static IEnumerable<Type> Bases(Type type)
    {
        if (type.BaseType is not null)
        {
            foreach (Type leaf in Expand(type.BaseType))
            {
                yield return leaf;
            }
        }

        foreach (Type contract in type.GetInterfaces())
        {
            foreach (Type leaf in Expand(contract))
            {
                yield return leaf;
            }
        }
    }

    private static IEnumerable<MemberInfo> VisibleMembers(Type type) =>
        type.GetMembers(Declared).Where(IsVisibleOutsideAssembly);

    private static bool IsVisibleOutsideAssembly(MemberInfo member) => member switch
    {
        Type nested => nested.IsNestedPublic || nested.IsNestedFamily || nested.IsNestedFamORAssem,
        FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
        MethodBase method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly,
        PropertyInfo property => Accessors(property).Any(IsVisibleOutsideAssembly),
        EventInfo raised => Accessors(raised).Any(IsVisibleOutsideAssembly),
        _ => false,
    };

    // GetExportedTypes 已包含嵌套的公开类型，因此嵌套类型本身只要展开，不必再遍历它的成员。
    private static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
    {
        Type nested => Expand(nested),
        FieldInfo field => Expand(field.FieldType),
        PropertyInfo property => Expand(property.PropertyType),
        EventInfo raised => Accessors(raised).SelectMany(SignatureTypes),
        MethodBase method => SignatureTypes(method),
        _ => Array.Empty<Type>(),
    };

    private static IEnumerable<Type> SignatureTypes(MethodBase method) =>
        method is MethodInfo call ? Expand(call.ReturnType).Concat(Parameters(call)) : Parameters(method);

    private static IEnumerable<Type> Parameters(MethodBase method) =>
        method.GetParameters()
            .SelectMany(parameter => Expand(parameter.ParameterType))
            .Concat(method.IsGenericMethodDefinition
                ? method.GetGenericArguments().SelectMany(Expand)
                : Array.Empty<Type>());

    private static IEnumerable<MethodBase> Accessors(PropertyInfo property)
    {
        if (property.GetMethod is not null)
        {
            yield return property.GetMethod;
        }

        if (property.SetMethod is not null)
        {
            yield return property.SetMethod;
        }
    }

    private static IEnumerable<MethodBase> Accessors(EventInfo raised)
    {
        if (raised.AddMethod is not null)
        {
            yield return raised.AddMethod;
        }

        if (raised.RemoveMethod is not null)
        {
            yield return raised.RemoveMethod;
        }

        if (raised.RaiseMethod is not null)
        {
            yield return raised.RaiseMethod;
        }
    }

    /// <summary>展开 byref、指针、数组与泛型实参，剩下真正归属某个程序集的类型。</summary>
    private static IEnumerable<Type> Expand(Type type)
    {
        Type? leaf = type.IsByRef || type.IsPointer || type.IsArray ? type.GetElementType() : type;
        if (leaf is null)
        {
            yield break;
        }

        yield return leaf;
        if (!leaf.IsGenericType)
        {
            yield break;
        }

        foreach (Type argument in leaf.GetGenericArguments())
        {
            foreach (Type nested in Expand(argument))
            {
                yield return nested;
            }
        }
    }
}