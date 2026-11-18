package com.servicedesk.notify.push;

import static org.assertj.core.api.Assertions.assertThat;

import com.sun.net.httpserver.HttpServer;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.Map;
import java.util.concurrent.atomic.AtomicReference;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import tools.jackson.databind.JsonNode;
import tools.jackson.databind.json.JsonMapper;

/** The JDK's HttpServer plays Expo. */
class ExpoPushSenderTest {

    HttpServer server;
    final AtomicReference<String> body = new AtomicReference<>();
    final AtomicReference<String> contentType = new AtomicReference<>();
    volatile int status = 200;
    volatile String response = "{\"data\":{\"status\":\"ok\",\"id\":\"x\"}}";
    volatile long delayMs = 0;
    final JsonMapper mapper = JsonMapper.builder().build();

    PushMessage message = new PushMessage("ExponentPushToken[abc]", "Approval needed", "Laptop", Map.of("requestId", "r1"));

    @BeforeEach
    void start() throws IOException {
        server = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        server.createContext("/send", ex -> {
            body.set(new String(ex.getRequestBody().readAllBytes(), StandardCharsets.UTF_8));
            contentType.set(ex.getRequestHeaders().getFirst("Content-Type"));
            try { Thread.sleep(delayMs); } catch (InterruptedException ignored) { }
            byte[] out = response.getBytes(StandardCharsets.UTF_8);
            ex.getResponseHeaders().add("Content-Type", "application/json");
            ex.sendResponseHeaders(status, out.length);
            ex.getResponseBody().write(out);
            ex.close();
        });
        server.start();
    }

    @AfterEach
    void stop() {
        server.stop(0);
    }

    ExpoPushSender sender(Duration timeout) {
        return new ExpoPushSender("http://127.0.0.1:" + server.getAddress().getPort() + "/send", timeout);
    }

    @Test
    void postsTheExpoShapeAndMapsOk() throws Exception {
        assertThat(sender(Duration.ofSeconds(2)).send(message)).isEqualTo(PushResult.OK);

        assertThat(contentType.get()).startsWith("application/json");
        JsonNode json = mapper.readTree(body.get());
        assertThat(json.get("to").asString()).isEqualTo("ExponentPushToken[abc]");
        assertThat(json.get("title").asString()).isEqualTo("Approval needed");
        assertThat(json.get("body").asString()).isEqualTo("Laptop");
        assertThat(json.get("data").get("requestId").asString()).isEqualTo("r1");
    }

    @Test
    void deviceNotRegisteredTicketMapsToInvalidToken() {
        response = "{\"data\":{\"status\":\"error\",\"message\":\"gone\",\"details\":{\"error\":\"DeviceNotRegistered\"}}}";

        assertThat(sender(Duration.ofSeconds(2)).send(message)).isEqualTo(PushResult.INVALID_TOKEN);
    }

    @Test
    void otherErrorTicketMapsToFailed() {
        response = "{\"data\":{\"status\":\"error\",\"message\":\"bad\",\"details\":{\"error\":\"MessageRateExceeded\"}}}";

        assertThat(sender(Duration.ofSeconds(2)).send(message)).isEqualTo(PushResult.FAILED);
    }

    @Test
    void arrayShapedDataIsAlsoUnderstood() {
        response = "{\"data\":[{\"status\":\"error\",\"details\":{\"error\":\"DeviceNotRegistered\"}}]}";

        assertThat(sender(Duration.ofSeconds(2)).send(message)).isEqualTo(PushResult.INVALID_TOKEN);
    }

    @Test
    void serverErrorGarbageAndTimeoutNeverThrow() {
        status = 500;
        assertThat(sender(Duration.ofSeconds(2)).send(message)).isEqualTo(PushResult.FAILED);

        status = 200;
        response = "<html>not json</html>";
        assertThat(sender(Duration.ofSeconds(2)).send(message)).isEqualTo(PushResult.FAILED);

        response = "{}";
        assertThat(sender(Duration.ofSeconds(2)).send(message)).isEqualTo(PushResult.FAILED);

        delayMs = 1500;
        assertThat(sender(Duration.ofMillis(200)).send(message)).isEqualTo(PushResult.FAILED);
    }

    @Test
    void unreachableServerNeverThrows() {
        ExpoPushSender s = new ExpoPushSender("http://127.0.0.1:1/send", Duration.ofMillis(300));

        assertThat(s.send(message)).isEqualTo(PushResult.FAILED);
    }
}
