package com.servicedesk.notify.events;

import org.springframework.amqp.core.AmqpAdmin;
import org.springframework.amqp.core.MessageListenerContainer;
import org.springframework.amqp.rabbit.listener.RabbitListenerEndpointRegistry;
import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.boot.health.contributor.Health;
import org.springframework.boot.health.contributor.HealthIndicator;
import org.springframework.stereotype.Component;

/**
 * UP only when {@code notify.events} exists and every listener container is running. The
 * container declares the topology (queue and binding) before it starts consuming, so a
 * running container means the queue is bound.
 */
@Component
@ConditionalOnProperty(name = "app.messaging.enabled", havingValue = "true", matchIfMissing = true)
public class TopologyHealthIndicator implements HealthIndicator {

    private final AmqpAdmin admin;
    private final RabbitListenerEndpointRegistry registry;

    public TopologyHealthIndicator(AmqpAdmin admin, RabbitListenerEndpointRegistry registry) {
        this.admin = admin;
        this.registry = registry;
    }

    @Override
    public Health health() {
        try {
            if (admin.getQueueProperties(RabbitTopology.QUEUE) == null) {
                return Health.down().build();
            }
            var containers = registry.getListenerContainers();
            boolean consuming = !containers.isEmpty()
                    && containers.stream().allMatch(MessageListenerContainer::isRunning);
            return consuming ? Health.up().build() : Health.down().build();
        } catch (RuntimeException e) {
            return Health.down().build();
        }
    }
}
