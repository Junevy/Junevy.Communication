using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Modbus;
using Junevy.Communication.Modbus.DependencyInjection;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.Test
{
    /// <summary>
    /// Modbus 调试工具的视图模型（v3，经 <see cref="PlcClientFactory"/> 创建客户端）。支持 TCP 与串口，读写 C、DI、IR、HR。
    /// 每次连接使用新的客户端名称，因此修改配置后重新连接即可生效。
    /// </summary>
    public partial class MainWindowViewModel : ObservableObject
    {
        private const string ConnectedText = "Connected";
        private const string DisconnectedText = "Disconnected";

        private readonly PlcClientFactory factory;
        private IPlcClient? client;
        private int connections;

        [ObservableProperty]
        private bool isConnected;

        [ObservableProperty]
        private ModbusTransport transport = ModbusTransport.Tcp;

        [ObservableProperty]
        private string host = "127.0.0.1";

        [ObservableProperty]
        private int port = 502;

        [ObservableProperty]
        private string? portName;

        [ObservableProperty]
        private int baudRate = 9600;

        [ObservableProperty]
        private byte unitId = 1;

        [ObservableProperty]
        private string area = "HR";

        [ObservableProperty]
        private int start;

        [ObservableProperty]
        private int count = 1;

        [ObservableProperty]
        private string value = "0";

        [ObservableProperty]
        private string status = DisconnectedText;

        /// <summary>读取结果（地址与值），显示在表格中。</summary>
        public ObservableCollection<ModbusData> DataList { get; } = new ObservableCollection<ModbusData>();

        /// <summary>本机的串口名。</summary>
        public ObservableCollection<string> Serials { get; } = new ObservableCollection<string>(SerialPort.GetPortNames());

        /// <summary>可选的波特率。</summary>
        public int[] BaudRates { get; } = { 9600, 19200, 38400, 57600, 115200 };

        /// <summary>可选的传输方式。</summary>
        public ModbusTransport[] Transports { get; } = { ModbusTransport.Tcp, ModbusTransport.Serial };

        /// <summary>可选的存储区：C 线圈、DI 离散输入、IR 输入寄存器、HR 保持寄存器。</summary>
        public string[] Areas { get; } = { "C", "DI", "IR", "HR" };

        /// <summary>创建视图模型。</summary>
        /// <param name="factory">套件的客户端工厂（由 <c>AddModbus</c> 注册）。</param>
        public MainWindowViewModel(PlcClientFactory factory)
        {
            this.factory = factory;
            PortName = Serials.Count > 0 ? Serials[0] : null;
        }

        /// <summary>连接：按当前配置创建客户端并连接。</summary>
        [RelayCommand]
        private async Task ConnectAsync()
        {
            IPlcClient created = factory.GetOrAdd($"debug-{Interlocked.Increment(ref connections)}", BuildConfig());
            client = created;
            CommResult result = await created.ConnectAsync();
            IsConnected = result.IsSuccess;
            Status = result.IsSuccess ? ConnectedText : result.ErrorMessage;
        }

        /// <summary>断开连接。</summary>
        [RelayCommand]
        private async Task DisconnectAsync()
        {
            if (client is not null)
                await client.DisconnectAsync();

            IsConnected = false;
            Status = DisconnectedText;
        }

        /// <summary>读取 <see cref="Count"/> 个点，结果显示在表格中。</summary>
        [RelayCommand]
        private async Task ReadAsync()
        {
            if (client is null)
                return;

            string address = $"{Area}{Start}";
            if (Area is "C" or "DI")
            {
                CommResult<bool[]> bits = await client.ReadBitsAsync(address, Count);
                Show(bits.IsSuccess ? bits.Data.Select((bit, i) => new ModbusData { Address = (ushort)(Start + i), Value = (ushort)(bit ? 1 : 0) }) : null, bits.ErrorMessage);
                return;
            }

            CommResult<ushort[]> words = await client.ReadUInt16Async(address, Count);
            Show(words.IsSuccess ? words.Data.Select((word, i) => new ModbusData { Address = (ushort)(Start + i), Value = word }) : null, words.ErrorMessage);
        }

        /// <summary>写入 <see cref="Value"/> 到起始地址（一个点）。</summary>
        [RelayCommand]
        private async Task WriteAsync()
        {
            if (client is null)
                return;

            string address = $"{Area}{Start}";
            CommResult result = Area switch
            {
                "C" => await client.WriteBoolAsync(address, ParseBool(Value)),
                "HR" => await client.WriteUInt16Async(address, ushort.Parse(Value)),
                _ => CommResult.Fail($"The {Area} area is read-only.", CommErrorKind.InvalidRequest),
            };
            Status = result.IsSuccess ? ConnectedText : result.ErrorMessage;
        }

        private ModbusClientConfig BuildConfig()
        {
            var options = new ModbusClientOptions { UnitId = UnitId };
            return Transport == ModbusTransport.Serial
                ? new ModbusClientConfig
                {
                    Transport = ModbusTransport.Serial,
                    Serial = new SerialChannelConfig { PortName = PortName ?? string.Empty, BaudRate = BaudRate },
                    Options = options,
                }
                : new ModbusClientConfig
                {
                    Transport = ModbusTransport.Tcp,
                    Tcp = new TcpClientChannelConfig { Host = Host, Port = Port },
                    Options = options,
                };
        }

        private void Show(IEnumerable<ModbusData>? rows, string? error)
        {
            DataList.Clear();
            if (rows is null)
            {
                Status = error ?? "Read failed.";
                return;
            }

            foreach (ModbusData row in rows)
                DataList.Add(row);
            Status = ConnectedText;
        }

        private static bool ParseBool(string text) => text.Trim() is "1" or "true" or "True";
    }
}
