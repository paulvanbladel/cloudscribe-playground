using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using Serilog.Sinks.MSSqlServer;
using System.Diagnostics;


// Query the Log
// SELECT 
// 	Properties.value('(/properties/property[@key="myDto"]/structure[@type="MyDto"]/property[@key="FirstName"])[1]', 'nvarchar(50)') AS FirstName,
// 	Properties.value('(/properties/property[@key="RId"])[1]', 'nvarchar(50)') AS RId,
// 	*
// FROM SeriLog
// --WHERE MessageTemplate = 'Contact {@contact} added to cache with key {@cacheKey}'
// --	AND Properties.value('(/properties/property[@key="contact"]/structure[@type="Contact"]/property[@key="ContactId"])[1]', 'nvarchar(max)') = 'f7d10f53-4c11-44f4-8dce-d0e0e22cb6ab'  
// where Level='Information'

namespace Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Console.Title = "Api";

            BuildWebHost(args).Run();
        }

        public static IWebHost BuildWebHost(string[] args)
        {
            Serilog.Debugging.SelfLog.Enable(d=>
            {
                Debug.WriteLine(d);
            });
            string tableName = "SeriLog";
            var columnOptions = new ColumnOptions();
            Log.Logger = new LoggerConfiguration()
               .MinimumLevel.Verbose()
               .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
               .MinimumLevel.Override("System", LogEventLevel.Warning)
               .MinimumLevel.Override("Microsoft.AspNetCore.Authentication", LogEventLevel.Information)
               .Enrich.FromLogContext()
               .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] [{RId}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}{NewLine}", theme: AnsiConsoleTheme.Literate)
               .CreateLogger();

            return WebHost.CreateDefaultBuilder(args)
                .UseStartup<Startup>()
                .ConfigureLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.AddSerilog();
                })
                .Build();
        }
    }
}
