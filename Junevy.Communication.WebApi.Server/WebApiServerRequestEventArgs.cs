using System.Net;

namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// <c>RequestReceived</c> 的事件参数：请求进入服务器、路由之前触发（P32-D10）。只读，不含请求头与正文，避免凭据进入事件。
/// </summary>
public sealed class WebApiServerRequestEventArgs : EventArgs
{
    /// <summary>创建事件参数（由服务器创建）。</summary>
    internal WebApiServerRequestEventArgs(string serverName, long requestId, string method, string path, IPEndPoint? remoteEndPoint)
    {
        ServerName = serverName;
        RequestId = requestId;
        Method = method;
        Path = path;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>服务器名称。</summary>
    public string ServerName { get; }

    /// <summary>本服务器内递增的请求编号，与 <c>RequestCompleted</c> 对应。</summary>
    public long RequestId { get; }

    /// <summary>HTTP 方法，大写。</summary>
    public string Method { get; }

    /// <summary>请求路径（不含查询串）。</summary>
    public string Path { get; }

    /// <summary>对端地址；取不到时为 null。</summary>
    public IPEndPoint? RemoteEndPoint { get; }
}
