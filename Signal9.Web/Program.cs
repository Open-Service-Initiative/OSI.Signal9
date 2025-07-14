using Signal9.Web.Services;
using Signal9.Shared.Configuration;
using Signal9.Shared.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Add Signal9 data services (Entity Framework, Azure Tables)
builder.Services.AddSignal9Data(builder.Configuration);

// Register HTTP client for calling Azure Functions (legacy support)
builder.Services.AddHttpClient<IDashboardService, DashboardService>();

// Register services  
builder.Services.AddScoped<IDashboardService, DashboardService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// Add health check endpoint
app.MapHealthChecks("/health");

app.MapRazorPages();
app.MapBlazorHub();

// Map SignalR hub
app.MapHub<AgentHub>("/hubs/agent");

app.MapFallbackToPage("/_Host");

app.Run();
