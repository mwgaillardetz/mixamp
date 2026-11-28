using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photino.NET;
using Mixamp.Core;
using Microsoft.Extensions.Hosting;

namespace Mixamp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        bool fixture = args is ["--fixture-host", _];
        bool checkWindow = args is ["--check-window", _];
        string profile = fixture || checkWindow ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library","Application Support","Mixamp");
        using var library = new MacLibrary(profile,allowKeychainPrompt:!fixture && !checkWindow);
        string root = Path.Combine(AppContext.BaseDirectory,"wwwroot");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath=AppContext.BaseDirectory,WebRootPath=root });
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
        web.MapMethods("/audio/{name}",["GET","HEAD"],(string name,HttpContext ctx)=>library.Audio(name,ctx));
        web.MapGet("/artwork/{id}",(string id, CancellationToken ct)=>library.Artwork(id,ct));
        web.MapPost("/command",async (Request r,CancellationToken requestAborted)=>
        {
            try
            {
                using var stopped=CancellationTokenSource.CreateLinkedTokenSource(requestAborted,web.Lifetime.ApplicationStopping);
                var result = await library.Command(r,stopped.Token);
                if (checkWindow && r.Action == "resume") Console.WriteLine("Library window initialized");
                return Results.Json(result);
            }
            catch(OperationCanceledException) when(requestAborted.IsCancellationRequested || web.Lifetime.ApplicationStopping.IsCancellationRequested)
            { return Results.StatusCode(499); }
            catch(OperationCanceledException) {return Results.Json(new {error="Plex timed out."},statusCode:504);}
            catch(Exception ex) when(r.Action is "songs" or "playlist")
            {
                var reason=ex switch {
                    HttpRequestException { StatusCode: {} code } => $"Plex returned HTTP {(int)code}.",
                    HttpRequestException => "The connection to Plex was interrupted.",
                    IOException => ex.Message,
                    _ => "Mixamp could not read the track list returned by Plex."
                };
                return Results.Json(new {error=reason},statusCode:502);
            }
            catch(Exception ex){return Results.BadRequest(new {error=ex is IOException or ArgumentException or TimeoutException ? ex.Message :
                ex is HttpRequestException h ? $"Plex connection failed ({h.StatusCode})." : "Operation failed. Check the library and try again."});}
        });
        web.StartAsync().GetAwaiter().GetResult();
        try
        {
            string url=web.Urls.Single()+"/launch/"+ticket;
            if(fixture){Console.WriteLine(url);web.WaitForShutdownAsync().GetAwaiter().GetResult();return;}
            var window = new PhotinoWindow().SetTitle("Mixamp").SetUseOsDefaultSize(false).SetSize(1120,720).SetMinSize(740,400);
            bool menuTestStarted=false,glassTestStarted=false;
            window.RegisterWebMessageReceivedHandler((_,message) =>
            {
                if (!OperatingSystem.IsMacOS()) return;
                if (message.StartsWith("playback-glass:",StringComparison.Ordinal)) {window.Invoke(()=>MacTitleBar.LayoutPlaybackGlass(message[15..]));return;}
                if (message.StartsWith("menus:",StringComparison.Ordinal)) {window.Invoke(()=>MacTitleBar.InstallMenus(window,message[6..]));return;}
                if (message.StartsWith("checks:",StringComparison.Ordinal)) {window.Invoke(()=>{
                    MacTitleBar.MenuChecks(message[7..]);
                    if(checkWindow && !menuTestStarted)
                    {
                        menuTestStarted=true;
                        if(MacTitleBar.CheckMenusAndSelectTheme()) Console.WriteLine("Native menus ready");
                    }
                });return;}
                bool webReduced=message.StartsWith("muted:",StringComparison.Ordinal);
                bool glass=webReduced || message.StartsWith("glass:",StringComparison.Ordinal);
                if (!glass && !message.StartsWith("theme:",StringComparison.Ordinal)) return;
                var theme = ThemeCatalog.All.FirstOrDefault(t => t.Name == message[6..]);
                if (theme != null) window.Invoke(() =>
                {
                    bool applied=MacTitleBar.Apply(theme,glass,webReduced,checkWindow,out bool active);
                    window.SendWebMessage(MacTitleBar.HasPlaybackGlass?"glass-active:liquid":active?"glass-active:on":"glass-active:off");
                    if (applied && checkWindow)
                    {
                        Console.WriteLine("Window theme applied");
                        if(menuTestStarted && theme.Name=="Velvet Mocha" && !glassTestStarted)
                        {
                            Console.WriteLine("Native theme action applied");
                            glassTestStarted=true;
                            MacTitleBar.CheckGlassAction();
                        }
                        if(glassTestStarted && active) Console.WriteLine("Native glass applied");
                    }
                });
            });
            window.SetMediaAutoplayEnabled(true).Load(url).WaitForClose();
        }
        finally {web.StopAsync().GetAwaiter().GetResult();web.DisposeAsync().AsTask().GetAwaiter().GetResult();}
    }
}
