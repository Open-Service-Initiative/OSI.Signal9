using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OSI.Signal9.Web;
using OSI.Signal9.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ApiBaseUrl is empty in production, where Static Web Apps routes /api to the linked backend.
var apiBaseUrl = builder.Configuration["ApiBaseUrl"];
var apiBaseAddress = new Uri(string.IsNullOrWhiteSpace(apiBaseUrl) ? builder.HostEnvironment.BaseAddress : apiBaseUrl);

builder.Services.AddScoped(_ => new Signal9ApiClient(new HttpClient { BaseAddress = apiBaseAddress }));
builder.Services.AddScoped(sp => new DashboardHubConnection(apiBaseAddress, sp.GetRequiredService<ILogger<DashboardHubConnection>>()));

await builder.Build().RunAsync();
