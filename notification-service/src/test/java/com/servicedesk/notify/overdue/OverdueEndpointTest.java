package com.servicedesk.notify.overdue;

import static org.assertj.core.api.Assertions.assertThat;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

import com.servicedesk.notify.security.TestTokens;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.time.ZoneOffset;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.stream.Stream;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.Arguments;
import org.junit.jupiter.params.provider.MethodSource;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.context.TestConfiguration;
import org.springframework.boot.testcontainers.service.connection.ServiceConnection;
import org.springframework.boot.webmvc.test.autoconfigure.AutoConfigureMockMvc;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Import;
import org.springframework.context.annotation.Primary;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.web.servlet.MockMvc;
import org.testcontainers.junit.jupiter.Container;
import org.testcontainers.junit.jupiter.Testcontainers;
import org.testcontainers.postgresql.PostgreSQLContainer;
import org.yaml.snakeyaml.Yaml;
import tools.jackson.databind.JsonNode;
import tools.jackson.databind.json.JsonMapper;

/** SLA in this profile is PT1H. The clock is injected, so nothing sleeps. */
@SpringBootTest
@AutoConfigureMockMvc
@ActiveProfiles("dbtest")
@Testcontainers
@Import(OverdueEndpointTest.ClockConfig.class)
class OverdueEndpointTest {

    static final Instant NOW = Instant.parse("2026-11-18T12:00:00Z");
    static final Duration SLA = Duration.ofHours(1);

    static final UUID U1 = UUID.fromString("00000000-0000-4000-8000-000000000001");
    static final UUID U2 = UUID.fromString("00000000-0000-4000-8000-000000000002");
    static final UUID U3 = UUID.fromString("00000000-0000-4000-8000-000000000003");
    static final UUID A = UUID.fromString("aaaaaaaa-0000-4000-8000-000000000000");
    static final UUID B = UUID.fromString("bbbbbbbb-0000-4000-8000-000000000000");
    static final UUID C = UUID.fromString("cccccccc-0000-4000-8000-000000000000");
    static final UUID D = UUID.fromString("dddddddd-0000-4000-8000-000000000000");

    static class MutableClock extends Clock {
        volatile Instant now = NOW;
        public java.time.ZoneId getZone() { return ZoneOffset.UTC; }
        public Clock withZone(java.time.ZoneId zone) { return this; }
        public Instant instant() { return now; }
    }

    @TestConfiguration
    static class ClockConfig {
        @Bean @Primary
        MutableClock testClock() {
            return new MutableClock();
        }
    }

    @Container @ServiceConnection
    static PostgreSQLContainer postgres = new PostgreSQLContainer("postgres:17-alpine");

    @Autowired MockMvc mvc;
    @Autowired JdbcClient jdbc;
    @Autowired MutableClock clock;
    @Autowired JsonMapper mapper;

    @BeforeEach
    void clean() {
        jdbc.sql("DELETE FROM pending_step").update();
        clock.now = NOW;
    }

    void insert(UUID id, List<UUID> assigned, Instant pendingSince, boolean closed) {
        String arr = assigned.stream().map(UUID::toString).collect(java.util.stream.Collectors.joining(",", "{", "}"));
        jdbc.sql("""
                INSERT INTO pending_step (request_id, title, requester_id, step_order, position,
                    assigned_user_ids, pending_since, last_event_at, closed)
                VALUES (:id, 'Laptop', :req, 2, 'ItManager', CAST(:arr AS uuid[]), :ts, :ts, :closed)""")
                .param("id", id).param("req", U3).param("arr", arr)
                .param("ts", pendingSince.atOffset(ZoneOffset.UTC)).param("closed", closed).update();
    }

    static String bearer(String sub, Object role) {
        return "Bearer " + TestTokens.token(TestTokens.KEY_B64, "ServiceDesk", "ServiceDesk.Api", sub, role,
                Instant.now().plusSeconds(600));
    }

    List<String> ids(String authorization) throws Exception {
        String body = mvc.perform(get("/notify/overdue").header("Authorization", authorization))
                .andExpect(status().isOk()).andReturn().getResponse().getContentAsString();
        List<String> out = new ArrayList<>();
        mapper.readTree(body).forEach(n -> out.add(n.get("requestId").asString()));
        return out;
    }

    static List<String> s(UUID... ids) {
        return Stream.of(ids).map(UUID::toString).toList();
    }

    static Stream<Arguments> scoping() {
        return Stream.of(
                Arguments.of(U1, "Approver", s(A, D)),
                Arguments.of(U2, "Approver", s(B, D)),
                Arguments.of(U3, "Approver", s()),            // assigned to nothing
                Arguments.of(U1, "Requester", s()),           // assigned, but a Requester
                Arguments.of(U1, null, s()),                  // token with no role claim
                Arguments.of(U3, "Admin", s(A, B, C, D)),     // includes unassigned C
                Arguments.of(U3, List.of("Admin", "Approver"), s(A, B, C, D)));
    }

    @ParameterizedTest
    @MethodSource("scoping")
    void scopingByRole(UUID sub, Object role, List<String> expected) throws Exception {
        insert(D, List.of(U1, U2), NOW.minus(Duration.ofHours(2)), false);
        insert(B, List.of(U2), NOW.minus(Duration.ofHours(4)), false);
        insert(A, List.of(U1), NOW.minus(Duration.ofHours(5)), false);
        insert(C, List.of(), NOW.minus(Duration.ofHours(3)), false); // unassigned

        assertThat(ids(bearer(sub.toString(), role))).isEqualTo(expected);
    }

    @ParameterizedTest
    @MethodSource("authCases")
    void unauthenticatedOrTriageServiceIsRefused(String authorization, int expected) throws Exception {
        var req = get("/notify/overdue");
        if (authorization != null) req = req.header("Authorization", authorization);
        mvc.perform(req).andExpect(status().is(expected));
    }

    static Stream<Arguments> authCases() {
        return Stream.of(
                Arguments.of(null, 401),
                Arguments.of("Bearer junk", 401),
                Arguments.of(bearer("triage-copilot", "TriageService"), 403));
    }

    @Test
    void nonUuidSubjectGetsEmptyListNotAnError() throws Exception {
        insert(A, List.of(U1), NOW.minus(Duration.ofHours(5)), false);

        assertThat(ids(bearer("not-a-uuid", "Approver"))).isEmpty();
    }

    @Test
    void closedRowsNeverAppear() throws Exception {
        insert(A, List.of(U1), NOW.minus(Duration.ofHours(5)), true);

        assertThat(ids(bearer(U3.toString(), "Admin"))).isEmpty();
    }

    @Test
    void notYetDueAndExactlyDueAreExcludedOneSecondLaterIsIncluded() throws Exception {
        insert(A, List.of(U1), NOW.minus(SLA), false);                    // dueAt == now
        insert(B, List.of(U1), NOW.minus(Duration.ofMinutes(10)), false); // not due for 50 more minutes
        String admin = bearer(U3.toString(), "Admin");

        assertThat(ids(admin)).isEmpty();

        clock.now = NOW.plusSeconds(1);
        assertThat(ids(admin)).containsExactly(A.toString());
    }

    @Test
    void orderedByDueAtOldestFirst() throws Exception {
        insert(C, List.of(U1), NOW.minus(Duration.ofHours(2)), false);
        insert(A, List.of(U1), NOW.minus(Duration.ofHours(9)), false);
        insert(B, List.of(U1), NOW.minus(Duration.ofHours(5)), false);

        assertThat(ids(bearer(U3.toString(), "Admin"))).isEqualTo(s(A, B, C));
    }

    @Test
    void responseMatchesTheOverdueStepContract() throws Exception {
        Path file = Path.of("../contracts/openapi/notification-service.yaml");
        assertThat(file).as("contract file must exist (run from notification-service/)").exists();
        Map<String, Object> schema = schema(file);
        @SuppressWarnings("unchecked")
        Set<String> contractKeys = Set.copyOf((List<String>) schema.get("required"));
        @SuppressWarnings("unchecked")
        Set<String> declared = ((Map<String, Object>) schema.get("properties")).keySet();
        assertThat(contractKeys).isEqualTo(declared); // every field is required
        assertThat(contractKeys).hasSize(8);

        Instant pending = Instant.parse("2026-11-18T08:15:02.418Z");
        insert(A, List.of(U1, U2), pending, false);

        String body = mvc.perform(get("/notify/overdue").header("Authorization", bearer(U3.toString(), "Admin")))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$[0].pendingSince").value("2026-11-18T08:15:02.418Z"))
                .andExpect(jsonPath("$[0].dueAt").value("2026-11-18T09:15:02.418Z"))
                .andExpect(jsonPath("$[0].position").value("ItManager"))
                .andExpect(jsonPath("$[0].stepOrder").value(2))
                .andReturn().getResponse().getContentAsString();
        JsonNode first = mapper.readTree(body).get(0);
        Set<String> actual = new java.util.HashSet<>();
        first.propertyNames().forEach(actual::add);
        assertThat(actual).isEqualTo(contractKeys);
        assertThat(first.get("assignedUserIds").isArray()).isTrue();
        assertThat(first.get("assignedUserIds")).hasSize(2);
        assertThat(first.get("requestId").asString()).isEqualTo(A.toString());
        assertThat(first.get("requesterId").asString()).isEqualTo(U3.toString());
    }

    @SuppressWarnings("unchecked")
    static Map<String, Object> schema(Path file) throws Exception {
        Map<String, Object> root = new Yaml().load(Files.readString(file));
        Map<String, Object> components = (Map<String, Object>) root.get("components");
        return (Map<String, Object>) ((Map<String, Object>) components.get("schemas")).get("OverdueStep");
    }
}
