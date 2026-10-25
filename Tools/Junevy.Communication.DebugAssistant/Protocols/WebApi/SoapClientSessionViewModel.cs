using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Xml;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Soap;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Protocols.WebApi;

/// <summary>
/// SOAP 客户端页（计划第 7.10 节）：连接选项（地址、版本、默认命名空间、健康检查地址，以及与 REST 共用的认证、重试与日志）；
/// 原始调用（SOAPAction 与正文 XML）；响应与 Fault（<c>RemoteError</c>，远端码为 fault code，Fault 不重试）；事件时间线与可用性。
/// <paramref name="httpHandler"/> 是测试用的组件钩子：正式运行时为 null，库使用自己的 HTTP 处理器。
/// </summary>
public sealed partial class SoapClientSessionViewModel : SessionViewModel
{
    private const int MaxTimelineRows = 200;
    private const string NotConnectedMessage = "页签没有连接，请先连接。";

    private readonly HttpMessageHandler? _httpHandler;
    private SoapClient? _client;

    public SoapClientSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies, HttpMessageHandler? httpHandler = null)
        : base(descriptor, dependencies)
    {
        _httpHandler = httpHandler;
        SendCommand = new AsyncRelayCommand(SendAsync, () => IsLinked);
        ClearTimelineCommand = new RelayCommand(Timeline.Clear);
        PropertyChanged += OnOwnPropertyChanged;
    }

    public IReadOnlyList<SoapVersion> VersionChoices { get; } = Enum.GetValues<SoapVersion>();

    public IReadOnlyList<WebApiAuthScheme> AuthChoices { get; } = Enum.GetValues<WebApiAuthScheme>();

    /// <summary>调用时间线，最新的在最前。</summary>
    public ObservableCollection<WebApiTimelineRow> Timeline { get; } = [];

    /// <summary>已创建客户端；只有这时才能调用。</summary>
    public bool IsLinked => State == SessionState.Connected;

    [ObservableProperty]
    private string _url = "http://127.0.0.1:8080/service.asmx";

    [ObservableProperty]
    private SoapVersion _version = SoapVersion.Soap11;

    [ObservableProperty]
    private string _defaultNamespace = "http://mes.example.com/ns";

    [ObservableProperty]
    private string _healthCheckUrl = string.Empty;

    [ObservableProperty]
    private int _timeoutMs = 5000;

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

    [ObservableProperty]
    private int _retryCount;

    [ObservableProperty]
    private int _retryDelayMs = 1000;

    [ObservableProperty]
    private bool _retryUnsafeMethods;

    [ObservableProperty]
    private int _healthCheckIntervalMs = 30000;

    [ObservableProperty]
    private int _offlineThreshold = 3;

    [ObservableProperty]
    private bool _logBodies;

    [ObservableProperty]
    private string _redactedHeadersText = "Authorization, Proxy-Authorization";

    [ObservableProperty]
    private string _redactedQueryParametersText = "access_token, token, api_key, apikey";

    /// <summary>SOAPAction；调用时作为请求头发送（SOAP 1.1）或放进 Content-Type（SOAP 1.2）。</summary>
    [ObservableProperty]
    private string _soapAction = "urn:GetOrder";

    [ObservableProperty]
    private string _bodyXml = "<GetOrder><OrderNo>A-1</OrderNo></GetOrder>";

    [ObservableProperty]
    private string _responseStatus = "—";

    /// <summary>远端码：Fault 的 fault code，或业务码；没有时为 "—"。</summary>
    [ObservableProperty]
    private string _responseRemoteCode = "—";

    [ObservableProperty]
    private string _responseErrorKind = "—";

    [ObservableProperty]
    private string _responseMessage = string.Empty;

    [ObservableProperty]
    private string _responseXml = string.Empty;

    [ObservableProperty]
    private string _availabilityText = WebApiSupport.AvailabilityText(WebApiAvailability.Unknown);

    /// <summary>调用的说明：输入不合法、未连接。</summary>
    [ObservableProperty]
    private string _requestMessage = string.Empty;

    public IAsyncRelayCommand SendCommand { get; }

    public IRelayCommand ClearTimelineCommand { get; }

    public override SessionSettings CaptureSettings() => Capture();

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not SoapClientSettings saved)
        {
            throw new ArgumentException("The settings do not belong to a SOAP client page.", nameof(settings));
        }

        Url = saved.Url;
        Version = saved.Version;
        DefaultNamespace = saved.DefaultNamespace ?? string.Empty;
        HealthCheckUrl = saved.HealthCheckUrl ?? string.Empty;
        TimeoutMs = saved.Timeout;
        AuthScheme = saved.Auth.Scheme;
        UserNameVariable = saved.Auth.UserNameVariable ?? string.Empty;
        PasswordVariable = saved.Auth.PasswordVariable ?? string.Empty;
        TokenVariable = saved.Auth.TokenVariable ?? string.Empty;
        ApiKeyHeader = saved.Auth.ApiKeyHeader;
        ApiKeyVariable = saved.Auth.ApiKeyVariable ?? string.Empty;
        RetryCount = saved.Retry.RetryCount;
        RetryDelayMs = saved.Retry.RetryDelay;
        RetryUnsafeMethods = saved.Retry.RetryUnsafeMethods;
        HealthCheckIntervalMs = saved.HealthCheckInterval;
        OfflineThreshold = saved.OfflineThreshold;
        LogBodies = saved.Logging.LogBodies;
        RedactedHeadersText = WebApiSupport.JoinList(saved.Logging.RedactedHeaders);
        RedactedQueryParametersText = WebApiSupport.JoinList(saved.Logging.RedactedQueryParameters);
    }

    protected override Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        SoapClient client = CreateClient(Capture());
        // 先订阅，再读取可用性（与 REST 页相同：健康检查在创建时就开始探测）。
        client.AvailabilityChanged += (_, e) => PostToUi(() => OnAvailabilityChanged(e));
        client.RequestSending += (_, e) => PostToUi(() => AddTimeline("发送", e.Attempt, string.Empty, $"{e.Method} {e.RequestUri}"));
        client.ResponseReceived += (_, e) => PostToUi(() => AddTimeline("响应", e.Attempt, WebApiSupport.StatusText(e.StatusCode), WebApiSupport.ResponseDetail(e)));
        client.RequestCompleted += (_, e) => PostToUi(() => AddTimeline("完成", e.Attempts, WebApiSupport.StatusText(e.StatusCode), WebApiSupport.CompletedDetail(e)));
        _client = client;
        AvailabilityText = WebApiSupport.AvailabilityText(client.Availability);
        State = SessionState.Connected;
        return Task.CompletedTask;
    }

    protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_client is not { } client)
        {
            ResetState();
            return;
        }

        _client = null;
        ResetState();
        await client.DisposeAsync().ConfigureAwait(true);
    }

    private SoapClientSettings Capture() => new(
        Url.Trim(),
        Version,
        WebApiSupport.Optional(DefaultNamespace),
        WebApiSupport.Optional(HealthCheckUrl),
        TimeoutMs,
        new WebApiAuthSettings(
            AuthScheme,
            WebApiSupport.Optional(UserNameVariable),
            WebApiSupport.Optional(PasswordVariable),
            WebApiSupport.Optional(TokenVariable),
            ApiKeyHeader.Trim(),
            WebApiSupport.Optional(ApiKeyVariable)),
        new WebApiRetrySettings(RetryCount, RetryDelayMs, RetryUnsafeMethods),
        HealthCheckIntervalMs,
        OfflineThreshold,
        new WebApiLogSettings(
            LogBodies,
            WebApiSupport.ParseList(RedactedHeadersText),
            WebApiSupport.ParseList(RedactedQueryParametersText)));

    // 库拒绝配置（例如地址为空）时抛出 ArgumentException，这里转成请求无效，与其它页签一致。
    private SoapClient CreateClient(SoapClientSettings settings)
    {
        var config = new SoapClientConfig
        {
            Url = settings.Url,
            Version = settings.Version,
            DefaultNamespace = settings.DefaultNamespace,
            HealthCheckUrl = settings.HealthCheckUrl,
            Timeout = settings.Timeout,
            Auth = WebApiSupport.ToAuthOptions(settings.Auth),
            Retry = WebApiSupport.ToRetryOptions(settings.Retry),
            HealthCheckInterval = settings.HealthCheckInterval,
            OfflineThreshold = settings.OfflineThreshold,
            Logging = WebApiSupport.ToLogOptions(settings.Logging),
        };
        WebApiComponents? components = _httpHandler is null ? null : new WebApiComponents { HttpHandler = _httpHandler };

        try
        {
            return new SoapClient(config, new Logger<SoapClient>(SessionLoggerFactory), components);
        }
        catch (ArgumentException ex)
        {
            throw new CommFailureException(CommResult.Fail(ex.Message, CommErrorKind.InvalidRequest));
        }
    }

    private async Task SendAsync()
    {
        if (_client is not { } client)
        {
            RequestMessage = NotConnectedMessage;
            return;
        }

        string action = SoapAction.Trim();
        if (action.Length == 0)
        {
            RequestMessage = "SOAPAction 不能为空。";
            return;
        }

        XElement body;
        try
        {
            body = XElement.Parse(BodyXml);
        }
        catch (XmlException ex)
        {
            RequestMessage = $"正文不是有效的 XML：{ex.Message}";
            return;
        }

        RequestMessage = string.Empty;
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        WebApiResult<XElement> result = await client.InvokeRawAsync(action, body).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "调用", action, clock.Elapsed, result));
        ResponseStatus = WebApiSupport.StatusText(result.StatusCode);
        ResponseRemoteCode = result.RemoteCode ?? "—";
        ResponseErrorKind = result.IsSuccess ? "成功" : CommErrorKindNames.Of(result.ErrorKind);
        ResponseMessage = result.IsSuccess ? string.Empty : result.ErrorMessage ?? string.Empty;
        ResponseXml = result.IsSuccess && result.Data is { } element ? element.ToString() : string.Empty;
    }

    private void OnAvailabilityChanged(WebApiAvailabilityChangedEventArgs e)
    {
        AvailabilityText = WebApiSupport.AvailabilityText(e.CurrentAvailability);
        AddTimeline(
            "可用性",
            0,
            string.Empty,
            $"{WebApiSupport.AvailabilityText(e.PreviousAvailability)} → {WebApiSupport.AvailabilityText(e.CurrentAvailability)}");
    }

    private void AddTimeline(string eventName, int attempt, string status, string detail) =>
        Timeline.InsertNewest(
            WebApiSupport.TimelineRow(Dependencies.Clock.GetLocalNow(), eventName, attempt, status, detail),
            MaxTimelineRows);

    private void ResetState()
    {
        State = SessionState.NotConnected;
        AvailabilityText = WebApiSupport.AvailabilityText(WebApiAvailability.Unknown);
    }

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(State))
        {
            return;
        }

        OnPropertyChanged(nameof(IsLinked));
        SendCommand.NotifyCanExecuteChanged();
    }
}
