using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PoolWatch.Core.Links;
using PoolWatch.Native;

namespace PoolWatch.Engine;

/// <summary>A value of an environment variable as the registry holds it: its text, never expanded, and whether it is REG_EXPAND_SZ.</summary>
internal readonly record struct EnvValue(string Text, bool Expand);

/// <summary>
/// The environment variables in the registry that the AppData links fixes read and change: the account's own
/// (HKCU\Environment) and the machine's (HKLM\...\Session Manager\Environment). Tests pass a fake, so no test reads or
/// changes a real variable.
/// </summary>
internal interface IEnvironmentStore
{
    /// <summary>
    /// The value as written (a %NAME% in it is never filled in) and its kind; null when there is no such value. Throws
    /// when it cannot be read, or is not text.
    /// </summary>
    EnvValue? Read(EnvScope scope, string name);

    /// <summary>
    /// Writes <paramref name="value"/> (null removes the value), only while the value there is still
    /// <paramref name="expected"/> (null: no value), read again through the key the write goes through, right before it.
    /// Returns null, or why it did not write.
    /// </summary>
    string? Write(EnvScope scope, string name, EnvValue? expected, EnvValue? value);

    /// <summary>Tells running programs the environment changed, so programs Explorer starts from now on get the new values.</summary>
    void Broadcast();
}

/// <summary>
/// The registry behind <see cref="IEnvironmentStore"/>. PoolWatch runs as administrator, and the account's own key is
/// one the account can change: a key there could be a registry link that leads somewhere else. So every key is checked
/// by the name the kernel gives the open handle (<see cref="EnvironmentNative.KeyName"/>), and a key that is not the
/// account's own Environment key (or the machine's) is refused before anything is read or written through it. The
/// account is the one PoolWatch runs as: when a standard user elevated it with another administrator's password, that
/// administrator's (SECURITY.md, Known limitations).
/// </summary>
internal sealed class WindowsEnvironmentStore : IEnvironmentStore
{
    public static readonly WindowsEnvironmentStore Instance = new();

    private const string UserKey = "Environment", MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>What a write says when the value is no longer as it was shown.</summary>
    internal const string ChangedSince = "it changed since PoolWatch looked at it";

    public EnvValue? Read(EnvScope scope, string name)
    {
        using var key = Open(scope, writable: false);
        return key is null ? null : ValueOf(key, name);
    }

    public string? Write(EnvScope scope, string name, EnvValue? expected, EnvValue? value)
    {
        try
        {
            using var key = Open(scope, writable: true);
            if (key is null) return scope == EnvScope.User ? "your account has no environment key in the registry" : "the machine's environment key is not there";
            if (ValueOf(key, name) != expected) return ChangedSince;
            if (value is { } v)
            {
                if (v.Text.Length > PathList.MaxChars) return "the value would be too long";
                key.SetValue(name, v.Text, v.Expand ? RegistryValueKind.ExpandString : RegistryValueKind.String);
            }
            else key.DeleteValue(name, throwOnMissingValue: false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException)
        {
            return ex.Message;
        }
    }

    public void Broadcast() => EnvironmentNative.BroadcastEnvironmentChange();

    /// <summary>The key, checked to be the one asked for; null when it is not there. Throws when it is another key (a link).</summary>
    private static RegistryKey? Open(EnvScope scope, bool writable)
    {
        using var hive = RegistryKey.OpenBaseKey(scope == EnvScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
        var key = hive.OpenSubKey(scope == EnvScope.User ? UserKey : MachineKey, writable);
        if (key is null) return null;
        string? sid;
        using (var me = WindowsIdentity.GetCurrent()) sid = me.User?.Value;
        if (WhyNotItsKey(scope, EnvironmentNative.KeyName(key.Handle), sid) is { } why)
        {
            key.Dispose();
            throw new UnauthorizedAccessException(why);
        }
        return key;
    }

    /// <summary>
    /// Pure: why a key whose kernel name is <paramref name="name"/> is not the environment key of <paramref name="scope"/>,
    /// or null when it is: the account's is exactly \REGISTRY\USER\&lt;its SID&gt;\Environment; the machine's is under
    /// one of its control sets, or CurrentControlSet, the link Windows itself keeps to one of them (only an administrator
    /// can make a link under HKLM).
    /// </summary>
    internal static string? WhyNotItsKey(EnvScope scope, string? name, string? userSid)
    {
        if (name is null) return "PoolWatch could not read which registry key it opened, so it did not use it";
        bool its = scope == EnvScope.User
            ? userSid is { Length: > 0 } && name.Equals($@"\REGISTRY\USER\{userSid}\Environment", StringComparison.OrdinalIgnoreCase)
            : MachineName.IsMatch(name);
        return its ? null : "the environment key is a link to another place in the registry, so PoolWatch did not use it";
    }

    private static readonly Regex MachineName = new(@"^\\REGISTRY\\MACHINE\\SYSTEM\\(ControlSet[0-9]{3}|CurrentControlSet)\\Control\\Session Manager\\Environment$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The value as written and its kind; null when there is none. Throws when it is not text, or longer than a variable can be.</summary>
    private static EnvValue? ValueOf(RegistryKey key, string name)
    {
        if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value) return null;
        var kind = key.GetValueKind(name);
        if (value is not string text || kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            throw new InvalidDataException($"{name} is not text in the registry");
        if (text.Length > PathList.MaxChars) throw new InvalidDataException($"{name} is longer than an environment variable can be");
        return new EnvValue(text, kind == RegistryValueKind.ExpandString);
    }
}
