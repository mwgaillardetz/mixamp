using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.UI.ViewManagement;

namespace Mixamp;

public partial class MainWindow
{
    readonly MenuItem glassOption = new() { Header="Glass appearance", IsCheckable=true };
    UISettings? effectsSettings;
    [StructLayout(LayoutKind.Sequential)]
    struct FrameMargins { public int Left,Right,Top,Bottom; }
    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(nint hwnd,ref FrameMargins margins);

    void ConfigureGlass(MenuItem appearance)
    {
        appearance.Items.Add(glassOption);
        glassOption.IsChecked=vm.Store.Get("installation","glass")=="on";
        glassOption.Click+=(_,_)=>{
            vm.Store.Set("installation","glass",glassOption.IsChecked?"on":"off");
            ApplyCaption();
        };
        try {
            effectsSettings=new UISettings();
            effectsSettings.AdvancedEffectsEnabledChanged+=EffectsChanged;
            Closed+=(_,_)=>effectsSettings.AdvancedEffectsEnabledChanged-=EffectsChanged;
        }
        catch(PlatformNotSupportedException){effectsSettings=null;}
    }

    void EffectsChanged(UISettings sender,object args) => Dispatcher.BeginInvoke(ApplyCaption);

    bool ApplyGlass(nint hwnd)
    {
        bool enabled=glassOption.IsChecked && !SystemParameters.HighContrast &&
            OperatingSystem.IsWindowsVersionAtLeast(10,0,22621) && effectsSettings?.AdvancedEffectsEnabled==true;
        int backdrop=enabled?3:1; // DWMSBT_TRANSIENTWINDOW (acrylic) or DWMSBT_NONE
        if(DwmSetWindowAttribute(hwnd,38,ref backdrop,sizeof(int))!=0)enabled=false;
        var margins=new FrameMargins{Left=enabled?-1:0};
        if(DwmExtendFrameIntoClientArea(hwnd,ref margins)!=0)enabled=false;
        var surface=((SolidColorBrush)FindResource("Canvas")).Color;
        var source=HwndSource.FromHwnd(hwnd);
        if(source?.CompositionTarget!=null)source.CompositionTarget.BackgroundColor=enabled?Colors.Transparent:surface;
        if(enabled)Background=Brushes.Transparent;
        else SetResourceReference(BackgroundProperty,"Canvas");
        void Surface(DependencyObject control,DependencyProperty property,string key,double opacity)
        {
            var brush=((Brush)FindResource(key)).Clone();
            brush.Opacity=enabled?opacity:1;
            control.SetValue(property,brush);
        }
        Surface(Shell,Panel.BackgroundProperty,"Canvas",.30);
        Surface(Rail,Border.BackgroundProperty,"Side",.57);
        Surface(PlayerDeck,Border.BackgroundProperty,"Deck",.65);
        Surface(Menus,Control.BackgroundProperty,"Side",.60);
        return enabled;
    }
}
