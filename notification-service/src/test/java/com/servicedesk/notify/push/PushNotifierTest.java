package com.servicedesk.notify.push;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

import com.servicedesk.notify.events.EventEnvelope;
import com.servicedesk.notify.events.EventListener;
import com.servicedesk.notify.events.ProcessedEventStore;
import com.servicedesk.notify.events.ProjectionHandler;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Instant;
import java.util.List;
import java.util.UUID;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.springframework.amqp.core.MessageBuilder;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.context.TestConfiguration;
import org.springframework.boot.test.system.CapturedOutput;
import org.springframework.boot.test.system.OutputCaptureExtension;
import org.springframework.boot.testcontainers.service.connection.ServiceConnection;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Primary;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.test.context.ActiveProfiles;
import org.testcontainers.junit.jupiter.Container;
import org.testcontainers.junit.jupiter.Testcontainers;
import org.testcontainers.postgresql.PostgreSQLContainer;
import tools.jackson.databind.json.JsonMapper;

/** Real PostgreSQL, fake Expo. Events come from the contract examples. */
@SpringBootTest
@ActiveProfiles("dbtest")
@Testcontainers
@ExtendWith(OutputCaptureExtension.class)
class PushNotifierTest {

    @Container @ServiceConnection
    static PostgreSQLContainer postgres = new PostgreSQLContainer("postgres:17-alpine");

    @TestConfiguration
    static class Config {
        @Bean @Primary
        FakePushSender fakePushSender() {
            return new FakePushSender();
        }
    }

    static final UUID APPROVER_1 = UUID.fromString("5b8d0f2a-4c6e-4a1b-9d3f-7e5c3a1b9d0f"); // submitted example
    static final UUID APPROVER_2 = UUID.fromString("9e7c5a3b-1d0f-4e2a-8b6c-4d2e0f8a6c1b"); // step_approved example
    static final UUID OTHER = UUID.fromString("00000000-0000-4000-8000-0000000000cc");
    static final UUID ADMIN = UUID.fromString("00000000-0000-4000-8000-0000000000ad");
    static final String TITLE = "Replacement laptop for field engineer";

    @Autowired FakePushSender sender;
    @Autowired DeviceRepository devices;
    @Autowired ProjectionHandler handler;
    @Autowired PushNotifier notifier;
    @Autowired JdbcClient jdbc;
    @Autowired JsonMapper mapper;

    ProcessedEventStore store = mock(ProcessedEventStore.class);
    EventListener listener;

    @BeforeEach
    void clean() {
        jdbc.sql("DELETE FROM pending_step").update();
        jdbc.sql("DELETE FROM device").update();
        sender.reset();
        when(store.isProcessed(any())).thenReturn(false);
        listener = new EventListener(mapper, store, handler, notifier);
        devices.upsert("ExponentPushToken[a1]", APPROVER_1, "ios", List.of("Approver"), Instant.now());
        devices.upsert("ExponentPushToken[a2]", APPROVER_1, "android", List.of("Approver"), Instant.now());
        devices.upsert("ExponentPushToken[b1]", APPROVER_2, "ios", List.of("Approver"), Instant.now());
        devices.upsert("ExponentPushToken[c1]", OTHER, "ios", List.of("Approver"), Instant.now());
        devices.upsert("ExponentPushToken[d1]", ADMIN, "ios", List.of("Admin"), Instant.now());
    }

    static String example(String name) throws Exception {
        return Files.readString(Path.of("../contracts/events/examples/" + name + ".json"));
    }

    void deliver(String json) {
        listener.onMessage(MessageBuilder.withBody(json.getBytes(StandardCharsets.UTF_8)).build());
    }

    @Test
    void submittedPushesTheFirstStepApproversDevicesOnly() throws Exception {
        deliver(example("request.submitted"));

        // APPROVER_1 has two devices; OTHER, APPROVER_2 and the admin are not approvers of this step.
        assertThat(sender.recipients()).containsExactly("ExponentPushToken[a1]", "ExponentPushToken[a2]");
        PushMessage m = sender.sent.get(0);
        assertThat(m.title()).isEqualTo("Approval needed");
        assertThat(m.body()).contains(TITLE);
        assertThat(m.data()).containsEntry("requestId", "3f6c2a9e-8b1d-4e57-9a2c-1d7e5b4f0a11")
                .containsEntry("type", "request.submitted");
    }

    @Test
    void stepApprovedPushesTheNextStepApproversWithTheStoredTitle() throws Exception {
        deliver(example("request.submitted"));
        sender.sent.clear();

        deliver(example("request.step_approved"));

        assertThat(sender.recipients()).containsExactly("ExponentPushToken[b1]");
        assertThat(sender.sent.get(0).body()).contains(TITLE);
    }

    @Test
    void emptyApproverListPushesAdminDevicesOnly() throws Exception {
        deliver(example("request.submitted").replaceAll("\"approverUserIds\": \\[[^\\]]*\\]", "\"approverUserIds\": []"));

        assertThat(sender.recipients()).containsExactly("ExponentPushToken[d1]");
        assertThat(sender.sent.get(0).body()).startsWith("No approver assigned");
    }

    @Test
    void approverWithNoDeviceMeansNoPushAndNoError() throws Exception {
        jdbc.sql("DELETE FROM device").update();

        deliver(example("request.submitted"));

        assertThat(sender.sent).isEmpty();
        assertThat(jdbc.sql("SELECT count(*) FROM pending_step").query(Long.class).single()).isEqualTo(1);
    }

    @Test
    void closingEventsPushNothing() throws Exception {
        deliver(example("request.submitted"));
        sender.sent.clear();

        deliver(example("request.approved"));
        deliver(example("request.fulfilled"));

        assertThat(sender.sent).isEmpty();
    }

    @Test
    void sameTimestampEventThatChangesNothingPushesNothing() throws Exception {
        deliver(example("request.submitted"));
        sender.sent.clear();

        // same request, same instant as the stored row: the strict guard ignores it
        deliver(example("request.submitted").replace("0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d20",
                "0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d21"));

        assertThat(sender.sent).isEmpty();
    }

    @Test
    void strictlyOlderEventPushesNothingAndLeavesTheRowAlone() throws Exception {
        deliver(example("request.submitted"));
        sender.sent.clear();

        deliver(example("request.submitted")
                .replace("0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d20", "0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d22")
                .replace("2026-11-16T08:15:02.418Z", "2026-11-16T08:00:00.000Z"));

        assertThat(sender.sent).isEmpty();
        assertThat(jdbc.sql("SELECT pending_since FROM pending_step").query(java.time.OffsetDateTime.class).single()
                .toInstant()).isEqualTo(Instant.parse("2026-11-16T08:15:02.418Z"));
    }

    @Test
    void senderThrowingDoesNotBreakProcessingOrTheProjection() throws Exception {
        sender.behaviour = m -> { throw new IllegalStateException("expo exploded"); };

        deliver(example("request.submitted"));

        assertThat(jdbc.sql("SELECT count(*) FROM pending_step").query(Long.class).single()).isEqualTo(1);
        verify(store).markProcessed(UUID.fromString("0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d20"));
    }

    @Test
    void failedResultIsKeptAndOtherDevicesStillGetTheirPush() throws Exception {
        sender.behaviour = m -> m.to().equals("ExponentPushToken[a1]") ? PushResult.FAILED : PushResult.OK;

        deliver(example("request.submitted"));

        assertThat(sender.recipients()).containsExactly("ExponentPushToken[a1]", "ExponentPushToken[a2]");
        assertThat(devices.tokensForUsers(List.of(APPROVER_1))).hasSize(2);
    }

    @Test
    void invalidTokenResultRemovesThatDevice() throws Exception {
        sender.behaviour = m -> m.to().equals("ExponentPushToken[a1]") ? PushResult.INVALID_TOKEN : PushResult.OK;

        deliver(example("request.submitted"));

        assertThat(devices.tokensForUsers(List.of(APPROVER_1))).containsExactly("ExponentPushToken[a2]");
    }

    @Test
    void logsNeverContainATokenOrTheRequestTitle(CapturedOutput out) throws Exception {
        sender.behaviour = m -> PushResult.FAILED;

        deliver(example("request.submitted"));

        assertThat(out.getAll()).doesNotContain("ExponentPushToken[").doesNotContain(TITLE);
    }

    @Test
    void disabledSwitchSendsNothing() throws Exception {
        PushNotifier off = new PushNotifier(devices, sender, false);

        off.notifyPending(new PendingNotice(UUID.randomUUID(), "request.submitted", "t", List.of(APPROVER_1)));

        assertThat(sender.sent).isEmpty();
    }
}
