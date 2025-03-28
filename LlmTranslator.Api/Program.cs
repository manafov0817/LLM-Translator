using LlmTranslator.Api.Services;
using LlmTranslator.Api.Utils;
using Microsoft.AspNetCore.WebSockets;
using WebSocketManager = LlmTranslator.Api.WebSockets.WebSocketManager;

var builder = WebApplication.CreateBuilder(args);

// Print language configuration during startup for debugging
Console.WriteLine($"CallingPartyLanguage: {builder.Configuration["CallingPartyLanguage"] ?? Environment.GetEnvironmentVariable("CallingPartyLanguage") ?? "Not set, using default"}");
Console.WriteLine($"CalledPartyLanguage: {builder.Configuration["CalledPartyLanguage"] ?? Environment.GetEnvironmentVariable("CalledPartyLanguage") ?? "Not set, using default"}");

// Configure JSON options (important for JsonElement serialization/deserialization)
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = null;
    options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddEndpointsApiExplorer();

// Register services
builder.Services.AddSingleton<YardMaster>();
builder.Services.AddSingleton<WebSocketManager>();

// Register translation services
// Choose which one to use based on configuration
if (!string.IsNullOrEmpty(builder.Configuration["OpenAI:ApiKey"]))
{
    Console.WriteLine("Using OpenAI translation service");
    builder.Services.AddSingleton<ITranslationService, OpenAiTranslationService>();
}
else if (!string.IsNullOrEmpty(builder.Configuration["Ultravox:ApiKey"]))
{
    Console.WriteLine("Using Ultravox translation service");
    builder.Services.AddSingleton<ITranslationService, UltravoxTranslationService>();
}
else
{
    throw new Exception("Either OpenAI:ApiKey or Ultravox:ApiKey must be configured");
}

// Add Jambonz service with HttpClient
builder.Services.AddHttpClient();
builder.Services.AddSingleton<JambonzService>();

// Add WebSockets with increased buffer size for better audio handling
builder.Services.AddWebSockets(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(120);
    options.ReceiveBufferSize = 16 * 1024; // Increased to 16KB buffer size
});

// Configure logging to include more details
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Debug);

// Make IConfiguration available to the CallSession
builder.Services.AddSingleton<IConfiguration>(builder.Configuration);

// Modify YardMaster factory method to include IConfiguration
var app = builder.Build();

app.UseCors(policy =>
{
    policy.AllowAnyOrigin()
          .AllowAnyMethod()
          .AllowAnyHeader();
});

app.UseHttpsRedirection();
app.UseAuthorization();

// Configure WebSockets
app.UseWebSockets();

// Map WebSocket endpoint for audio streaming
app.Map("/audio-stream", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var webSocketManager = app.Services.GetRequiredService<WebSocketManager>();
        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        await webSocketManager.HandleWebSocketConnection(webSocket, context);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

app.MapControllers();

app.Run();