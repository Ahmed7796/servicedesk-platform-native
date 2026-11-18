package com.servicedesk.notify.security;

import static com.servicedesk.notify.security.TestTokens.*;
import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.time.Instant;
import org.junit.jupiter.api.Test;
import org.springframework.security.oauth2.jwt.Jwt;
import org.springframework.security.oauth2.jwt.JwtDecoder;
import org.springframework.security.oauth2.jwt.JwtException;

class JwtDecoderTest {

    private final JwtDecoder decoder =
            JwtConfig.build(new JwtProperties(KEY_B64, "ServiceDesk", "ServiceDesk.Api"));

    private static final String SUB = "3f2b8c1e-6a4d-4e0f-9b7a-1c2d3e4f5a6b";

    @Test
    void validTokenIsAccepted() {
        Jwt jwt = decoder.decode(valid(SUB, "Approver"));
        assertThat(jwt.getSubject()).isEqualTo(SUB);
        assertThat(jwt.getClaimAsString("role")).isEqualTo("Approver");
        assertThat(jwt.getClaimAsString("name")).isEqualTo("Test User");
    }

    @Test
    void nonGuidSubjectIsAccepted() {
        assertThat(decoder.decode(valid("triage-copilot", "TriageService")).getSubject())
                .isEqualTo("triage-copilot");
    }

    @Test
    void wrongKeyIsRejected() {
        String t = token(OTHER_KEY_B64, "ServiceDesk", "ServiceDesk.Api", SUB, "Admin", Instant.now().plusSeconds(600));
        assertThatThrownBy(() -> decoder.decode(t)).isInstanceOf(JwtException.class);
    }

    @Test
    void wrongIssuerIsRejected() {
        String t = token(KEY_B64, "Evil", "ServiceDesk.Api", SUB, "Admin", Instant.now().plusSeconds(600));
        assertThatThrownBy(() -> decoder.decode(t)).isInstanceOf(JwtException.class);
    }

    @Test
    void wrongAudienceIsRejected() {
        String t = token(KEY_B64, "ServiceDesk", "Other.Api", SUB, "Admin", Instant.now().plusSeconds(600));
        assertThatThrownBy(() -> decoder.decode(t)).isInstanceOf(JwtException.class);
    }

    @Test
    void expiredTokenIsRejected() {
        String t = token(KEY_B64, "ServiceDesk", "ServiceDesk.Api", SUB, "Admin", Instant.now().minusSeconds(600));
        assertThatThrownBy(() -> decoder.decode(t)).isInstanceOf(JwtException.class);
    }

    @Test
    void garbageIsRejected() {
        assertThatThrownBy(() -> decoder.decode("not-a-jwt")).isInstanceOf(JwtException.class);
    }

    @Test
    void tokenWithoutExpIsRejected() {
        String t = token(KEY_B64, "ServiceDesk", "ServiceDesk.Api", SUB, "Admin", null);
        assertThatThrownBy(() -> decoder.decode(t)).isInstanceOf(JwtException.class);
    }

    @Test
    void hs512TokenIsRejected() {
        byte[] key64 = new byte[64];
        System.arraycopy(java.util.Base64.getDecoder().decode(KEY_B64), 0, key64, 0, 32);
        assertThatThrownBy(() -> decoder.decode(hs512(key64))).isInstanceOf(JwtException.class);
    }

    @Test
    void algNoneTokenIsRejected() {
        assertThatThrownBy(() -> decoder.decode(algNone())).isInstanceOf(JwtException.class);
    }

    @Test
    void shortKeyFailsFastWithoutPrintingTheKey() {
        String shortKey = java.util.Base64.getEncoder().encodeToString(new byte[16]);
        assertThatThrownBy(() -> JwtConfig.build(new JwtProperties(shortKey, "ServiceDesk", "ServiceDesk.Api")))
                .isInstanceOf(IllegalStateException.class)
                .hasMessageContaining("JWT_SECRET_KEY")
                .hasMessageContaining("32 bytes")
                .hasMessageNotContaining(shortKey);
    }
}
