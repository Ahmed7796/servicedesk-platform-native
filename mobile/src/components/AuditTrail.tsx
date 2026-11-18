import React from 'react';
import { StyleSheet, Text, View } from 'react-native';
import type { AuditEntry } from '../api/generated/servicedesk';
import { formatAge } from '../api/formatAge';

/** Entries are shown in the order the API sends them (oldest first); never re-sorted here. */
export function AuditTrail({ entries }: { entries: AuditEntry[] }) {
  return (
    <View testID="audit-trail" style={styles.box}>
      <Text accessibilityRole="header" style={styles.heading}>
        History
      </Text>
      {entries.map((e, i) => (
        <View key={i} style={styles.entry}>
          <Text testID={`audit-${i}`}>
            {e.action} by {e.actorName} · {e.statusBefore ? `${e.statusBefore} → ` : ''}
            {e.statusAfter} · {formatAge(e.occurredAt)}
          </Text>
          {e.details ? <Text style={styles.details}>{e.details}</Text> : null}
        </View>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  box: { gap: 8 },
  heading: { fontWeight: '600', fontSize: 16 },
  entry: { gap: 2 },
  details: { color: '#555' },
});
