using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://*:5005");

var app = builder.Build();

app.MapGet("/", () => "Hello World");
app.MapGet("/api/hello", () => Results.Json(new { message = "Hello from C#!" }));

app.Run();