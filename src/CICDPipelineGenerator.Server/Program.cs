using System.Text.Json;
using System.Text.Json.Serialization;
using CICDPipelineGenerator.Infrastructure;
using CICDPipelineGenerator.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    // A tool for this computer: it listens only here.
    builder.WebHost.UseUrls("http://localhost:5090");
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPipelineEndpoints();
app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
