package com.servicedesk.notify.overdue;

import static org.assertj.core.api.Assertions.assertThat;

import com.servicedesk.notify.events.EventEnvelope;
import com.servicedesk.notify.events.ProjectionHandler;
import com.servicedesk.notify.push.DeviceRepository;
import com.servicedesk.notify.push.FakePushSender;
import com.servicedesk.notify.push.PushMessage;
import com.servicedesk.notify.push.PushResult;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.time.OffsetDateTime;
import java.time.ZoneOffset;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.CyclicBarrier;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.stream.Collectors;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.RepeatedTest;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.context.TestConfiguration;
import org.springframework.boot.test.system.CapturedOutput;
import org.springframework.boot.test.system.OutputCaptureExtension;
import org.springframework.boot.testcontainers.service.connection.ServiceConnection;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Import;
import org.springframework.context.annotation.Primary;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.transaction.PlatformTransactionManager;
import org.springframework.transaction.support.TransactionTemplate;
import org.testcontainers.junit.jupiter.Container;
import org.testcontainers.junit.jupiter.Testcontainers;
import org.testcontainers.postgresql.PostgreSQLContainer;
import tools.jackson.databind.json.JsonMapper;

/** Real PostgreSQL, fake Expo, injected clock. SLA in the dbtest profile is PT1H. Nothing sleeps. */
@SpringBootTest
@ActiveProfiles("dbtest")
@Testcontainers
@ExtendWith(OutputCaptureExtension.class)
@Import(OverdueJobTest.TestBeans.class)
class OverdueJobTest {

    static final Instant NOW = Instant.parse("2026-11-18T12:00:00Z");
    static final Duration SLA = Duration.ofHours(1);
    static final UUID REQUESTER = UUID.fromString("00000000-0000-4000-8000-000000000003");
    static final UUID APPROVER = UUID.fromString("00000000-0000-4000-8000-0000000000a1");
    static final UUID ADMIN = UUID.fromString("00000000-0000-4000-8000-0000000000ad");
    static final String TITLE = "Secret laptop title";
    static final String TOKEN = "ExponentPushToken[secret-token-1]";

    static class MutableClock extends Clock {
        volatile Instant now = NOW;
        public java.time.ZoneId getZone() { return ZoneOffset.UTC; }
        public Clock withZone(java.time.ZoneId zone) { return this; }
        public Instant instant() { return now; }
    }

    @TestConfiguration
    static class TestBeans {
        @Bean @Primary MutableClock testClock() { return new MutableClock(); }
        @Bean @Primary FakePushSender fakePushSender() { return new FakePushSender(); }
    }

    @Container @ServiceConnection
    static PostgreSQLContainer postgres = new PostgreSQLContainer("postgres:17-alpine");

    @Autowired OverdueJob job;
    @Autowired OverdueClaimRepository claims;
    @Autowired ProjectionHandler handler;
    @Autowired DeviceRepository devices;
    @Autowired FakePushSender sender;
    @Autowired MutableClock clock;
    @Autowired JdbcClient jdbc;
    @Autowired JsonMapper mapper;
    @Autowired PlatformTransactionManager txManager;

    @BeforeEach
    void clean() {
        jdbc.sql("DELETE FROM pending_step").update();
        jdbc.sql("DELETE FROM device").update();
        sender.reset();
        clock.now = NOW;
        devices.upsert(TOKEN, APPROVER, "ios", List.of("Approver"), NOW);
    }

    UUID insert(List<UUID> assigned, Instant pendingSince, boolean closed) {
        UUID id = UUID.randomUUID();
        String arr = assigned.stream().map(UUID::toString).collect(Collectors.joining(",", "{", "}"));
        jdbc.sql("""
                INSERT INTO pending_step (request_id, title, requester_id, step_order, position,
                    assigned_user_ids, pending_since, last_event_at, closed)
                VALUES (:id, :title, :req, 1, 'AnyPosition', CAST(:arr AS uuid[]), :ts, :ts, :closed)""")
                .param("id", id).param("title", TITLE).param("req", REQUESTER).param("arr", arr)
                .param("ts", pendingSince.atOffset(ZoneOffset.UTC)).param("closed", closed).update();
        return id;
    }

    Instant overdueSince(UUID id) {
        OffsetDateTime t = jdbc.sql("SELECT overdue_since FROM pending_step WHERE request_id = :id")
                .param("id", id).query(OffsetDateTime.class).optional().orElse(null);
        return t == null ? null : t.toInstant();
    }

    static final Instant LONG_AGO = NOW.minus(Duration.ofHours(2));

    // ---- claimed once

    @Test
    void dueStepIsClaimedAndPushedOnceAcrossTwoSequentialRuns() {
        UUID id = insert(List.of(APPROVER), LONG_AGO, false);

        job.runOnce();
        clock.now = NOW.plusSeconds(60);
        job.runOnce();

        assertThat(sender.sent).hasSize(1);
        assertThat(sender.sent.get(0).to()).isEqualTo(TOKEN);
        assertThat(overdueSince(id)).isEqualTo(NOW); // set by the first run, not moved by the second
    }

    @RepeatedTest(5)
    void twoConcurrentRunsPushEachStepExactlyOnce() throws Exception {
        List<UUID> ids = new ArrayList<>();
        for (int i = 0; i < 5; i++) {
            UUID user = UUID.randomUUID();
            devices.upsert("ExponentPushToken[u" + i + "]", user, "ios", List.of("Approver"), NOW);
            ids.add(insert(List.of(user), LONG_AGO, false));
        }
        CyclicBarrier start = new CyclicBarrier(2);
        ExecutorService pool = Executors.newFixedThreadPool(2);
        try {
            List<Future<?>> runs = new ArrayList<>();
            for (int t = 0; t < 2; t++) {
                runs.add(pool.submit(() -> { start.await(10, TimeUnit.SECONDS); job.runOnce(); return null; }));
            }
            for (Future<?> f : runs) f.get(30, TimeUnit.SECONDS);
        } finally {
            pool.shutdownNow();
        }
        assertThat(sender.recipients()).hasSize(5).doesNotHaveDuplicates();
        ids.forEach(id -> assertThat(overdueSince(id)).isEqualTo(NOW));
    }

    /**
     * Forces the real overlap: run A holds the row locks inside an open transaction, run B is
     * proven blocked on them (pg_stat_activity shows a lock wait), and only then does A commit.
     * B must wake up, re-check overdue_since IS NULL and claim nothing.
     */
    @Test
    void secondClaimBlockedOnTheSameRowsClaimsNothingAfterTheFirstCommits() throws Exception {
        for (int i = 0; i < 3; i++) insert(List.of(APPROVER), LONG_AGO, false);
        ExecutorService pool = Executors.newFixedThreadPool(2);
        try {
            var aHoldsLocks = new java.util.concurrent.CountDownLatch(1);
            var bIsBlocked = new java.util.concurrent.CountDownLatch(1);
            Future<Integer> a = pool.submit(() -> new TransactionTemplate(txManager).execute(status -> {
                int n = claims.claimDue(NOW, SLA).size();
                aHoldsLocks.countDown();
                try {
                    if (!bIsBlocked.await(20, TimeUnit.SECONDS)) throw new IllegalStateException("B never blocked");
                } catch (InterruptedException e) {
                    throw new IllegalStateException(e);
                }
                return n;
            }));
            assertThat(aHoldsLocks.await(20, TimeUnit.SECONDS)).isTrue();
            Future<Integer> b = pool.submit(() -> claims.claimDue(NOW, SLA).size());

            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(20);
            boolean blocked = false;
            while (!blocked && System.nanoTime() < deadline) {
                blocked = jdbc.sql("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' "
                        + "AND query LIKE 'UPDATE pending_step SET overdue_since%'").query(Long.class).single() > 0;
                if (!blocked) Thread.onSpinWait();
            }
            assertThat(blocked).as("B waits on A's row locks").isTrue();
            assertThat(b.isDone()).isFalse();
            bIsBlocked.countDown(); // A commits now

            assertThat(a.get(30, TimeUnit.SECONDS)).isEqualTo(3);
            assertThat(b.get(30, TimeUnit.SECONDS)).isZero();
        } finally {
            pool.shutdownNow();
        }
    }

    // ---- reset on change (contract examples: submitted 08:15:02.418Z, step_approved 09:02:47.105Z)

    static final UUID REQ = UUID.fromString("3f6c2a9e-8b1d-4e57-9a2c-1d7e5b4f0a11");
    static final UUID APPROVER_1 = UUID.fromString("5b8d0f2a-4c6e-4a1b-9d3f-7e5c3a1b9d0f");
    static final UUID APPROVER_2 = UUID.fromString("9e7c5a3b-1d0f-4e2a-8b6c-4d2e0f8a6c1b");

    static String example(String name) throws Exception {
        return Files.readString(Path.of("../contracts/events/examples/" + name + ".json"));
    }

    void apply(String json) throws Exception {
        handler.handle(mapper.readValue(json, EventEnvelope.class));
    }

    @Test
    void stepChangeResetsTheClaimSoTheNewStepGetsItsOwnPush() throws Exception {
        devices.upsert("ExponentPushToken[a1]", APPROVER_1, "ios", List.of("Approver"), NOW);
        devices.upsert("ExponentPushToken[b1]", APPROVER_2, "ios", List.of("Approver"), NOW);
        apply(example("request.submitted"));
        clock.now = Instant.parse("2026-11-16T09:20:00Z"); // step 1 due 09:15:02
        job.runOnce();
        assertThat(sender.recipients()).containsExactly("ExponentPushToken[a1]");
        assertThat(overdueSince(REQ)).isNotNull();
        sender.sent.clear();

        apply(example("request.step_approved")); // step 2 pending since 09:02:47, due 10:02:47
        assertThat(overdueSince(REQ)).isNull();
        job.runOnce();
        assertThat(sender.sent).isEmpty(); // not due yet

        clock.now = Instant.parse("2026-11-16T10:10:00Z");
        job.runOnce();
        assertThat(sender.recipients()).containsExactly("ExponentPushToken[b1]");
        job.runOnce();
        assertThat(sender.sent).hasSize(1);
    }

    @Test
    void resubmitResetsTheClaimOnlyWhenTheGuardLetsTheWriteThrough() throws Exception {
        apply(example("request.submitted"));
        clock.now = Instant.parse("2026-11-16T09:20:00Z");
        job.runOnce();
        Instant claimedAt = overdueSince(REQ);
        assertThat(claimedAt).isNotNull();
        String base = example("request.submitted");

        // same instant as the stored row, and strictly older: the strict guard blocks both
        apply(base.replace("0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d20", "0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d21"));
        apply(base.replace("0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d20", "0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d22")
                .replace("2026-11-16T08:15:02.418Z", "2026-11-16T08:00:00.000Z"));
        assertThat(overdueSince(REQ)).isEqualTo(claimedAt);

        // strictly newer: the write goes through and the claim is cleared
        apply(base.replace("0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d20", "0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d23")
                .replace("2026-11-16T08:15:02.418Z", "2026-11-16T09:10:00.000Z"));
        assertThat(overdueSince(REQ)).isNull();
    }

    // ---- skipped rows

    @Test
    void closedNotYetDueAndExactlyDueRowsAreSkipped() {
        UUID closed = insert(List.of(APPROVER), LONG_AGO, true);
        UUID fresh = insert(List.of(APPROVER), NOW.minus(Duration.ofMinutes(30)), false);
        UUID exact = insert(List.of(APPROVER), NOW.minus(SLA), false); // due exactly now: not overdue
        UUID justPast = insert(List.of(APPROVER), NOW.minus(SLA).minusSeconds(1), false);

        job.runOnce();

        assertThat(overdueSince(closed)).isNull();
        assertThat(overdueSince(fresh)).isNull();
        assertThat(overdueSince(exact)).isNull();
        assertThat(overdueSince(justPast)).isEqualTo(NOW);
        assertThat(sender.sent).hasSize(1);
    }

    // ---- sender failure

    @Test
    void failedOrThrowingSenderDoesNotUnmarkTheStepAndIsNotRetried() {
        UUID id = insert(List.of(APPROVER), LONG_AGO, false);
        sender.behaviour = m -> PushResult.FAILED;
        job.runOnce();
        assertThat(overdueSince(id)).isEqualTo(NOW);

        UUID id2 = insert(List.of(APPROVER), LONG_AGO, false);
        sender.behaviour = m -> { throw new IllegalStateException("expo exploded"); };
        job.runOnce();
        assertThat(overdueSince(id2)).isEqualTo(NOW);

        sender.sent.clear();
        sender.behaviour = m -> PushResult.OK;
        job.runOnce();
        assertThat(sender.sent).isEmpty(); // at most once: no retry
    }

    @Test
    void invalidTokenResultRemovesThatDevice() {
        insert(List.of(APPROVER), LONG_AGO, false);
        sender.behaviour = m -> PushResult.INVALID_TOKEN;
        job.runOnce();
        assertThat(devices.tokensForUsers(List.of(APPROVER))).isEmpty();
    }

    // ---- recipients and wording

    @Test
    void unassignedStepGoesToAdminDevicesOnlyAndAssignedStepToItsApprovers() {
        devices.upsert("ExponentPushToken[admin]", ADMIN, "ios", List.of("Admin"), NOW);
        insert(List.of(), LONG_AGO, false);

        job.runOnce();
        assertThat(sender.recipients()).containsExactly("ExponentPushToken[admin]");
        assertThat(sender.sent.get(0).body()).isEqualTo("Overdue: " + TITLE);

        sender.sent.clear();
        insert(List.of(APPROVER), LONG_AGO, false);
        job.runOnce();
        assertThat(sender.recipients()).containsExactly(TOKEN);
    }

    @Test
    void messageUsesOverdueWordingAndType() {
        UUID id = insert(List.of(APPROVER), LONG_AGO, false);
        job.runOnce();

        PushMessage m = sender.sent.get(0);
        assertThat(m.title()).isEqualTo("Overdue");
        assertThat(m.body()).isEqualTo("Overdue: " + TITLE);
        assertThat(m.data()).containsEntry("requestId", id.toString()).containsEntry("type", "step.overdue");
    }

    // ---- logs

    @Test
    void logsNeverContainATokenOrTheTitle(CapturedOutput output) {
        insert(List.of(APPROVER), LONG_AGO, false);
        sender.behaviour = m -> PushResult.FAILED;

        job.runOnce();

        assertThat(output.getAll()).contains("Overdue job: 1 step(s) newly overdue")
                .doesNotContain("secret-token-1").doesNotContain("ExponentPushToken").doesNotContain(TITLE);
    }

    @Test
    void runOnceAndItsClassAreNotTransactional() throws Exception {
        assertThat(OverdueJob.class.getMethod("runOnce")
                .isAnnotationPresent(org.springframework.transaction.annotation.Transactional.class)).isFalse();
        assertThat(OverdueJob.class
                .isAnnotationPresent(org.springframework.transaction.annotation.Transactional.class)).isFalse();
    }
}
