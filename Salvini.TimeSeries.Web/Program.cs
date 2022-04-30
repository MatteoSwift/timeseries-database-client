using System.Linq;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Salvini.TimeSeries;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var services = builder.Services;
services.Configure<FormOptions>(x => { x.ValueLengthLimit = int.MaxValue; x.MultipartBodyLengthLimit = long.MaxValue; });
//services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings());
services.AddMemoryCache();
services.AddHttpClient();
services.AddControllers();
services.AddDirectoryBrowser();
services.AddCors(options => options.AddPolicy("cors", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
services.AddSingleton(Client.Create(args.FirstOrDefault(x => x.StartsWith("--cn="))?[5..] ?? ""));

var app = builder.Build();

// Configure the HTTP request pipeline.

app.UseDeveloperExceptionPage();
app.UseCors("cors");
app.UseRouting();
app.UseStaticFiles();
app.Use(async (ctx, next) => { ctx.Response.Headers.Add("Author", "Salvini"); ctx.Response.Headers.Add("Powered-By", "https://salvini.cn?version=7.60.11.10"); await next(); });
app.UseEndpoints(endpoints => { endpoints.MapControllers(); });

app.Run();