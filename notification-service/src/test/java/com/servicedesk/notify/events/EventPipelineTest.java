package com.servicedesk.notify.events;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.argThat;
import static org.mockito.Mockito.doCallRealMethod;
import static org.mockito.Mockito.doThrow;
import static org.mockito.Mockito.times;
import static org.mockito.Mockito.verify;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.content;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.Set;
import java.util.UUID;
import org.awaitility.Awaitility;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.amqp.core.Message;
import org.springframework.amqp.core.MessageBuilder;
import org.springframework.amqp.core.MessageDeliveryMode;
import org.springframework.amqp.rabbit.core.RabbitAdmin;
import org.springframework.amqp.rabbit.core.RabbitTemplate;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.health.actuate.endpoint.CompositeHealthDescriptor;
import org.springframework.boot.health.actuate.endpoint.HealthEndpoint;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.testcontainers.service.connection.ServiceConnection;
import org.springframework.boot.webmvc.test.autoconfigure.AutoConfigureMockMvc;
import org.springframework.data.redis.core.StringRedisTemplate;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.context.bean.override.mockito.MockitoSpyBean;
import org.springframework.test.web.servlet.MockMvc;
import org.testcontainers.containers.GenericContainer;
import org.testcontainers.junit.jupiter.Container;
import org.testcontainers.junit.jupiter.Testcontainers;
import org.testcontainers.postgresql.PostgreSQLContainer;
import org.testcontainers.rabbitmq.RabbitMQContainer;

/** RabbitMQ + Redis + PostgreSQL containers: the whole consumer path. */
@SpringBootTest
@AutoConfigureMockMvc
@ActiveProfiles("pipeline")
@Testcontainers
class EventPipelineTest {

    @Container @ServiceConnection
    static PostgreSQLContainer postgres = new PostgreSQLContainer("postgres:17-alpine");

    @Container @ServiceConnection
    static RabbitMQContainer rabbit = new RabbitMQContainer("rabbitmq:4.1-management-alpine");

    @Container @ServiceConnection(name = "redis")
    static GenericContainer<?> redis = new GenericContainer<>("redis:7.4-alpine").withExposedPorts(6379);

    static final String EXCHANGE = "servicedesk.events";
    static final String REQUEST_ID = "3f6c2a9e-8b1d-4e57-9a2c-1d7e5b4f0a11";

    @Autowired RabbitTemplate rabbitTemplate;
    @Autowired RabbitAdmin rabbitAdmin;
    @Autowired StringRedisTemplate redisTemplate;
    @Autowired JdbcClient jdbc;
    @Autowired MockMvc mvc;
    @Autowired HealthEndpoint healthEndpoint;
    @MockitoSpyBean ProjectionHandler handler;

    @BeforeEach
    void clean() {
        jdbc.sql("DELETE FROM pending_step").update();
        rabbitAdmin.purgeQueue(RabbitTopology.QUEUE, false);
        rabbitAdmin.purgeQueue(RabbitTopology.DLQ, false);
    }

    static String example(String name) throws Exception {
        return Files.readString(Path.of("../contracts/events/examples/" + name + ".json"));
    }

    static String withEventId(String json, UUID id) {
        return json.replaceFirst("\"eventId\": \"[^\"]+\"", "\"eventId\": \"" + id + "\"");
    }

    void publish(String routingKey, String body, String messageId) {
        Message m = MessageBuilder.withBody(body.getBytes(StandardCharsets.UTF_8))
                .setContentType("application/json")
                .setMessageId(messageId)
                .setType(routingKey)
                .setDeliveryMode(MessageDeliveryMode.PERSISTENT)
                .build();
        rabbitTemplate.send(EXCHANGE, routingKey, m);
    }

    /** Publishes a fulfilled event (a no-op for the table) and waits until it is processed. */
    void drainViaSentinel() throws Exception {
        UUID id = UUID.randomUUID();
        publish("request.fulfilled", withEventId(example("request.fulfilled"), id), id.toString());
        awaitProcessed(id);
    }

    void awaitProcessed(UUID id) {
        Awaitility.await().atMost(Duration.ofSeconds(20))
                .until(() -> Boolean.TRUE.equals(redisTemplate.hasKey("processed:" + id)));
    }

    long rowCount() {
        return jdbc.sql("SELECT count(*) FROM pending_step").query(Long.class).single();
    }

    @Test
    void exchangeQueueAndDeadLetterQueueExist() {
        assertThat(rabbitAdmin.getQueueProperties(RabbitTopology.QUEUE)).isNotNull();
        assertThat(rabbitAdmin.getQueueProperties(RabbitTopology.DLQ)).isNotNull();
        rabbitTemplate.execute(channel -> channel.exchangeDeclarePassive(EXCHANGE)); // throws if absent
    }

    @Test
    void sameMessagePublishedTwiceChangesTheTableOnce() throws Exception {
        UUID submittedId = UUID.fromString("0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d20");
        String body = example("request.submitted");

        publish("request.submitted", body, submittedId.toString());
        publish("request.submitted", body, submittedId.toString());
        drainViaSentinel(); // queue is FIFO: the duplicate has been consumed by now

        assertThat(rowCount()).isEqualTo(1);
        verify(handler, times(1)).handle(argThat(e -> e.eventId().equals(submittedId)));
        assertThat(redisTemplate.hasKey("processed:" + submittedId)).isTrue();
    }

    @Test
    void messagesFollowingTheLifecycleEndUpClosed() throws Exception {
        for (String name : new String[] {"request.submitted", "request.step_approved", "request.approved"}) {
            UUID id = UUID.randomUUID();
            publish(name, withEventId(example(name), id), id.toString());
        }
        drainViaSentinel();

        assertThat(jdbc.sql("SELECT closed FROM pending_step WHERE request_id = ?::uuid")
                .param(REQUEST_ID).query(Boolean.class).single()).isTrue();
    }

    @Test
    void malformedBodyGoesToTheDeadLetterQueue() {
        publish("request.submitted", "this is not json", UUID.randomUUID().toString());

        Message dead = rabbitTemplate.receive(RabbitTopology.DLQ, 20_000);

        assertThat(dead).isNotNull();
        assertThat(new String(dead.getBody(), StandardCharsets.UTF_8)).isEqualTo("this is not json");
        assertThat(rowCount()).isZero();
    }

    @Test
    void unknownTypeIsAckedAndNotDeadLettered() throws Exception {
        UUID id = UUID.randomUUID();
        publish("request.teleported",
                withEventId(example("request.submitted"), id).replace("\"request.submitted\"", "\"request.teleported\""),
                id.toString());
        drainViaSentinel();

        assertThat(rowCount()).isZero();
        assertThat(rabbitTemplate.receive(RabbitTopology.DLQ, 500)).isNull();
    }

    @Test
    void transientFailureIsRetriedThenSucceeds() throws Exception {
        doThrow(new IllegalStateException("db down")).doThrow(new IllegalStateException("db down"))
                .doCallRealMethod().when(handler).handle(any());
        UUID id = UUID.randomUUID();

        publish("request.submitted", withEventId(example("request.submitted"), id), id.toString());
        awaitProcessed(id);

        assertThat(rowCount()).isEqualTo(1);
        verify(handler, times(3)).handle(any());
    }

    @Test
    void persistentFailureIsDeadLetteredAfterThreeAttemptsAndNotMarked() throws Exception {
        doThrow(new IllegalStateException("db down")).when(handler).handle(any());
        UUID id = UUID.randomUUID();

        publish("request.submitted", withEventId(example("request.submitted"), id), id.toString());
        Message dead = rabbitTemplate.receive(RabbitTopology.DLQ, 20_000);

        assertThat(dead).isNotNull();
        verify(handler, times(3)).handle(any());
        assertThat(redisTemplate.hasKey("processed:" + id)).isFalse();
    }

    @Test
    void healthIsUpAndIncludesRabbitRedisAndTopology() throws Exception {
        mvc.perform(get("/notify/health"))
                .andExpect(status().isOk())
                .andExpect(content().json("{\"status\":\"UP\"}", true));

        Set<String> components = ((CompositeHealthDescriptor) healthEndpoint.health()).getComponents().keySet();
        assertThat(components).contains("rabbit", "redis", "topology");
    }
}
