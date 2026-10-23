using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.FluentUI.AspNetCore.Components;
using OSI.Signal9.Web.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddFluentUIComponents();
builder.Services.AddDashboard(new Uri(builder.HostEnvironment.BaseAddress), () => new HttpClientHandler());

await builder.Build().RunAsync();
