package com.servicedesk.notify.push;

import java.net.http.HttpClient;
import java.time.Duration;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.http.MediaType;
import org.springframework.http.client.JdkClientHttpRequestFactory;
import org.springframework.stereotype.Component;
import org.springframework.web.client.RestClient;
import tools.jackson.databind.JsonNode;

/** Expo's push API over plain HTTP. Never throws and never logs the token. */
@Component
public class ExpoPushSender implements PushSender {

    private static final Logger log = LoggerFactory.getLogger(ExpoPushSender.class);

    private final RestClient client;
    private final String url;

    @Autowired
    public ExpoPushSender(@Value("${app.notify.push.url}") String url) {
        this(url, Duration.ofSeconds(5));
    }

    ExpoPushSender(String url, Duration timeout) {
        this.url = url;
        HttpClient http = HttpClient.newBuilder().connectTimeout(timeout).build();
        JdkClientHttpRequestFactory factory = new JdkClientHttpRequestFactory(http);
        factory.setReadTimeout(timeout);
        this.client = RestClient.builder().requestFactory(factory).build();
    }

    @Override
    public PushResult send(PushMessage m) {
        try {
            JsonNode response = client.post().uri(url)
                    .contentType(MediaType.APPLICATION_JSON).accept(MediaType.APPLICATION_JSON)
                    .body(new Body(m.to(), m.title(), m.body(), m.data(), "default"))
                    .retrieve().body(JsonNode.class);
            return interpret(response);
        } catch (Exception e) {
            log.warn("Push to Expo failed: {}", e.getClass().getSimpleName());
            return PushResult.FAILED;
        }
    }

    private static PushResult interpret(JsonNode response) {
        JsonNode ticket = response == null ? null : response.get("data");
        if (ticket != null && ticket.isArray()) ticket = ticket.isEmpty() ? null : ticket.get(0);
        if (ticket == null || !ticket.isObject()) return PushResult.FAILED;
        JsonNode status = ticket.get("status");
        if (status != null && "ok".equals(status.asString())) return PushResult.OK;
        JsonNode error = ticket.path("details").path("error");
        return "DeviceNotRegistered".equals(error.asString("")) ? PushResult.INVALID_TOKEN : PushResult.FAILED;
    }

    private record Body(String to, String title, String body, java.util.Map<String, String> data, String sound) {}
}
