using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mixamp.Core;

namespace Mixamp;

sealed class Keychain(Store store)
{
    const string Security = "/System/Library/Frameworks/Security.framework/Security";
    const string Service = "Mixamp Plex", LegacyAccount = "Plex account", Preference = "plexKeychainAccount";
    const int NotFound = -25300;
    [DllImport(Security)] static extern int SecKeychainFindGenericPassword(nint keychain,uint serviceLength,string service,uint accountLength,string account,out uint length,out nint password,out nint item);
    [DllImport(Security)] static extern int SecKeychainItemFreeContent(nint attributes,nint data);
    [DllImport(Security)] static extern int SecKeychainAddGenericPassword(nint keychain,uint serviceLength,string service,uint accountLength,string account,uint length,byte[] password,nint item);
    [DllImport(Security)] static extern int SecKeychainGetUserInteractionAllowed(out byte allowed);
    [DllImport(Security)] static extern int SecKeychainSetUserInteractionAllowed(byte allowed);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] static extern void CFRelease(nint obj);

    string? sessionLogin;
    public string? SaveWarning { get; private set; }

    // Older installations used a fixed account name. Only the item name goes in preferences.
    string AccountName => store.Get("installation",Preference) ?? LegacyAccount;

    public string? Read(bool allowPrompt)
    {
        if (sessionLogin != null) return sessionLogin;
        var name = AccountName;
        if (name.Length == 0) return null;
        int status = SecKeychainGetUserInteractionAllowed(out byte allowed);
        if (status == 0) status = SecKeychainSetUserInteractionAllowed(allowPrompt ? (byte)1 : (byte)0);
        if (status == 0)
        {
            try
            {
                status = SecKeychainFindGenericPassword(0,(uint)Service.Length,Service,(uint)name.Length,name,out var length,out var data,out var item);
                try
                {
                    if (status == 0 && length > 0) return sessionLogin=Marshal.PtrToStringUTF8(data,checked((int)length));
                    if (status is 0 or NotFound) return null;
                }
                finally
                {
                    if (data != 0) SecKeychainItemFreeContent(0,data);
                    if (item != 0) CFRelease(item);
                }
            }
            finally { SecKeychainSetUserInteractionAllowed(allowed); }
        }
        if(status==-128)throw new IOException("Keychain approval canceled. Reconnect to try again.");
        throw new IOException($"Keychain could not read the saved login (macOS {status}). Reconnect to retry, or use Library → Sign in with Plex to replace it.");
    }

    public void Write(string? token)
    {
        sessionLogin = token;
        SaveWarning = null;
        if (token == null)
        {
            // Plex has already revoked this token. Skip its item on the next sign-in.
            store.Set("installation",Preference,"");
            return;
        }

        // Create an item owned by this build. Switch the pointer only after the write succeeds.
        var name = "Plex account " + Guid.NewGuid().ToString("N");
        var bytes = Encoding.UTF8.GetBytes(token);
        int status;
        try { status = SecKeychainAddGenericPassword(0,(uint)Service.Length,Service,(uint)name.Length,name,(uint)bytes.Length,bytes,0); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        if (status == 0) store.Set("installation",Preference,name);
        else SaveWarning = $"Connected for this session. Keychain could not save the Plex login (macOS {status}).";
    }
}
