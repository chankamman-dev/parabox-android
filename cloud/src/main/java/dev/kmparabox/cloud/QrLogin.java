package dev.kmparabox.cloud;

import java.util.concurrent.TimeUnit;
import java.util.function.Consumer;

import in.dragonbra.javasteam.enums.EOSType;
import in.dragonbra.javasteam.steam.authentication.AuthPollResult;
import in.dragonbra.javasteam.steam.authentication.AuthSessionDetails;
import in.dragonbra.javasteam.steam.authentication.QrAuthSession;

// Steam's QR sign-in: the URL is shown as a code, the Steam app on the phone approves it, and the
// result is a refresh token. No password ever passes through this app.
final class QrLogin {
    static String[] run(SteamSession s, Consumer<String> showUrl) throws Exception {
        s.connect();
        AuthSessionDetails d = new AuthSessionDetails();
        d.deviceFriendlyName = "Patrick's Parabox Android";
        d.clientOSType = EOSType.Android9;
        d.persistentSession = true;
        QrAuthSession q = s.client.getAuthentication().beginAuthSessionViaQR(d).get(30, TimeUnit.SECONDS);
        showUrl.accept(q.getChallengeUrl());
        q.setChallengeUrlChanged(changed -> showUrl.accept(changed.getChallengeUrl()));
        AuthPollResult r = q.pollingWaitForResult().get();
        return new String[] { r.getAccountName(), r.getRefreshToken() };
    }

    private QrLogin() { }
}
