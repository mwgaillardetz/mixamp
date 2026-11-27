using System.Runtime.InteropServices;
using Mixamp.Core;

namespace Mixamp;

internal static partial class MacTitleBar
{
    // Photino owns the NSWindow. AppKit exposes the title bar and its traffic lights
    // separately from the web view, so update them on the window's UI thread.
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] static extern nint objc_getClass(string name);
    [DllImport(ObjC)] static extern nint sel_registerName(string name);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] static extern nint Get(nint obj, nint selector);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] static extern nint ObjectArg(nint obj, nint selector, nint arg);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] static extern nint StringArg(nint obj, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] static extern void SetObject(nint obj, nint selector, nint arg);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] static extern void SetBool(nint obj, nint selector, byte arg);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] static extern nint Color(nint obj, nint selector, double r, double g, double b, double a);

    public static bool Apply(PlayerTheme theme,bool glass,bool webReduced,bool checkWindow,out bool active)
    {
        active=false;
        var app = Get(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        var windows = Get(app, sel_registerName("windows"));
        var count = Get(windows, sel_registerName("count"));
        for (nint i = 0; i < count; i++)
        {
            var window = ObjectArg(windows, sel_registerName("objectAtIndex:"), i);
            var title = Get(window, sel_registerName("title"));
            if (Marshal.PtrToStringUTF8(Get(title, sel_registerName("UTF8String"))) != "Mixamp") continue;
            var web=WebView(Get(window,sel_registerName("contentView")),objc_getClass("WKWebView"));
            if(web==0)continue;
            var rgb = Convert.FromHexString(theme.Palette.Side[1..]);
            var color = Color(objc_getClass("NSColor"), sel_registerName("colorWithSRGBRed:green:blue:alpha:"),
                rgb[0]/255d, rgb[1]/255d, rgb[2]/255d, 1);
            var style = rgb[0]+rgb[1]+rgb[2] < 384 ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua";
            var name = StringArg(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), style);
            var appearance = ObjectArg(objc_getClass("NSAppearance"), sel_registerName("appearanceNamed:"), name);
            SetObject(window, sel_registerName("setAppearance:"), appearance);
            bool reduced=glass && (webReduced || ShouldReduceTransparency());
            active=ApplyMaterial(window,web,glass && !reduced);
            if(checkWindow && glass)
            {
                Console.WriteLine($"Glass selection received, reduced transparency={reduced}, material attached={active}");
                // Exercise attachment even when the CI desktop requests reduced transparency.
                // Its isolated first-launch window is restored before returning to the event loop.
                if(reduced && ApplyMaterial(window,web,true))
                {
                    ApplyMaterial(window,web,false);
                    Console.WriteLine("Native glass applied and restored for reduced transparency");
                }
            }
            if(active) color=Get(objc_getClass("NSColor"),sel_registerName("clearColor"));
            SetObject(window, sel_registerName("setBackgroundColor:"), color);
            SetBool(window, sel_registerName("setTitlebarAppearsTransparent:"), active?(byte)0:(byte)1);
            return true;
        }
        if(checkWindow) Console.WriteLine("Mixamp window with its WKWebView was unavailable");
        return false;
    }
}
