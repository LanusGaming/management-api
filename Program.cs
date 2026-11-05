using Management.Api.Data;
using Management.Api.DTOs;
using Management.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddDbContext<AppDbContext>(options =>
   options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSingleton<MetricsBroadcaster<List<ContainerSummary>>>();
builder.Services.AddSingleton<MetricsBroadcaster<HostSummary>>();
builder.Services.AddSingleton<FileIoReader>();
builder.Services.AddHostedService<MetricsCollector>();
builder.Services.AddHostedService<HostMetricsCollector>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Jwt:Authority"];
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Authority"],
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if ((ctx.Request.Path.StartsWithSegments("/api/containers/stream")
                        || ctx.Request.Path.StartsWithSegments("/api/host/stream"))
                    && ctx.Request.Query.TryGetValue("access_token", out var token))
                {
                    ctx.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();



using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Migrations copy whole history tiers (millions of rows), which can take longer than the 30 s default
    db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
    db.Database.Migrate();
}

app.Run();