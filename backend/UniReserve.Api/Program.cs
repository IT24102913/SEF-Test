using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using UniReserve.Api.Data;
using UniReserve.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Explicitly configure Kestrel to bind IPv4-only (IPAddress.Any = 0.0.0.0)
// This bypasses AnyIPListenOptions which always attempts an IPv6 [::] bind
// and causes SocketException (98) on containers where IPv6 port is pre-claimed.
var rawPort = Environment.GetEnvironmentVariable("PORT")
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")
    ?? "8080";
if (!int.TryParse(rawPort, out var listenPort)) listenPort = 8080;

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Listen(IPAddress.Any, listenPort);
});
Console.WriteLine($"[KESTREL] Listening on http://0.0.0.0:{listenPort} (IPv4 only)");

// Add Database Context
var connectionString = GetNormalizedPostgresConnectionString(builder.Configuration);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// Add Custom Application Services
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<IEquipmentService, EquipmentService>();
builder.Services.AddScoped<IReservationService, ReservationService>();

// Add JWT Authentication
var jwtKey = builder.Configuration["Jwt:Key"] ?? "UniReserve_Super_Secret_Secure_Key_2026_For_JWT_Auth_Tokens_Min32Bytes!";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "UniReserveApi";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "UniReserveClient";

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ValidateIssuer = true,
        ValidIssuer = jwtIssuer,
        ValidateAudience = true,
        ValidAudience = jwtAudience,
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorization();

// Add CORS Policy for frictionless frontend & online tunnel access
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Add Controllers with JSON formatting
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

// Add Swagger / OpenAPI with JWT Authorization Support
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "UniReserve API",
        Version = "v1",
        Description = "University Equipment Reservation REST API - Full-Stack Hackathon Solution"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and your token.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Auto-migrate and seed database on startup
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        await DbInitializer.InitializeAsync(context);
        app.Logger.LogInformation("PostgreSQL Database initialized and seeded successfully.");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "An error occurred while seeding the database.");
    }
}

// Enable Swagger UI in development & production for easy testing & judging
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "UniReserve API v1");
    c.RoutePrefix = "swagger";
});

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "UniReserve API", timestamp = DateTime.UtcNow }));

app.MapControllers();

app.Run();

static string GetNormalizedPostgresConnectionString(IConfiguration config)
{
    // Check all possible environment variable sources
    var connStr = Environment.GetEnvironmentVariable("DATABASE_URL")
        ?? Environment.GetEnvironmentVariable("POSTGRES_URL")
        ?? Environment.GetEnvironmentVariable("DATABASE_PUBLIC_URL")
        ?? Environment.GetEnvironmentVariable("DATABASE_PRIVATE_URL")
        ?? config.GetConnectionString("DefaultConnection")
        ?? config["DATABASE_URL"]
        ?? config["POSTGRES_URL"];

    // Check individual Railway PG environment variables
    var pgHost = Environment.GetEnvironmentVariable("PGHOST");
    var pgPort = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";
    var pgDb = Environment.GetEnvironmentVariable("PGDATABASE") ?? "railway";
    var pgUser = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres";
    var pgPass = Environment.GetEnvironmentVariable("PGPASSWORD");

    if (!string.IsNullOrEmpty(pgHost) && !string.IsNullOrEmpty(pgPass))
    {
        Console.WriteLine($"[DATABASE CONFIG] Using Railway PGHOST environment variables. Connecting to Host: {pgHost}, Port: {pgPort}, Database: {pgDb}, User: {pgUser}");
        return $"Host={pgHost};Port={pgPort};Database={pgDb};Username={pgUser};Password={pgPass};Include Error Detail=true;SSL Mode=Prefer";
    }

    var isDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
    var defaultHost = isDocker ? "host.docker.internal" : "localhost";

    if (string.IsNullOrEmpty(connStr))
    {
        Console.WriteLine($"[DATABASE CONFIG] No remote DATABASE_URL found. Falling back to {defaultHost}:5432.");
        return $"Host={defaultHost};Port=5432;Database=unireserve_db;Username=postgres;Password=postgres123;Include Error Detail=true";
    }

    if (connStr.StartsWith("postgres://") || connStr.StartsWith("postgresql://"))
    {
        try
        {
            var uri = new Uri(connStr);
            var userInfo = uri.UserInfo.Split(':');
            var username = userInfo.Length > 0 ? userInfo[0] : "postgres";
            var password = userInfo.Length > 1 ? userInfo[1] : "";
            var database = uri.AbsolutePath.TrimStart('/');
            var port = uri.Port > 0 ? uri.Port : 5432;
            Console.WriteLine($"[DATABASE CONFIG] Parsed Railway URI -> Host: {uri.Host}, Port: {port}, Database: {database}, User: {username}");
            return $"Host={uri.Host};Port={port};Database={database};Username={username};Password={password};Include Error Detail=true;SSL Mode=Prefer";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DATABASE CONFIG] Warning: URI parse error ({ex.Message}), passing connection string as is.");
            return connStr;
        }
    }

    Console.WriteLine($"[DATABASE CONFIG] Using standard ADO.NET connection string.");
    return connStr;
}
