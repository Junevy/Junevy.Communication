using System.Reflection;
using System.Reflection.Emit;

namespace Junevy.Communication.DebugAssistant.Tests.Architecture;

/// <summary>
/// 收集一个类型引用到的程序集：基类、接口、字段、属性、事件、特性、成员签名，以及方法体里的 IL 引用。
/// 用于 U2 分层规则（计划第 3 节）：视图模型与服务层不能引用视图相关的程序集。
/// </summary>
internal static class AssemblyReferenceScanner
{
    private const BindingFlags AllMembers = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
        | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<ushort, OpCode> OpCodes = BuildOpCodeTable();

    /// <summary>类型（含嵌套类型与编译器生成的类型）引用到的程序集名称。</summary>
    public static HashSet<string> Of(Type type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(type, names);
        return names;
    }

    private static void Collect(Type type, HashSet<string> names)
    {
        AddType(type, names);
        AddType(type.BaseType, names);
        foreach (Type @interface in type.GetInterfaces())
        {
            AddType(@interface, names);
        }

        foreach (FieldInfo field in type.GetFields(AllMembers))
        {
            AddType(field.FieldType, names);
        }

        foreach (PropertyInfo property in type.GetProperties(AllMembers))
        {
            AddType(property.PropertyType, names);
        }

        foreach (EventInfo evt in type.GetEvents(AllMembers))
        {
            AddType(evt.EventHandlerType, names);
        }

        foreach (CustomAttributeData attribute in type.CustomAttributes)
        {
            AddType(attribute.AttributeType, names);
        }

        foreach (MethodBase method in type.GetMethods(AllMembers).Cast<MethodBase>().Concat(type.GetConstructors(AllMembers)))
        {
            AddSignature(method, names);
            ScanBody(method, names);
        }

        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            Collect(nested, names);
        }
    }

    private static void AddSignature(MethodBase method, HashSet<string> names)
    {
        if (method is MethodInfo info)
        {
            AddType(info.ReturnType, names);
        }

        foreach (ParameterInfo parameter in method.GetParameters())
        {
            AddType(parameter.ParameterType, names);
        }
    }

    private static void AddType(Type? type, HashSet<string> names)
    {
        if (type is null)
        {
            return;
        }

        if (type.Assembly.GetName().Name is { } name)
        {
            names.Add(name);
        }

        if (type.HasElementType)
        {
            AddType(type.GetElementType(), names);
        }

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                AddType(argument, names);
            }
        }
    }

    /// <summary>逐条解码 IL，解析每个令牌操作数所指的成员或类型。</summary>
    private static void ScanBody(MethodBase method, HashSet<string> names)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return;
        }

        int index = 0;
        while (index < il.Length)
        {
            ushort code = il[index++];
            if (code == 0xFE)
            {
                code = (ushort)(0xFE00 | il[index++]);
            }

            OpCode opCode = OpCodes[code];
            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    index += 1;
                    break;
                case OperandType.InlineVar:
                    index += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    index += 8;
                    break;
                case OperandType.InlineSwitch:
                    int count = BitConverter.ToInt32(il, index);
                    index += 4 + (4 * count);
                    break;
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                case OperandType.InlineSig:
                    ResolveToken(method, BitConverter.ToInt32(il, index), names);
                    index += 4;
                    break;
                default:
                    index += 4;
                    break;
            }
        }
    }

    private static void ResolveToken(MethodBase method, int token, HashSet<string> names)
    {
        try
        {
            switch (method.Module.ResolveMember(token))
            {
                case MethodBase resolvedMethod:
                    AddType(resolvedMethod.DeclaringType, names);
                    AddSignature(resolvedMethod, names);
                    break;
                case FieldInfo field:
                    AddType(field.DeclaringType, names);
                    AddType(field.FieldType, names);
                    break;
                case Type type:
                    AddType(type, names);
                    break;
            }
        }
        catch (ArgumentException)
        {
            AddType(TryResolveType(method, token), names);
        }
    }

    private static Type? TryResolveType(MethodBase method, int token)
    {
        try
        {
            return method.Module.ResolveType(token);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static Dictionary<ushort, OpCode> BuildOpCodeTable()
    {
        var table = new Dictionary<ushort, OpCode>();
        foreach (FieldInfo field in typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var opCode = (OpCode)field.GetValue(null)!;
            table[(ushort)opCode.Value] = opCode;
        }

        return table;
    }
}
