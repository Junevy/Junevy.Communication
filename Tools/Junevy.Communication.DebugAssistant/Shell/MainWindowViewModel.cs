using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.DebugAssistant.Services;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Controls.Controls.Menu;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Shell;

/// <summary>主窗口的视图模型：左侧协议树、右侧页签集合、工具栏命令。</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IThemeService _themes;
    private readonly ISessionFactory _sessionFactory;
    private readonly IConfirmService _confirm;
    private readonly ILogger<MainWindowViewModel> _logger;

    /// <summary>关闭一个页签时，等待会话释放的时限（计划 DA-D9）。</summary>
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>退出程序时，等待全部会话释放的总时限（计划 DA-D9）。</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    public MainWindowViewModel(
        IThemeService themes,
        ISessionFactory sessionFactory,
        IConfirmService confirm,
        ILogger<MainWindowViewModel> logger)
    {
        _themes = themes;
        _sessionFactory = sessionFactory;
        _confirm = confirm;
        _logger = logger;
        Tree = ProtocolTreeBuilder.Build();
        Sessions.CollectionChanged += OnSessionsChanged;
    }

    /// <summary>左侧协议树的根节点（分组）。</summary>
    public ObservableCollection<TreeMenuItem> Tree { get; }

    /// <summary>右侧的调试页签，按打开的顺序排列。</summary>
    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    /// <summary>是否有打开的页签；没有时右侧显示空状态提示。</summary>
    public bool HasSessions => Sessions.Count > 0;

    [ObservableProperty]
    private SessionViewModel? _selectedSession;

    [RelayCommand]
    private void ToggleTheme() => _themes.Toggle();

    /// <summary>双击左侧节点：叶节点打开一个新页签并选中它；分组节点不做任何事（展开与收起由树控件处理）。</summary>
    [RelayCommand]
    private void OpenSession(TreeMenuItem? item)
    {
        if (item is not ProtocolTreeItem { Descriptor: { } descriptor })
        {
            return;
        }

        SessionViewModel session = _sessionFactory.Create(descriptor.Kind);
        Sessions.Add(session);
        SelectedSession = session;
    }

    /// <summary>关闭页签：先等会话释放完成（限时 5 秒），再把它从集合中移除。释放失败时仍然移除，并写日志。</summary>
    [RelayCommand]
    private async Task CloseSessionAsync(SessionViewModel? session)
    {
        if (session is null || !Sessions.Contains(session))
        {
            return;
        }

        await ReleaseAsync(session);

        if (ReferenceEquals(SelectedSession, session))
        {
            int index = Sessions.IndexOf(session);
            SelectedSession = index + 1 < Sessions.Count ? Sessions[index + 1] : index > 0 ? Sessions[index - 1] : null;
        }

        Sessions.Remove(session);
    }

    /// <summary>
    /// 退出程序时调用（计划 DA-D9）：并发释放全部会话，总时限 10 秒。超时或失败的会话只写日志，不阻止退出。
    /// </summary>
    public async Task ShutdownAsync()
    {
        Task all = Task.WhenAll(Sessions.ToArray().Select(ReleaseAsync));
        try
        {
            await all.WaitAsync(ShutdownTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Not every session was released within {Seconds} seconds; exiting anyway.", ShutdownTimeout.TotalSeconds);
        }

        Sessions.Clear();
    }

    /// <summary>释放一个会话，限时 <see cref="ReleaseTimeout"/>；失败或超时都写日志，调用方照常继续。</summary>
    private async Task ReleaseAsync(SessionViewModel session)
    {
        try
        {
            await session.DisposeAsync().AsTask().WaitAsync(ReleaseTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Releasing the session '{Title}' took longer than {Seconds} seconds.", session.Title, ReleaseTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Releasing the session '{Title}' failed.", session.Title);
        }
    }

    [RelayCommand]
    private async Task CloseAllSessionsAsync()
    {
        if (Sessions.Count == 0)
        {
            return;
        }

        bool confirmed = await _confirm.ConfirmAsync("关闭全部页签", $"将关闭 {Sessions.Count} 个页签，页签中的未保存内容会丢失。");
        if (!confirmed)
        {
            return;
        }

        foreach (SessionViewModel session in Sessions.ToArray())
        {
            await CloseSessionAsync(session);
        }
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(HasSessions));
}
