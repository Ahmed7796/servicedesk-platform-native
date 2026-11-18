package com.servicedesk.notify.push;

import java.util.Set;
import java.util.regex.Pattern;
import tools.jackson.databind.JsonNode;

/**
 * Body of POST /notify/devices, checked by hand against the contract (additionalProperties
 * false, token pattern, platform enum). The global Jackson setting stays lenient because the
 * event parsing must ignore unknown fields, and {@code @JsonIgnoreProperties(ignoreUnknown=false)}
 * does not override it (tried: the extra-property test still got 204). No validation starter is
 * on the classpath either, so a small explicit check is the least machinery.
 */
public record DeviceRegistration(String expoPushToken, String platform) {

    private static final Pattern TOKEN = Pattern.compile("^Expo(nent)?PushToken\\[.+\\]$");
    private static final Set<String> PLATFORMS = Set.of("ios", "android");
    private static final Set<String> FIELDS = Set.of("expoPushToken", "platform");

    /** Returns a problem description, or null when the body is valid. */
    public static String problem(JsonNode body) {
        if (body == null || !body.isObject()) return "body must be a JSON object";
        for (String name : body.propertyNames()) {
            if (!FIELDS.contains(name)) return "unknown property: " + name;
        }
        JsonNode token = body.get("expoPushToken");
        if (token == null || !token.isString() || !TOKEN.matcher(token.asString()).matches()) {
            return "expoPushToken must look like ExponentPushToken[...]";
        }
        JsonNode platform = body.get("platform");
        if (platform == null || !platform.isString() || !PLATFORMS.contains(platform.asString())) {
            return "platform must be ios or android";
        }
        return null;
    }

    /** Only call after {@link #problem} returned null. */
    public static DeviceRegistration of(JsonNode body) {
        return new DeviceRegistration(body.get("expoPushToken").asString(), body.get("platform").asString());
    }
}
