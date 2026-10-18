using Junevy.Communication.Modbus.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Windows;

namespace Junevy.Communication.Test
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static new App Current => (App)Application.Current;
        public IServiceProvider Provider { get; private set; }


        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            InitialLogger();
            InitialContainer();

            Log.Logger.Information("Logger Initialized.");

            MainWindow w = Provider.GetRequiredService<MainWindow>();
            w.Show();
        }

        private void InitialLogger()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    path: "logs/app-.log",
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] {Message:1j}{NewLine}",
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 5 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 31,
                    encoding: System.Text.Encoding.UTF8)
                .CreateLogger();
        }

        private void InitialContainer()
        {
            var container = new ServiceCollection();

            container.AddSingleton<MainWindow>();
            container.AddSingleton<MainWindowViewModel>();
            //container
            container.AddLogging( builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog();
            });

            container.AddModbus();

            this.Provider = container.BuildServiceProvider();
        }

        //overri
    }

}
