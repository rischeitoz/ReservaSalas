using Microsoft.AspNetCore.HttpOverrides;
using SalaReservas.Models;
using SalaReservas.Services;

var builder = WebApplication.CreateBuilder(args);

// Secretos: appsettings < variables de entorno (Graph__ClientSecret) < user-secrets en desarrollo
builder.Services.Configure<GraphOptions>(builder.Configuration.GetSection("Graph"));
builder.Services.Configure<RoomOptions>(builder.Configuration.GetSection("Room"));
builder.Services.AddSingleton<IRoomService, GraphRoomService>();
builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); });

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(a => a.Run(c => c.Response.WriteAsync("Error interno.")));
    app.UseHsts();
}

app.UseHttpsRedirection();

// Clave de acceso de la tablet (opcional pero recomendada).
// Abre una vez https://servidor/?key=TU_CLAVE y se guarda en una cookie segura de 1 año.
var kioskKey = app.Configuration["Kiosk:AccessKey"];
if (!string.IsNullOrEmpty(kioskKey))
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Query["key"] == kioskKey)
        {
            ctx.Response.Cookies.Append("kiosk", kioskKey, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict,
                Expires = DateTimeOffset.UtcNow.AddYears(1)
            });
            ctx.Response.Redirect(ctx.Request.Path);
            return;
        }
        if (ctx.Request.Cookies["kiosk"] != kioskKey)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Acceso no autorizado.");
            return;
        }
        await next();
    });
}

app.UseStaticFiles();
app.UseRouting();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
