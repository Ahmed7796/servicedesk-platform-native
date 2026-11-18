package com.servicedesk.notify.security;

import org.springframework.boot.context.properties.ConfigurationProperties;

/** secretKey is base64 text; it is decoded to raw bytes, not used as UTF-8. */
@ConfigurationProperties(prefix = "app.jwt")
public record JwtProperties(String secretKey, String issuer, String audience) {
}
