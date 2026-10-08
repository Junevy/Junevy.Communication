using Junevy.Communication.Modbus.Core.Interfaces;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// 扩展方法公开表面快照：重构 ModbusExtensions 时，任何方法名、参数、返回类型的变化都会被这里拦住。
    /// 只关心"签名面"，不关心行为（行为由 ExtensionRequestTests 等覆盖）。
    /// </summary>
    public class ExtensionSurfaceTests
    {
        private static readonly string[] ExpectedSurface = new[]
{
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[Junevy.Communication.Modbus.Core.Models.ModbusCommEventCounter, Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] GetCommEventCounter(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[Junevy.Communication.Modbus.Core.Models.ModbusCommEventLog, Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] GetCommEventLog(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Boolean[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] ReadCoils(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Boolean[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] ReadDiscreteInputs(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte, System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] ReadExceptionStatus(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] Diagnostics(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.Byte[])",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] Diagnostics(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] MaskWriteRegister(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] ReportServerId(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] WriteMultipleCoils(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.Boolean[])",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] WriteMultipleRegisters(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16[])",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] WriteSingleCoil(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.Boolean)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] WriteSingleRegister(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.UInt16[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] ReadHoldingRegisters(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.UInt16[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] ReadInputRegisters(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.UInt16[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] ReadWriteMultipleRegisters(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.UInt16, System.UInt16[])",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[Junevy.Communication.Modbus.Core.Models.ModbusCommEventCounter, Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] GetCommEventCounterAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[Junevy.Communication.Modbus.Core.Models.ModbusCommEventLog, Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] GetCommEventLogAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Boolean[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] ReadCoilsAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Boolean[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] ReadDiscreteInputsAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte, System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] ReadExceptionStatusAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] DiagnosticsAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.Byte[], System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] DiagnosticsAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] MaskWriteRegisterAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] ReportServerIdAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] WriteMultipleCoilsAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.Boolean[], System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] WriteMultipleRegistersAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16[], System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] WriteSingleCoilAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.Boolean, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.Byte[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] WriteSingleRegisterAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.UInt16[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] ReadHoldingRegistersAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.UInt16[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] ReadInputRegistersAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask`1[[Junevy.Communication.Modbus.Core.Models.ModbusResult`1[[System.UInt16[], System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], Junevy.Communication.Modbus, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null]] ReadWriteMultipleRegistersAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.UInt16, System.UInt16[], System.Threading.CancellationToken)",
        };

        [Fact]
        public void PublicExtensionMethods_MatchExpectedSurface()
        {
            string[] actual = CollectPublicExtensionMethods();

            if (actual.Length != ExpectedSurface.Length)
            {
                DumpActual(actual);
                Assert.Fail($"extension surface count changed: expected {ExpectedSurface.Length}, actual {actual.Length}\n(actual dumped to extension-surface-actual.txt)");
            }

            for (int i = 0; i < actual.Length; i++)
            {
                if (actual[i] != ExpectedSurface[i])
                {
                    DumpActual(actual);
                    Assert.Fail($"extension surface changed at [{i}]:\n  expected: {ExpectedSurface[i]}\n  actual:   {actual[i]}\n(actual dumped to extension-surface-actual.txt)");
                }
            }
        }

        /// <summary>
        /// 收集程序集中命名空间为 Junevy.Communication.Modbus.Extensions、带 ExtensionAttribute、
        /// 第一个参数为 IModbus 的公开静态方法，格式化为 "返回类型全名 方法名(参数类型全名, ...)"。
        /// </summary>
        private static string[] CollectPublicExtensionMethods()
        {
            var assembly = typeof(IModbus).Assembly;
            var lines = new List<string>();

            foreach (Type type in assembly.GetExportedTypes())
            {
                if (type.Namespace != "Junevy.Communication.Modbus.Extensions")
                    continue;

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (!method.IsDefined(typeof(ExtensionAttribute), inherit: false))
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 0 || parameters[0].ParameterType != typeof(IModbus))
                        continue;

                    var sb = new StringBuilder();
                    sb.Append(method.ReturnType.FullName).Append(' ').Append(method.Name).Append('(');
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        sb.Append(parameters[i].ParameterType.FullName);
                    }

                    sb.Append(')');
                    lines.Add(sb.ToString());
                }
            }

            lines.Sort(StringComparer.Ordinal);
            return lines.ToArray();
        }

        private static void DumpActual(string[] actual)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "extension-surface-actual.txt");
            File.WriteAllLines(path, actual);
            Console.WriteLine("Dumped extension surface (" + actual.Length + " methods) to " + path);
        }
    }
}