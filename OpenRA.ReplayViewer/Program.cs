using MudBlazor.Services;
using OpenRA.ReplayViewer.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddMudServices();

// Add core asset services
builder.Services.AddSingleton<FileSystemService>();
builder.Services.AddSingleton<SpriteLoaderService>();
builder.Services.AddSingleton<PaletteService>();
builder.Services.AddSingleton<FrameCacheService>();
builder.Services.AddSingleton<TilesetLoaderService>();

// Add replay services
builder.Services.AddScoped<SimpleMapLoaderService>();
builder.Services.AddScoped<AssetLoaderService>();
builder.Services.AddScoped<ReplayServiceV2>();
builder.Services.AddScoped<TerrainRenderService>();

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
