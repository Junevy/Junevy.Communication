using System.Net;
using Junevy.Communication.Channels;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 客户端通道的公开接口（设计文档 7.1）。
/// </summary>
public interface ITcpClientChannel : IClientChannel
{
    /// <summary>构造时传入的配置对象。</summary>
    TcpClientChannelConfig Config { get; }

    /// <summary>已连接时当前套接字的远端端点；否则为 null。</summary>
    IPEndPoint? RemoteEndPoint { get; }

    /// <summary>已连接时当前套接字的本地端点；否则为 null。</summary>
    IPEndPoint? LocalEndPoint { get; }

    /// <summary>当前连接是否已完成 TLS 认证（认证成功后为 true，连接结束后为 false）。</summary>
    bool IsTlsActive { get; }
}
