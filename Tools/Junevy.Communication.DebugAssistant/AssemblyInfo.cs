using System.Runtime.CompilerServices;

// DA-D12 的可配置部件是 internal 的纯逻辑；只向本应用的测试项目开放。模拟器项目不加任何 InternalsVisibleTo（DA-D14）。
[assembly: InternalsVisibleTo("Junevy.Communication.DebugAssistant.Tests")]
