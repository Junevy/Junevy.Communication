using System.IO;
using System.Reflection;
using System.Text.Json.Serialization;
using Junevy.Communication.DebugAssistant.Workspace;

namespace Junevy.Communication.DebugAssistant.Tests.Workspace;

public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "junevy-da-store-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string PathOf(string name = "workspace.json") => Path.Combine(_directory, name);

    [Fact]
    public void SaveThenLoad_RoundTripsTheTabsTitlesAndSelection()
    {
        var store = new WorkspaceStore(PathOf());
        var saved = new WorkspaceDocument(
            WorkspaceDocument.CurrentSchemaVersion,
            [new WorkspaceSession("Modbus", "Modbus #1", new PlaceholderSettings()), new WorkspaceSession("Mc", "产线 A", new PlaceholderSettings())],
            SelectedIndex: 1);

        store.Save(saved);
        WorkspaceLoadResult loaded = store.Load();

        Assert.Null(loaded.Notice);
        Assert.Equal(saved.SchemaVersion, loaded.Document.SchemaVersion);
        Assert.Equal(saved.Sessions.Select(s => (s.Kind, s.Title)), loaded.Document.Sessions.Select(s => (s.Kind, s.Title)));
        Assert.All(loaded.Document.Sessions, s => Assert.IsType<PlaceholderSettings>(s.Settings));
        Assert.Equal(saved.SelectedIndex, loaded.Document.SelectedIndex);
    }

    [Fact]
    public void MissingFile_LoadsAnEmptyWorkspace_WithoutANotice()
    {
        WorkspaceLoadResult loaded = new WorkspaceStore(PathOf()).Load();

        Assert.Empty(loaded.Document.Sessions);
        Assert.Null(loaded.Notice);
    }

    [Fact]
    public void CorruptFile_IsKeptAsBak_AndAnEmptyWorkspaceIsReturned()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathOf(), "{ not json");

        WorkspaceLoadResult loaded = new WorkspaceStore(PathOf()).Load();

        Assert.Empty(loaded.Document.Sessions);
        Assert.NotNull(loaded.Notice);
        Assert.Contains("workspace.json.bak", loaded.Notice, StringComparison.Ordinal);
        Assert.False(File.Exists(PathOf()));
        Assert.Equal("{ not json", File.ReadAllText(PathOf("workspace.json.bak")));
    }

    [Fact]
    public void SchemaVersionMismatch_IsKeptAsBak()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathOf(), """{ "schemaVersion": 99, "sessions": [], "selectedIndex": null }""");

        WorkspaceLoadResult loaded = new WorkspaceStore(PathOf()).Load();

        Assert.Empty(loaded.Document.Sessions);
        Assert.Contains("版本", loaded.Notice, StringComparison.Ordinal);
        Assert.True(File.Exists(PathOf("workspace.json.bak")));
    }

    [Fact]
    public void Save_WhenTheTemporaryFileCannotBeWritten_KeepsTheOriginalFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathOf(), "ORIGINAL");
        Directory.CreateDirectory(PathOf("workspace.json.tmp"));   // 临时文件的位置被占用，写入会失败
        var store = new WorkspaceStore(PathOf());

        // 目录占用临时文件名时，.NET 抛出 UnauthorizedAccessException；其他平台可能是 IOException。
        Assert.ThrowsAny<Exception>(() => store.Save(WorkspaceDocument.Empty));

        Assert.Equal("ORIGINAL", File.ReadAllText(PathOf()));
    }

    [Fact]
    public void PasswordFields_OfEverySettingsType_AreNotWritten()
    {
        // 当前没有带密码的参数类型；每种协议加入密码字段时，这里会强制它标记 [JsonIgnore]（计划 DA-D11）。
        IEnumerable<Type> settingsTypes = typeof(SessionSettings).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(SessionSettings)));

        foreach (Type settingsType in settingsTypes)
        {
            // 以 Variable 结尾的属性保存的是环境变量的名称（口令本身只在创建时从环境变量读取），名称不是口令，可以写入文件。
            foreach (PropertyInfo property in settingsType.GetProperties().Where(p =>
                p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) && !p.Name.EndsWith("Variable", StringComparison.OrdinalIgnoreCase)))
            {
                Assert.NotNull(property.GetCustomAttribute<JsonIgnoreAttribute>());
            }
        }
    }
}
