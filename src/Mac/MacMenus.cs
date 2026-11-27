using System.Runtime.InteropServices;
using System.Text.Json;
using Photino.NET;

namespace Mixamp;

internal static partial class MacTitleBar
{
    sealed record MenuEntry(string Label, string? Id = null, MenuEntry[]? Children = null);
    [DllImport(ObjC)] static extern nint objc_allocateClassPair(nint superclass, string name, nuint extra);
    [DllImport(ObjC)] static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] static extern byte class_addMethod(nint cls, nint selector, nint implementation, string signature);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Click(nint self, nint cmd, nint sender);
    static readonly Dictionary<int, string> commands = new();
    static readonly Dictionary<string, nint> commandItems = new();
    static PhotinoWindow? host;
    static readonly Click clicked = (_, _, sender) =>
    {
        if (commands.TryGetValue((int)Get(sender, sel_registerName("tag")), out var id))
            host?.SendWebMessage("menu:" + id);
    };
    static nint target;
    static nint Alloc(string cls) => Get(objc_getClass(cls), sel_registerName("alloc"));
    static nint NativeString(string s) => StringArg(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), s);
    static nint Menu(string title) => ObjectArg(Alloc("NSMenu"), sel_registerName("initWithTitle:"), NativeString(title));
    static nint Item(string title)
    {
        var item = Get(Alloc("NSMenuItem"), sel_registerName("init"));
        SetObject(item, sel_registerName("setTitle:"), NativeString(title));
        return item;
    }
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void Insert(nint obj, nint selector, nint item, nint index);
    static void AddEntries(nint parent, MenuEntry[] entries)
    {
        foreach (var entry in entries)
        {
            var item = Item(entry.Label);
            if (entry.Children != null)
            {
                var sub = Menu(entry.Label); AddEntries(sub, entry.Children);
                SetObject(item, sel_registerName("setSubmenu:"), sub); SendVoid(sub, sel_registerName("release"));
            }
            else if (entry.Id is { } id)
            {
                int tag = commands.Count + 1; commands.Add(tag, id); commandItems[id] = item;
                SetObject(item, sel_registerName("setTag:"), tag);
                SetObject(item, sel_registerName("setTarget:"), target);
                SetObject(item, sel_registerName("setAction:"), sel_registerName("mixampMenuAction:"));
            }
            SetObject(parent, sel_registerName("addItem:"), item); SendVoid(item, sel_registerName("release"));
        }
    }
    public static void InstallMenus(PhotinoWindow window, string json)
    {
        if (host == null)
        {
            var groups = JsonSerializer.Deserialize<MenuEntry[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var cls = objc_allocateClassPair(objc_getClass("NSObject"), "MixampMenuTarget", 0);
            class_addMethod(cls, sel_registerName("mixampMenuAction:"), Marshal.GetFunctionPointerForDelegate(clicked), "v@:@");
            objc_registerClassPair(cls); target = Get(Get(cls, sel_registerName("alloc")), sel_registerName("init"));
            var app = Get(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            var bar = Get(app, sel_registerName("mainMenu"));
            int index = 1;
            foreach (var group in groups)
            {
                var existing = ObjectArg(bar, sel_registerName("itemWithTitle:"), NativeString(group.Label));
                var item = existing == 0 ? Item(group.Label) : Get(existing, sel_registerName("retain"));
                var sub = Get(item, sel_registerName("submenu"));
                if (existing != 0) SetObject(bar, sel_registerName("removeItem:"), item);
                if (sub == 0)
                {
                    sub = Menu(group.Label); SetObject(item, sel_registerName("setSubmenu:"), sub); SendVoid(sub, sel_registerName("release"));
                }
                else if (Get(sub, sel_registerName("numberOfItems")) > 0)
                    SetObject(sub, sel_registerName("addItem:"), Get(objc_getClass("NSMenuItem"), sel_registerName("separatorItem")));
                AddEntries(sub, group.Children!);
                Insert(bar, sel_registerName("insertItem:atIndex:"), item, index++); SendVoid(item, sel_registerName("release"));
            }
            host = window;
        }
        window.SendWebMessage("menus-ready");
    }
    public static bool CheckMenusAndSelectTheme()
    {
        var app = Get(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        var bar = Get(app, sel_registerName("mainMenu"));
        string[] names = ["Library", "Edit", "View", "EQ"];
        if (Get(bar, sel_registerName("numberOfItems")) < 5) return false;
        for (int i = 0; i < names.Length; i++)
        {
            var item = ObjectArg(bar, sel_registerName("itemAtIndex:"), i + 1);
            var title = Get(item, sel_registerName("title"));
            if (Marshal.PtrToStringUTF8(Get(title, sel_registerName("UTF8String"))) != names[i]) return false;
        }
        var view = Get(ObjectArg(bar, sel_registerName("itemWithTitle:"), NativeString("View")), sel_registerName("submenu"));
        var themes = Get(ObjectArg(view, sel_registerName("itemWithTitle:"), NativeString("Themes")), sel_registerName("submenu"));
        var choice = ObjectArg(themes, sel_registerName("indexOfItemWithTitle:"), NativeString("Velvet Mocha"));
        if (themes == 0 || choice < 0) return false;
        SetObject(themes, sel_registerName("performActionForItemAtIndex:"), choice);
        return true;
    }
    public static void CheckGlassAction()
    {
        var app = Get(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        var bar = Get(app, sel_registerName("mainMenu"));
        var view = Get(ObjectArg(bar, sel_registerName("itemWithTitle:"), NativeString("View")), sel_registerName("submenu"));
        var item = ObjectArg(view, sel_registerName("indexOfItemWithTitle:"), NativeString("Glass appearance"));
        if(item>=0)SetObject(view,sel_registerName("performActionForItemAtIndex:"),item);
    }
    public static void MenuChecks(string json)
    {
        var checkedIds = JsonSerializer.Deserialize<string[]>(json) ?? [];
        foreach (var (id, item) in commandItems)
            SetObject(item, sel_registerName("setState:"), checkedIds.Contains(id) ? 1 : 0);
    }
}
