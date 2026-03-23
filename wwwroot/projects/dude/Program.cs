using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using System.Text.Json; // For Results.Json

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel to listen on all network interfaces
builder.WebHost.ConfigureKestrel(options =>
{
    // Listen on port 5005 on ALL network interfaces
    options.ListenAnyIP(5005);
});

var app = builder.Build();

// Simple in-memory storage
var visitCounts = new Dictionary<string, int>();

// Homepage
app.MapGet("/", () => Results.Content("""
    <!DOCTYPE html>
    <html>
    <head>
        <title>Arcade Counter</title>
        <style>
            body { font-family: Arial; text-align: center; padding: 50px; background: #0b1022; color: white; }
            button { padding: 15px 30px; font-size: 20px; background: #5f7cff; border: none; color: white; border-radius: 10px; cursor: pointer; }
            #count { font-size: 48px; color: #ffd966; margin: 20px; }
        </style>
    </head>
    <body>
        <h1>🎮 Arcade Counter</h1>
        <p>You've visited this game:</p>
        <div id="count">0</div>
        <button onclick="trackVisit()">Play Game</button>
        <script>
            async function trackVisit() {
                let res = await fetch('/api/visit?game=dude');
                let data = await res.json();
                document.getElementById('count').innerText = data.count;
            }
            window.onload = trackVisit;
        </script>
    </body>
    </html>
    """, "text/html"));

// API endpoint
app.MapGet("/api/visit", (string game) =>
{
    if (!visitCounts.ContainsKey(game))
        visitCounts[game] = 0;
    
    visitCounts[game]++;
    
    return Results.Json(new { 
        game = game, 
        count = visitCounts[game],
        message = $"You've played {game} {visitCounts[game]} times!"
    });
});

app.Run();