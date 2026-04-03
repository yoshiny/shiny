using Serilog;
using System.ComponentModel.DataAnnotations;

using Shiny.Core;
using Shiny.Hosting;
using Shiny.Bootstrap;

namespace Shiny {
    public class Program {
        static void Main(string[] args) {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.WithThreadId()
                .WriteTo.Console(outputTemplate : "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [Thread:{ThreadId}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [Thread:{ThreadId}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            LaunchServer();
        }

        static void LaunchServer() {
            var lobbyServer1 = new Server(new LobbyServerBootstrap(), 20);

            var processHost = new ServerProcessHost();
            processHost.Add(new ServerHost(lobbyServer1, new ServerHostOptions { ThreadName = "Lobby-1" }));

            processHost.StartAll();
            processHost.JoinAll();
        }

        private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) {
            Log.Fatal(e.Exception, "TaskScheduler_UnobservedTaskException");
        }
    }
}
