using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photino.NET;
using Microsoft.Extensions.Hosting;

namespace Mixamp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        bool fixture = args is ["--fixture-host", _];
        string profile = fixture ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library","Application Support","Mixamp");
        using var library = new MacLibrary(profile);
        string root = Path.Combine(AppContext.BaseDirectory,"wwwroot");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath=AppContext.BaseDirectory,WebRootPath=root });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o=>o.Listen(IPAddress.Loopback,0));
        var web = builder.Build();
        string ticket=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), cookie=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string cookieName="mixamp_"+ticket[..12];
        web.Use(async (ctx,next) =>
        {
            if(ctx.Request.Host.Host!="127.0.0.1") {ctx.Response.StatusCode=403;return;}
            if(ctx.Request.Path=="/launch/"+ticket)
            {
                ctx.Response.Cookies.Append(cookieName,cookie,new CookieOptions{HttpOnly=true,SameSite=SameSiteMode.Strict,Path="/"});
                ctx.Response.Headers.CacheControl="no-store"; ctx.Response.Redirect("/");return;
            }
            if(ctx.Request.Cookies[cookieName]!=cookie || (ctx.Request.Method=="POST" &&
                (ctx.Request.Headers["X-Mixamp"]!="app" || ctx.Request.Headers.Origin!=$"http://{ctx.Request.Host}")))
            {ctx.Response.StatusCode=403;return;}
            ctx.Response.Headers.ContentSecurityPolicy="default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; media-src 'self'; frame-ancestors 'none'; object-src 'none'";
            ctx.Response.Headers["Referrer-Policy"]="no-referrer";
            ctx.Response.Headers.CacheControl="no-store";
            await next();
        });
        web.UseDefaultFiles();web.UseStaticFiles();
        web.MapGet("/progress",()=>Results.Text(library.Progress));
        web.MapGet("/audio/{name}",(string name)=>library.Audio(name));
        web.MapPost("/command",async (Request r)=>
        {
            try{return Results.Json(await library.Command(r));}
            catch(Exception ex){return Results.BadRequest(new {error=ex is IOException or ArgumentException ? ex.Message :
                ex is HttpRequestException h ? $"Plex connection failed ({h.StatusCode})." : "Operation failed. Check the library and try again."});}
        });
        web.StartAsync().GetAwaiter().GetResult();
        try
        {
            string url=web.Urls.Single()+"/launch/"+ticket;
            if(fixture){Console.WriteLine(url);web.WaitForShutdownAsync().GetAwaiter().GetResult();return;}
            new PhotinoWindow().SetTitle("Mixamp").SetUseOsDefaultSize(false).SetSize(1120,720)
                .SetMinSize(740,400).Load(url).WaitForClose();
        }
        finally {web.StopAsync().GetAwaiter().GetResult();web.DisposeAsync().AsTask().GetAwaiter().GetResult();}
    }
}
