using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Extensions;
using Junevy.Communication.Modbus.Factory;
using Junevy.Communication.Modbus.Rtu;
using Junevy.Communication.Modbus.Tcp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO.Ports;

namespace Junevy.Communication.Test
{
    public partial class MainWindowViewModel : ObservableObject
    {
        private readonly ModbusRtuClient mr;
        private IModbus tcp;
        private IModbusFactory factory;

        private bool isConnected = false;
        public bool IsConnected
        {
            get => isConnected;
            set => SetProperty(ref isConnected, value);
        }

        [ObservableProperty]
        private ushort length = 1;

        #region observable collection
        public ObservableCollection<ModbusData> DataList { get; set; } = [];
        public ObservableCollection<string> Serials { get; private set; } = new(SerialPort.GetPortNames());

        public Array ParityList => Enum.GetValues(typeof(Parity));
        public Array StopBitsList => Enum.GetValues(typeof(StopBits));
        public Array RegionList => Enum.GetValues(typeof(ModbusFunctionCode));
        public int[] Bits { get; private set; } = [5, 6, 7, 8];
        public int[] BaudRates { get; private set; } = [9600, 19200, 38400, 57600, 115200];
        #endregion

        public ModbusRtuClientConfig Config { get; set; } = new();
        public ModbusRequest Tx { get; set; } = new();

        public MainWindowViewModel(IModbusFactory factory)
        {
            this.mr = new ModbusRtuClient(Config);
            this.factory = factory;

            StateMonitor();
        }

        [RelayCommand]
        public void Connect()
        {
            //ModbusFactory factory = new();
            //var result = factory.TryAdd("test", new ModbusTcpClientConfig(), out tcp);
            this.tcp = factory.GetOrAdd("test", new ModbusTcpClientConfig());
            tcp.Connect();
            //if (result)
            //    _ = tcp?.Connect();

        }


        [RelayCommand]
        public async Task ExecuteAsync()
        {

            // var r = await tcp.ReadCoilsAsync(1,0,4);
            // var r = tcp.ReadCoils(1,0,5);
            // var r = tcp.ReadHoldingRegisters(1,0,5);
            // var r = await tcp.ReadHoldingRegistersAsync(1,0,5);
            // var r = tcp.ReadCoils(1,0,5);
            // var r = tcp.WriteSingleCoil(1, 0, true);
            // var r = await tcp.WriteSingleCoilAsync(1, 0, true);

            // var r = await tcp.WriteMultipleCoilsAsync(1, 0, new bool[5] {true, false, true, false, true});
            // var r = tcp.WriteSingleRegister(1, 2, 100);
            // var r = await tcp.WriteSingleRegisterAsync(1, 2, 100);

            // var r = await tcp.WriteMultipleRegistersAsync(1, 2, new ushort[5] {100, 200, 300, 400, 500});

            // var r = mr.ReadCoils(1,0,5);
            // var r = await mr.ReadDiscreteInputsAsync(1,0,5);

            // var r = mr.ReadHoldingRegisters(1,0,5);

            // var r = await mr.WriteMultipleRegistersAsync(1, 0, new ushort[5] {123, 1, 123, 1, 0});
            // Console.Write(r.ToString());

            // byte[] test = r

            var r = await tcp.WriteMultipleRegistersAsync(1, 2, [123, 1, 123, 1, 0]);
            Console.Write(r.ToString());


            //MessageBox.Show(r.ToString());

        }

        [RelayCommand]
        public void Disconnect()
        {
            tcp?.Disconnect();
        }

        private void StateMonitor()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    bool current = mr.IsConnected;

                    if (current != IsConnected)
                    {
                        IsConnected = current;
                    }

                    await Task.Delay(1000); // 轮询间隔
                }
            });
        }


        partial void OnLengthChanged(ushort oldValue, ushort newValue)
        {
            DataList.Clear();

            if (newValue > 128)
                newValue = 127;

            for (ushort i = 0; i < newValue; i++)
            {
                DataList.Add(new ModbusData() { Address = i });
            }

            Tx.Quantity = newValue;
        }

    }
}
