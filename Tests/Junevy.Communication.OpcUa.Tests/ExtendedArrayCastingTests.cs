using Junevy.Communication.OpcUa.Sdk;
using Opc.Ua;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// F19：.NET 7 起 C# 支持"扩展数组转换"——同宽度的数值元素数组之间可以互相硬转换，
/// 因此 <c>sbyte[]</c> 在 <c>is byte[]</c> 下为真，硬转换也成功（逐元素重解释）。
/// 值映射必须按精确类型分派，否则 <c>sbyte[]</c> 会被当成 <c>byte[]</c>（ByteString）。
/// </summary>
public sealed class ExtendedArrayCastingTests
{
    [Fact]
    public void SByteArray_PassesTheByteArrayTypePattern()
    {
        object value = new sbyte[] { -1, 2 };

        Assert.True(value is byte[]);
        Assert.False(value.GetType() == typeof(byte[]));
        Assert.Equal(typeof(byte), ((byte[])value)[0].GetType());
    }

    [Fact]
    public void Int16Array_DoesNotPassTheInt32ArrayTypePattern()
    {
        // 扩展数组转换只覆盖同宽度的数值类型：short[] 与 int[] 之间没有这种转换。
        object value = new short[] { 1, 2 };

        Assert.False(value is int[]);
    }

    [Fact]
    public void TheValueMapper_StillTellsSByteFromByte()
    {
        Assert.True(OpcUaSdkValues.TryToSdk(new sbyte[] { -1 }, out Variant sbytes));
        Assert.Equal(BuiltInType.SByte, sbytes.TypeInfo.BuiltInType);

        Assert.True(OpcUaSdkValues.TryToSdk(new byte[] { 1 }, out Variant bytes));
        Assert.Equal(BuiltInType.ByteString, bytes.TypeInfo.BuiltInType);
    }

    [Fact]
    public void TheValueMapper_StillTellsInt16FromInt32()
    {
        Assert.True(OpcUaSdkValues.TryToSdk(new short[] { 1 }, out Variant shorts));
        Assert.Equal(BuiltInType.Int16, shorts.TypeInfo.BuiltInType);

        Assert.True(OpcUaSdkValues.TryToSdk(new[] { 1 }, out Variant ints));
        Assert.Equal(BuiltInType.Int32, ints.TypeInfo.BuiltInType);
    }
}