using LightsOff.App.Api;
using LightsOff.App.Components;
using LightsOff.App.Services;
using LightsOff.App.Workers;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Configuration;
using LightsOff.Infrastructure;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var stateRoot = new LightsOffOptions().StateRoot;
var logDir = Path.Combine(stateRoot, "logs");
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDir, "app-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting lights-off App");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(services.GetRequiredService<IOptions<LightsOffOptions>>().Value.StateRoot, "logs", "app-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            shared: true,
            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}"));

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddScoped<ToastService>();
    builder.Services.AddSingleton<MarkdownRenderer>();
    builder.Services.AddSingleton<TestRunnerService>();
    builder.Services.AddSingleton<StepRunnerService>();
    builder.Services.AddSingleton<IStepSessionSink>(sp => sp.GetRequiredService<StepRunnerService>());
    builder.Services.AddLightsOffInfrastructure(builder.Configuration);

    builder.Services.AddHostedService<WorkspaceHealthWorker>();

    var app = builder.Build();

    await app.Services.EnsureDatabaseCreatedAsync();

    app.UseSerilogRequestLogging();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    app.MapLightsOffApi();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "lights-off App terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
