using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Server;

namespace Junevy.Communication.DebugAssistant.Protocols.WebApi;

/// <summary>
/// WebApi 服务端页（计划第 7.11 节）：监听地址（可写端口 0，启动后显示实际地址）、认证、并发与超时、业务信封、去重、HTTPS、
/// 路由表（运行中不能修改）与请求日志。每次启动都用当前的配置与路由创建新的库服务器，因此停止后可以用新的参数再启动。
/// </summary>
public sealed partial class WebApiServerSessionViewModel : SessionViewModel
{
    private const int MaxRequestRows = 200;

    private WebApiServer? _server;

    public WebApiServerSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies)
    {
        AddRouteCommand = new RelayCommand(AddRoute);
        RemoveRouteCommand = new RelayCommand<ServerRouteSettings?>(RemoveRoute);
        ClearRequestLogCommand = new RelayCommand(RequestLog.Clear);
        Routes.Add(new ServerRouteSettings("GET", "/hello", ServerRouteKind.Fixed, "{\"message\":\"hello\"}", null, null, null, 0));
        Routes.Add(new ServerRouteSettings("POST", "/reports", ServerRouteKind.Echo, null, null, null, null, 0));
    }

    public IReadOnlyList<string> MethodChoices => WebApiSupport.MethodChoices;

    public IReadOnlyList<ServerRouteKind> KindChoices { get; } = Enum.GetValues<ServerRouteKind>();

    public IReadOnlyList<WebApiAuthScheme> AuthChoices { get; } = Enum.GetValues<WebApiAuthScheme>();

    /// <summary>路由表：每次启动时按顺序注册。</summary>
    public ObservableCollection<ServerRouteSettings> Routes { get; } = [];

    /// <summary>请求日志（服务端完成的每个请求一行），最新的在最前。</summary>
    public ObservableCollection<ServerRequestRow> RequestLog { get; } = [];

    [ObservableProperty]
    private string _urlsText = "http://127.0.0.1:0";

    [ObservableProperty]
    private WebApiAuthScheme _authScheme = WebApiAuthScheme.None;

    [ObservableProperty]
    private string _userNameVariable = string.Empty;

    [ObservableProperty]
    private string _passwordVariable = string.Empty;

    [ObservableProperty]
    private string _tokenVariable = string.Empty;

    [ObservableProperty]
    private string _apiKeyHeader = "X-API-Key";

    [ObservableProperty]
    private string _apiKeyVariable = string.Empty;

    /// <summary>HTTPS 证书的 .pfx 路径；留空表示只用 HTTP。</summary>
    [ObservableProperty]
    private string _certificatePath = string.Empty;

    /// <summary>证书口令所在的环境变量名（不保存口令本身）。</summary>
    [ObservableProperty]
    private string _certificatePasswordVariable = string.Empty;

    [ObservableProperty]
    private int _maxConcurrentRequests = 16;

    [ObservableProperty]
    private int _requestTimeoutMs = 30000;

    [ObservableProperty]
    private long _maxRequestBodySize = 1048576;

    [ObservableProperty]
    private int _stopTimeoutMs = 5000;

    [ObservableProperty]
    private bool _envelopeEnabled;

    [ObservableProperty]
    private string _codePath = "code";

    [ObservableProperty]
    private string _messagePath = "msg";

    [ObservableProperty]
    private string _dataPath = "data";

    [ObservableProperty]
    private string _successCodesText = "0";

    [ObservableProperty]
    private int _businessFailureStatusCode = 200;

    [ObservableProperty]
    private bool _deduplicationEnabled;

    [ObservableProperty]
    private string _deduplicationHeaderName = "Idempotency-Key";

    [ObservableProperty]
    private int _deduplicationWindowMs = 600000;

    [ObservableProperty]
    private int _deduplicationMaxEntries = 1000;

    [ObservableProperty]
    private bool _logBodies;

    [ObservableProperty]
    private string _redactedHeadersText = "Authorization, Proxy-Authorization";

    [ObservableProperty]
    private string _redactedQueryParametersText = "access_token, token, api_key, apikey";

    [ObservableProperty]
    private string _newRouteMethod = "GET";

    [ObservableProperty]
    private string _newRouteTemplate = "/orders/{id:int}";

    [ObservableProperty]
    private ServerRouteKind _newRouteKind = ServerRouteKind.Fixed;

    [ObservableProperty]
    private string _newRouteJson = "{\"id\":1,\"name\":\"a\"}";

    [ObservableProperty]
    private string _newRouteCode = string.Empty;

    [ObservableProperty]
    private string _newRouteMessage = string.Empty;

    /// <summary>显式状态码：指定状态码与业务失败时使用；固定 JSON 与回显时可空。</summary>
    [ObservableProperty]
    private int? _newRouteStatusCode;

    /// <summary>延迟（毫秒），用于演示超过 <see cref="RequestTimeoutMs"/> 时的 504。</summary>
    [ObservableProperty]
    private int _newRouteDelayMs;

    [ObservableProperty]
    private ServerRouteSettings? _selectedRoute;

    /// <summary>路由表的说明：登记结果或不能登记的原因。</summary>
    [ObservableProperty]
    private string _routeMessage = string.Empty;

    /// <summary>服务端状态的中文名称。</summary>
    [ObservableProperty]
    private string _serverStateText = WebApiSupport.ServerStateText(WebApiServerState.Stopped);

    /// <summary>启动后实际绑定的地址（端口 0 时由系统分配）。</summary>
    [ObservableProperty]
    private string _boundUrls = string.Empty;

    public IRelayCommand AddRouteCommand { get; }

    public IRelayCommand<ServerRouteSettings?> RemoveRouteCommand { get; }

    public IRelayCommand ClearRequestLogCommand { get; }

    public override string ConnectText => "启动";

    public override string DisconnectText => "停止";

    public override SessionSettings CaptureSettings() => Capture();

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not WebApiServerSettings saved)
        {
            throw new ArgumentException("The settings do not belong to a WebApi server page.", nameof(settings));
        }

        UrlsText = WebApiSupport.JoinLines(saved.Urls);
        AuthScheme = saved.Auth.Scheme;
        UserNameVariable = saved.Auth.UserNameVariable ?? string.Empty;
        PasswordVariable = saved.Auth.PasswordVariable ?? string.Empty;
        TokenVariable = saved.Auth.TokenVariable ?? string.Empty;
        ApiKeyHeader = saved.Auth.ApiKeyHeader;
        ApiKeyVariable = saved.Auth.ApiKeyVariable ?? string.Empty;
        CertificatePath = saved.CertificatePath ?? string.Empty;
        CertificatePasswordVariable = saved.CertificatePasswordVariable ?? string.Empty;
        MaxConcurrentRequests = saved.MaxConcurrentRequests;
        RequestTimeoutMs = saved.RequestTimeout;
        MaxRequestBodySize = saved.MaxRequestBodySize;
        StopTimeoutMs = saved.StopTimeout;
        EnvelopeEnabled = saved.Envelope.Enabled;
        CodePath = saved.Envelope.CodePath;
        MessagePath = saved.Envelope.MessagePath;
        DataPath = saved.Envelope.DataPath;
        SuccessCodesText = WebApiSupport.JoinList(saved.Envelope.SuccessCodes);
        BusinessFailureStatusCode = saved.BusinessFailureStatusCode;
        DeduplicationEnabled = saved.Deduplication.Enabled;
        DeduplicationHeaderName = saved.Deduplication.HeaderName;
        DeduplicationWindowMs = saved.Deduplication.Window;
        DeduplicationMaxEntries = saved.Deduplication.MaxEntries;
        LogBodies = saved.Logging.LogBodies;
        RedactedHeadersText = WebApiSupport.JoinList(saved.Logging.RedactedHeaders);
        RedactedQueryParametersText = WebApiSupport.JoinList(saved.Logging.RedactedQueryParameters);
        Routes.Clear();
        foreach (ServerRouteSettings route in saved.Routes)
        {
            Routes.Add(route);
        }
    }

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        WebApiServerSettings settings = Capture();
        if (WebApiSupport.ServerProblem(settings) is { } problem)
        {
            throw new CommFailureException(CommResult.Fail(problem, CommErrorKind.InvalidRequest));
        }

        foreach (ServerRouteSettings route in settings.Routes)
        {
            if (WebApiSupport.RouteProblem(route) is { } routeProblem)
            {
                throw new CommFailureException(CommResult.Fail($"{route.Method} {route.Template}：{routeProblem}", CommErrorKind.InvalidRequest));
            }
        }

        WebApiServer server = CreateServer(settings);
        server.StateChanged += (_, e) => PostToUi(() =>
        {
            if (ReferenceEquals(_server, server))
            {
                ServerStateText = WebApiSupport.ServerStateText(e.Current);
            }
        });
        server.RequestCompleted += (_, e) => PostToUi(() =>
        {
            if (ReferenceEquals(_server, server))
            {
                RecordRequest(e);
            }
        });
        _server = server;
        ServerStateText = WebApiSupport.ServerStateText(WebApiServerState.Starting);

        CommResult started = await server.StartAsync(cancellationToken).ConfigureAwait(true);
        if (!started.IsSuccess)
        {
            _server = null;
            await server.DisposeAsync().ConfigureAwait(true);
            ResetState();
            throw new CommFailureException(started);
        }

        BoundUrls = WebApiSupport.JoinLines(server.Urls);
        State = SessionState.Running;
    }

    protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        // 没有在运行时不改变状态：释放未启动的页签时，状态通知不应在非界面线程上发出。
        if (_server is not { } server)
        {
            return;
        }

        _server = null;
        ServerStateText = WebApiSupport.ServerStateText(WebApiServerState.Stopping);
        try
        {
            await server.StopAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(true);
            ResetState();
        }
    }

    private WebApiServerSettings Capture() => new(
        WebApiSupport.ParseLines(UrlsText),
        new WebApiAuthSettings(
            AuthScheme,
            WebApiSupport.Optional(UserNameVariable),
            WebApiSupport.Optional(PasswordVariable),
            WebApiSupport.Optional(TokenVariable),
            ApiKeyHeader.Trim(),
            WebApiSupport.Optional(ApiKeyVariable)),
        WebApiSupport.Optional(CertificatePath),
        WebApiSupport.Optional(CertificatePasswordVariable),
        MaxConcurrentRequests,
        RequestTimeoutMs,
        MaxRequestBodySize,
        StopTimeoutMs,
        new WebApiEnvelopeSettings(
            EnvelopeEnabled,
            CodePath.Trim(),
            MessagePath.Trim(),
            DataPath.Trim(),
            WebApiSupport.ParseList(SuccessCodesText)),
        BusinessFailureStatusCode,
        new WebApiDeduplicationSettings(DeduplicationEnabled, DeduplicationHeaderName.Trim(), DeduplicationWindowMs, DeduplicationMaxEntries),
        new WebApiLogSettings(
            LogBodies,
            WebApiSupport.ParseList(RedactedHeadersText),
            WebApiSupport.ParseList(RedactedQueryParametersText)),
        Routes.ToArray());

    // 库在构造与映射路由时拒绝非法参数（例如模板重复），这里转成请求无效；创建失败时释放已创建的服务器。
    private WebApiServer CreateServer(WebApiServerSettings settings)
    {
        var config = new WebApiServerConfig
        {
            Urls = settings.Urls,
            Https = new WebApiServerHttpsOptions
            {
                CertificatePath = settings.CertificatePath,
                CertificatePasswordVariable = settings.CertificatePasswordVariable,
            },
            Auth = WebApiSupport.ToAuthOptions(settings.Auth),
            MaxConcurrentRequests = settings.MaxConcurrentRequests,
            RequestTimeout = settings.RequestTimeout,
            MaxRequestBodySize = settings.MaxRequestBodySize,
            StopTimeout = settings.StopTimeout,
            Envelope = WebApiSupport.ToEnvelopeOptions(settings.Envelope),
            BusinessFailureStatusCode = settings.BusinessFailureStatusCode,
            Deduplication = new WebApiServerDeduplicationOptions
            {
                Enabled = settings.Deduplication.Enabled,
                HeaderName = settings.Deduplication.HeaderName,
                Window = settings.Deduplication.Window,
                MaxEntries = settings.Deduplication.MaxEntries,
            },
            Logging = WebApiSupport.ToLogOptions(settings.Logging),
        };

        WebApiServer server;
        try
        {
            server = new WebApiServer("webapi-server", config, null, SessionLoggerFactory);
        }
        catch (ArgumentException ex)
        {
            throw new CommFailureException(CommResult.Fail(ex.Message, CommErrorKind.InvalidRequest));
        }

        try
        {
            foreach (ServerRouteSettings route in settings.Routes)
            {
                MapRoute(server, route);
            }
        }
        catch (ArgumentException ex)
        {
            server.Dispose();
            throw new CommFailureException(CommResult.Fail(ex.Message, CommErrorKind.InvalidRequest));
        }

        return server;
    }

    // 方法决定 Map 的重载：GET 与 DELETE 没有正文，POST 与 PUT 把正文读成 JsonElement；响应统一用 object，由序列化器写出。
    private static void MapRoute(WebApiServer server, ServerRouteSettings route)
    {
        switch (route.Method)
        {
            case "GET":
                server.MapGet<object>(route.Template, (context, cancellationToken) => RespondAsync(route, context, null, cancellationToken));
                break;
            case "DELETE":
                server.MapDelete<object>(route.Template, (context, cancellationToken) => RespondAsync(route, context, null, cancellationToken));
                break;
            case "POST":
                server.MapPost<JsonElement, object>(route.Template, (body, context, cancellationToken) => RespondAsync(route, context, body, cancellationToken));
                break;
            case "PUT":
                server.MapPut<JsonElement, object>(route.Template, (body, context, cancellationToken) => RespondAsync(route, context, body, cancellationToken));
                break;
            default:
                throw new ArgumentException($"Unsupported method '{route.Method}'.", nameof(route));
        }
    }

    private static async Task<WebApiServerResponse<object>> RespondAsync(
        ServerRouteSettings route,
        WebApiServerContext context,
        JsonElement? body,
        CancellationToken cancellationToken)
    {
        if (route.DelayMs > 0)
        {
            await Task.Delay(route.DelayMs, cancellationToken);
        }

        switch (route.Kind)
        {
            case ServerRouteKind.Fixed:
                return WebApiServerResponse<object>.Ok(WebApiSupport.ParseJson(route.Json!), route.StatusCode);
            case ServerRouteKind.Status:
                object? data = string.IsNullOrWhiteSpace(route.Json) ? null : WebApiSupport.ParseJson(route.Json);
                return WebApiServerResponse<object>.Ok(data, route.StatusCode ?? 200);
            case ServerRouteKind.Echo:
                object echo = body is { } payload
                    ? payload
                    : JsonSerializer.SerializeToElement(new { method = context.Method, path = context.Path, query = context.Query });
                return WebApiServerResponse<object>.Ok(echo, route.StatusCode);
            default:
                return WebApiServerResponse<object>.Fail(route.Code!, route.Message!, route.StatusCode);
        }
    }

    private void RecordRequest(WebApiServerCompletedEventArgs e)
    {
        var row = new ServerRequestRow(
            Dependencies.Clock.GetLocalNow().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            e.Method,
            e.Path,
            e.RouteTemplate ?? "—",
            WebApiSupport.OutcomeText(e.Outcome),
            e.StatusCode.ToString(CultureInfo.InvariantCulture),
            $"{e.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} 毫秒");
        RequestLog.InsertNewest(row, MaxRequestRows);
    }

    private void AddRoute()
    {
        if (!IsEditable)
        {
            RouteMessage = "运行中不能修改路由，请先停止。";
            return;
        }

        var route = new ServerRouteSettings(
            NewRouteMethod,
            NewRouteTemplate.Trim(),
            NewRouteKind,
            WebApiSupport.Optional(NewRouteJson),
            WebApiSupport.Optional(NewRouteCode),
            WebApiSupport.Optional(NewRouteMessage),
            NewRouteStatusCode,
            NewRouteDelayMs);
        if (WebApiSupport.RouteProblem(route) is { } problem)
        {
            RouteMessage = problem;
            return;
        }

        // 同一方法与模板的路由替换原有的一条（与 SECS 规则表相同）。
        foreach (ServerRouteSettings existing in Routes
            .Where(candidate => candidate.Method == route.Method && candidate.Template.Equals(route.Template, StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            Routes.Remove(existing);
        }

        Routes.Add(route);
        RouteMessage = $"已登记 {route.Method} {route.Template}。";
    }

    private void RemoveRoute(ServerRouteSettings? route)
    {
        if (!IsEditable)
        {
            RouteMessage = "运行中不能修改路由，请先停止。";
            return;
        }

        if (route is not null)
        {
            Routes.Remove(route);
            RouteMessage = $"已删除 {route.Method} {route.Template}。";
        }
    }

    private void ResetState()
    {
        State = SessionState.Stopped;
        ServerStateText = WebApiSupport.ServerStateText(WebApiServerState.Stopped);
        BoundUrls = string.Empty;
    }

}
