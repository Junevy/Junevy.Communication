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
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<Junevy.Communication.Modbus.Core.Models.ModbusCommEventCounter> GetCommEventCounter(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<Junevy.Communication.Modbus.Core.Models.ModbusCommEventLog> GetCommEventLog(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Boolean[]> ReadCoils(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Boolean[]> ReadDiscreteInputs(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte> ReadExceptionStatus(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]> Diagnostics(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.Byte[])",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]> Diagnostics(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]> MaskWriteRegister(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]> ReportServerId(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]> WriteMultipleCoils(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.Boolean[])",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]> WriteMultipleRegisters(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16[])",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]> WriteSingleCoil(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.Boolean)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]> WriteSingleRegister(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.UInt16[]> ReadHoldingRegisters(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.UInt16[]> ReadInputRegisters(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16)",
            "Junevy.Communication.Modbus.Core.Models.ModbusResult<System.UInt16[]> ReadWriteMultipleRegisters(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.UInt16, System.UInt16[])",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<Junevy.Communication.Modbus.Core.Models.ModbusCommEventCounter>> GetCommEventCounterAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<Junevy.Communication.Modbus.Core.Models.ModbusCommEventLog>> GetCommEventLogAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Boolean[]>> ReadCoilsAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Boolean[]>> ReadDiscreteInputsAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte>> ReadExceptionStatusAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]>> DiagnosticsAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.Byte[], System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]>> DiagnosticsAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]>> MaskWriteRegisterAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]>> ReportServerIdAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]>> WriteMultipleCoilsAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.Boolean[], System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]>> WriteMultipleRegistersAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16[], System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]>> WriteSingleCoilAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.Boolean, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.Byte[]>> WriteSingleRegisterAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.UInt16[]>> ReadHoldingRegistersAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.UInt16[]>> ReadInputRegistersAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.Threading.CancellationToken)",
            "System.Threading.Tasks.ValueTask<Junevy.Communication.Modbus.Core.Models.ModbusResult<System.UInt16[]>> ReadWriteMultipleRegistersAsync(Junevy.Communication.Modbus.Core.Interfaces.IModbus, System.Byte, System.UInt16, System.UInt16, System.UInt16, System.UInt16[], System.Threading.CancellationToken)",
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
                    sb.Append(FormatType(method.ReturnType)).Append(' ').Append(method.Name).Append('(');
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        sb.Append(FormatType(parameters[i].ParameterType));
                    }

                    sb.Append(')');
                    lines.Add(sb.ToString());
                }
            }

            lines.Sort(StringComparer.Ordinal);
            return lines.ToArray();
        }

        /// <summary>
        /// 格式化类型名，**不使用程序集限定名**——程序集限定名含 Version，
        /// 会让本快照在每次版本号变更时假失败（2.0.0 升级时踩过一次）。
        /// </summary>
        private static string FormatType(Type type)
        {
            if (type.IsGenericType)
            {
                var definition = new StringBuilder();
                definition.Append(type.Namespace).Append('.').Append(type.Name);
                int tick = definition.ToString().IndexOf('`');
                if (tick >= 0)
                    definition.Length = tick;

                definition.Append('<');
                Type[] args = type.GetGenericArguments();
                for (int i = 0; i < args.Length; i++)
                {
                    if (i > 0)
                        definition.Append(", ");
                    definition.Append(FormatType(args[i]));
                }

                return definition.Append('>').ToString();
            }

            if (type.IsArray)
                return FormatType(type.GetElementType()!) + "[]";

            return type.FullName ?? type.Name;
        }

        private static void DumpActual(string[] actual)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "extension-surface-actual.txt");
            File.WriteAllLines(path, actual);
            Console.WriteLine("Dumped extension surface (" + actual.Length + " methods) to " + path);
        }
    }
}