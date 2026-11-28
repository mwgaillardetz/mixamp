using System.Runtime.InteropServices;

namespace Mixamp;

internal static partial class MacTitleBar
{
    [DllImport(ObjC,EntryPoint="objc_msgSend")] static extern void SetKey(nint obj,nint selector,nint value,nint key);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] static extern byte IsClass(nint obj,nint selector,nint cls);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] static extern byte GetBool(nint obj,nint selector);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] static extern void SendVoid(nint obj,nint selector);
    const int FullSizeContentView = 1 << 15;
    [DllImport(ObjC,EntryPoint="objc_msgSend")] static extern void SetDouble(nint obj,nint selector,double value);
    static nint materialView, playerView, materialTint;
    static byte translatesMask;
    static bool addedFullSize;

    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Point(double X,double Y);
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Rect(double X,double Y,double Width,double Height);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] static extern Point ConvertPoint(nint view,nint selector,Point point,nint source);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] static extern void SetFrame(nint view,nint selector,Rect frame);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] static extern void InsertView(nint parent,nint selector,nint view,nint order,nint sibling);

    static readonly nint[] playbackGlass=new nint[3];
    public static bool HasPlaybackGlass => playbackGlass[0]!=0;
    public static void LayoutPlaybackGlass(string json)
    {
        if(!HasPlaybackGlass)return;
        double[][]? boxes;
        try { boxes=System.Text.Json.JsonSerializer.Deserialize<double[][]>(json); }
        catch(System.Text.Json.JsonException) { return; }
        if(boxes?.Length!=playbackGlass.Length || boxes.Any(b=>b?.Length!=5 || b.Any(n=>!double.IsFinite(n))))return;
        var web=WebView(playerView,objc_getClass("WKWebView"));
        if(web==0)return;
        bool flipped=GetBool(web,sel_registerName("isFlipped"))!=0;
        for(int i=0;i<boxes.Length;i++)
        {
            var b=boxes[i]; // left, top, width, height, distance from viewport bottom
            var origin=ConvertPoint(materialView,sel_registerName("convertPoint:fromView:"),new(b[0],flipped?b[1]:b[4]),web);
            var far=ConvertPoint(materialView,sel_registerName("convertPoint:fromView:"),new(b[0]+b[2],(flipped?b[1]:b[4])+b[3]),web);
            var frame=new Rect(Math.Min(origin.X,far.X),Math.Min(origin.Y,far.Y),Math.Abs(far.X-origin.X),Math.Abs(far.Y-origin.Y));
            SetFrame(playbackGlass[i],sel_registerName("setFrame:"),frame);
            SetDouble(playbackGlass[i],sel_registerName("setCornerRadius:"),frame.Height/2);
        }
    }

    static nint WebView(nint view,nint webClass)
    {
        if(IsClass(view,sel_registerName("isKindOfClass:"),webClass)!=0)return view;
        var children=Get(view,sel_registerName("subviews"));
        for(nint i=0;i<Get(children,sel_registerName("count"));i++){
            var found=WebView(ObjectArg(children,sel_registerName("objectAtIndex:"),i),webClass);
            if(found!=0)return found;
        }
        return 0;
    }
    static bool ShouldReduceTransparency()
    {
        var workspace=Get(objc_getClass("NSWorkspace"),sel_registerName("sharedWorkspace"));
        return GetBool(workspace,sel_registerName("accessibilityDisplayShouldReduceTransparency"))!=0;
    }
    static bool ApplyMaterial(nint window,nint web,bool enable)
    {
        if(enable==(materialView!=0))return enable;
        var mask=Get(window,sel_registerName("styleMask"));
        if(enable){
            materialView=Get(Alloc("NSVisualEffectView"),sel_registerName("init"));
            if(materialView==0)return false;
            playerView=Get(Get(window,sel_registerName("contentView")),sel_registerName("retain"));
            translatesMask=GetBool(playerView,sel_registerName("translatesAutoresizingMaskIntoConstraints"));
            addedFullSize=(mask & FullSizeContentView)==0;
            SetObject(materialView,sel_registerName("setMaterial:"),7); // NSVisualEffectMaterialSidebar
            SetObject(materialView,sel_registerName("setState:"),1); // NSVisualEffectStateActive
            SetObject(materialView,sel_registerName("setBlendingMode:"),0); // Behind the window
            SetObject(window,sel_registerName("setStyleMask:"),mask | FullSizeContentView);
            SetObject(window,sel_registerName("setContentView:"),materialView);
            materialTint=Get(Alloc("NSBox"),sel_registerName("init"));
            SetObject(materialTint,sel_registerName("setBoxType:"),4); // NSBoxCustom
            SetObject(materialTint,sel_registerName("setTitlePosition:"),0);
            SetDouble(materialTint,sel_registerName("setBorderWidth:"),0);
            SetBool(materialTint,sel_registerName("setTranslatesAutoresizingMaskIntoConstraints:"),0);
            SetObject(materialView,sel_registerName("addSubview:"),materialTint);
            SetObject(materialView,sel_registerName("addSubview:"),playerView);
            if(objc_getClass("NSGlassEffectView")!=0)
            {
                for(int i=0;i<playbackGlass.Length;i++)
                {
                    var panel=Get(Alloc("NSGlassEffectView"),sel_registerName("init"));
                    InsertView(materialView,sel_registerName("addSubview:positioned:relativeTo:"),panel,-1,playerView);
                    playbackGlass[i]=panel;
                    SendVoid(panel,sel_registerName("release")); // owned by materialView
                }
            }
            SetBool(playerView,sel_registerName("setTranslatesAutoresizingMaskIntoConstraints:"),0);
            // The material fills the title bar. Keep playback controls below the traffic lights.
            var layout=Get(window,sel_registerName("contentLayoutGuide"));
            foreach(var edge in new[]{"leadingAnchor","trailingAnchor","topAnchor","bottomAnchor"}){
                var tintAnchor=Get(materialTint,sel_registerName(edge));
                var bound=Get(materialView,sel_registerName(edge));
                SetBool(ObjectArg(tintAnchor,sel_registerName("constraintEqualToAnchor:"),bound),sel_registerName("setActive:"),1);

                var anchor=Get(playerView,sel_registerName(edge));
                var constraint=ObjectArg(anchor,sel_registerName("constraintEqualToAnchor:"),Get(layout,sel_registerName(edge)));
                SetBool(constraint,sel_registerName("setActive:"),1);
            }
        }else{
            Array.Clear(playbackGlass);
            SendVoid(materialTint,sel_registerName("removeFromSuperview"));
            SendVoid(materialTint,sel_registerName("release"));materialTint=0;
            SendVoid(playerView,sel_registerName("removeFromSuperview"));
            SetBool(playerView,sel_registerName("setTranslatesAutoresizingMaskIntoConstraints:"),translatesMask);
            if(addedFullSize)SetObject(window,sel_registerName("setStyleMask:"),mask & ~FullSizeContentView);
            SetObject(window,sel_registerName("setContentView:"),playerView);
            SendVoid(playerView,sel_registerName("release"));playerView=0;
            SendVoid(materialView,sel_registerName("release"));materialView=0;
        }
        var background=ObjectArg(objc_getClass("NSNumber"),sel_registerName("numberWithInt:"),enable?0:1);
        SetObject(web,sel_registerName("setUnderPageBackgroundColor:"),enable?Get(objc_getClass("NSColor"),sel_registerName("clearColor")):0);
        SetKey(web,sel_registerName("setValue:forKey:"),background,NativeString("drawsBackground"));
        SetBool(window,sel_registerName("setOpaque:"),enable?(byte)0:(byte)1);
        return enable;
    }
}
