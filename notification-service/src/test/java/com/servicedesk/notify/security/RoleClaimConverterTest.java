package com.servicedesk.notify.security;

import static org.assertj.core.api.Assertions.assertThat;

import java.time.Instant;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;
import org.springframework.security.core.GrantedAuthority;
import org.springframework.security.oauth2.jwt.Jwt;

class RoleClaimConverterTest {

    private final RoleClaimConverter converter = new RoleClaimConverter();

    private static Jwt jwt(String sub, Object role) {
        Jwt.Builder b = Jwt.withTokenValue("x").header("alg", "HS256").subject(sub)
                .issuedAt(Instant.now()).expiresAt(Instant.now().plusSeconds(60));
        if (role != null) b.claim("role", role);
        else b.claim("name", "n");
        return b.build();
    }

    private List<String> authorities(Jwt jwt) {
        return converter.convert(jwt).getAuthorities().stream().map(GrantedAuthority::getAuthority).toList();
    }

    @ParameterizedTest
    @ValueSource(strings = {"Requester", "Approver", "Admin", "TriageService"})
    void roleMapsToAuthority(String role) {
        assertThat(authorities(jwt("u1", role))).containsExactly("ROLE_" + role);
    }

    @Test
    void approverDoesNotGetAdmin() {
        assertThat(authorities(jwt("u1", "Approver"))).doesNotContain("ROLE_Admin");
    }

    @Test
    void missingOrUnknownRoleGrantsNothing() {
        assertThat(authorities(jwt("u1", null))).isEmpty();
        assertThat(authorities(jwt("u1", "Superuser"))).isEmpty();
    }

    @Test
    void roleListIsSupported() {
        assertThat(authorities(jwt("u1", List.of("Approver", "Nope")))).containsExactly("ROLE_Approver");
    }

    @Test
    void subjectIsKeptAsPlainName() {
        assertThat(converter.convert(jwt("triage-copilot", "TriageService")).getName())
                .isEqualTo("triage-copilot");
    }

    @Test
    void microsoftUriClaimIsNotUsed() {
        Jwt j = Jwt.withTokenValue("x").header("alg", "HS256").subject("u")
                .claims(c -> c.putAll(Map.of(
                        "http://schemas.microsoft.com/ws/2008/06/identity/claims/role", "Admin")))
                .build();
        assertThat(authorities(j)).isEmpty();
    }
}
