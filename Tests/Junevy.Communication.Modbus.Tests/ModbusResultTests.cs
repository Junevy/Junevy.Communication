using Junevy.Communication.Modbus.Core.Models;
using System.Reflection;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// ModbusResult&lt;T&gt; 的形态契约：sealed、属性只读、只能经 Success/Fail 工厂方法构造，
    /// 且 Fail 不接受 ModbusErrorKind.None。
    /// </summary>
    public class ModbusResultTests
    {
        [Fact]
        public void Success_HasNoneKindAndData()
        {
            var result = ModbusResult<int>.Success(42);

            Assert.True(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.None, result.ErrorKind);
            Assert.Null(result.ErrorMessage);
            Assert.Equal(42, result.Data);
        }

        [Fact]
        public void Fail_DefaultKind_IsUnspecified()
        {
            var result = ModbusResult<int>.Fail("x");

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Unspecified, result.ErrorKind);
            Assert.Equal("x", result.ErrorMessage);
        }

        [Fact]
        public void Fail_WithKind_CarriesKindAndData()
        {
            var result = ModbusResult<int>.Fail("x", ModbusErrorKind.Timeout, 7);

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Timeout, result.ErrorKind);
            Assert.Equal(7, result.Data);
        }

        [Fact]
        public void Properties_HaveNoPublicSetters()
        {
            foreach (var property in typeof(ModbusResult<int>).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.True(
                    property.GetSetMethod(nonPublic: false) == null,
                    $"property {property.Name} still has a public setter");
            }
        }

        [Fact]
        public void Type_IsSealed()
        {
            Assert.True(typeof(ModbusResult<int>).IsSealed, "ModbusResult<T> must be sealed");
        }

        [Fact]
        public void Fail_WithNoneKind_Throws()
        {
            Assert.Throws<ArgumentException>(() => ModbusResult<int>.Fail("x", ModbusErrorKind.None));
        }
    }
}