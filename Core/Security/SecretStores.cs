using System;

namespace erp_inteligente.Core.Security;

/// <summary>Factory for the key store of the current operating system.</summary>
public static class SecretStores
{
    public static ISecretStore ForCurrentPlatform()
    {
        if (OperatingSystem.IsLinux()) return new LibSecretStore();
        if (OperatingSystem.IsWindows()) return new DpapiSecretStore();
        throw new PlatformNotSupportedException("O cofre do macOS (Keychain) ainda não é suportado.");
    }
}
