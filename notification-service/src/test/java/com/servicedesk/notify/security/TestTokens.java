package com.servicedesk.notify.security;

import com.nimbusds.jose.JWSAlgorithm;
import com.nimbusds.jose.JWSHeader;
import com.nimbusds.jose.crypto.MACSigner;
import com.nimbusds.jwt.JWTClaimsSet;
import com.nimbusds.jwt.SignedJWT;
import java.time.Instant;
import java.util.Base64;
import java.util.Date;

/** Builds signed test tokens the way servicedesk-api does (HS256, base64 key). */
public final class TestTokens {
    public static final String KEY_B64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    public static final String OTHER_KEY_B64 = "HxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo=";

    private TestTokens() {}

    public static String token(String keyB64, String issuer, String audience, String sub,
                               Object role, Instant expires) {
        try {
            JWTClaimsSet.Builder b = new JWTClaimsSet.Builder()
                    .subject(sub).issuer(issuer).audience(audience)
                    .claim("name", "Test User")
                    .issueTime(Date.from(Instant.now().minusSeconds(3600)))
                    .expirationTime(expires == null ? null : Date.from(expires));
            if (role != null) b.claim("role", role);
            SignedJWT jwt = new SignedJWT(new JWSHeader(JWSAlgorithm.HS256), b.build());
            jwt.sign(new MACSigner(Base64.getDecoder().decode(keyB64)));
            return jwt.serialize();
        } catch (Exception e) {
            throw new IllegalStateException(e);
        }
    }

    /** Signed with HS512 using a 64-byte key; same claims as a valid token. */
    public static String hs512(byte[] key64) {
        try {
            JWTClaimsSet c = new JWTClaimsSet.Builder().subject("u").issuer("ServiceDesk")
                    .audience("ServiceDesk.Api").claim("role", "Admin")
                    .expirationTime(Date.from(Instant.now().plusSeconds(600))).build();
            SignedJWT jwt = new SignedJWT(new JWSHeader(JWSAlgorithm.HS512), c);
            jwt.sign(new MACSigner(key64));
            return jwt.serialize();
        } catch (Exception e) {
            throw new IllegalStateException(e);
        }
    }

    /** Unsigned token with alg=none. */
    public static String algNone() {
        var enc = Base64.getUrlEncoder().withoutPadding();
        String h = enc.encodeToString("{\"alg\":\"none\"}".getBytes());
        String p = enc.encodeToString(("{\"sub\":\"u\",\"iss\":\"ServiceDesk\",\"aud\":\"ServiceDesk.Api\","
                + "\"role\":\"Admin\",\"exp\":" + (Instant.now().getEpochSecond() + 600) + "}").getBytes());
        return h + "." + p + ".";
    }

    public static String valid(String sub, String role) {
        return token(KEY_B64, "ServiceDesk", "ServiceDesk.Api", sub, role, Instant.now().plusSeconds(600));
    }
}
