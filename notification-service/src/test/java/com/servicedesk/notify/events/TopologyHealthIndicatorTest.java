package com.servicedesk.notify.events;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.when;

import java.util.List;
import java.util.Properties;
import org.junit.jupiter.api.Test;
import org.springframework.amqp.core.AmqpAdmin;
import org.springframework.amqp.core.MessageListenerContainer;
import org.springframework.amqp.rabbit.listener.RabbitListenerEndpointRegistry;
import org.springframework.boot.health.contributor.Status;

/** DOWN until the queue exists and a listener container is running (so it is bound). */
class TopologyHealthIndicatorTest {

    AmqpAdmin admin = mock(AmqpAdmin.class);
    RabbitListenerEndpointRegistry registry = mock(RabbitListenerEndpointRegistry.class);
    TopologyHealthIndicator indicator = new TopologyHealthIndicator(admin, registry);
    MessageListenerContainer running = container(true);
    MessageListenerContainer stopped = container(false);

    static MessageListenerContainer container(boolean running) {
        MessageListenerContainer c = mock(MessageListenerContainer.class);
        when(c.isRunning()).thenReturn(running);
        return c;
    }

    @Test
    void downWhenQueueIsMissing() {
        when(admin.getQueueProperties(RabbitTopology.QUEUE)).thenReturn(null);
        when(registry.getListenerContainers()).thenReturn(List.of(running));

        assertThat(indicator.health().getStatus()).isEqualTo(Status.DOWN);
    }

    @Test
    void downWhenNoListenerContainerIsRunning() {
        when(admin.getQueueProperties(RabbitTopology.QUEUE)).thenReturn(new Properties());
        when(registry.getListenerContainers()).thenReturn(List.of(stopped));

        assertThat(indicator.health().getStatus()).isEqualTo(Status.DOWN);
    }

    @Test
    void downWhenThereAreNoListenerContainers() {
        when(admin.getQueueProperties(RabbitTopology.QUEUE)).thenReturn(new Properties());
        when(registry.getListenerContainers()).thenReturn(List.of());

        assertThat(indicator.health().getStatus()).isEqualTo(Status.DOWN);
    }

    @Test
    void downWhenTheBrokerThrows() {
        when(admin.getQueueProperties(RabbitTopology.QUEUE)).thenThrow(new IllegalStateException("broker down"));

        assertThat(indicator.health().getStatus()).isEqualTo(Status.DOWN);
    }

    @Test
    void upWhenQueueExistsAndListenerIsRunning() {
        when(admin.getQueueProperties(RabbitTopology.QUEUE)).thenReturn(new Properties());
        when(registry.getListenerContainers()).thenReturn(List.of(running));

        assertThat(indicator.health().getStatus()).isEqualTo(Status.UP);
    }
}
