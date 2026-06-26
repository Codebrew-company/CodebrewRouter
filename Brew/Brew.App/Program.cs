using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Syncfusion.Blazor;
using Brew.App;
using Brew.App.Models;
using Brew.App.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Syncfusion Blazor — community license
Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense("Community");
builder.Services.AddSyncfusionBlazor();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Voice service configuration
builder.Services.Configure<VoiceConfig>(builder.Configuration.GetSection("Voice"));
builder.Services.AddScoped<VoiceService>();

await builder.Build().RunAsync();
