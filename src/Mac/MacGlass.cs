using System.Runtime.InteropServices;

namespace Mixamp;

internal static partial class MacTitleBar
{
    [DllImport(ObjC,EntryPoint="objc_msgSend")] static extern void PlaceSubview(nint view,nint selector,nint child,nint order,nint sibling);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] static extern void SetKey(nint obj,nint selector,nint value,nint key);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] static extern byte IsClass(nint obj,nint selector,nint cls);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] static extern byte GetBool(nint obj,nint selector);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] static extern void SendVoid(nint obj,nint selector);
    static nint materialView;

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
        var parent=Get(web,sel_registerName("superview"));
        if(enable){
            materialView=Get(Alloc("NSVisualEffectView"),sel_registerName("init"));
            if(materialView==0)return false;
            SetObject(materialView,sel_registerName("setMaterial:"),7); // NSVisualEffectMaterialSidebar
            SetObject(materialView,sel_registerName("setBlendingMode:"),0); // Behind the window
            SetBool(materialView,sel_registerName("setTranslatesAutoresizingMaskIntoConstraints:"),0);
            PlaceSubview(parent,sel_registerName("addSubview:positioned:relativeTo:"),materialView,-1,web);
            if(Get(materialView,sel_registerName("superview"))!=parent || parent==0){
                SendVoid(materialView,sel_registerName("release"));materialView=0;return false;
            }
            foreach(var edge in new[]{"leadingAnchor","trailingAnchor","topAnchor","bottomAnchor"}){
                var anchor=Get(materialView,sel_registerName(edge));
                var constraint=ObjectArg(anchor,sel_registerName("constraintEqualToAnchor:"),Get(web,sel_registerName(edge)));
                SetBool(constraint,sel_registerName("setActive:"),1);
            }
        }else{
            SendVoid(materialView,sel_registerName("removeFromSuperview"));
            SendVoid(materialView,sel_registerName("release"));materialView=0;
        }
        var background=ObjectArg(objc_getClass("NSNumber"),sel_registerName("numberWithInt:"),enable?0:1);
        SetKey(web,sel_registerName("setValue:forKey:"),background,NativeString("drawsBackground"));
        SetBool(window,sel_registerName("setOpaque:"),enable?(byte)0:(byte)1);
        return enable;
    }
}
