using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.OpenApi;
using PDVMottainai.Api.Data.Local;
using PDVMottainai.Api.Services;
using PDVMottainai.Api.OpenApi;

var builder = WebApplication.CreateBuilder(args);

var port = builder.Configuration.GetValue("Api:Port", 5097);
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Mottainai POS API",
        Version = "v1",
        Description = "Local-first point-of-sale API for authentication, product lookup, sales, shifts, approvals, and offline synchronization."
    });
    options.AddSecurityDefinition(PosBearerSecurityOperationFilter.PosBearerScheme, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "POS access token",
        Description = "Use the local access token returned by POST /auth/login or POST /auth/offline-login for protected POS operations."
    });
    options.AddSecurityDefinition(PosBearerSecurityOperationFilter.FirebaseBearerScheme, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "Firebase ID token",
        Description = "Use a Firebase ID token for POST /auth/login and manager approval decisions."
    });
    options.OperationFilter<PosBearerSecurityOperationFilter>();
});
builder.Services.AddAuthentication("PosBearer").AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, PDVMottainai.Api.Security.PosBearerHandler>("PosBearer", _ => { });
var firebaseProjectId = builder.Configuration["Firebase:ProjectId"];
builder.Services.AddAuthentication().AddJwtBearer("Firebase", options =>
{
    if (!string.IsNullOrWhiteSpace(firebaseProjectId))
    {
        options.Authority = $"https://securetoken.google.com/{firebaseProjectId}";
        options.Audience = firebaseProjectId;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;
    }
});
builder.Services.AddAuthorization();
builder.Services.AddSingleton<LocalStore>();
builder.Services.AddSingleton<OutboxRepository>();
builder.Services.AddSingleton<OfflineCacheRepository>();
builder.Services.AddSingleton<PDVMottainai.Api.Data.PostgresRequestScopeFactory>();
builder.Services.AddHostedService<LocalStoreInitializer>();
builder.Services.AddHostedService<OutboxSyncService>();
builder.Services.AddHttpClient<PosApprovalCloudClient>();
builder.Services.AddDataProtection()
    .SetApplicationName("Mottainai.PDV.Api")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mottainai", "PDV", "DataProtection")))
    .ProtectKeysWithDpapi();

var app = builder.Build();

app.Use(async (context, next) =>
{
    try { await next(); }
    catch (IdempotencyConflictException ex)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { error = "idempotency_key_conflict", message = ex.Message });
    }
});

app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Mottainai POS API v1");
        options.RoutePrefix = "swagger";
    });
}
app.MapControllers();

app.Run();

public partial class Program;
