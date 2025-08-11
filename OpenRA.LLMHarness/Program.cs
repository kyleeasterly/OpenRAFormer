using MudBlazor.Services;
using OpenRA.LLMHarness.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddMudServices();

// Add HttpClient for OllamaService
builder.Services.AddHttpClient<OllamaService>();

// Add OllamaService as a singleton
builder.Services.AddSingleton<OllamaService>();

// Add hosted service for file watching
builder.Services.AddHostedService<FileWatcherService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Error");
	app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
