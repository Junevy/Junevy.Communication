using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Rest;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Protocols.WebApi;

/// <summary>
/// REST 客户端页（计划第 7.9 节）：连接选项（地址、超时、认证只填环境变量名、重试、业务信封、健康检查、日志脱敏）；
/// 请求构造（方法、路径、查询参数、请求头、正文、幂等键）；响应（HTTP 状态码、业务码、错误种类与数据）；
/// 事件时间线（逐次尝试）与可用性。库的事件先经 <see cref="SessionViewModel.PostToUi"/> 回到界面线程。
/// </summary>
public sealed partial class RestClientSessionViewModel : SessionViewModel
{
    private const int MaxTimelineRows = 200;
    private const string NotConnectedMessage = "页签没有连接，请先连接。";

    private WebApiClient? _client;

    public RestClientSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies)
    {
        SendCommand = new AsyncRelayCommand(SendAsync, () => IsLinked);
        AddQueryCommand = new RelayCommand(AddQuery);
        RemoveQueryCommand = new RelayCommand<WebApiNameValueRow?>(RemoveQuery);
        AddHeaderCommand = new RelayCommand(AddHeader);
        RemoveHeaderCommand = new RelayCommand<WebApiNameValueRow?>(RemoveHeader);
        ClearTimelineCommand = new RelayCommand(Timeline.Clear);
        PropertyChanged += OnOwnPropertyChanged;
    }

    public IReadOnlyList<string> MethodChoices { get; } = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    public IReadOnlyList<WebApiAuthScheme> AuthChoices { get; } = Enum.GetValues<WebApiAuthScheme>();

    /// <summary>查询参数（名称可以重复，发送时同名取最后一个）。</summary>
    public ObservableCollection<WebApiNameValueRow> QueryRows { get; } = [];

    /// <summary>请求头，覆盖配置里同名的默认头。</summary>
    public ObservableCollection<WebApiNameValueRow> HeaderRows { get; } = [];

    /// <summary>调用时间线，最新的在最前。</summary>
    public ObservableCollection<WebApiTimelineRow> Timeline { get; } = [];

    /// <summary>已创建客户端；只有这时才能发送。</summary>
    public bool IsLinked => State == SessionState.Connected;

    [ObservableProperty]
    private string _baseUrl = "http://127.0.0.1:8080/api";

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
    private string _healthCheckPath = string.Empty;

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

    [ObservableProperty]
    private string _method = "POST";

    [ObservableProperty]
    private string _path = "reports";

    [ObservableProperty]
    private string _queryName = string.Empty;

    [ObservableProperty]
    private string _queryValue = string.Empty;

    [ObservableProperty]
    private WebApiNameValueRow? _selectedQuery;

    [ObservableProperty]
    private string _headerName = string.Empty;

    [ObservableProperty]
    private string _headerValue = string.Empty;

    [ObservableProperty]
    private WebApiNameValueRow? _selectedHeader;

    [ObservableProperty]
    private string _bodyText = string.Empty;

    [ObservableProperty]
    private string _contentType = "application/json";

    /// <summary>幂等键：设置后允许重试 POST、PUT、PATCH 与 DELETE，对端据此去重（计划 7.9）。</summary>
    [ObservableProperty]
    private string _idempotencyKey = string.Empty;

    /// <summary>以文本显示响应（不解析 JSON，也不取业务信封的数据）。</summary>
    [ObservableProperty]
    private bool _responseAsText;

    [ObservableProperty]
    private string _responseStatus = "—";

    [ObservableProperty]
    private string _responseRemoteCode = "—";

    [ObservableProperty]
    private string _responseErrorKind = "—";

    [ObservableProperty]
    private string _responseMessage = string.Empty;

    [ObservableProperty]
    private string _responseData = string.Empty;

    [ObservableProperty]
    private string _availabilityText = WebApiSupport.AvailabilityText(WebApiAvailability.Unknown);

    /// <summary>请求构造的说明（输入不合法、未连接）。</summary>
    [ObservableProperty]
    private string _requestMessage = string.Empty;

    public IAsyncRelayCommand SendCommand { get; }

    public IRelayCommand AddQueryCommand { get; }

    public IRelayCommand<WebApiNameValueRow?> RemoveQueryCommand { get; }

    public IRelayCommand AddHeaderCommand { get; }

    public IRelayCommand<WebApiNameValueRow?> RemoveHeaderCommand { get; }

    public IRelayCommand ClearTimelineCommand { get; }

    public override SessionSettings CaptureSettings() => Capture();

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not RestClientSettings saved)
        {
            throw new ArgumentException("The settings do not belong to a REST client page.", nameof(settings));
        }

        BaseUrl = saved.BaseUrl;
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
        EnvelopeEnabled = saved.Envelope.Enabled;
        CodePath = saved.Envelope.CodePath;
        MessagePath = saved.Envelope.MessagePath;
        DataPath = saved.Envelope.DataPath;
        SuccessCodesText = WebApiSupport.JoinList(saved.Envelope.SuccessCodes);
        HealthCheckPath = saved.HealthCheckPath ?? string.Empty;
        HealthCheckIntervalMs = saved.HealthCheckInterval;
        OfflineThreshold = saved.OfflineThreshold;
        LogBodies = saved.Logging.LogBodies;
        RedactedHeadersText = WebApiSupport.JoinList(saved.Logging.RedactedHeaders);
        RedactedQueryParametersText = WebApiSupport.JoinList(saved.Logging.RedactedQueryParameters);
    }

    protected override Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        WebApiClient client = CreateClient(Capture());
        // 先订阅，再读取可用性：健康检查在创建时就开始探测，之后的变化只能通过事件得到（README "Availability"）。
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

    private RestClientSettings Capture() => new(
        BaseUrl.Trim(),
        TimeoutMs,
        new WebApiAuthSettings(
            AuthScheme,
            WebApiSupport.Optional(UserNameVariable),
            WebApiSupport.Optional(PasswordVariable),
            WebApiSupport.Optional(TokenVariable),
            ApiKeyHeader.Trim(),
            WebApiSupport.Optional(ApiKeyVariable)),
        new WebApiRetrySettings(RetryCount, RetryDelayMs, RetryUnsafeMethods),
        new WebApiEnvelopeSettings(
            EnvelopeEnabled,
            CodePath.Trim(),
            MessagePath.Trim(),
            DataPath.Trim(),
            WebApiSupport.ParseList(SuccessCodesText)),
        WebApiSupport.Optional(HealthCheckPath),
        HealthCheckIntervalMs,
        OfflineThreshold,
        new WebApiLogSettings(
            LogBodies,
            WebApiSupport.ParseList(RedactedHeadersText),
            WebApiSupport.ParseList(RedactedQueryParametersText)));

    // 库拒绝配置（例如地址为空或绝对路径）时抛出 ArgumentException，这里转成请求无效，与其它页签一致。
    private WebApiClient CreateClient(RestClientSettings settings)
    {
        var config = new WebApiClientConfig
        {
            BaseUrl = settings.BaseUrl,
            Timeout = settings.Timeout,
            Auth = WebApiSupport.ToAuthOptions(settings.Auth),
            Retry = WebApiSupport.ToRetryOptions(settings.Retry),
            Envelope = WebApiSupport.ToEnvelopeOptions(settings.Envelope),
            HealthCheckPath = settings.HealthCheckPath,
            HealthCheckInterval = settings.HealthCheckInterval,
            OfflineThreshold = settings.OfflineThreshold,
            Logging = WebApiSupport.ToLogOptions(settings.Logging),
        };

        try
        {
            return new WebApiClient(config, new Logger<WebApiClient>(SessionLoggerFactory));
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

        if (!TryBuildRequest(out WebApiRequest? request, out string? problem) || request is null)
        {
            RequestMessage = problem ?? string.Empty;
            return;
        }

        RequestMessage = string.Empty;
        string target = $"{request.Method.Method} {request.Path}";
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        if (ResponseAsText)
        {
            WebApiResult<string> result = await client.SendAsync<string>(request).ConfigureAwait(true);
            RecordOperation(OperationRecords.From(started, "请求", target, clock.Elapsed, result));
            ShowResponse(result.IsSuccess, result.StatusCode, result.RemoteCode, result.ErrorKind, result.ErrorMessage, result.Data);
        }
        else
        {
            WebApiResult<JsonElement> result = await client.SendAsync<JsonElement>(request).ConfigureAwait(true);
            RecordOperation(OperationRecords.From(started, "请求", target, clock.Elapsed, result));
            // 成功但没有数据（例如 204）时 Data 是默认值，ValueKind 为 Undefined。
            string? data = result.IsSuccess && result.Data.ValueKind != JsonValueKind.Undefined ? result.Data.GetRawText() : null;
            ShowResponse(result.IsSuccess, result.StatusCode, result.RemoteCode, result.ErrorKind, result.ErrorMessage, data);
        }
    }

    private bool TryBuildRequest(out WebApiRequest? request, out string? problem)
    {
        request = null;
        problem = null;
        string path = Path.Trim();
        if (path.Length == 0)
        {
            problem = "路径不能为空。";
            return false;
        }

        string contentType = ContentType.Trim();
        request = new WebApiRequest
        {
            Method = new HttpMethod(Method),
            Path = path,
            Query = QueryRows.Count == 0
                ? null
                : QueryRows.GroupBy(row => row.Name).ToDictionary(group => group.Key, group => (string?)group.Last().Value),
            Headers = HeaderRows.Count == 0
                ? null
                : HeaderRows.GroupBy(row => row.Name).ToDictionary(group => group.Key, group => group.Last().Value),
            Body = BodyText.Length == 0 ? null : BodyText,
            ContentType = contentType.Length == 0 ? null : contentType,
            IdempotencyKey = WebApiSupport.Optional(IdempotencyKey),
        };
        return true;
    }

    private void ShowResponse(bool success, int? status, string? remoteCode, CommErrorKind kind, string? message, string? data)
    {
        ResponseStatus = WebApiSupport.StatusText(status);
        ResponseRemoteCode = remoteCode ?? "—";
        ResponseErrorKind = success ? "成功" : CommErrorKindNames.Of(kind);
        ResponseMessage = success ? string.Empty : message ?? string.Empty;
        ResponseData = WebApiSupport.Pretty(data);
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

    private void AddQuery()
    {
        string name = QueryName.Trim();
        if (name.Length == 0)
        {
            RequestMessage = "查询参数名不能为空。";
            return;
        }

        QueryRows.Add(new WebApiNameValueRow(name, QueryValue));
        QueryName = string.Empty;
        QueryValue = string.Empty;
    }

    private void RemoveQuery(WebApiNameValueRow? row)
    {
        if (row is not null)
        {
            QueryRows.Remove(row);
        }
    }

    private void AddHeader()
    {
        string name = HeaderName.Trim();
        if (name.Length == 0)
        {
            RequestMessage = "请求头名称不能为空。";
            return;
        }

        HeaderRows.Add(new WebApiNameValueRow(name, HeaderValue));
        HeaderName = string.Empty;
        HeaderValue = string.Empty;
    }

    private void RemoveHeader(WebApiNameValueRow? row)
    {
        if (row is not null)
        {
            HeaderRows.Remove(row);
        }
    }

    private void ResetState()
    {
        State = SessionState.NotConnected;
        AvailabilityText = WebApiSupport.AvailabilityText(WebApiAvailability.Unknown);
    }

    private void OnOwnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(State))
        {
            return;
        }

        OnPropertyChanged(nameof(IsLinked));
        SendCommand.NotifyCanExecuteChanged();
    }
}
