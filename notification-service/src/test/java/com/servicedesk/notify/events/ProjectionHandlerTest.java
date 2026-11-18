package com.servicedesk.notify.events;

import static org.assertj.core.api.Assertions.assertThat;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.system.CapturedOutput;
import org.springframework.boot.test.system.OutputCaptureExtension;
import org.springframework.boot.testcontainers.service.connection.ServiceConnection;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.test.context.ActiveProfiles;
import org.testcontainers.junit.jupiter.Container;
import org.testcontainers.junit.jupiter.Testcontainers;
import org.testcontainers.postgresql.PostgreSQLContainer;
import tools.jackson.databind.json.JsonMapper;

/** Feeds the contract's example events (as JSON) to the handler against a real PostgreSQL. */
@SpringBootTest
@ActiveProfiles("dbtest")
@Testcontainers
@ExtendWith(OutputCaptureExtension.class)
class ProjectionHandlerTest {

    @Container @ServiceConnection
    static PostgreSQLContainer postgres = new PostgreSQLContainer("postgres:16-alpine");

    static final String REQUEST_ID = "3f6c2a9e-8b1d-4e57-9a2c-1d7e5b4f0a11";
    static final String APPROVER_1 = "5b8d0f2a-4c6e-4a1b-9d3f-7e5c3a1b9d0f";
    static final String APPROVER_2 = "9e7c5a3b-1d0f-4e2a-8b6c-4d2e0f8a6c1b";
    static final String SUBMITTED_AT = "2026-11-16T08:15:02.418Z";
    static final String STEP_APPROVED_AT = "2026-11-16T09:02:47.105Z";

    @Autowired ProjectionHandler handler;
    @Autowired JsonMapper mapper;
    @Autowired JdbcClient jdbc;

    @BeforeEach
    void clean() {
        jdbc.sql("DELETE FROM pending_step").update();
    }

    static String example(String name) throws Exception {
        return Files.readString(Path.of("../contracts/events/examples/" + name + ".json"));
    }

    void apply(String json) throws Exception {
        handler.handle(mapper.readValue(json, EventEnvelope.class));
    }

    Map<String, Object> row() {
        return jdbc.sql("SELECT request_id, title, requester_id, step_order, position, pending_since, last_event_at, closed FROM pending_step WHERE request_id = ?::uuid")
                .param(REQUEST_ID).query().singleRow();
    }

    long rowCount() {
        return jdbc.sql("SELECT count(*) FROM pending_step").query(Long.class).single();
    }

    List<String> approvers() {
        return jdbc.sql("SELECT unnest(assigned_user_ids)::text FROM pending_step WHERE request_id = ?::uuid")
                .param(REQUEST_ID).query(String.class).list();
    }

    @Test
    void submittedCreatesRowForCurrentStep() throws Exception {
        apply(example("request.submitted"));

        Map<String, Object> r = row();
        assertThat(r.get("title")).isEqualTo("Replacement laptop for field engineer");
        assertThat(r.get("requester_id").toString()).isEqualTo("7a1e4c2b-5d3f-4a8e-b6c9-2e0f1a3b4c5d");
        assertThat(r.get("step_order")).isEqualTo(1);
        assertThat(r.get("position")).isEqualTo("DepartmentHead");
        assertThat(r.get("closed")).isEqualTo(false);
        assertThat(approvers()).containsExactly(APPROVER_1);
        assertThat(((java.sql.Timestamp) r.get("pending_since")).toInstant())
                .isEqualTo(java.time.Instant.parse(SUBMITTED_AT));
        assertThat(((java.sql.Timestamp) r.get("last_event_at")).toInstant())
                .isEqualTo(java.time.Instant.parse(SUBMITTED_AT));
    }

    @Test
    void stepApprovedMovesRowToNextStepAndKeepsTitle() throws Exception {
        apply(example("request.submitted"));
        apply(example("request.step_approved"));

        Map<String, Object> r = row();
        assertThat(r.get("title")).isEqualTo("Replacement laptop for field engineer");
        assertThat(r.get("step_order")).isEqualTo(2);
        assertThat(r.get("position")).isEqualTo("ItManager");
        assertThat(approvers()).containsExactly(APPROVER_2);
        assertThat(((java.sql.Timestamp) r.get("pending_since")).toInstant())
                .isEqualTo(java.time.Instant.parse(STEP_APPROVED_AT));
        assertThat(r.get("closed")).isEqualTo(false);
    }

    @Test
    void approvedRejectedAndCancelledEachCloseTheRow() throws Exception {
        for (String terminal : List.of("request.approved", "request.rejected", "request.cancelled")) {
            clean();
            apply(example("request.submitted"));
            // terminal examples are all later than the submitted example
            apply(example(terminal));
            assertThat(row().get("closed")).as(terminal).isEqualTo(true);
        }
    }

    @Test
    void fulfilledChangesNothing() throws Exception {
        apply(example("request.submitted"));
        apply(example("request.approved"));
        Map<String, Object> before = row();

        apply(example("request.fulfilled"));

        assertThat(row()).isEqualTo(before);
    }

    @Test
    void replayingTheSameEventTwiceChangesTheTableOnce() throws Exception {
        apply(example("request.submitted"));
        apply(example("request.step_approved"));
        Map<String, Object> afterFirst = row();

        apply(example("request.step_approved"));
        apply(example("request.submitted")); // older replay must not move it back

        assertThat(rowCount()).isEqualTo(1);
        assertThat(row()).isEqualTo(afterFirst);
    }

    @Test
    void olderStepApprovedDoesNotOverwriteNewerState() throws Exception {
        apply(example("request.submitted"));
        apply(example("request.step_approved")); // step 2 at 09:02
        String older = example("request.step_approved")
                .replace(STEP_APPROVED_AT, "2026-11-16T08:30:00.000Z")
                .replace("\"order\": 2", "\"order\": 3")
                .replace("ItManager", "GeneralManager");

        apply(older);

        assertThat(row().get("step_order")).isEqualTo(2);
        assertThat(row().get("position")).isEqualTo("ItManager");
    }

    @Test
    void olderSubmittedDoesNotResurrectAClosedRequest() throws Exception {
        apply(example("request.submitted"));
        apply(example("request.rejected"));

        apply(example("request.submitted")); // arrives late

        assertThat(rowCount()).isEqualTo(1);
        assertThat(row().get("closed")).isEqualTo(true);
    }

    @Test
    void emptyApproverListIsStoredAsEmpty() throws Exception {
        apply(example("request.submitted").replace("\"" + APPROVER_1 + "\"", ""));

        assertThat(rowCount()).isEqualTo(1);
        assertThat(approvers()).isEmpty();
    }

    @Test
    void unknownPositionIsStoredAsIs() throws Exception {
        apply(example("request.submitted").replace("\"position\": \"DepartmentHead\",\n      \"scope\": \"RequesterDepartment\",\n      \"escalatedFrom\": null,\n      \"approverUserIds\"",
                "\"position\": \"ChiefOfSnacks\",\n      \"scope\": \"RequesterDepartment\",\n      \"escalatedFrom\": null,\n      \"approverUserIds\""));

        assertThat(row().get("position")).isEqualTo("ChiefOfSnacks");
    }

    @Test
    void unknownFieldsAtAnyLevelAreIgnored() throws Exception {
        String json = example("request.submitted")
                .replace("\"version\": 1", "\"version\": 1, \"newTopLevel\": {\"a\": 1}")
                .replace("\"requestType\"", "\"newPayloadField\": true, \"requestType\"")
                .replace("\"approverUserIds\"", "\"newStepField\": \"x\", \"approverUserIds\"");

        apply(json);

        assertThat(rowCount()).isEqualTo(1);
    }

    @Test
    void unknownTypeOrVersionIsIgnored() throws Exception {
        apply(example("request.submitted").replace("\"request.submitted\"", "\"request.teleported\""));
        apply(example("request.submitted").replace("\"version\": 1", "\"version\": 2"));

        assertThat(rowCount()).isZero();
    }

    @Test
    void stepApprovedWithNoRowIsIgnored() throws Exception {
        apply(example("request.step_approved"));

        assertThat(rowCount()).isZero();
    }

    @Test
    void closedRowCannotBeReopenedByALaterStepApproved() throws Exception {
        apply(example("request.submitted"));
        apply(example("request.rejected"));
        String later = example("request.step_approved").replace(STEP_APPROVED_AT, "2026-11-16T12:00:00.000Z");

        apply(later);

        assertThat(row().get("closed")).isEqualTo(true);
        assertThat(row().get("step_order")).isEqualTo(1);
        assertThat(row().get("position")).isEqualTo("DepartmentHead");
    }

    @Test
    void escalatedStepStoresGeneralManager() throws Exception {
        apply(example("request.submitted").replace(
                "\"position\": \"DepartmentHead\",\n      \"scope\": \"RequesterDepartment\",\n      \"escalatedFrom\": null,\n      \"approverUserIds\"",
                "\"position\": \"GeneralManager\",\n      \"scope\": \"RequesterDepartment\",\n      \"escalatedFrom\": \"DepartmentHead\",\n      \"approverUserIds\""));

        assertThat(row().get("position")).isEqualTo("GeneralManager");
    }

    @Test
    void emptyApproverListLogsUnassignedWarning(CapturedOutput output) throws Exception {
        apply(example("request.submitted").replace("\"" + APPROVER_1 + "\"", ""));

        assertThat(output.getOut())
                .contains("Unassigned step")
                .contains("eventId=0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d20")
                .contains("requestId=" + REQUEST_ID)
                .contains("order=1")
                .contains("position=DepartmentHead");
    }

    @Test
    void migrationCreatesExpectedColumns() {
        List<String> cols = jdbc.sql("""
                SELECT column_name FROM information_schema.columns
                WHERE table_name = 'pending_step' ORDER BY ordinal_position""")
                .query(String.class).list();
        assertThat(cols).containsExactly("request_id", "title", "requester_id", "step_order",
                "position", "assigned_user_ids", "pending_since", "last_event_at", "closed");
    }
}
