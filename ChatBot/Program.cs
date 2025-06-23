using ChatBot.Services;

var builder = WebApplication.CreateBuilder(args);

// MVC + Services
builder.Services.AddControllersWithViews();
builder.Services.AddScoped<ChatGPTService>();
builder.Services.AddScoped<ChatDbService>();
builder.Services.AddHostedService<ScraperHostedService>();

// ✅ Add session services
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

var app = builder.Build();


app.UseStaticFiles();
app.UseRouting();

// ✅ Enable session middleware
app.UseSession();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Chat}/{action=Index}/{id?}"
);

app.Run();
