using System.Runtime.InteropServices;
using System.Text;

namespace Mixamp;

static class Keychain
{
    const string Security = "/System/Library/Frameworks/Security.framework/Security";
    const string Service = "Mixamp Plex", User = "Plex account";
    [DllImport(Security)] static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, string service, uint userLength, string user, out uint length, out IntPtr password, out IntPtr item);
    [DllImport(Security)] static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport(Security)] static extern int SecKeychainAddGenericPassword(IntPtr keychain,uint serviceLength,string service,uint userLength,string user,uint length,byte[] password,IntPtr item);
    [DllImport(Security)] static extern int SecKeychainItemModifyAttributesAndData(IntPtr item,IntPtr attributes,uint length,byte[] password);
    [DllImport(Security)] static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] static extern void CFRelease(IntPtr obj);
    static string? Access(bool write,string? value)
    {
        var status = SecKeychainFindGenericPassword(0,(uint)Service.Length,Service,(uint)User.Length,User,out uint length,out var data,out var item);
        if (status != 0 && status != -25300) throw new IOException("Unable to access the Mixamp login in Keychain.");
        try
        {
            if (!write) return status == 0 ? Marshal.PtrToStringUTF8(data, (int)length) : null;
            var bytes = Encoding.UTF8.GetBytes(value ?? "");
            status = status == 0 ? value == null ? SecKeychainItemDelete(item) : SecKeychainItemModifyAttributesAndData(item,0,(uint)bytes.Length,bytes)
                : value == null ? 0 : SecKeychainAddGenericPassword(0,(uint)Service.Length,Service,(uint)User.Length,User,(uint)bytes.Length,bytes,0);
            if (status != 0) throw new IOException("Unable to save the Mixamp login in Keychain.");
            return null;
        }
        finally { if (data != 0) SecKeychainItemFreeContent(0,data); if (item != 0) CFRelease(item); }
    }
    public static string? Read() => Access(false,null);
    public static void Write(string? value) => Access(true,value);
}
