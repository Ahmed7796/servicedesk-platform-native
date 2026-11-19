package com.servicedesk.notify.push;

import java.util.List;
import java.util.Map;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Service;

/**
 * Decides who to push and sends. Never throws: a push failure must not affect event
 * processing. Logs carry the request id and counts only, never a token or a title.
 */
@Service
public class PushNotifier {

    private static final Logger log = LoggerFactory.getLogger(PushNotifier.class);

    private final DeviceRepository devices;
    private final PushSender sender;
    private final boolean enabled;

    @Autowired
    public PushNotifier(DeviceRepository devices, PushSender sender,
                        @Value("${app.notify.push.enabled:true}") boolean enabled) {
        this.devices = devices;
        this.sender = sender;
        this.enabled = enabled;
    }

    public void notifyPending(PendingNotice n) {
        deliver(n, "Approval needed", "No approver assigned: " + n.title(),
                n.title() + " is waiting for your decision");
    }

    /** The step passed its due time. Same recipients as {@link #notifyPending}. */
    public void notifyOverdue(PendingNotice n) {
        String body = "Overdue: " + n.title();
        deliver(n, "Overdue", body, body);
    }

    private void deliver(PendingNotice n, String title, String unassignedBody, String assignedBody) {
        if (!enabled) return;
        try {
            boolean unassigned = n.approverUserIds().isEmpty();
            // No user directory here: "unassigned" goes to devices registered by Admins.
            List<String> tokens = unassigned ? devices.tokensForRole("Admin") : devices.tokensForUsers(n.approverUserIds());
            String body = unassigned ? unassignedBody : assignedBody;
            Map<String, String> data = Map.of("requestId", n.requestId().toString(), "type", n.type());
            int failed = 0;
            for (String token : tokens) {
                if (!sendOne(new PushMessage(token, title, body, data), token)) failed++;
            }
            log.info("Push for request {}: {} device(s), {} not delivered", n.requestId(), tokens.size(), failed);
        } catch (Exception e) {
            log.warn("Push for request {} skipped: {}", n.requestId(), e.getClass().getSimpleName());
        }
    }

    private boolean sendOne(PushMessage message, String token) {
        try {
            PushResult result = sender.send(message);
            if (result == PushResult.INVALID_TOKEN) devices.delete(token);
            return result == PushResult.OK;
        } catch (Exception e) {
            return false;
        }
    }
}
