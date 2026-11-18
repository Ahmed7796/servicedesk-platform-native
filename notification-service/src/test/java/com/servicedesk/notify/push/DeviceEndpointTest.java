package com.servicedesk.notify.push;

import static org.assertj.core.api.Assertions.assertThat;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

import com.servicedesk.notify.security.TestTokens;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Instant;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.testcontainers.service.connection.ServiceConnection;
import org.springframework.boot.webmvc.test.autoconfigure.AutoConfigureMockMvc;
import org.springframework.http.MediaType;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.MvcResult;
import org.testcontainers.junit.jupiter.Container;
import org.testcontainers.junit.jupiter.Testcontainers;
import org.testcontainers.postgresql.PostgreSQLContainer;
import org.yaml.snakeyaml.Yaml;
import tools.jackson.databind.JsonNode;
import tools.jackson.databind.json.JsonMapper;

@SpringBootTest
@AutoConfigureMockMvc
@ActiveProfiles("dbtest")
@Testcontainers
class DeviceEndpointTest {

    @Container @ServiceConnection
    static PostgreSQLContainer postgres = new PostgreSQLContainer("postgres:17-alpine");

    static final UUID U1 = UUID.fromString("00000000-0000-4000-8000-000000000001");
    static final UUID U2 = UUID.fromString("00000000-0000-4000-8000-000000000002");
    static final String TOKEN = "ExponentPushToken[abc123]";
    static final String OK_BODY = "{\"expoPushToken\":\"" + TOKEN + "\",\"platform\":\"ios\"}";

    @Autowired MockMvc mvc;
    @Autowired JdbcClient jdbc;
    @Autowired JsonMapper mapper;

    @BeforeEach
    void clean() {
        jdbc.sql("DELETE FROM device").update();
    }

    static String bearer(String sub, Object role) {
        return "Bearer " + TestTokens.token(TestTokens.KEY_B64, "ServiceDesk", "ServiceDesk.Api", sub, role,
                Instant.now().plusSeconds(600));
    }

    MvcResult register(String auth, String body) throws Exception {
        var req = post("/notify/devices").contentType(MediaType.APPLICATION_JSON).content(body);
        if (auth != null) req = req.header("Authorization", auth);
        return mvc.perform(req).andReturn();
    }

    List<Map<String, Object>> rows() {
        return jdbc.sql("SELECT expo_push_token, user_id::text AS user_id, platform, roles::text AS roles FROM device ORDER BY expo_push_token")
                .query().listOfRows();
    }

    @Test
    void registersTheTokenForTheCallerAndStoresRoles() throws Exception {
        MvcResult r = register(bearer(U1.toString(), List.of("Approver", "Admin")), OK_BODY);

        assertThat(r.getResponse().getStatus()).isEqualTo(204);
        assertThat(r.getResponse().getContentAsString()).isEmpty();
        assertThat(rows()).hasSize(1);
        Map<String, Object> row = rows().get(0);
        assertThat(row.get("expo_push_token")).isEqualTo(TOKEN);
        assertThat(row.get("user_id")).isEqualTo(U1.toString());
        assertThat(row.get("platform")).isEqualTo("ios");
        assertThat((String) row.get("roles")).contains("Approver").contains("Admin");
    }

    @Test
    void sameTokenTwiceIsOneRowAndStill204() throws Exception {
        register(bearer(U1.toString(), "Approver"), OK_BODY);
        MvcResult second = register(bearer(U1.toString(), "Approver"),
                OK_BODY.replace("ios", "android"));

        assertThat(second.getResponse().getStatus()).isEqualTo(204);
        assertThat(rows()).hasSize(1);
        assertThat(rows().get(0).get("platform")).isEqualTo("android");
    }

    @Test
    void sameTokenForAnotherUserMovesToThatUserAndTakesTheirRoles() throws Exception {
        register(bearer(U1.toString(), "Admin"), OK_BODY);
        MvcResult r = register(bearer(U2.toString(), "Requester"), OK_BODY);

        assertThat(r.getResponse().getStatus()).isEqualTo(204);
        assertThat(rows()).hasSize(1);
        assertThat(rows().get(0).get("user_id")).isEqualTo(U2.toString());
        assertThat((String) rows().get(0).get("roles")).doesNotContain("Admin");
    }

    @Test
    void oneUserCanHaveSeveralDevices() throws Exception {
        register(bearer(U1.toString(), "Approver"), OK_BODY);
        register(bearer(U1.toString(), "Approver"),
                "{\"expoPushToken\":\"ExpoPushToken[second]\",\"platform\":\"android\"}");

        assertThat(rows()).hasSize(2);
        assertThat(rows()).allMatch(r -> r.get("user_id").equals(U1.toString()));
    }

    @ParameterizedTest
    @ValueSource(strings = {
        "",
        "not json",
        "{}",
        "{\"expoPushToken\":\"ExponentPushToken[abc]\"}",
        "{\"platform\":\"ios\"}",
        "{\"expoPushToken\":\"ExponentPushToken[abc]\",\"platform\":\"windows\"}",
        "{\"expoPushToken\":\"ExponentPushToken[abc]\",\"platform\":\"IOS\"}",
        "{\"expoPushToken\":\"abc\",\"platform\":\"ios\"}",
        "{\"expoPushToken\":\"ExponentPushToken[]\",\"platform\":\"ios\"}",
        "{\"expoPushToken\":\"xExponentPushToken[a]\",\"platform\":\"ios\"}",
        "{\"expoPushToken\":\"ExponentPushToken[a]x\",\"platform\":\"ios\"}",
        "{\"expoPushToken\":5,\"platform\":\"ios\"}",
        "{\"expoPushToken\":null,\"platform\":\"ios\"}",
        "{\"expoPushToken\":\"ExponentPushToken[abc]\",\"platform\":\"ios\",\"extra\":1}",
        "[]",
    })
    void invalidBodyIs400AndNothingIsStored(String body) throws Exception {
        MvcResult r = register(bearer(U1.toString(), "Approver"), body);

        assertThat(r.getResponse().getStatus()).as(body).isEqualTo(400);
        assertThat(rows()).as(body).isEmpty();
    }

    @Test
    void errorBodyMatchesTheContractProblemDetails() throws Exception {
        Path file = Path.of("../contracts/openapi/notification-service.yaml");
        assertThat(file).as("contract file must exist (run from notification-service/)").exists();
        @SuppressWarnings("unchecked")
        Map<String, Object> root = new Yaml().load(Files.readString(file));
        @SuppressWarnings("unchecked")
        Map<String, Object> comps = (Map<String, Object>) root.get("components");
        @SuppressWarnings("unchecked")
        Map<String, Object> schema = (Map<String, Object>) ((Map<String, Object>) comps.get("schemas")).get("ProblemDetails");
        @SuppressWarnings("unchecked")
        Set<String> required = Set.copyOf((List<String>) schema.get("required"));
        @SuppressWarnings("unchecked")
        Map<String, Object> props = (Map<String, Object>) schema.get("properties");

        for (String body : List.of("{}", "not json", "{\"expoPushToken\":\"ExponentPushToken[a]\",\"platform\":\"ios\",\"x\":1}")) {
            MvcResult r = register(bearer(U1.toString(), "Approver"), body);
            assertThat(r.getResponse().getStatus()).isEqualTo(400);
            assertThat(r.getResponse().getContentType()).startsWith("application/problem+json");
            JsonNode json = mapper.readTree(r.getResponse().getContentAsString());
            for (String key : required) assertThat(json.has(key)).as(key).isTrue();
            assertThat(json.get("status").asInt()).isEqualTo(400);
            assertThat(json.get("title").isString()).isTrue();
            json.propertyNames().forEach(n -> {
                if (props.containsKey(n)) {
                    String type = (String) ((Map<?, ?>) props.get(n)).get("type");
                    if ("string".equals(type)) assertThat(json.get(n).isString()).as(n).isTrue();
                    if ("integer".equals(type)) assertThat(json.get(n).isInt()).as(n).isTrue();
                }
            });
        }
    }

    @Test
    void nonUuidSubjectIs400() throws Exception {
        MvcResult r = register(bearer("not-a-uuid", "Approver"), OK_BODY);

        assertThat(r.getResponse().getStatus()).isEqualTo(400);
        assertThat(rows()).isEmpty();
    }

    @Test
    void noTokenIs401AndJunkTokenIs401() throws Exception {
        assertThat(register(null, OK_BODY).getResponse().getStatus()).isEqualTo(401);
        assertThat(register("Bearer junk", OK_BODY).getResponse().getStatus()).isEqualTo(401);
        assertThat(rows()).isEmpty();
    }

    @Test
    void triageServiceTokenGets403EvenWithAnInvalidBodyAndWritesNothing() throws Exception {
        String auth = bearer("triage-copilot", "TriageService");

        assertThat(register(auth, OK_BODY).getResponse().getStatus()).isEqualTo(403);
        assertThat(register(auth, "{}").getResponse().getStatus()).isEqualTo(403);
        assertThat(rows()).isEmpty();
    }

    @Test
    void requestWithValidBodyReturnsNoBody() throws Exception {
        mvc.perform(post("/notify/devices").contentType(MediaType.APPLICATION_JSON).content(OK_BODY)
                .header("Authorization", bearer(U1.toString(), "Requester")))
                .andExpect(status().isNoContent());
    }
}
