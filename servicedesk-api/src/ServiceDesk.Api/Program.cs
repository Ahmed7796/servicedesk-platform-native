using Serilog;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Api;
using ServiceDesk.Application;
using ServiceDesk.Infrastructure;
using ServiceDesk.Infrastructure.Messaging;
using ServiceDesk.Infrastructure.Persistence;

// Container health probe: `dotnet ServiceDesk.Api.dll --healthcheck` exits 0 or 1 without starting the host.
if (await HealthProbe.RunAsync(args) is { } probeExitCode) return probeExitCode;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console());

// Layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOutboxPublisher(builder.Configuration); // needs RabbitMq__* settings in every environment

// Api
builder.Services.AddApiControllers();
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddOpenApiWithBearer();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

var app = builder.Build();

// Dev convenience only: outside Development, migrations run as a deployment step and demo data is never seeded.
if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(db);

    app.MapOpenApi();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "ServiceDesk API v1"));
}
else
{
    // In Docker the API listens on HTTP 8080; TLS is terminated by a reverse proxy in the deployed demo.
    app.UseHttpsRedirection();
}

app.UseSerilogRequestLogging();
app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

await app.RunAsync();
return 0;