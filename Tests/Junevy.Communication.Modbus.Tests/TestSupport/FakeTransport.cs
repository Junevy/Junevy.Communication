using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Core.Parsing;
using Junevy.Communication.Modbus.Core.Transports;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Modbus.Tests.TestSupport
{
    /// <summary>
    /// 测试辅助传输层：继承 <see cref="ModbusTransportBase"/>，不使用任何真实 I/O，
    /// 供基类请求/重试/重连骨架的确定性测试使用。各钩子经可注入委托与计数器暴露。
    /// </summary>
    internal sealed class FakeTransport : ModbusTransportBase
    {
        public bool Connected = true;

        public int OpenConnectionCalls;
        public int OpenConnectionAsyncCalls;
        public int SendFrameCalls;
        public int ReceiveFrameCalls;
        public int InvalidateConnectionCalls;

        /// <summary>同步连接钩子。默认返回 true 并把 Connected 置为 true。</summary>
        public Func<bool> OnOpenConnection = () => true;

        /// <summary>异步连接钩子。默认返回 true 并把 Connected 置为 true。</summary>
        public Func<CancellationToken, Task<bool>> OnOpenConnectionAsync = ct =>
        {
            return Task.FromResult(true);
        };

        /// <summary>发送钩子。默认返回 true；可改为返回 false 或抛出异常。</summary>
        public Func<bool> OnSendFrame = () => true;

        /// <summary>接收结果队列：每次接收出队一个；为空时返回默认成功帧。</summary>
        public Queue<ModbusResult<byte[]>> ReceiveResults = new();

        public FakeTransport(bool reconnectEnabled, int retryCount, int retryInterval)
            : base(NullLogger.Instance, new TcpProtocolParser(), new ModbusFrameBuilder())
        {
            ReconnectEnabled = reconnectEnabled;
            RetryCount = retryCount;
            RetryInterval = retryInterval;
        }

        public override bool IsConnected => Connected;

        public override ModbusProtocolType ProtocolType => ModbusProtocolType.TCP;

        protected override bool AssignsTransactionId => true;

        protected override bool ReconnectEnabled { get; }

        protected override int RetryCount { get; }

        protected override int RetryInterval { get; }

        protected override bool OpenConnection()
        {
            OpenConnectionCalls++;
            bool result = OnOpenConnection();
            if (result)
                Connected = true;
            return result;
        }

        protected override async Task<bool> OpenConnectionAsync(CancellationToken cancellationToken)
        {
            OpenConnectionAsyncCalls++;
            bool result = await OnOpenConnectionAsync(cancellationToken);
            if (result)
                Connected = true;
            return result;
        }

        protected override void CloseConnection() => Connected = false;

        protected override void InvalidateConnection()
        {
            InvalidateConnectionCalls++;
            Connected = false;
        }

        protected override void DisposeConnection() => Connected = false;

        protected override bool SendFrame(ModbusRequest request)
        {
            SendFrameCalls++;
            return OnSendFrame();
        }

        protected override Task<bool> SendFrameAsync(ModbusRequest request, CancellationToken cancellationToken)
        {
            SendFrameCalls++;
            return Task.FromResult(OnSendFrame());
        }

        protected override ModbusResult<byte[]> ReceiveFrame(ModbusRequest request)
        {
            ReceiveFrameCalls++;
            return ReceiveResults.Count > 0 ? ReceiveResults.Dequeue() : DefaultSuccess();
        }

        protected override Task<ModbusResult<byte[]>> ReceiveFrameAsync(ModbusRequest request, CancellationToken cancellationToken)
        {
            ReceiveFrameCalls++;
            return Task.FromResult(ReceiveResults.Count > 0 ? ReceiveResults.Dequeue() : DefaultSuccess());
        }

        // ————————————————— 消息/日志钩子：固定桩 —————————————————

        protected override string GetNotConnectedMessage(bool isAsync) => "fake";

        protected override string GetSendFailedMessage(bool isAsync) => "fake";

        protected override string GetRequestFailedLogText(bool isAsync) => "fake";

        protected override void LogReconnectAttempt(bool isAsync)
        {
        }

        protected override void LogReconnectFailed(Exception ex, bool isAsync)
        {
        }

        /// <summary>建模 TCP 语义：Timeout / ConnectionClosed 需要重建连接后才能重试。</summary>
        protected override bool RequiresNewConnection(ModbusResult<byte[]> result)
            => result.ErrorKind == ModbusErrorKind.Timeout || result.ErrorKind == ModbusErrorKind.ConnectionClosed;

        private static ModbusResult<byte[]> DefaultSuccess()
            => ModbusResult<byte[]>.Success(new byte[] { 0, 0, 0, 0, 0, 0, 1, 3, 2, 0, 1 });
    }
}
