package dev.kmparabox.cloud;

import java.security.Provider;
import java.security.Security;

import org.bouncycastle.jce.provider.BouncyCastleProvider;

// Android ships a cut-down provider named "BC". JavaSteam registers BouncyCastle under that name and
// asks for algorithms from it, but registering does nothing while Android's is installed, so it ends
// up with the cut-down one. Replacing it first makes "BC" the real BouncyCastle.
// Approach taken from SilksongAndroid (MIT).
final class SteamCrypto {
    private static boolean installed;

    static synchronized void install() {
        if (installed) return;
        installed = true;
        Provider existing = Security.getProvider("BC");
        if (existing != null && !(existing instanceof BouncyCastleProvider)) Security.removeProvider("BC");
        // Appended, not inserted first: first place would also make it the default for TLS.
        if (Security.getProvider("BC") == null) Security.addProvider(new BouncyCastleProvider());
    }

    private SteamCrypto() { }
}
