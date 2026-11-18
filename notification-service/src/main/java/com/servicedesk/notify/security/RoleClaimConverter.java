package com.servicedesk.notify.security;

import java.util.Collection;
import java.util.List;
import java.util.Set;
import org.springframework.core.convert.converter.Converter;
import org.springframework.security.authentication.AbstractAuthenticationToken;
import org.springframework.security.core.GrantedAuthority;
import org.springframework.security.core.authority.SimpleGrantedAuthority;
import org.springframework.security.oauth2.jwt.Jwt;
import org.springframework.security.oauth2.server.resource.authentication.JwtAuthenticationToken;

/** Maps the plain "role" claim to ROLE_ authorities. Unknown roles grant nothing. */
public class RoleClaimConverter implements Converter<Jwt, AbstractAuthenticationToken> {

    static final Set<String> KNOWN_ROLES = Set.of("Requester", "Approver", "Admin", "TriageService");

    @Override
    public AbstractAuthenticationToken convert(Jwt jwt) {
        Object claim = jwt.getClaims().get("role");
        List<?> values = claim instanceof Collection<?> c ? List.copyOf(c)
                : claim == null ? List.of() : List.of(claim);
        Collection<GrantedAuthority> authorities = values.stream()
                .map(String::valueOf)
                .filter(KNOWN_ROLES::contains)
                .<GrantedAuthority>map(r -> new SimpleGrantedAuthority("ROLE_" + r))
                .toList();
        // sub is used as the name as-is: it may be a GUID or "triage-copilot".
        return new JwtAuthenticationToken(jwt, authorities, jwt.getSubject());
    }
}
